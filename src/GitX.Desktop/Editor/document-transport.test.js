import test from 'node:test';
import assert from 'node:assert/strict';
import { DocumentTransport } from './document-transport.js';
import { SourceEditing } from './source-editing.js';

const frame = (version, text = 'disk\r\n', id = 'body', reuse = false) => ({ version, contentId: id, reuseContent: reuse,
  document: { path: 'a.txt', language: 'plaintext', text, editing: { rootKey: 'repo', fingerprint: 'disk1', canEdit: true }, commentContext: { snapshot: 'proof' } },
  toolbar: { sourceMode: true, activeSourceTabId: 'a', sourceTabs: [{id: 'a', path: 'a.txt', localFile: true}] } });
const response = payload => ({ ok: true, json: async () => payload });

test('metadata uses the exact disk body including CRLF, Unicode and empty source', async () => {
  for (const text of ['', '中文\r\n🙂\n']) {
    const urls = [], replies = [frame(1, text), {...frame(2, '', 'body', true), document: {...frame(2).document,text:'',preserveView:true,revealLine:9,blame:[{line:1}]}}];
    const transport = new DocumentTransport(async url => { urls.push(url); return response(replies.shift()); });
    assert.equal((await transport.read(1)).document.text, text);
    const next = await transport.read(2); assert.equal(next.document.text, text);
    assert.equal(next.document.revealLine,9); assert.equal(next.document.blame.length,1);
    assert.equal(urls[0],'document?v=1'); assert.equal(urls[1],'document?v=2&content=body');
  }
});

test('missing advertised body refetches once and never interprets metadata as empty source', async () => {
  const urls=[]; let transport;
  transport=new DocumentTransport(async url=>{urls.push(url);if(url.includes('content=')){transport.clear();return response(frame(2,'','body',true));}return response(frame(url.includes('v=1')?1:2,'完整正文'));});
  await transport.read(1); assert.equal((await transport.read(2)).document.text,'完整正文');
  assert.deepEqual(urls,['document?v=1','document?v=2&content=body','document?v=2']);
  transport.fetcher=async()=>response(frame(3,'','unavailable',true));
  await assert.rejects(()=>transport.read(3),/正文不可用/);
});

test('obsolete and aborted responses cannot replace the accepted body, including the retry', async () => {
  let complete, current=true; const transport=new DocumentTransport(async()=>response(frame(1,'current')));await transport.read(1);
  transport.fetcher=()=>new Promise(resolve=>{complete=resolve;});
  const pending=transport.read(2,{isCurrent:()=>current});current=false;complete(response(frame(2,'stale','other')));
  assert.equal(await pending,null);assert.equal(transport.content.text,'current');
  const cancellation=new AbortController();transport.fetcher=async()=>{cancellation.abort();return response(frame(2,'aborted','other'));};
  assert.equal(await transport.read(2,{signal:cancellation.signal}),null);assert.equal(transport.content.text,'current');
  let calls=0;transport.fetcher=async()=>{calls++;if(calls===1)return response(frame(3,'','missing',true));current=false;return response(frame(3,'late','new'));};
  current=true;assert.equal(await transport.read(3,{isCurrent:()=>current}),null);assert.equal(transport.content.text,'current');
});

test('notice, Diff, legacy frames and a new body release the previous body', async () => {
  const transport=new DocumentTransport();
  for(const document of [{isNotice:true},{isDiff:true,diff:{}},{}]){
    transport.fetcher=async()=>response(frame(1,'old'));await transport.read(1);
    const legacy={...frame(2,'new',null),document:{...frame(2,'new').document,...document}};
    transport.fetcher=async()=>response(legacy);assert.equal((await transport.read(2)).document.text,'new');assert.equal(transport.content,null);
  }
  transport.fetcher=async()=>response(frame(3,'replacement','different'));await transport.read(3);
  assert.deepEqual(transport.content,{id:'different',text:'replacement'});transport.clear();assert.equal(transport.content,null);
});

test('HTTP errors, wrong versions and malformed reuse cannot poison the current body', async () => {
  const transport=new DocumentTransport(async()=>response(frame(1)));await transport.read(1);
  transport.fetcher=async()=>({ok:false});await assert.rejects(()=>transport.read(2),/无法读取/);
  transport.fetcher=async()=>response(frame(3,'other','new'));assert.equal(await transport.read(2),null);
  transport.fetcher=async()=>response({...frame(2,'','body',true),document:{text:'',isDiff:true}});
  await assert.rejects(()=>transport.read(2),/格式无效/);assert.equal(transport.content.text,'disk\r\n');
});

test('metadata transport never replaces a dirty model, undo checkpoint or save baseline', async () => {
  const replies=[frame(1),frame(2,'','body',true)];
  const transport=new DocumentTransport(async()=>response(replies.shift()));let model;
  const editing=new SourceEditing({createModel:text=>(model={text,version:1,id:'model',getValue(){return this.text;},getAlternativeVersionId(){return this.version;},
    onDidChangeContent(){return{dispose(){}};},setValue(){throw new Error('must not overwrite draft');},dispose(){}}),getVersion:()=>2});
  const initial=await transport.read(1);editing.prepare(initial.document,initial.toolbar);model.text='unsaved draft';model.version=2;
  const metadata=await transport.read(2), update=editing.prepare(metadata.document,metadata.toolbar);
  assert.equal(update.model,model);assert.equal(update.doc.text,'unsaved draft');assert.equal(update.doc.commentContext,null);
  assert.equal(editing.draft('a').expected,'disk1');assert.equal(editing.sessions.get('a').saved,1);
  assert.equal(transport.content.text,'disk\r\n');editing.dispose();
});
