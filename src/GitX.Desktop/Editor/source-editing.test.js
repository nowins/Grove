import test from 'node:test';
import assert from 'node:assert/strict';
import { SourceEditing, renderedModelCount } from './source-editing.js';
let next=0;
class Model {
  constructor(text){this.value=text;this.id=String(++next);this.version=1;this.listeners=new Set();this.disposed=false;}
  getValue(){return this.value;}getValueLength(){return this.value.length;}getAlternativeVersionId(){return this.version;}
  setValue(text){this.value=text;this.version++;for(const listener of this.listeners)listener();}
  pushStackElement(){}
  getFullModelRange(){return {};}
  pushEditOperations(_,edits){this.setValue(edits[0].text);}
  undoTo(text,version){this.value=text;this.version=version;for(const listener of this.listeners)listener();}
  onDidChangeContent(listener){this.listeners.add(listener);return {dispose:()=>this.listeners.delete(listener)};}
  isDisposed(){return this.disposed;}
  dispose(){this.disposed=true;}
}
const info=(token='disk1',root='root')=>({fingerprint:token,rootKey:root,bom:false,newline:'LF',canEdit:true});
const doc=(text='one',token='disk1',root='root')=>({path:'a.txt',text,language:'plaintext',editing:info(token,root),commentContext:{snapshot:'old'}});
const tabs=(active='a',ids=['a','b'])=>({sourceMode:true,activeSourceTabId:active,sourceTabs:ids.map(id=>({id,path:id+'.txt',localFile:true}))});
const create=(options={})=>new SourceEditing({createModel:t=>new Model(t),getVersion:()=>1,...options});
test('dirty model and undo survive tab switches, refresh and deleted-file notices',()=>{
 const e=create();const a=e.prepare(doc(),tabs()).model;a.setValue('draft');e.prepare(doc('two'),tabs('b'));
 const back=e.prepare(doc('external','disk2'),tabs());assert.equal(back.model,a);assert.equal(back.doc.text,'draft');assert.equal(back.doc.commentContext,null);assert.equal(e.metadata().find(s=>s.id==='a').conflict,true);
 const notice=e.prepare({path:'a.txt',text:'missing',isNotice:true},tabs());assert.equal(notice.model,a);assert.equal(notice.doc.isNotice,false);assert.equal(a.getValue(),'draft');
 a.undoTo('one',1);assert.equal(e.dirty(e.active),false);e.dispose();assert.equal(a.disposed,true);
});
test('save acknowledges the captured checkpoint, never later input',()=>{
 const e=create();const model=e.prepare(doc(),tabs()).model;model.setValue('saved');const draft=e.draft('a');e.saving(draft);model.setValue('typed during save');
 e.saved({...draft,info:info('disk2')});assert.equal(e.active.info.fingerprint,'disk2');assert.equal(e.dirty(e.active),true);assert.equal(model.getValue(),'typed during save');
 model.undoTo('saved',draft.checkpoint);assert.equal(e.dirty(e.active),false);e.dispose();
});
test('failed and late saves retain draft; model identity prevents updating replacement',()=>{
 const e=create();const model=e.prepare(doc(),tabs()).model;model.setValue('mine');const draft=e.draft('a');e.saving(draft);e.saved({...draft,error:'disk changed'});
 assert.equal(e.active.error,'disk changed');assert.equal(e.dirty(e.active),true);assert.equal(model.getValue(),'mine');e.saved({...draft,model:'other',info:info('bad')});assert.equal(e.active.info.fingerprint,'disk1');e.dispose();
});
test('clean refresh follows disk, root identity and historical/diff never reuse writable model',()=>{
 const e=create();const a=e.prepare(doc(),tabs()).model;e.prepare(doc('disk changed','disk2'),tabs());assert.equal(a.getValue(),'disk changed');assert.equal(e.dirty(e.active),false);
 assert.equal(e.prepare({...doc(),sourceRevision:'sha'},tabs()).model,null);assert.equal(e.editable(),false);
 assert.equal(e.prepare({...doc(),diff:{}},tabs()).model,null);const other=e.prepare(doc('other','disk1','other-root'),tabs()).model;assert.notEqual(a,other);e.disposeRetired([other]);assert.equal(a.disposed,true);e.dispose();
});
test('closing prunes models only after detaching, and leases can overlap safely',()=>{
 const e=create();const a=e.prepare(doc(),tabs()).model;e.lock(['a'],true);e.lock(['a'],true);e.lock(['a'],false);assert.equal(e.editable(),false);e.lock(['a'],false);assert.equal(e.editable(),true);
 e.sync(tabs('b',['b']));assert.equal(e.editable(),false);assert.equal(e.owns(a),true);e.disposeRetired([a]);assert.equal(a.disposed,false);e.disposeRetired([]);assert.equal(a.disposed,true);e.dispose();
});
test('uniform CRLF is modeled as LF while save metadata keeps original convention',()=>{
 const e=create();const prepared=e.prepare({...doc('a\r\nb\r\n'),editing:{...info(),bom:true,newline:'CRLF'}},tabs());assert.equal(prepared.model.getValue(),'a\nb\n');assert.equal(e.draft('a').text,'a\nb\n');assert.equal(e.active.info.newline,'CRLF');e.dispose();
});
test('new unsupported or conflict file remains readonly and no draft can be pulled',()=>{
 const e=create();assert.equal(e.prepare({...doc(),editing:{...info(),canEdit:false,reason:'mixed'}},tabs()).model,null);assert.equal(e.draft('a'),null);assert.equal(e.editable(),false);e.dispose();
});

test('new conflict or permission restriction freezes an existing dirty buffer without replacing it',()=>{
 const e=create();const a=e.prepare(doc(),tabs()).model;a.setValue('draft');
 e.prepare({...doc('disk','disk2'),editing:{...info('disk2'),canEdit:false,reason:'conflict'}},tabs());
 assert.equal(a.getValue(),'draft');assert.equal(e.editable(),false);assert.equal(e.active.readOnlyReason,'conflict');assert.equal(e.dirty(e.active),true);e.dispose();
});

test('loaded counts only attached current-document models, never retained drafts or loading notices',()=>{
 const cached=new Model('draft'), current=new Model('source'), old=new Model('diff old');
 const panes=[{getModel:()=>old},{getModel:()=>current}];
 assert.equal(renderedModelCount({isNotice:true},[old,current],panes),0);
 assert.equal(renderedModelCount({},[null,current],panes),1);
 assert.equal(renderedModelCount({isDiff:true},[old,current],panes),2);
 assert.equal(renderedModelCount({},[null,cached],panes),0);
 current.isDisposed=()=>true;old.isDisposed=()=>false;cached.isDisposed=()=>false;
 assert.equal(renderedModelCount({},[null,current],panes),0);
});


test('explicit recovery retains original baseline and never marks changed disk clean',()=>{
 const e=create();const model=e.prepare(doc('external','disk2'),tabs()).model;
 assert.equal(e.restore('a',{path:'a.txt',baseline:info('disk1'),text:'recovered'}),true);
 assert.equal(model.getValue(),'recovered');assert.equal(e.draft('a').expected,'disk1');
 assert.equal(e.metadata()[0].conflict,true);assert.equal(e.dirty(e.active),true);
 e.prepare(doc('external newer','disk3'),tabs());assert.equal(model.getValue(),'recovered');
 assert.equal(e.restore('a',{path:'a.txt',baseline:info(),text:'late'}),false);e.dispose();
});
test('recovery rejects wrong path root inactive dirty locked or readonly model',()=>{
 const record={path:'a.txt',baseline:info(),text:'recovered'};
 const e=create();const model=e.prepare(doc(),tabs()).model;
 assert.equal(e.restore('a',{...record,path:'b.txt'}),false);
 assert.equal(e.restore('a',{...record,baseline:info('disk1','other')}),false);
 e.lock(['a'],true);assert.equal(e.restore('a',record),false);e.lock(['a'],false);
 e.prepare(doc('b'),tabs('b'));assert.equal(e.restore('a',record),false);
 e.prepare(doc(),tabs());model.setValue('current');assert.equal(e.restore('a',record),false);
 assert.equal(model.getValue(),'current');e.dispose();
});
test('recovered draft retains normal save checkpoint semantics',()=>{
 const e=create();e.prepare(doc(),tabs());e.restore('a',{path:'a.txt',baseline:info(),text:'recovered'});
 const captured=e.draft('a');assert.deepEqual(captured.baseline,info());e.saving(captured);
 e.saved({...captured,info:info('saved')});assert.equal(e.dirty(e.active),false);e.dispose();
});

test('native recovery acknowledgement survives JSON bridge and read-only navigation',async()=>{
 const {installNativeBridge}=await import('./native-bridge.js');const target={};installNativeBridge(target,()=>()=>{});
 const e=create();e.prepare(doc(),tabs());
 const ack=JSON.parse(target.groveNative.json(()=>({restored:e.restore('a',{path:'a.txt',baseline:info(),text:'private recovered'})})));
 assert.deepEqual(ack,{restored:true});
 e.prepare({path:'a.txt',diff:{old:{},new:{}}},{...tabs(),sourceMode:false});
 assert.equal(e.draft('a').text,'private recovered');assert.equal(e.draft('a').dirty,true);
 const failed=JSON.parse(target.groveNative.json(()=>({restored:e.restore('a',{path:'a.txt',baseline:info(),text:'replace'})})));
 assert.deepEqual(failed,{restored:false});
 e.prepare(doc('external','disk2'),tabs());assert.equal(e.draft('a').text,'private recovered');assert.equal(e.draft('a').expected,'disk1');e.dispose();
});
test('disk-write lease freezes existing and newly opened tabs until reconciliation',()=>{
 const e=create();e.prepare(doc(),tabs());e.lockDiskWrites(true);assert.equal(e.editable(),false);
 e.prepare(doc('second'),tabs('b'));assert.equal(e.editable(),false);
 assert.equal(e.restore('b',{path:'b.txt',baseline:info(),text:'cannot race'}),false);
 e.lockDiskWrites(true);e.lockDiskWrites(false);assert.equal(e.editable(),false);
 e.lockDiskWrites(false);assert.equal(e.editable(),true);
 e.prepare(doc(),tabs());assert.equal(e.editable(),true);e.dispose();
});

test('untouched tabs keep lightweight sessions while resident models obey LRU count and byte budgets',()=>{
 const e=create({pristineModels:2,pristineBytes:12}),ids=['a','b','c','d'];
 const open=(id,text='one')=>{const model=e.prepare(doc(text),tabs(id,ids)).model;e.disposeRetired([model]);return model;};
 const a=open('a'),b=open('b');assert.equal(open('a'),a);const c=open('c');
 assert.equal(b.disposed,true);assert.equal(a.disposed,false);assert.equal(e.sessions.size,3);assert.equal(e.draft('b'),null);
 assert.equal(e.metadata().find(s=>s.id==='b').dirty,false);assert.equal(e.metadata().find(s=>s.id==='b').editable,true);
 const d=open('d','12345');assert.equal(a.disposed,true);assert.equal(c.disposed,true);assert.equal(d.disposed,false);
 const reopened=open('b','new');assert.notEqual(reopened,b);assert.equal(reopened.getValue(),'new');assert.equal(d.disposed,true);e.dispose();
});

test('editing history remains resident after save or undo to clean, and recovered equal text stays protected',()=>{
 const e=create({pristineModels:0,pristineBytes:0}),ids=['a','b','c','d'];
 const open=id=>{const model=e.prepare(doc(),tabs(id,ids)).model;e.disposeRetired([model]);return model;};
 const a=open('a');a.setValue('draft');a.undoTo('one',1);assert.equal(e.dirty(e.active),false);
 const b=open('b');b.setValue('saved');const captured=e.draft('b');e.saving(captured);e.saved({...captured,info:info('saved')});
 const c=open('c');assert.equal(e.restore('c',{path:'c.txt',baseline:info(),text:'one'}),true);
 open('d');assert.equal(a.disposed,false);assert.equal(b.disposed,false);assert.equal(c.disposed,false);
 assert.equal(e.draft('a').dirty,false);assert.equal(e.draft('b').dirty,false);assert.equal(e.draft('c').dirty,true);
 e.sync(tabs('d',['d']));e.disposeRetired([e.active?.model]);assert.equal(a.disposed,true);assert.equal(b.disposed,true);assert.equal(c.disposed,true);e.dispose();
});

test('attached models and active save or write leases cannot be evicted under pressure',()=>{
 const e=create({pristineModels:0,pristineBytes:0});const a=e.prepare(doc(),tabs()).model;
 const captured=e.draft('a');e.saving(captured);const b=e.prepare(doc('two'),tabs('b')).model;
 e.disposeRetired([a,b]);assert.equal(a.disposed,false);e.disposeRetired([b]);assert.equal(a.disposed,false);
 e.saved({...captured,info:info()});e.lock(['a'],true);e.disposeRetired([b]);assert.equal(a.disposed,false);
 e.lock(['a'],false);e.lockDiskWrites(true);e.disposeRetired([b]);assert.equal(a.disposed,false);
 e.lockDiskWrites(false);e.disposeRetired([b]);assert.equal(a.disposed,true);assert.equal(b.disposed,false);e.dispose();
});

test('disk refresh stays reconstructible, while stale replies cannot mutate a rehydrated model',()=>{
 const e=create({pristineModels:1}),ids=['a','b'];
 const a=e.prepare(doc(),tabs()).model;const captured=e.draft('a');
 e.prepare(doc('disk changed','disk2'),tabs());assert.equal(e.sessions.get('a').history,false);
 const b=e.prepare(doc('two'),tabs('b')).model;e.disposeRetired([b]);assert.equal(a.disposed,true);
 const fresh=e.prepare(doc('latest','disk3'),tabs()).model;e.disposeRetired([fresh]);
 assert.equal(fresh.getValue(),'latest');assert.equal(e.draft('a').expected,'disk3');assert.equal(e.saving(captured),false);
 e.saved({...captured,info:info('stale')});assert.equal(e.draft('a').expected,'disk3');
 e.prepare({...doc(),editing:{...info(),canEdit:false}},tabs('b'));assert.equal(e.sessions.has('b'),false);
 e.sync(tabs(null,[]));e.disposeRetired([]);assert.equal(e.sessions.size,0);e.dispose();
});
