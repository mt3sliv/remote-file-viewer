'use strict';
const $ = id => document.getElementById(id);
const canvas = $('screen');
const context = canvas.getContext('2d');
let socket = null, sessionId = null;
let generation = 0, opening = false, leaving = false;
const page = document.documentElement?.dataset || {};
const base = page.remoteFrameBase || '';
const selectedFileId = new URLSearchParams(location.search || '').get('fileId');
const endpoint = path => base + path;
const status = message => { $('status').textContent = message; };
async function api(url, options={}) {
  const csrf = options.method && options.method !== 'GET' && page.csrfHeader && page.csrfToken
    ? {[page.csrfHeader]:page.csrfToken} : {};
  const response = await fetch(endpoint(url), {
    credentials:'same-origin', ...options,
    headers: { 'Content-Type':'application/json', ...csrf, ...(options.headers||{}) }
  });
  if (!response.ok) throw new Error((await response.json().catch(()=>null))?.error || `Request failed (${response.status})`);
  return response.status === 204 ? null : response.json();
}
$('selectedFile').textContent = selectedFileId || 'No file selected';
let limits = null;
async function loadSettings() {
  try {
    limits = await api('/api/frame-settings');
    $('save').hidden = limits.showSaveButton !== true;
    $('fps').value = limits.defaults.framesPerSecond;
    $('fps').max = limits.maxFramesPerSecond;
    $('captureWidth').value = limits.defaults.captureWidth;
    $('captureWidth').max = limits.maxCaptureWidth;
    $('captureHeight').value = limits.defaults.captureHeight;
    $('captureHeight').max = limits.maxCaptureHeight;
    $('streamWidth').value = limits.defaults.streamWidth;
    $('streamWidth').max = limits.maxCaptureWidth;
    $('streamHeight').value = limits.defaults.streamHeight;
    $('streamHeight').max = limits.maxCaptureHeight;
    $('fitViewport').checked = limits.fitStreamToViewport === true;
  } catch (e) { status(e.message); throw e; }
}
const settingsReady = loadSettings();
settingsReady.then(() => {
  $('open').disabled = !selectedFileId || leaving;
  if (selectedFileId && !leaving) openFile();
  else status('Open a file from your application’s file list.');
}).catch(e => status(e.message));
function requestedConfiguration() {
  const captureWidth = Number($('captureWidth').value), captureHeight = Number($('captureHeight').value);
  // CSS width is stable when the PNG size changes. Bound DPR to avoid excessive captures.
  const ratio = $('fitViewport').checked
    ? Math.min(1, Math.max(160, Math.round(canvas.getBoundingClientRect().width * Math.min(devicePixelRatio || 1, 2))) / captureWidth,
        Math.max(100, Math.round(innerHeight * Math.min(devicePixelRatio || 1, 2))) / captureHeight) : 1;
  return {framesPerSecond:Number($('fps').value), captureWidth, captureHeight,
    streamWidth:$('fitViewport').checked ? Math.max(160, Math.round(captureWidth * ratio)) : Number($('streamWidth').value),
    streamHeight:$('fitViewport').checked ? Math.max(100, Math.round(captureHeight * ratio)) : Number($('streamHeight').value)};
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
function sessionActions(enabled) {
  for (const id of ['close', 'applySettings']) $(id).disabled = !enabled;
  $('fullscreen').disabled = !document.fullscreenElement && (!enabled || !$('viewer').requestFullscreen);
  canvas.tabIndex = enabled ? 0 : -1;
  canvas.setAttribute('aria-disabled', String(!enabled));
}
sessionActions(false);
$('fullscreen').onclick = async () => {
  try {
    if (document.fullscreenElement === $('viewer')) await document.exitFullscreen();
    else await $('viewer').requestFullscreen();
  } catch (e) { status(e.message); }
};
document.addEventListener('fullscreenchange', () => {
  cancelPointer();
  const full = document.fullscreenElement === $('viewer');
  $('fullscreen').textContent = full ? 'Exit fullscreen' : 'Enter fullscreen';
  $('fullscreen').setAttribute('aria-pressed', String(full));
  sessionActions(socket?.readyState === WebSocket.OPEN);
  if ($('fitViewport').checked) updateConfiguration();
});
async function closeSession() {
  generation++;
  $('save').disabled=true;
  cancelPointer();
  const ws = socket, id = sessionId;
  socket=null; sessionId=null;
  sessionActions(false);
  context.clearRect(0, 0, canvas.width, canvas.height);
  if (ws) ws.close();
  if (id) {
    await api('/api/sessions/'+encodeURIComponent(id),{method:'DELETE',keepalive:true});
  }
}
$('close').onclick = async () => {
  $('open').disabled = true;
  try { await closeSession(); status('Session closed'); }
  catch (e) { status('Could not close the server session: ' + e.message); }
  finally { $('open').disabled = leaving || !selectedFileId; }
};
$('open').onclick = openFile;
async function openFile() {
  if (opening || leaving) return;
  opening = true;
  $('open').disabled = true;
  let attempt;
  try {
    await closeSession();
    attempt = generation;
    await settingsReady;
    if (attempt !== generation || leaving) return;
    if (!selectedFileId) { status('Open a file from your application’s file list.'); return; }
    status('Opening the selected file…');
    const data = await api('/api/sessions',{method:'POST',body:JSON.stringify({fileId:selectedFileId,configuration:requestedConfiguration()})});
    if (attempt !== generation || leaving) {
      await api('/api/sessions/'+encodeURIComponent(data.id),{method:'DELETE',keepalive:true});
      return;
    }
    sessionId=data.id;
    const editable = data.canSave === true;
    const proto=location.protocol==='https:'?'wss:':'ws:';
    const ws = new WebSocket(`${proto}//${location.host}${endpoint('/ws/'+encodeURIComponent(sessionId))}`);
    socket=ws;
    ws.binaryType='blob';
    ws.onopen=()=>{ if(socket!==ws)return; sessionActions(true); $('save').disabled=!editable || limits.showSaveButton !== true; status(editable && limits.showSaveButton === true ? 'Connected. Click the application to edit. Save updates the server file.' : 'Connected. Use the mouse wheel or navigation keys to scroll.');canvas.focus();};
    ws.onmessage = latestFrameReceiver(ws);
    ws.onerror=()=>{if(socket===ws)status('WebSocket error.');};
    ws.onclose=()=>{ if(socket===ws) {
      cancelPointer(); const id=sessionId; socket=null; sessionId=null;
      sessionActions(false); context.clearRect(0, 0, canvas.width, canvas.height);
      if(id) api('/api/sessions/'+encodeURIComponent(id),{method:'DELETE',keepalive:true}).catch(()=>{});
      $('save').disabled=true; status('Viewer connection closed.');
    } };
  } catch(e) { if (attempt === generation) { await closeSession().catch(()=>{}); status(e.message); } }
  finally { opening=false; $('open').disabled=leaving || !selectedFileId; }
}
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
  // Work in CSS pixels, then map the contained image to its native frame pixels.
  // DPR changes stream resolution only; multiplying input coordinates by DPR would apply it twice.
  const width = canvas.clientWidth || r.width, height = canvas.clientHeight || r.height;
  const scale = Math.min(width / canvas.width, height / canvas.height);
  const left = r.left + (canvas.clientLeft || 0) + (width - canvas.width * scale) / 2;
  const top = r.top + (canvas.clientTop || 0) + (height - canvas.height * scale) / 2;
  return {x:Math.max(0,Math.min(canvas.width-1,Math.round((e.clientX-left)/scale))),y:Math.max(0,Math.min(canvas.height-1,Math.round((e.clientY-top)/scale))),button:e.button,
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
  if (socket?.readyState !== WebSocket.OPEN) return;
  const r = canvas.getBoundingClientRect();
  const scale = Math.min((canvas.clientWidth || r.width)/canvas.width, (canvas.clientHeight || r.height)/canvas.height);
  const x = e.clientX-r.left-(canvas.clientLeft||0), y = e.clientY-r.top-(canvas.clientTop||0);
  if (Math.abs(x-(canvas.clientWidth||r.width)/2)>canvas.width*scale/2 || Math.abs(y-(canvas.clientHeight||r.height)/2)>canvas.height*scale/2) return;
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
$('save').onclick=()=>{if($('save').hidden || $('save').disabled)return;sendInput({kind:'save'});status('Save shortcut sent. Check the application for any save or format prompts.');};
window.addEventListener('pagehide',()=>{leaving=true;closeSession().catch(()=>{});});
