'use strict';
const $ = id => document.getElementById(id);
const canvas = $('screen');
const context = canvas.getContext('2d');
let socket = null, sessionId = null;
const status = message => { $('status').textContent = message; };
async function api(url, options={}) {
  const response = await fetch(url, {
    credentials:'same-origin', ...options,
    headers: { 'Content-Type':'application/json', ...(options.headers||{}) }
  });
  if (!response.ok) throw new Error((await response.json().catch(()=>null))?.error || `Request failed (${response.status})`);
  return response.status === 204 ? null : response.json();
}
async function loadFiles() {
  try {
    const files = await api('/api/files');
    $('files').replaceChildren(...files.map(file => new Option(file,file)));
    status('Choose a sample file. Office edits are saved to the demo output copy.');
  } catch (e) { status(e.message); }
}
let limits = null;
async function loadSettings() {
  try {
    limits = await api('/api/frame-settings');
    $('fps').value = limits.defaults.framesPerSecond;
    $('fps').max = limits.maxFramesPerSecond;
    $('captureWidth').value = limits.defaults.captureWidth;
    $('captureWidth').max = limits.maxCaptureWidth;
    $('captureHeight').value = limits.defaults.captureHeight;
    $('captureHeight').max = limits.maxCaptureHeight;
  } catch (e) { status(e.message); }
}
const settingsReady = loadSettings();
loadFiles();
function requestedConfiguration() {
  const captureWidth = Number($('captureWidth').value), captureHeight = Number($('captureHeight').value);
  // CSS width is stable when the PNG size changes. Bound DPR to avoid excessive captures.
  const ratio = $('fitViewport').checked
    ? Math.min(1, Math.max(160, Math.round(canvas.getBoundingClientRect().width * Math.min(devicePixelRatio || 1, 2))) / captureWidth,
        Math.max(100, Math.round(innerHeight * Math.min(devicePixelRatio || 1, 2))) / captureHeight) : 1;
  return {framesPerSecond:Number($('fps').value), captureWidth, captureHeight,
    streamWidth:Math.max(160, Math.round(captureWidth * ratio)), streamHeight:Math.max(100, Math.round(captureHeight * ratio))};
}
let updating = false, settingsDirty = false;
async function updateConfiguration() {
  if (!sessionId) return;
  if (updating) { settingsDirty = true; return; }
  updating = true;
  const id = sessionId;
  try {
    const data = await api('/api/sessions/' + encodeURIComponent(id) + '/configuration',
      {method:'PUT',body:JSON.stringify(requestedConfiguration())});
    if (id === sessionId) status(`Streaming up to ${data.configuration.framesPerSecond} FPS at ${data.configuration.streamWidth} × ${data.configuration.streamHeight}.`);
  } catch(e) { if (id === sessionId) status(e.message); }
  finally { updating = false; if(settingsDirty) { settingsDirty = false; updateConfiguration(); } }
}
$('applySettings').onclick = updateConfiguration;
let resizeTimer;
new ResizeObserver(() => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => { if ($('fitViewport').checked) updateConfiguration(); }, 250);
}).observe(canvas.parentElement);
window.addEventListener('resize', () => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => { if ($('fitViewport').checked) updateConfiguration(); }, 250);
});
async function closeSession() {
  $('save').disabled=true;
  cancelPointer();
  if (socket) { socket.close(); socket=null; }
  if (sessionId) {
    const id = sessionId; sessionId=null;
    try { await api('/api/sessions/'+encodeURIComponent(id),{method:'DELETE'}); } catch {}
  }
}
$('close').onclick = async () => { await closeSession(); status('Session closed.'); };
$('open').onclick = async () => {
  try {
    await closeSession();
    await settingsReady;
    status('Opening the selected sample…');
    const data = await api('/api/sessions',{method:'POST',body:JSON.stringify({fileId:$('files').value,configuration:requestedConfiguration()})});
    sessionId=data.id;
    const editable=/\.(docx?|odt|xlsx?|xlsm|ods|pptx?|odp)$/i.test($('files').value);
    const proto=location.protocol==='https:'?'wss:':'ws:';
    const ws = new WebSocket(`${proto}//${location.host}/ws/${encodeURIComponent(sessionId)}`);
    socket=ws;
    ws.binaryType='blob';
    ws.onopen=()=>{ $('save').disabled=!editable; status(editable ? 'Connected. Click the application to edit. Save updates the demo output copy.' : 'Connected. Read-only text: use the mouse wheel or navigation keys to scroll.');canvas.focus();};
    ws.onmessage = latestFrameReceiver(ws);
    ws.onerror=()=>status('WebSocket error.');
    ws.onclose=()=>{ if(socket===ws) { $('save').disabled=true; status('Viewer connection closed.'); } };
  } catch(e) { status(e.message); }
};
function latestFrameReceiver(ws) {
  let decoding = false, pending = null;
  return async event => {
    if (socket !== ws) return;
    pending = event.data;
    if (decoding) return;
    decoding = true;
    try {
      while (pending !== null && socket === ws) {
        const data = pending;
        pending = null;
        let image;
        try {
          image = await createImageBitmap(data);
          if (socket !== ws) return;
          // Assigning either dimension clears the canvas and resets its state.
          if (canvas.width !== image.width) canvas.width = image.width;
          if (canvas.height !== image.height) canvas.height = image.height;
          context.drawImage(image, 0, 0);
        } catch(e) { if (socket === ws) status(e.message); }
        finally { image?.close(); }
      }
    } finally { pending = null; decoding = false; }
  };
}
function sendInput(input){if(socket?.readyState===WebSocket.OPEN)socket.send(JSON.stringify(input));}
canvas.addEventListener('wheel', e=>{
  e.preventDefault();
  sendInput({kind:'wheel',delta: e.deltaY > 0 ? 1 : -1,...point(e)});
},{passive:false});
canvas.addEventListener('keydown', e=>{
  if (['Control','Alt','Shift','Meta'].includes(e.key)) return;
  e.preventDefault();
  sendInput({kind:'key',key:e.key,ctrl:e.ctrlKey,alt:e.altKey,shift:e.shiftKey});
});
function point(e) {
  const r=canvas.getBoundingClientRect();
  return {x:Math.round((e.clientX-r.left)*canvas.width/r.width),y:Math.round((e.clientY-r.top)*canvas.height/r.height),button:e.button,
    ctrl:!!e.ctrlKey,shift:!!e.shiftKey,alt:!!e.altKey,frameWidth:canvas.width,frameHeight:canvas.height};
}
let activePointer = null, heldButtons = 0;
const mouseButtons = [[0,1],[1,4],[2,2]]; // DOM buttons uses a different middle-button mask than Win32.
function syncPointerButtons(e) {
  const next = e.buttons & 7;
  // Release before pressing when a chord changes; do not throttle button transitions.
  for (const [button, bit] of mouseButtons)
    if ((heldButtons & bit) && !(next & bit)) sendInput({kind:'pointerup',...point(e),button});
  for (const [button, bit] of mouseButtons)
    if (!(heldButtons & bit) && (next & bit)) sendInput({kind:'pointerdown',...point(e),button});
  heldButtons = next;
}
function cancelPointer() {
  if (activePointer === null && heldButtons === 0) return;
  const id = activePointer;
  activePointer = null; heldButtons = 0;
  sendInput({kind:'release'});
  if (id !== null && canvas.hasPointerCapture(id)) canvas.releasePointerCapture(id);
}
canvas.addEventListener('pointerdown',e=>{
  if (activePointer !== null && activePointer !== e.pointerId) return;
  e.preventDefault(); canvas.focus(); activePointer = e.pointerId;
  try { canvas.setPointerCapture(e.pointerId); }
  catch { cancelPointer(); return; }
  syncPointerButtons(e);
});
canvas.addEventListener('pointerup',e=>{
  if (activePointer !== e.pointerId) return;
  e.preventDefault(); syncPointerButtons(e);
  if (heldButtons === 0) {
    activePointer = null;
    if (canvas.hasPointerCapture(e.pointerId)) canvas.releasePointerCapture(e.pointerId);
  }
});
let lastMove=0;
canvas.addEventListener('pointermove',e=>{
  if (activePointer !== null && activePointer !== e.pointerId) return;
  if (activePointer === e.pointerId) {
    syncPointerButtons(e); // Heal a missed up, including chorded mouse-button releases.
    if (heldButtons === 0) cancelPointer();
  }
  if(Date.now()-lastMove>40){lastMove=Date.now();sendInput({kind:'pointermove',...point(e)});}
});
canvas.addEventListener('contextmenu',e=>e.preventDefault());
canvas.addEventListener('pointercancel',e=>{if(activePointer===e.pointerId)cancelPointer();});
canvas.addEventListener('lostpointercapture',e=>{if(activePointer===e.pointerId)cancelPointer();});
canvas.addEventListener('blur',cancelPointer);
window.addEventListener('blur',cancelPointer);
document.addEventListener('visibilitychange',()=>{if(document.hidden)cancelPointer();});
$('save').onclick=()=>{sendInput({kind:'save'});status('Save shortcut sent. Check the application for any save or format prompts.');};
window.addEventListener('pagehide',()=>{cancelPointer();if(socket)socket.close();});
