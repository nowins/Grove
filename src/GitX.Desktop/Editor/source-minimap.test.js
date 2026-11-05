import test from 'node:test';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';

// These tests run the real SourceMinimap methods, with only its unrelated
// Monaco dependency stubbed. No browser/Monaco memory measurement is implied.
const hooks = registerHooks({
  resolve(specifier, context, nextResolve) {
    if (specifier === 'monaco-editor/editor/editor.api.js')
      return { url: 'data:text/javascript,export const editor = {};', shortCircuit: true };
    return nextResolve(specifier, context);
  }
});
const { SourceMinimap } = await import('./source-minimap.js');
hooks.deregister();

function fixture(t) {
  const originalFrame = globalThis.requestAnimationFrame;
  const callbacks = [], revealed = [];
  globalThis.requestAnimationFrame = callback => { callbacks.push(callback); return callbacks.length; };
  t.after(() => {
    if (originalFrame === undefined) delete globalThis.requestAnimationFrame;
    else globalThis.requestAnimationFrame = originalFrame;
  });
  const column = { clientWidth: 0, classList: { toggle() {} }, style: { setProperty() {} } };
  const host = { parentElement: column, getBoundingClientRect: () => ({ top: 0 }) };
  const plan = { oldLines: ['a', 'b', 'c'], newLines: ['a', 'b', 'c'], blocks: [] };
  const minimap = Object.create(SourceMinimap.prototype);
  Object.assign(minimap, {
    plan, sizes: [{ row: 1 }, { row: 1 }], hosts: [host, host],
    reveal: (side, line) => revealed.push({ side, line }), draw() {}, scheduleColors() {}
  });
  return { minimap, plan, callbacks, revealed };
}

test('completed minimap seek releases the pending document reference', t => {
  const { minimap, callbacks, revealed } = fixture(t);
  minimap.seek({ clientY: 1 }, 1);
  callbacks.shift()();
  assert.deepEqual(revealed, [{ side: 1, line: 2 }]);
  assert.equal(minimap.pending, null);
  assert.equal(minimap.frame, null);
});

test('multiple pointer events in one frame use only the latest target', t => {
  const { minimap, callbacks, revealed } = fixture(t);
  minimap.seek({ clientY: 0 }, 0);
  minimap.seek({ clientY: 2 }, 1);
  assert.equal(callbacks.length, 1);
  callbacks.shift()();
  assert.deepEqual(revealed, [{ side: 1, line: 3 }]);
  assert.equal(minimap.pending, null);
});
test('closing the document before its seek frame drops the old plan immediately', t => {
  const { minimap, callbacks, revealed } = fixture(t);
  minimap.seek({ clientY: 1 }, 1);
  minimap.update(null, false);
  assert.equal(minimap.pending, null);
  callbacks.shift()();
  assert.deepEqual(revealed, []);
  assert.equal(minimap.pending, null);
});

test('replacing a document before its seek frame never navigates the old plan', t => {
  const { minimap, callbacks, revealed } = fixture(t);
  minimap.seek({ clientY: 1 }, 1);
  minimap.update({ oldLines: ['other'], newLines: ['other'], blocks: [] }, true);
  assert.equal(minimap.pending, null);
  callbacks.shift()();
  assert.deepEqual(revealed, []);
});

test('layout updates for the same plan do not cancel a valid queued seek', t => {
  const { minimap, plan, callbacks, revealed } = fixture(t);
  minimap.seek({ clientY: 1 }, 1);
  minimap.update(plan, true);
  callbacks.shift()();
  assert.deepEqual(revealed, [{ side: 1, line: 2 }]);
  assert.equal(minimap.pending, null);
});

test('a seek queued while revealing is preserved for the next frame', t => {
  const { minimap, callbacks, revealed } = fixture(t);
  let reentered = false;
  minimap.reveal = (side, line) => {
    revealed.push({ side, line });
    if (!reentered) { reentered = true; minimap.seek({ clientY: 2 }, 0); }
  };
  minimap.seek({ clientY: 0 }, 1);
  callbacks.shift()();
  assert.equal(callbacks.length, 1);
  assert.notEqual(minimap.pending, null);
  callbacks.shift()();
  assert.deepEqual(revealed, [{ side: 1, line: 1 }, { side: 0, line: 3 }]);
  assert.equal(minimap.pending, null);
});

test('a throwing reveal does not leave the old plan in pending', t => {
  const { minimap, callbacks } = fixture(t);
  minimap.reveal = () => { throw new Error('reveal failed'); };
  minimap.seek({ clientY: 1 }, 1);
  assert.throws(() => callbacks.shift()(), /reveal failed/);
  assert.equal(minimap.pending, null);
});
