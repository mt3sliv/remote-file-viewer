const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const script = fs.readFileSync('src/RemoteFrame.AspNetCore/Viewer/viewer.js','utf8');
const flush = async () => { for(let i=0;i<5;i++) await new Promise(setImmediate); };
function app({fileId, pending, connectThrows=false, showSaveButton=true}={}) {
  const elements = new Map(), handlers = new Map(), requests = [], sockets = [];
  const element = id => {
    if(!elements.has(id)) elements.set(id, {value:'',checked:false,width:1280,height:800,disabled:false,
      setAttribute(name,value){this[name]=value;},getContext:()=>({clearRect(){element(id).cleared=true;}}),getBoundingClientRect:()=>({width:640}),focus(){},parentElement:{},
      replaceChildren(...options){this.value=options[0]?.value||'';this.options=options;},
      addEventListener(name,fn){handlers.set(`${id}:${name}`,fn);},hasPointerCapture:()=>false});
    return elements.get(id);
  };
  class Socket {
    static OPEN=1;
    constructor(url){if(connectThrows)throw new Error('Connection setup failed');this.url=url;this.readyState=1;sockets.push(this);}
    close(){this.readyState=3;this.onclose?.();}
    send(){}
  }
  const response = value => ({ok:true,status:200,json:async()=>value});
  const sandbox = {
    document:{documentElement:{dataset:{remoteFrameBase:'/portal/renderer',csrfHeader:'X-CSRF',csrfToken:'csrf-token'}},
      getElementById:element,addEventListener:(name,fn)=>handlers.set(`document:${name}`,fn)},
    window:{addEventListener:(name,fn)=>handlers.set(`window:${name}`,fn)},
    location:{protocol:'https:',host:'app.example',search:fileId===undefined?'':`?fileId=${encodeURIComponent(fileId)}`},
    URLSearchParams,Option:class{constructor(text,value){this.text=text;this.value=value;}},
    ResizeObserver:class{observe(){}},setTimeout,clearTimeout,devicePixelRatio:1,innerHeight:1000,WebSocket:Socket,
    fetch:async(url,options)=>{
      requests.push({url,...options});
      if(url.endsWith('/api/frame-settings'))return response({defaults:{framesPerSecond:12,captureWidth:1280,captureHeight:800,streamWidth:640,streamHeight:400},fitStreamToViewport:false,showSaveButton,maxFramesPerSecond:24,maxCaptureWidth:3840,maxCaptureHeight:2160});
      if(url.endsWith('/api/files'))return response(['opaque-id']);
      if(options.method==='DELETE')return {ok:true,status:204};
      if(options.method==='PUT')return response({configuration:JSON.parse(options.body)});
      if(options.method==='POST'){if(pending)await pending;return response({id:'session/one',canSave:true});}
      throw new Error('Unexpected URL: '+url);
    }
  };
  vm.createContext(sandbox);vm.runInContext(script,sandbox);
  return {element,handlers,requests,sockets,document:sandbox.document};
}
test('Selected opaque IDs auto-open with PathBase, server capability and CSRF headers',async()=>{
  const fileId='private/<img onerror=x> & report';
  const viewer=app({fileId});await flush();
  assert.equal(viewer.requests.filter(x=>x.url.endsWith('/api/files')).length,0);
  const post=viewer.requests.find(x=>x.method==='POST');
  assert.equal(post.url,'/portal/renderer/api/sessions');
  assert.equal(JSON.parse(post.body).fileId,fileId);
  assert.equal(JSON.parse(post.body).configuration.framesPerSecond,12);
  assert.equal(post.credentials,'same-origin');assert.equal(post.headers['X-CSRF'],'csrf-token');
  assert.equal(viewer.element('selectedFile').textContent,fileId);
  assert.equal(JSON.parse(post.body).configuration.streamWidth,640);
  assert.equal(JSON.parse(post.body).configuration.streamHeight,400);
  assert.equal(viewer.sockets[0].url,'wss://app.example/portal/renderer/ws/session%2Fone');
  viewer.sockets[0].onopen();assert.equal(viewer.element('save').disabled,false);
  viewer.element('fps').value=18;await viewer.element('applySettings').onclick();
  const put=viewer.requests.find(x=>x.method==='PUT');assert.equal(JSON.parse(put.body).framesPerSecond,18);
  assert.equal(put.headers['X-CSRF'],'csrf-token');
  await viewer.element('close').onclick();
  const deleted=viewer.requests.find(x=>x.method==='DELETE');
  assert.equal(deleted.url,'/portal/renderer/api/sessions/session%2Fone');assert.equal(deleted.keepalive,true);
  assert.equal(viewer.element('save').disabled,true);
});
test('No ID never enumerates files or opens a session',async()=>{
  const viewer=app();await flush();assert.equal(viewer.requests.filter(x=>x.method==='POST').length,0);
  assert.equal(viewer.element('open').disabled,true);await viewer.element('open').onclick();
  assert.equal(viewer.requests.filter(x=>x.url.endsWith('/api/files')).length,0);
  assert.equal(viewer.requests.filter(x=>x.method==='POST').length,0);
  assert.equal(viewer.element('selectedFile').textContent,'No file selected');
});
test('Double open and closing/unloading during POST cannot leak an unattached session',async()=>{
  for(const ending of ['close','pagehide']){
    let finish;const pending=new Promise(resolve=>finish=resolve);
    const viewer=app({fileId:'opaque',pending});await flush();await viewer.element('open').onclick();
    assert.equal(viewer.requests.filter(x=>x.method==='POST').length,1);
    if(ending==='close')await viewer.element('close').onclick();else viewer.handlers.get('window:pagehide')();
    finish();await flush();assert.equal(viewer.sockets.length,0);
    assert.equal(viewer.requests.filter(x=>x.method==='DELETE'&&x.keepalive).length,1);
  }
});
test('WebSocket construction failure releases the newly created session',async()=>{
  const viewer=app({fileId:'opaque',connectThrows:true});await flush();
  assert.equal(viewer.requests.filter(x=>x.method==='DELETE').length,1);
  assert.equal(viewer.element('status').textContent,'Connection setup failed');
  assert.equal(viewer.element('open').disabled,false);
});
test('Failed or disconnected sockets release sessions even if the upgrade never reached the server',async()=>{
  const viewer=app({fileId:'opaque'});await flush();
  viewer.sockets[0].onerror();viewer.sockets[0].onclose();viewer.sockets[0].onclose();await flush();
  assert.equal(viewer.requests.filter(x=>x.method==='DELETE').length,1);
  assert.equal(viewer.element('save').disabled,true);
});

test('Save visibility defaults hidden unless explicitly enabled; close clears and disables actions',async()=>{
  for (const showSaveButton of [undefined,false,true]) {
    const viewer=app({fileId:'opaque',showSaveButton:showSaveButton === undefined ? null : showSaveButton});await flush();
    viewer.sockets[0].onopen();
    assert.equal(viewer.element('save').hidden,showSaveButton!==true);
    assert.equal(viewer.element('save').disabled,showSaveButton!==true);
    await viewer.element('close').onclick();
    assert.equal(viewer.element('screen').cleared,true);
    assert.equal(viewer.element('screen')['aria-disabled'],'true');
    assert.equal(viewer.element('close').disabled,true);
    assert.equal(viewer.element('applySettings').disabled,true);
    assert.equal(viewer.element('status').textContent,'Session closed');
  }
});

test('Fullscreen toggles the viewer and follows browser Escape changes',async()=>{
  const viewer=app({fileId:'opaque'});await flush();
  const container=viewer.element('viewer');
  container.requestFullscreen=async()=>{viewer.document.fullscreenElement=container;};
  viewer.document.exitFullscreen=async()=>{viewer.document.fullscreenElement=null;};
  viewer.sockets[0].onopen();
  assert.equal(viewer.element('fullscreen').disabled,false);
  await viewer.element('fullscreen').onclick();
  viewer.handlers.get('document:fullscreenchange')();
  assert.equal(viewer.element('fullscreen')['aria-pressed'],'true');
  await viewer.element('fullscreen').onclick();
  viewer.handlers.get('document:fullscreenchange')();
  assert.equal(viewer.element('fullscreen').textContent,'Enter fullscreen');
  await viewer.element('fullscreen').onclick();
  viewer.document.fullscreenElement=null; // Escape is controlled by the browser.
  viewer.handlers.get('document:fullscreenchange')();
  assert.equal(viewer.element('fullscreen')['aria-pressed'],'false');
});
