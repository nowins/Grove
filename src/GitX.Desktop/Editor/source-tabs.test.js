import test from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import { SourceTabs } from './source-tabs.js';

function fixture(isMac = false, count = 3) {
  const dom = new JSDOM('<!doctype html><body><button id="outside">outside</button><div id="source-tabs"></div><div id="file-path"></div><div id="workspace"></div></body>');
  globalThis.window = dom.window; globalThis.document = dom.window.document;
  globalThis.ResizeObserver = class { observe() {} disconnect() {} };
  const state = { version: 7, ready: true }, messages = [];
  const tabs = Array.from({length:count}, (_,i) => ({id:`tab-${i}`,path:i === 1 ? '目录/同名.ts' : `folder-${i}/同名.ts`,revision:i === 2 ? 'abcdef0123456789' : null}));
  const control = new SourceTabs({ getVersion:()=>state.version, isReady:()=>state.ready, send:m=>messages.push(m), isMac });
  const update = (values=tabs, active=values.at(-1)?.id) => control.update({sourceTabs:values,activeSourceTabId:active});
  update();
  Object.defineProperty(document.documentElement,'clientWidth',{value:800,configurable:true});
  Object.defineProperty(document.documentElement,'clientHeight',{value:600,configurable:true});
  control.menu.menu.getBoundingClientRect=()=>({width:252,height:92});
  const item = i => control.host.querySelectorAll('.source-tab')[i];
  const button = i => item(i).querySelector('[role=tab]');
  const menuButton = action => control.menu.menu.querySelector(`[data-action="${action}"]`);
  const key = (target,key,extra={}) => target.dispatchEvent(new dom.window.KeyboardEvent('keydown',{key,bubbles:true,cancelable:true,...extra}));
  const context = (i,extra={}) => button(i).dispatchEvent(new dom.window.MouseEvent('contextmenu',{button:2,clientX:180,clientY:22,bubbles:true,cancelable:true,...extra}));
  return {dom,state,messages,tabs,control,update,item,button,menuButton,key,context,dispose(){control.dispose();dom.window.close();delete globalThis.window;delete globalThis.document;delete globalThis.ResizeObserver;}};
}

for (const mac of [false,true]) test(`${mac?'Mac':'Windows'} right-click targets the clicked background tab and cancel leaves the active file unchanged`,()=>{
  const t=fixture(mac);
  try {
    const selected=t.control.activeId;
    assert.equal(t.context(0),false); assert.equal(t.control.menu.opened,true);
    assert.deepEqual(t.messages,[]); assert.equal(t.control.activeId,selected);
    assert.equal(t.button(0).getAttribute('aria-selected'),'false');
    assert.match(t.control.menu.menu.getAttribute('aria-label'),/folder-0/);
    t.key(t.menuButton('close'),'Escape');
    assert.equal(t.control.menu.opened,false); assert.equal(document.activeElement,t.button(0));
    assert.equal(t.control.activeId,selected); assert.deepEqual(t.messages,[]);
    t.context(0); t.menuButton('close').click();
    assert.deepEqual(t.messages,[{type:'source-tab',action:'close',id:'tab-0',version:7}]);
  } finally {t.dispose();}
});

test('Mac Control-click never selects or closes a tab, including its close icon; Windows Control-click keeps primary selection',()=>{
  const t=fixture(true);
  try {
    for(const target of [t.button(0),t.item(0).querySelector('.close-source-tab')]) {
      target.dispatchEvent(new t.dom.window.MouseEvent('click',{button:0,ctrlKey:true,bubbles:true}));
      target.dispatchEvent(new t.dom.window.MouseEvent('contextmenu',{button:0,ctrlKey:true,clientX:170,clientY:20,bubbles:true,cancelable:true}));
      assert.deepEqual(t.messages,[]); assert.equal(t.control.menu.opened,true);
      t.key(t.menuButton('close'),'Escape');
    }
    t.control.isMac=false;
    t.button(0).dispatchEvent(new t.dom.window.MouseEvent('click',{button:0,ctrlKey:true,bubbles:true}));
    assert.deepEqual(t.messages,[{type:'source-tab',action:'select',id:'tab-0',version:7}]);
  } finally {t.dispose();}
});

test('keyboard menu navigation skips disabled Close Others and Enter sends one atomic Close All',()=>{
  const t=fixture(false,1);
  try {
    t.button(0).focus(); t.key(t.button(0),'F10',{shiftKey:true});
    assert.equal(t.control.menu.opened,true); assert.equal(t.menuButton('close-others').disabled,true);
    t.menuButton('close-others').click(); assert.deepEqual(t.messages,[]);
    t.key(t.menuButton('close'),'ArrowDown'); assert.equal(document.activeElement,t.menuButton('close-all'));
    t.key(t.menuButton('close-all'),'ArrowUp'); assert.equal(document.activeElement,t.menuButton('close'));
    t.key(t.menuButton('close'),'End'); assert.equal(document.activeElement,t.menuButton('terminal')); t.key(t.menuButton('terminal'),'Home'); t.key(t.menuButton('close'),'ArrowDown'); t.key(t.menuButton('close-all'),'Enter');
    assert.deepEqual(t.messages,[{type:'source-tab',action:'close-all',id:'tab-0',version:7}]); assert.equal(t.control.menu.opened,false);
  } finally {t.dispose();}
});

test('ContextMenu key, Home and Space operate on the focused tab without activating it first',()=>{
  const t=fixture();
  try {
    t.button(1).focus();t.key(t.button(1),'ContextMenu');
    assert.equal(t.control.menu.opened,true); assert.equal(t.control.activeId,'tab-2');
    t.key(t.menuButton('close'),'ArrowDown'); t.key(t.menuButton('close-others'),' ');
    assert.deepEqual(t.messages,[{type:'source-tab',action:'close-others',id:'tab-1',version:7}]);
    t.context(0);t.key(t.menuButton('close'),'End');t.key(t.menuButton('close-all'),'Home');
    assert.equal(document.activeElement,t.menuButton('close'));
  } finally {t.dispose();}
});

test('outside click, Tab, resize, scroll and deactivation cancel without any command',()=>{
  const t=fixture();
  try {
    const cancel=[()=>document.querySelector('#outside').dispatchEvent(new t.dom.window.Event('pointerdown',{bubbles:true})),
      ()=>t.key(t.menuButton('close'),'Tab'),()=>window.dispatchEvent(new t.dom.window.Event('resize')),
      ()=>t.control.host.dispatchEvent(new t.dom.window.Event('scroll')),()=>window.dispatchEvent(new t.dom.window.Event('blur'))];
    for(const action of cancel){t.context(0);action();assert.equal(t.control.menu.opened,false);assert.deepEqual(t.messages,[]);}
  } finally {t.dispose();}
});

test('publication, tab-set changes, loading and vanished targets reject stale commands',()=>{
  const t=fixture();
  try {
    t.context(0); let stale=t.menuButton('close-all'); t.state.version++;stale.click();assert.deepEqual(t.messages,[]);
    t.context(0);stale=t.menuButton('close-all');t.update(t.tabs.slice(1));stale.click();assert.equal(t.control.menu.opened,false);assert.deepEqual(t.messages,[]);
    t.state.ready=false;t.context(0);assert.equal(t.control.menu.opened,false);
    t.state.ready=true; assert.equal(t.control.menu.show('missing',{left:0,right:0,top:0,bottom:0}),false);
    t.context(0);stale=t.menuButton('close-all');t.state.ready=false;stale.click();assert.deepEqual(t.messages,[]);
    t.update([],null); assert.equal(t.control.menu.show('tab-0',{left:0,right:0,top:0,bottom:0}),false);
  } finally {t.dispose();}
});

test('popup remains inside the viewport; existing close and middle-click keep exact tab identity',()=>{
  const t=fixture();
  try {
    t.context(1,{clientX:799,clientY:599});
    assert.equal(t.control.menu.menu.style.left,'542px');assert.equal(t.control.menu.menu.style.top,'502px');
    t.key(t.menuButton('close'),'Escape');
    t.item(0).querySelector('.close-source-tab').click();
    t.item(1).dispatchEvent(new t.dom.window.MouseEvent('auxclick',{button:1,bubbles:true,cancelable:true}));
    assert.deepEqual(t.messages.map(x=>[x.action,x.id]),[['close','tab-0'],['close','tab-1']]);
  } finally {t.dispose();}
});

test('a toolbar rebuild retains comment draft metadata and cached views only for surviving tabs',()=>{
  const t=fixture();
  try {
    const draft={anchor:{context:{kind:0,path:t.tabs[1].path},startLine:3}};
    t.control.setDraft(draft);t.control.views.set('tab-0',{scrollTop:40});t.control.views.set('tab-1',{scrollTop:80});
    t.context(0);t.key(t.menuButton('close'),'Escape');
    t.update(t.tabs.slice(1));
    assert.equal(t.control.draft,draft);assert.equal(t.control.views.has('tab-0'),false);assert.equal(t.control.views.get('tab-1').scrollTop,80);
    assert.equal(t.control.host.querySelector('[data-id="tab-1"][role=tab]').classList.contains('has-comment-draft'),true);
  } finally {t.dispose();}
});

test('system-location actions use clicked working-tab identity; historical snapshots have disabled entries',()=>{
  const t=fixture(true);
  try {
    t.context(0); assert.match(t.menuButton('reveal').textContent,/Finder/);t.menuButton('reveal').click();
    assert.deepEqual(t.messages,[{type:'source-tab',action:'reveal',id:'tab-0',version:7}]);
    t.context(1);t.menuButton('terminal').click();assert.equal(t.messages[1].action,'terminal');assert.equal(t.messages[1].id,'tab-1');
    t.context(2);assert.equal(t.menuButton('reveal').disabled,true);assert.equal(t.menuButton('terminal').disabled,true);
    t.menuButton('reveal').click();t.menuButton('terminal').click();assert.equal(t.messages.length,2);
  } finally {t.dispose();}
});
