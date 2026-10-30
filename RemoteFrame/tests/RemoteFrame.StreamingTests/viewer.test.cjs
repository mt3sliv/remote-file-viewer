const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const elements = new Map();
const handlers = new Map();
const on = scope => (name, fn) => handlers.set(`${scope}:${name}`,fn);
const captures = new Set();
const element = id => {
  if (!elements.has(id)) elements.set(id, {value:'',checked:true,width:1280,height:800,
    getContext:()=>({}),getBoundingClientRect:()=>({width:640}),addEventListener:on(id),replaceChildren(){},parentElement:{},focus(){},
    setPointerCapture:id=>captures.add(id),hasPointerCapture:id=>captures.has(id),releasePointerCapture:id=>{
      captures.delete(id); handlers.get('screen:lostpointercapture')?.({pointerId:id});
    }});
  return elements.get(id);
};
const sandbox = {document:{getElementById:element,addEventListener:on('document')},fetch:async()=>({ok:true,status:200,json:async()=>[]}),
  ResizeObserver:class { observe(){} },window:{addEventListener:on('window')},setTimeout,clearTimeout,devicePixelRatio:1,innerHeight:1000,
  WebSocket:{OPEN:1},URLSearchParams,location:{search:'',protocol:'http:',host:'localhost'},
  Option:class{},console};
vm.createContext(sandbox);
vm.runInContext(fs.readFileSync('src/RemoteFrame.AspNetCore/Viewer/viewer.js','utf8'),sandbox);
element('captureWidth').value='1280'; element('captureHeight').value='800'; element('fps').value='10';
element('streamWidth').value='1280'; element('streamHeight').value='800';
let config = vm.runInContext('requestedConfiguration()',sandbox);
assert.equal(config.streamWidth,640); assert.equal(config.streamHeight,400);
sandbox.devicePixelRatio=4;
config=vm.runInContext('requestedConfiguration()',sandbox);
assert.equal(config.streamWidth,1280); assert.equal(config.streamHeight,800);
element('fitViewport').checked=false;
sandbox.devicePixelRatio=1;
assert.equal(vm.runInContext('requestedConfiguration().streamWidth',sandbox),1280);
element('screen').getBoundingClientRect=()=>({left:0,top:0,width:640,height:400});
const point=vm.runInContext('point({clientX:320,clientY:200,button:0})',sandbox);
assert.equal(point.x,640); assert.equal(point.y,400);
assert.equal(point.frameWidth,1280); assert.equal(point.frameHeight,800);
console.log('PASS: Viewport sizing, aspect ratio, DPR ceiling and full resolution selection');
function pointerChecks() {
  const sent = [];
  sandbox.testSocket = {readyState:1,send:value=>sent.push(JSON.parse(value))};
  vm.runInContext('socket = testSocket;',sandbox);
  const emit = (name, values={}) => handlers.get(`screen:${name}`)({pointerId:1,button:0,buttons:1,clientX:100,clientY:100,preventDefault(){},...values});
  emit('pointerdown'); emit('pointerup',{buttons:0});
  assert.deepEqual(sent.map(x=>x.kind),['pointerdown','pointerup']);
  assert.equal(captures.size,0);
  for (const ending of ['pointercancel','lostpointercapture','blur']) {
    sent.length=0; emit('pointerdown'); emit(ending); emit('pointerup',{buttons:0});
    assert.deepEqual(sent.map(x=>x.kind),['pointerdown','release']);
  }
  for (const ending of ['window:blur','document:visibilitychange']) {
    sent.length=0; emit('pointerdown'); sandbox.document.hidden=true; handlers.get(ending)();
    assert.deepEqual(sent.map(x=>x.kind),['pointerdown','release']);
  }
  sent.length=0; emit('pointerdown'); emit('pointermove',{buttons:0});
  assert.deepEqual(sent.filter(x=>x.kind!=='pointermove').map(x=>x.kind),['pointerdown','pointerup','release']);
  sent.length=0; emit('pointerdown'); emit('pointermove',{buttons:3});
  emit('pointermove',{buttons:2}); emit('pointerup',{button:2,buttons:0});
  assert.deepEqual(sent.filter(x=>x.kind!=='pointermove').map(x=>[x.kind,x.button]),
    [['pointerdown',0],['pointerdown',2],['pointerup',0],['pointerup',2]]);
  sent.length=0; emit('pointerdown'); emit('pointerup',{pointerId:2,buttons:0});
  assert.equal(sent.length,1); emit('pointercancel');
  console.log('PASS: Balanced mouse presses, capture loss/cancel/blur, hidden page, missed-up recovery, chords and unrelated pointers');
}
pointerChecks();
async function decodingChecks() {
  const requests = [], drawn = [], closed = [];
  sandbox.createImageBitmap = data => new Promise((resolve, reject) => requests.push({data,resolve,reject}));
  vm.runInContext('context.drawImage = image => recordDraw(image.id); socket = {}; globalThis.receive = latestFrameReceiver(socket);',
    Object.assign(sandbox, {recordDraw:id=>drawn.push(id)}));
  const bitmap = id => ({id,width:1280,height:800,close:()=>closed.push(id)});
  const first = sandbox.receive({data:1});
  await sandbox.receive({data:2});
  await sandbox.receive({data:3});
  assert.equal(requests.length,1);
  requests[0].resolve(bitmap(1));
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(requests[1].data,3);
  requests[1].resolve(bitmap(3));
  await first;
  assert.deepEqual(drawn,[1,3]);
  assert.deepEqual(closed,[1,3]);
  const stale = sandbox.receive({data:4});
  vm.runInContext('socket = {};',sandbox);
  requests[2].resolve(bitmap(4));
  await stale;
  assert.deepEqual(drawn,[1,3]);
  assert.deepEqual(closed,[1,3,4]);
  console.log('PASS: One decode in flight, newest pending frame retained, stale-session bitmap closed');
}
decodingChecks().catch(error=>{console.error(error);process.exitCode=1;});
