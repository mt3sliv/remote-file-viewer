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
loadFiles();
async function closeSession() {
  $('save').disabled=true;
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
    status('Opening the selected sample…');
    const data = await api('/api/sessions',{method:'POST',body:JSON.stringify({fileId:$('files').value})});
    sessionId=data.id;
    const editable=/\.(docx?|odt|xlsx?|ods|pptx?|odp)$/i.test($('files').value);
    const proto=location.protocol==='https:'?'wss:':'ws:';
    const ws = new WebSocket(`${proto}//${location.host}/ws/${encodeURIComponent(sessionId)}`);
    socket=ws;
    ws.binaryType='blob';
    ws.onopen=()=>{ $('save').disabled=!editable; status(editable ? 'Connected. Click the application to edit. Save updates the demo output copy.' : 'Connected. Read-only text: use the mouse wheel or navigation keys to scroll.');canvas.focus();};
    ws.onmessage=async event=>{
      if (socket!==ws) return;
      const image=await createImageBitmap(event.data);
      if (socket !== ws) { image.close(); return; }
      canvas.width=image.width; canvas.height=image.height;
      context.drawImage(image,0,0);
      image.close();
    };
    ws.onerror=()=>status('WebSocket error.');
    ws.onclose=()=>{ if(socket===ws) { $('save').disabled=true; status('Viewer connection closed.'); } };
  } catch(e) { status(e.message); }
};
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
  return {x:Math.round((e.clientX-r.left)*canvas.width/r.width),y:Math.round((e.clientY-r.top)*canvas.height/r.height),button:e.button};
}
canvas.addEventListener('pointerdown',e=>{e.preventDefault();canvas.focus();canvas.setPointerCapture(e.pointerId);sendInput({kind:'pointerdown',...point(e)});});
canvas.addEventListener('pointerup',e=>{e.preventDefault();sendInput({kind:'pointerup',...point(e)});});
let lastMove=0;
canvas.addEventListener('pointermove',e=>{if(Date.now()-lastMove>40){lastMove=Date.now();sendInput({kind:'pointermove',...point(e)});}});
canvas.addEventListener('contextmenu',e=>e.preventDefault());
canvas.addEventListener('lostpointercapture',()=>sendInput({kind:'release'}));
canvas.addEventListener('blur',()=>sendInput({kind:'release'}));
$('save').onclick=()=>{sendInput({kind:'save'});status('Save shortcut sent. Check the application for any save or format prompts.');};
window.addEventListener('pagehide',()=>{if(socket)socket.close();});
