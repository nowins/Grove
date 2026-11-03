import test from 'node:test';
import assert from 'node:assert/strict';
import { installNativeBridge } from './native-bridge.js';

test('all native commands return a string, including asynchronous document load', async () => {
  const target = {}, errors = []; let completed = false;
  const bridge = installNativeBridge(target, () => error => errors.push(error));
  for (const value of [undefined, null, true, 42, { editor: () => {} }, () => {}]) {
    assert.equal(bridge.command(() => value), 'ok');
  }
  assert.equal(bridge.command(async () => { await Promise.resolve(); completed = true; return { unsupported: () => {} }; }), 'ok');
  assert.equal(completed, false);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(completed, true); assert.deepEqual(errors, []);
  assert.equal(target.groveNative, bridge); assert.ok(Object.isFrozen(bridge));
});

test('rejected asynchronous commands report once through the captured message context', async () => {
  const messages = []; let version = 7;
  const bridge = installNativeBridge({}, () => { const captured = version; return error => messages.push([captured, error.message]); });
  bridge.command(() => Promise.reject(new Error('fetch failed'))); version = 8;
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(messages, [[7, 'fetch failed']]);
});

test('synchronous errors remain visible to the invoking host', () => {
  const bridge = installNativeBridge({}, () => assert.fail);
  assert.throws(() => bridge.command(() => { throw new Error('bad command'); }), /bad command/);
});

test('a failed error reporter does not cause an unhandled rejection', async () => {
  const bridge = installNativeBridge({}, () => () => { throw new Error('closed page'); });
  bridge.command(() => Promise.reject(new Error('late error')));
  await new Promise(resolve => setImmediate(resolve));
});

test('native snapshot queries return JSON strings without changing Unicode or nested view state', () => {
  const value = { version: 12, path: '目录/长文件.ts', views: [{ cursorState: [{ inSelectionMode: false }], scrollTop: 120.5 }], side: 1 };
  const bridge = installNativeBridge({}, () => assert.fail);
  assert.deepEqual(JSON.parse(bridge.json(() => value)), value);
  assert.equal(bridge.json(() => undefined), 'null'); assert.equal(bridge.json(() => null), 'null');
  assert.throws(() => bridge.json(async () => value), /must be synchronous/);
  assert.throws(() => bridge.json(() => { const cycle = {}; cycle.self = cycle; return cycle; }), /circular/i);
});
