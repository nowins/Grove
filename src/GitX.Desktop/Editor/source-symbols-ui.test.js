import test from 'node:test';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import { JSDOM } from 'jsdom';

// Use the real UI controller and DOM. CSS and the worker transport are the
// only substitutes; the parser itself is covered by source-symbols.test.js.
const hooks = registerHooks({
  load(url, context, nextLoad) {
    if (url.endsWith('/source-symbols.css')) return { format: 'module', source: '', shortCircuit: true };
    return nextLoad(url, context);
  }
});
const { SourceSymbols } = await import('./source-symbols.js');
hooks.deregister();

function fixture(t) {
  const dom = new JSDOM('<!doctype html><body><button id="source-symbols"></button></body>', { url: 'http://127.0.0.1/editor/index.html' });
  dom.window.HTMLElement.prototype.scrollIntoView = function () {};
  const previous = new Map(['window', 'document', 'location', 'Worker'].map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  const workers = [], calls = { focus: 0, reveal: [] };
  class Worker {
    constructor() { this.requests = []; this.terminated = false; workers.push(this); }
    postMessage(message) { this.requests.push(message); }
    terminate() { this.terminated = true; }
    complete(symbols) { this.onmessage({ data: { id: this.requests.at(-1).id, symbols, truncated: false, hasErrors: false } }); }
  }
  Object.assign(globalThis, { window: dom.window, document: dom.window.document, location: dom.window.location, Worker });
  const state = { available: true, identity: '1:model-a', language: 'javascript', path: 'a.js', text: () => 'function first() {}' };
  const control = new SourceSymbols({ getState: () => state, focus: () => calls.focus++, reveal: value => calls.reveal.push(value) });
  t.after(() => {
    control.dispose(); dom.window.close();
    for (const [key, descriptor] of previous) {
      if (descriptor) Object.defineProperty(globalThis, key, descriptor);
      else delete globalThis[key];
    }
  });
  const symbols = count => Array.from({ length: count }, (_, index) => ({ name: `item${index}`, label: `item${index}()`, kind: 'function', glyph: 'f', container: '', depth: 0, line: index + 1, column: 1, length: 5 }));
  return { control, state, workers, calls, symbols };
}

function assertReleased(control) {
  assert.equal(control.items.length, 0);
  assert.equal(control.filtered.length, 0);
  assert.equal(control.result, null);
  assert.equal(control.identity, null);
  assert.equal(control.list.childElementCount, 0);
  assert.equal(control.input.hasAttribute('aria-activedescendant'), false);
}

test('document invalidation drops completed hidden symbols, filtered entries and row closures', t => {
  const { control, workers, symbols } = fixture(t);
  control.open(); workers[0].complete(symbols(600)); control.close(false);
  assert.equal(control.items.length, 600); assert.equal(control.list.childElementCount, 500);
  control.invalidate();
  assertReleased(control);
  assert.equal(workers[0].terminated, false, 'the warm parser worker is intentionally retained');
});

test('document invalidation closes a visible result without restoring editor focus', t => {
  const { control, workers, calls, symbols } = fixture(t);
  control.open(); workers[0].complete(symbols(3)); control.invalidate();
  assert.equal(control.opened, false); assert.equal(calls.focus, 0);
  assertReleased(control);
});

test('ordinary close and reopen reuse the same document result and warm worker', t => {
  const { control, workers, symbols } = fixture(t);
  const result = symbols(2);
  control.open(); workers[0].complete(result); control.close(false); control.open();
  assert.equal(workers.length, 1); assert.equal(workers[0].requests.length, 1);
  assert.equal(control.items, result); assert.equal(control.result.symbols, result);
  assert.equal(control.list.childElementCount, 2);
});

test('invalidating an in-flight parse stops it and refuses a late result', t => {
  const { control, workers, calls, symbols } = fixture(t);
  control.open(); const worker = workers[0], late = worker.onmessage, id = worker.requests[0].id;
  control.invalidate();
  assert.equal(worker.terminated, true); assert.equal(control.worker, null);
  late({ data: { id, symbols: symbols(2) } });
  assertReleased(control); assert.equal(calls.focus, 0);
});

test('a late old-generation result cannot replace a newly opened document', t => {
  const { control, workers, state, symbols } = fixture(t);
  control.open(); const late = workers[0].onmessage, oldId = workers[0].requests[0].id;
  workers[0].complete(symbols(1)); control.invalidate();
  state.identity = '2:model-b'; state.path = 'b.js'; control.open();
  const current = symbols(3); workers[0].complete(current);
  late({ data: { id: oldId, symbols: symbols(7) } });
  assert.equal(control.items, current); assert.equal(control.identity, state.identity);
  assert.equal(control.list.childElementCount, 3);
});

test('repeated document changes release each prior result without discarding the warm worker', t => {
  const { control, workers, state, symbols } = fixture(t);
  for (let index = 0; index < 20; index++) {
    state.identity = `${index}:model`; state.path = `${index}.js`;
    control.open(); workers[0].complete(symbols(40)); control.close(false); control.invalidate();
    assertReleased(control);
  }
  assert.equal(workers.length, 1); assert.equal(workers[0].requests.length, 20);
});
