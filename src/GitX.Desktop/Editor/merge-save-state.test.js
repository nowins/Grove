import test from 'node:test';
import assert from 'node:assert/strict';
import { MergeSaveState } from './merge-save-state.js';
test('opening a generated candidate needs save but not a discard confirmation', () => {
  const state = new MergeSaveState('base', 'false', false);
  assert.equal(state.dirty('base', 'false'), true);
  assert.equal(state.edited('base', 'false'), false);
  assert.equal(state.edited('base', 'true'), true); // Keep result still confirms a conflict.
  assert.equal(state.edited('changed', 'false'), true);
  assert.equal(state.edited('base', 'false'), false); // Undo all.
});
test('successful save changes both baselines; undo after save is protected', () => {
  const state = new MergeSaveState('base', 'false', false);
  state.markSaved('chosen', 'true');
  assert.equal(state.dirty('chosen', 'true'), false);
  assert.equal(state.edited('chosen', 'true'), false);
  assert.equal(state.dirty('base', 'false'), true);
  assert.equal(state.edited('base', 'false'), true);
});
test('reopened worktree result is saved, including empty text', () => {
  const state = new MergeSaveState('', '', true);
  assert.equal(state.dirty('', ''), false);
  assert.equal(state.edited('', ''), false);
  assert.equal(state.dirty('typed', ''), true);
});
