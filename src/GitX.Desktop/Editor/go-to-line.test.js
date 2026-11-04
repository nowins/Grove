import test from 'node:test';
import assert from 'node:assert/strict';
import { parseLineInput, goToLineShortcut, sameLineContext } from './go-to-line-core.js';
const key = overrides => ({key:'g',ctrlKey:true,metaKey:false,shiftKey:false,altKey:false,isComposing:false,repeat:false,...overrides});
test('Ctrl+G remains available on Windows and Mac; Mac Command+L is an additional platform shortcut',()=>{
 assert.equal(goToLineShortcut(key(),false),true);assert.equal(goToLineShortcut(key(),true),true);
 assert.equal(goToLineShortcut(key({key:'l',ctrlKey:false,metaKey:true}),true),true);assert.equal(goToLineShortcut(key({key:'l',ctrlKey:false,metaKey:true}),false),false);
 for(const overrides of [{shiftKey:true},{altKey:true},{metaKey:true},{isComposing:true},{keyCode:229},{repeat:true},{defaultPrevented:true}])assert.equal(goToLineShortcut(key(overrides),true),false);
});
test('line input rejects empty, malformed and out-of-range values without clamping silently',()=>{
 for(const text of ['', ' ', '0', '-1', '1.5', '1:2', 'abc', '4','9999999999999999'])assert.ok(parseLineInput(text,3).error,text);
 assert.deepEqual(parseLineInput(' 3 ',3),{line:3});assert.deepEqual(parseLineInput('1',1),{line:1});
});
test('file, model and publication changes invalidate a pending jump',()=>{
 const model={};const original={available:true,path:'same.py',version:1,model};assert.equal(sameLineContext(original,{...original}),true);
 for(const changed of [{path:'other.py'},{version:2},{model:{}},{available:false}])assert.equal(sameLineContext(original,{...original,...changed}),false);
});

import { JSDOM } from 'jsdom';
import { GoToLine } from './go-to-line.js';

function picker() {
  const dom = new JSDOM('<!doctype html><body><button id="editor">source</button></body>');
  globalThis.document = dom.window.document;
  const calls = []; const model = {};
  const state = { available: true, path: 'history.py', version: 10, model, modelVersion: 1, lineCount: 3, line: 2 };
  const editor = document.querySelector('#editor');
  const form = new GoToLine({ getState: () => state, reveal: line => calls.push(line), focus: () => editor.focus() });
  dom.window.addEventListener('keydown', event => { form.handleKey(event); }, true);
  const key = (target, value, extra = {}) => target.dispatchEvent(new dom.window.KeyboardEvent('keydown', { key: value, bubbles: true, cancelable: true, ...extra }));
  return { dom, state, form, calls, editor, key, dispose() { form.dispose(); dom.window.close(); delete globalThis.document; } };
}

test('typing and invalid Enter never move the caret; valid Enter reveals exactly once', () => {
  const t = picker();
  try {
    assert.equal(t.form.open(), true); assert.equal(t.dom.window.document.activeElement, t.form.input);
    t.form.input.value = '4'; t.key(t.form.input, 'Enter');
    assert.equal(t.form.opened, true); assert.equal(t.form.input.getAttribute('aria-invalid'), 'true'); assert.deepEqual(t.calls, []);
    t.form.input.value = '3'; t.key(t.form.input, '3'); assert.deepEqual(t.calls, []);
    t.key(t.form.input, 'Enter'); assert.deepEqual(t.calls, [3]); assert.equal(t.form.opened, false); assert.equal(t.dom.window.document.activeElement, t.editor);
  } finally { t.dispose(); }
});

test('Escape, close-button Enter and outside click cancel without jumping; Tab remains inside', () => {
  const t = picker();
  try {
    t.form.open(); t.key(t.form.input, 'Tab'); assert.equal(t.dom.window.document.activeElement, t.form.closeButton);
    t.key(t.form.closeButton, 'Enter'); assert.equal(t.form.opened, false); assert.deepEqual(t.calls, []);
    t.form.open(); t.key(t.form.input, 'Escape'); assert.equal(t.form.opened, false); assert.equal(t.dom.window.document.activeElement, t.editor);
    t.form.open(); t.form.overlay.dispatchEvent(new t.dom.window.Event('pointerdown', { bubbles: true })); assert.equal(t.form.opened, false); assert.deepEqual(t.calls, []);
  } finally { t.dispose(); }
});

test('composition confirmation stays in the input and never accepts a line', () => {
  const t = picker();
  try {
    t.form.open(); t.form.input.value = '3'; t.key(t.form.input, 'Enter', { isComposing: true });
    assert.equal(t.form.opened, true); assert.deepEqual(t.calls, []);
    t.key(t.form.input, 'Escape', { isComposing: true }); assert.equal(t.form.opened, true);
  } finally { t.dispose(); }
});

test('changed publication/model text and async close invalidate the form without stealing focus', () => {
  const t = picker();
  try {
    for (const change of [() => t.state.version++, () => t.state.modelVersion++, () => { t.state.available = false; }]) {
      t.state.available = true; t.form.open(); change(); t.form.input.value = '3'; t.key(t.form.input, 'Enter');
      assert.equal(t.form.opened, false); assert.deepEqual(t.calls, []); assert.notEqual(t.dom.window.document.activeElement, t.editor);
    }
    t.state.available = false; assert.equal(t.form.open(), false);
  } finally { t.dispose(); }
});
