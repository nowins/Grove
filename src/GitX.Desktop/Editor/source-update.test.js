import test from 'node:test';
import assert from 'node:assert/strict';
import { canReuseSourceModel } from './source-update.js';
const source = { path: 'a.java', text: 'class A {}', language: 'java', commentContext: { repositoryId: 'repo', path: 'a.java', kind: 0, snapshot: 'hash' } };
const update = { ...source, preserveView: true, blame: [{ line: 1, author: 'author' }] };
test('annotation on/off reuses the same exact source snapshot', () => {
  assert.equal(canReuseSourceModel(source, update, 'tab', 'tab'), true);
  assert.equal(canReuseSourceModel(update, { ...update, blame: null }, 'tab', 'tab'), true);
});
test('file, tab, repository, revision and changed bytes require replacement', () => {
  for (const changed of [{ path: 'b.java' }, { text: 'class B {}' }, { language: 'plaintext' }, { sourceRevision: 'other' },
    { commentContext: { ...source.commentContext, repositoryId: 'other' } }, { commentContext: { ...source.commentContext, snapshot: 'changed' } },
    { commentContext: { ...source.commentContext, kind: 1 } }, { commentContext: null }, { preserveView: false }, { isNotice: true }, { isDiff: true }, { diff: {} }])
    assert.equal(canReuseSourceModel(source, { ...update, ...changed }, 'tab', 'tab'), false);
  assert.equal(canReuseSourceModel(source, update, 'tab', 'other'), false);
  assert.equal(canReuseSourceModel(null, update, 'tab', 'tab'), false);
  assert.equal(canReuseSourceModel(source, update, null, null), false);
});
