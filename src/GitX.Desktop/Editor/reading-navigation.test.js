import test from 'node:test';
import assert from 'node:assert/strict';
import { readingDirection, readingGesture } from './reading-navigation.js';

test('Windows Alt-arrows navigate but Ctrl, Shift and Meta chords do not', () => {
  assert.equal(readingDirection({ key: 'ArrowLeft', altKey: true }, false), -1);
  assert.equal(readingDirection({ key: 'ArrowRight', altKey: true }, false), 1);
  for (const extra of [{ ctrlKey: true }, { shiftKey: true }, { metaKey: true }, { altKey: false }])
    assert.equal(readingDirection({ key: 'ArrowLeft', altKey: true, ...extra }, false), 0);
});
test('Mac Command-Option-arrows leave word and line movement intact', () => {
  assert.equal(readingDirection({ key: 'ArrowLeft', metaKey: true, altKey: true }, true), -1);
  assert.equal(readingDirection({ key: 'ArrowRight', metaKey: true, altKey: true }, true), 1);
  assert.equal(readingDirection({ key: 'ArrowLeft', altKey: true }, true), 0);
  assert.equal(readingDirection({ key: 'ArrowLeft', metaKey: true }, true), 0);
});
test('composition and unrelated keys never become reading navigation', () => {
  for (const extra of [{ isComposing: true }, { keyCode: 229 }, { key: 'Enter' }])
    assert.equal(readingDirection({ key: 'ArrowLeft', altKey: true, ...extra }, false), 0);
});
test('explicit mouse/find/API jumps count, while typing, arrows and undo do not', () => {
  for (const source of ['mouse', 'api', 'grove.navigate']) assert.equal(readingGesture({ source, reason: 3 }), true);
  // Pinned Monaco's setPosition/setSelection default to NotSet (0), as used
  // by Go To Line and built-in Find; they are still deliberate jumps.
  for (const source of ['api', 'grove.navigate']) assert.equal(readingGesture({ source, reason: 0 }), true);
  for (const source of ['keyboard', 'mouse']) for (const reason of [0, 1, 2, 4, 5, 6])
    assert.equal(readingGesture({ source, reason }), false);
  for (const source of ['api', 'grove.navigate']) for (const reason of [1, 2, 4, 5, 6])
    assert.equal(readingGesture({ source, reason }), false);
  assert.equal(readingGesture({ source: 'keyboard', reason: 3 }), false);
});
