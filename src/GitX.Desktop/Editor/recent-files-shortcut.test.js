import test from 'node:test';
import assert from 'node:assert/strict';
import { recentFilesShortcut } from './recent-files-shortcut.js';
const event = changes => ({ key: 'e', ctrlKey: false, metaKey: false, altKey: false, shiftKey: false, repeat: false, isComposing: false, defaultPrevented: false, ...changes });
test('Windows Ctrl+E and Mac Command+E keep their platform modifier', () => {
  assert.equal(recentFilesShortcut(event({ ctrlKey: true }), false), true);
  assert.equal(recentFilesShortcut(event({ metaKey: true, key: 'E' }), true), true);
  assert.equal(recentFilesShortcut(event({ ctrlKey: true }), true), false);
  assert.equal(recentFilesShortcut(event({ metaKey: true }), false), false);
});
test('recent files does not steal reveal, accents, repeat, IME or handled keys', () => {
  for (const mac of [false, true]) for (const changes of [{ shiftKey: true }, { altKey: true }, { ctrlKey: true, metaKey: true }, { repeat: true }, { isComposing: true }, { keyCode: 229 }, { defaultPrevented: true }, { key: 'f' }]) {
    assert.equal(recentFilesShortcut(event({ [mac ? 'metaKey' : 'ctrlKey']: true, ...changes }), mac), false);
  }
});
