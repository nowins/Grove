import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DiffFolding } from './diff-folding.js';

function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}

function fixture(gate) {
  const models = [{}, {}], calls = [], errors = [], layouts = [];
  const lines = Array.from({length:50},(_,i)=>String(i));
  const panes = models.map((model, side) => ({
    model, getModel() { return this.model; },
    onDidChangeHiddenAreas() { return { dispose() {} }; },
    changeViewZones(callback) { callback({ removeZone() {} }); },
    getVisibleRanges() { return [{startLineNumber:1}]; },
    getTopForLineNumber(line) { return line * 21; }, getScrollTop() { return 0; }, getScrollLeft() { return 0; },
    getPosition() { return {lineNumber:1,column:1}; },
    getAction(id) { return { run: async args => { calls.push({side,id,args,model:this.model}); await gate.promise; } }; },
    setScrollPosition() { throw new Error('A disposed document must not change the new viewport'); },
    focus() { throw new Error('A disposed document must not take focus'); }
  }));
  const folding = new DiffFolding({ panes,models,plan:{oldLines:lines,newLines:lines,blocks:[{oldStart:24,oldCount:1,newStart:24,newCount:1}]},
    monaco:{languages:{FoldingRangeKind:{Region:{}}}},ranges:new WeakMap(),
    onLayout:()=>layouts.push(true),onCursorMoved:()=>{},onError:error=>errors.push(error) });
  return {folding,panes,calls,errors,layouts};
}

test('a fresh expanded diff is ready without waiting for a folding provider or changing its viewport', async t => {
  const gate = deferred(), f = fixture(gate);
  const previous = globalThis.document;
  const node = () => ({ dataset: {}, addEventListener() {}, append() {}, replaceChildren() {}, setAttribute() {} });
  globalThis.document = { createElement: node, createElementNS: node };
  t.after(() => { globalThis.document = previous; gate.resolve(); f.folding.dispose(); });
  let zones = 0;
  f.panes.forEach(pane => { pane.changeViewZones = action => action({ addZone: () => ++zones, removeZone() {} }); });
  f.folding.mount(false);
  // The provider is deliberately unresolved. The expanded model must already
  // be ready to receive its initial change position before the next frame.
  const ready = await Promise.race([f.folding.pending.then(() => true), new Promise(done => setImmediate(() => done(false)))]);
  assert.equal(ready, true);
  assert.equal(zones, f.folding.regions.length * 2);
  assert.equal(f.calls.length, 0);
  assert.equal(f.folding.collapsed.size, 0);
  assert.ok(f.folding.zones.flat().every(zone => zone.heightInLines === 0 && zone.domNode.dataset.expanded === 'true'));
  assert.deepEqual(f.errors, []);
});

test('switching documents before a queued fold starts never runs editor actions', async () => {
  const gate = deferred(), f = fixture(gate);
  const pending = f.folding.setAll(true, 0);
  f.folding.dispose();
  f.panes.forEach(pane=>{pane.model={};});
  await pending;
  assert.equal(f.calls.length,0);
  assert.equal(f.layouts.length,0);
});

test('late folding actions cannot continue against a replacement model or reclaim its viewport', async () => {
  const gate = deferred(), f = fixture(gate);
  const pending = f.folding.setState(new Set([0]), 1, true);
  await Promise.resolve(); // Both panes are waiting for the old provider/action.
  assert.equal(f.calls.length,2);
  assert.ok(f.calls.every(call=>call.id==='editor.unfold'));
  f.folding.dispose();
  f.panes.forEach(pane=>{pane.model={};});
  gate.resolve();
  await pending;
  assert.equal(f.calls.length,2); // No follow-up fold on the replacement document.
  assert.equal(f.layouts.length,1); // No late UI notification.
  assert.deepEqual(f.errors,[]);
});
