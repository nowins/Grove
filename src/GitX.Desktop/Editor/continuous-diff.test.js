import test from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import { ContinuousDiff, rowWindow, fileBytes } from './continuous-diff.js';

function manifest(id = 'scope', count = 12, offset = 0) {
  return { id, version: 7, title: 'HEAD → 当前版本', whitespace: '不忽略空白', offset, totalFiles: 450,
    files: Array.from({ length: count }, (_, index) => ({ index, path: index ? `dir/file${index}.cs` : '<img src=x onerror=bad>.cs', status: '修改' })) };
}
function file(owner, index, count = 80) {
  return { id: owner.id, index, old: {path:'old.cs',label:'base',format:'CRLF',exists:true,endsWithNewline:true},
    new: {path:'new.cs',label:'current',format:'LF',exists:true,endsWithNewline:false}, notice:'',truncated:false,
    rows: Array.from({ length: count }, (_, n) => ({ oldLine:n+1,newLine:n+1,oldText:'<script>old</script>',newText:`中文🙂 ${n}`,kind:n===5?'modified':'context' })) };
}
function fixture(fetcher) {
  const dom = new JSDOM('<!doctype html><body><section id="view" hidden></section></body>');
  globalThis.window = dom.window; globalThis.document = dom.window.document;
  globalThis.ResizeObserver = class { observe() {} disconnect() {} };
  let id = 0; const frames = new Map(), calls = [], messages = [];
  globalThis.requestAnimationFrame = action => { frames.set(++id, action); return id; };
  globalThis.cancelAnimationFrame = frame => frames.delete(frame);
  const host = document.getElementById('view'); host.getBoundingClientRect = () => ({top:0,bottom:300,height:300});
  const control = new ContinuousDiff({ host, send:message=>messages.push(message), fetchFile:async(owner,index,signal)=>{
    calls.push({owner,index,signal}); return fetcher ? fetcher(owner,index,signal) : {ok:true,json:async()=>file(owner,index)};
  } });
  const top = index => control.items.slice(0,index).reduce((sum,item)=>sum+Math.max(5,item.count)*22+88,0);
  function show(owner = manifest()) {
    control.show(owner);
    for (const item of control.items) {
      item.section.getBoundingClientRect = () => ({top:top(item.entry.index)-host.scrollTop,bottom:top(item.entry.index)-host.scrollTop+Math.max(5,item.count)*22+88});
      item.rows.getBoundingClientRect = () => ({top:top(item.entry.index)-host.scrollTop+58});
      item.section.scrollIntoView = () => { host.scrollTop=top(item.entry.index); control.scroll(); };
    }
  }
  async function flush(turns=15) {
    for(let n=0;n<turns;n++) { const batch=[...frames.values()]; frames.clear();batch.forEach(action=>action());await new Promise(resolve=>setImmediate(resolve)); }
  }
  return {dom,host,control,calls,messages,show,flush,top,dispose(){control.dispose();dom.window.close();for(const name of ['window','document','ResizeObserver','requestAnimationFrame','cancelAnimationFrame'])delete globalThis[name];}};
}

test('large-file windows remain bounded and clamp both ends',()=>{
  assert.deepEqual(rowWindow(6000,0,660),{start:0,end:46});
  const bottom=rowWindow(6000,6000*22,660);assert.equal(bottom.end,6000);assert.ok(bottom.start>=5990);
  assert.deepEqual(rowWindow(0,-100,660),{start:0,end:0});
});
test('first paint reads only near the viewport with at most two requests',async()=>{
  const waiting=[];const t=fixture((owner,index,signal)=>new Promise(resolve=>waiting.push({owner,index,signal,resolve})));
  try {
    t.show(manifest('scope',200));await t.flush(2);assert.equal(t.calls.length,2);assert.equal(t.control.active,2);
    assert.equal(t.host.querySelectorAll('.cd-file').length,200);assert.equal(t.host.querySelectorAll('.cd-row').length,0);
    waiting[0].resolve({ok:true,json:async()=>file(waiting[0].owner,0,6000)});await t.flush();
    assert.ok(t.host.querySelectorAll('.cd-row').length<=46);assert.ok(t.calls.length<=3);
    assert.equal(t.host.querySelectorAll('script,img').length,0);assert.match(t.host.textContent,/<script>old<\/script>/);
  }finally{t.dispose();}
});
test('scrolling evicts old data and DOM rows while retaining file height and the visible anchor',async()=>{
  const t=fixture();try {
    t.show();await t.flush();
    for(let index=1;index<12;index++){t.host.scrollTop=t.top(index);t.control.scroll();await t.flush();}
    assert.ok(t.control.items.filter(item=>item.data).length<=8);
    assert.ok(t.host.querySelectorAll('.cd-row').length<100);assert.equal(t.control.items[0].data,null);
    assert.equal(t.control.items[0].count,80);assert.equal(t.control.items[0].rows.style.height,'1760px');
  }finally{t.dispose();}
});
test('refresh replaces old code while retaining the visible file and its original-side line',async()=>{
  const t=fixture();try {
    t.show({...manifest('before',3),scope:'kept'});await t.flush();
    t.host.scrollTop=t.top(1)+440;t.control.scroll();await t.flush();
    const before=t.control.captureAnchor();const retired=t.control.items[1].header.querySelector('button');
    t.control.fetchFile=async(owner,index)=>({ok:true,json:async()=>{
      const value=file(owner,index);
      value.rows=[...Array.from({length:10},(_,n)=>({oldLine:null,newLine:n+1,oldText:'',newText:'fresh insertion',kind:'added'})),...value.rows];
      return value;
    }});
    t.show({...manifest('after',3),version:8,scope:'kept'});await t.flush();
    const after=t.control.captureAnchor();
    assert.equal(after.path,before.path);assert.equal(after.line,before.line);assert.equal(after.side,before.side);
    assert.equal(after.rowOffset,before.rowOffset);assert.equal(t.control.items[1].data.id,'after');
    retired.click();assert.equal(t.messages.length,0);
  }finally{t.dispose();}
});
test('user scrolling during a refresh cancels deferred row restoration',async()=>{
  let resolve;const t=fixture();try {
    t.show({...manifest('before',3),scope:'kept'});await t.flush();
    t.host.scrollTop=t.top(1)+440;t.control.scroll();await t.flush();
    t.control.fetchFile=async(owner,index)=>index===1?new Promise(done=>{resolve=()=>done({ok:true,json:async()=>file(owner,index)});}):{ok:true,json:async()=>file(owner,index)};
    t.show({...manifest('after',3),version:8,scope:'kept'});await t.flush(2);assert.ok(resolve);
    const position=t.top(2)+440;t.host.scrollTop=position;t.control.scroll();await t.flush();
    resolve();await t.flush();assert.equal(t.host.scrollTop,position);assert.equal(t.control.restoreAnchor,null);
  }finally{t.dispose();}
});
test('a removed visible file falls forward to a surviving file and an empty range stays visible',async()=>{
  const t=fixture();try {
    const before={...manifest('before',3),scope:'kept'};t.show(before);await t.flush();
    t.host.scrollTop=t.top(1)+220;t.control.scroll();await t.flush();
    const after={...before,id:'after',version:8,files:before.files.filter(entry=>entry.index!==1).map((entry,index)=>({...entry,index}))};
    t.show(after);await t.flush();assert.equal(t.control.captureAnchor().path,'dir/file2.cs');
    t.show({...after,id:'empty',version:9,files:[],totalFiles:0});await t.flush();
    assert.equal(t.host.hidden,false);assert.equal(t.host.querySelector('.cd-range').textContent,'0 个文件');
    assert.match(t.host.textContent,/当前比较范围没有文件差异/);assert.equal(t.host.querySelectorAll('.cd-file').length,0);
  }finally{t.dispose();}
});
test('late responses after replacement or close are ignored and all old requests abort',async()=>{
  const pending=[];const t=fixture((owner,index,signal)=>new Promise(resolve=>pending.push({owner,index,signal,resolve})));
  try {
    t.show(manifest('old'));await t.flush(2);const old=[...pending];t.show(manifest('new'));await t.flush(2);
    assert.ok(old.every(request=>request.signal.aborted));
    for(const request of old)request.resolve({ok:true,json:async()=>file(request.owner,request.index)});await t.flush();
    assert.equal(t.control.manifest.id,'new');assert.ok(t.control.items.every(item=>!item.data));
    t.control.close();assert.ok(pending.every(request=>request.signal.aborted));assert.equal(t.host.childElementCount,0);assert.equal(t.host.hidden,true);
  }finally{t.dispose();}
});
test('HTTP errors and wrong-scope bodies expose retry without caching poisoned results',async()=>{
  let attempts=0;const t=fixture((owner,index)=>({ok:++attempts>1,status:404,json:async()=>file(owner,index)}));
  try {
    t.show(manifest('scope',1));await t.flush();assert.match(t.host.textContent,/比较范围已失效/);assert.equal(t.control.items[0].data,null);
    t.host.querySelector('.cd-footer button').click();await t.flush();assert.ok(t.control.items[0].data);
    t.control.fetchFile=async(owner,index)=>({ok:true,json:async()=>file({...owner,id:'wrong'},index)});
    t.host.querySelector('.cd-footer button').click();await t.flush();assert.match(t.host.textContent,/文件响应与当前比较范围不符/);assert.equal(t.control.items[0].data,null);
  }finally{t.dispose();}
});
test('manual reread clears old code and side labels even if the replacement fails',async()=>{
  const t=fixture();try {
    t.show(manifest('scope',1));await t.flush();const item=t.control.items[0];
    assert.ok(item.rows.childElementCount);assert.ok(item.sides.childElementCount);const height=item.rows.style.height;
    t.control.fetchFile=async()=>{throw new Error('network failure');};
    t.host.querySelector('.cd-footer button').click();
    assert.equal(item.rows.childElementCount,0);assert.equal(item.sides.childElementCount,0);
    assert.equal(item.rows.style.height,height);assert.equal(item.data,null);assert.equal(item.bytes,0);
    await t.flush();assert.match(item.message.textContent,/network failure/);assert.equal(item.rows.childElementCount,0);
    t.control.fetchFile=async(owner,index)=>({ok:true,json:async()=>({...file(owner,index),rows:[{oldLine:1,newLine:1,oldText:'fresh old',newText:'fresh new',kind:'modified'}]})});
    t.host.querySelector('.cd-footer button').click();await t.flush();assert.match(item.rows.textContent,/fresh new/);
  }finally{t.dispose();}
});
test('manual reread aborts an in-flight request and refuses its late body',async()=>{
  const pending=[];const t=fixture((owner,index,signal)=>new Promise(resolve=>pending.push({owner,index,signal,resolve})));
  try {
    t.show(manifest('scope',1));await t.flush(2);assert.equal(pending.length,1);
    t.host.querySelector('.cd-footer button').click();assert.equal(pending[0].signal.aborted,true);await t.flush(2);
    assert.equal(pending.length,2);assert.equal(t.control.active,2);
    pending[1].resolve({ok:true,json:async()=>({...file(pending[1].owner,0),notice:'fresh capture'})});await t.flush();
    const accepted=t.control.items[0].data;assert.equal(accepted.notice,'fresh capture');
    pending[0].resolve({ok:true,json:async()=>({...file(pending[0].owner,0),notice:'obsolete capture'})});await t.flush();
    assert.equal(t.control.items[0].data,accepted);assert.equal(t.control.active,0);
  }finally{t.dispose();}
});
test('temporary server saturation retries without caching the busy response and close clears retry timers',async context=>{
  context.mock.timers.enable({apis:['setTimeout']});
  let now=0;context.mock.method(performance,'now',()=>now);
  let attempts=0;const t=fixture((owner,index)=>++attempts===1?{ok:false,status:429}:{ok:true,json:async()=>file(owner,index)});
  try {
    t.control.retryDelay=5;t.show(manifest('scope',1));await t.flush(2);
    assert.equal(t.control.items[0].data,null);assert.equal(t.control.items[0].error,'');assert.match(t.host.textContent,/自动重试/);
    now+=5;context.mock.timers.tick(5);await t.flush();assert.ok(t.control.items[0].data);assert.equal(attempts,2);
    t.control.retryDelay=1000;t.control.fetchFile=async()=>({ok:false,status:429});t.host.querySelector('.cd-footer button').click();await t.flush(2);
    assert.equal(t.control.timers.size,1);t.host.querySelector('.cd-footer button').click();assert.equal(t.control.timers.size,0);
    await t.flush(2);assert.equal(t.control.timers.size,1);t.control.close();assert.equal(t.control.timers.size,0);
    const reads=t.calls.length;now+=1000;context.mock.timers.tick(1000);await t.flush();assert.equal(t.calls.length,reads);
  }finally{t.dispose();}
});
test('complete-Diff and old-side row links use exact captured identity and stale controls do nothing',async()=>{
  const t=fixture();try {
    t.show(manifest('scope',1));await t.flush();
    const open=t.host.querySelector('.cd-file-header button'),page=t.host.querySelector('.cd-toolbar button');
    open.click();t.host.querySelectorAll('.cd-number')[10].dispatchEvent(new t.dom.window.MouseEvent('dblclick'));
    assert.deepEqual(t.messages,[{type:'continuous-diff',action:'file',id:'scope',index:0,side:1,line:1,version:7},{type:'continuous-diff',action:'file',id:'scope',index:0,side:0,line:6,version:7}]);
    t.show(manifest('next',1));open.click();page.click();assert.equal(t.messages.length,2);
  }finally{t.dispose();}
});
test('file pages and explicit truncation/newline notices remain visible',async()=>{
  const t=fixture((owner,index)=>({ok:true,json:async()=>({...file(owner,index),truncated:true})}));try {
    t.show(manifest('scope',1,200));await t.flush();
    assert.match(t.host.textContent,/201–201 \/ 450/);assert.match(t.host.textContent,/6,000/);assert.match(t.host.textContent,/末尾无换行/);
    const pages=t.host.querySelectorAll('.cd-toolbar button');pages[0].click();pages[1].click();
    assert.deepEqual(t.messages.map(message=>message.offset),[0,400]);
    assert.ok(fileBytes(file(manifest(),0))>80*64);
  }finally{t.dispose();}
});
