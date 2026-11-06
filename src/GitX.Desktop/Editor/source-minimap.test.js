import test from 'node:test';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import { JSDOM } from 'jsdom';

// These tests run the real SourceMinimap methods, with only its unrelated
// Monaco dependency stubbed. No browser/Monaco memory measurement is implied.
const hooks = registerHooks({
  resolve(specifier, context, nextResolve) {
    if (specifier === 'monaco-editor/editor/editor.api.js')
      return { url: 'data:text/javascript,export const editor = { colorizeModelLine: (...args) => globalThis.colorizeMinimapLine(...args) };', shortCircuit: true };
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
    reveal: (side, line) => revealed.push({ side, line }), draw() {}, renderViewport() {}, cancelColors() {}
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

function rasterFixture(t, count = 5000) {
  const dom = new JSDOM('<body><div><div id="old-map"></div></div><div><div id="new-map"></div></div></body>');
  const jobs = new Map(), calls = [], painted = [], redraws = [];
  let sequence = 0;
  dom.window.requestIdleCallback = callback => { jobs.set(++sequence, callback); return sequence; };
  dom.window.cancelIdleCallback = id => jobs.delete(id);
  const globals = { window: dom.window, document: dom.window.document, devicePixelRatio: 2,
    ResizeObserver: class { observe() {} disconnect() {} },
    getComputedStyle: () => ({ color: '#abcdef' }),
    colorizeMinimapLine: (model, line) => { calls.push([model.side, line]); return '<span class="mtk1">' + model.getLineContent(line) + '</span>'; } };
  const originals = Object.fromEntries(Object.keys(globals).map(key => [key, globalThis[key]]));
  Object.assign(globalThis, globals);
  t.after(() => { for (const key of Object.keys(globals)) { if (originals[key] === undefined) delete globalThis[key]; else globalThis[key] = originals[key]; } dom.window.close(); });
  dom.window.HTMLCanvasElement.prototype.getContext = () => ({ scale() {}, clearRect() { redraws.push(1); }, fillRect(...args) { painted.push(args); } });
  const models = [0, 1].map(side => ({ side, isDisposed: () => false, getLineContent: () => '  alpha beta '.repeat(100) }));
  const minimap = new SourceMinimap({ panes: models.map(model => ({ getModel: () => model, getVisibleRanges: () => [] })), reveal() {} });
  const geometry = { width: 145, height: 400, column: 700 };
  minimap.hosts.forEach(host => {
    Object.defineProperty(host.parentElement, 'clientWidth', { get: () => geometry.column });
    Object.defineProperty(host, 'clientWidth', { get: () => geometry.width });
    Object.defineProperty(host, 'clientHeight', { get: () => geometry.height });
  });
  const plan = { oldLines: Array(count).fill('  alpha beta '.repeat(100)), newLines: Array(count).fill('  alpha beta '.repeat(100)), blocks: [] };
  function flush() { let remaining = 1000; while (jobs.size) { assert.ok(remaining-- > 0); const [id, callback] = jobs.entries().next().value; jobs.delete(id); callback(); } }
  return { minimap, plan, geometry, jobs, calls, painted, redraws, models, flush };
}

test('hidden minimaps do not colorize or retain a canvas backing store', t => {
  const { minimap, plan, calls, jobs } = rasterFixture(t);
  minimap.update(plan, false);
  assert.equal(jobs.size, 0); assert.equal(calls.length, 0);
  minimap.hosts.forEach(host => assert.equal(host.firstChild.width, 0));
});

test('color work and retained runs are bounded by raster rows and visible columns', t => {
  const { minimap, plan, geometry, calls, redraws, flush } = rasterFixture(t);
  minimap.update(plan, true); flush();
  const expected = [];
  let previous = -1;
  const row = Math.min(2, Math.max(.05, (geometry.height - 4) / plan.oldLines.length));
  for (let i = 0; i < plan.oldLines.length; i++) {
    const y = Math.floor(i * row); if (y >= geometry.height) break;
    if (y !== previous) expected.push(i + 1); previous = y;
  }
  assert.deepEqual(calls.filter(([side]) => side === 0).map(([, line]) => line), expected);
  assert.deepEqual(calls.filter(([side]) => side === 1).map(([, line]) => line), expected);
  assert.ok(calls.length <= 2 * geometry.height);
  minimap.colors.forEach(cache => { assert.equal(cache.size, expected.length); for (const runs of cache.values()) assert.ok(runs.every(run => run.offset + run.length <= 220)); });
  assert.equal(minimap.colorHost.childNodes.length, 0); assert.equal(minimap.colorJob, null);
  // Idle slices must not repaint the entire bitmap each time they yield.
  assert.equal(redraws.length, 4); // Initial overview and completed colors, per side.
});

test('hiding or replacing a pending minimap cancels its work and releases colors', t => {
  const { minimap, plan, calls, jobs, flush } = rasterFixture(t);
  minimap.update(plan, true); assert.equal(jobs.size, 1);
  minimap.update(plan, false); assert.equal(jobs.size, 0);
  assert.equal(minimap.colorJob, null); assert.ok(minimap.colors.every(cache => cache.size === 0));
  minimap.update(plan, true); minimap.update(null, false); flush();
  assert.equal(calls.length, 0); assert.equal(minimap.colorJob, null);
});

test('resizing replaces the raster cache and showing again rebuilds it', t => {
  const { minimap, plan, geometry, calls, flush } = rasterFixture(t);
  minimap.update(plan, true); flush(); const firstCount = calls.length;
  minimap.update(plan, true); flush(); assert.equal(calls.length, firstCount);
  geometry.height = 100; minimap.draw(); flush();
  assert.ok(minimap.colors.every(cache => cache.size <= 100));
  geometry.column = 400; minimap.update(plan, true); assert.ok(minimap.colors.every(cache => cache.size === 0));
  geometry.column = 700; minimap.update(plan, true); flush();
  assert.ok(minimap.colors.every(cache => cache.size > 0 && cache.size <= 100));
});

test('a disposed model cancels pending color work without publishing stale colors', t => {
  const { minimap, plan, models, calls, flush } = rasterFixture(t);
  minimap.update(plan, true); models[0].isDisposed = () => true; flush();
  assert.equal(calls.length, 0); assert.equal(minimap.colorJob, null);
});
