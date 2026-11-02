import test from 'node:test';
import assert from 'node:assert/strict';
import { selectNotes, noteStatus } from './note-list.js';

const context = { repositoryId: 'repo', snapshot: 'current', kind: 0, path: 'src/订单.cs' };
const doc = { path: context.path, commentContext: context };
const note = (id, changes = {}, anchor = {}) => ({ id, body: '核对缓存', updated: '2026-10-29T08:00:00Z',
  anchor: { context, path: context.path, startLine: 2, code: 'Read<T>(value)', label: '工作区', ...anchor }, ...changes });
const select = (items, options = {}, document = doc, repo = 'repo') => selectNotes(items, options, document, repo).map(item => item.id);

test('literal AND tokens search body, paths, original code and labels without regex interpretation', () => {
  const items = [note('a'), note('b', { body: 'other' }, { path: 'other.cs', code: 'different' })];
  assert.deepEqual(select(items, { query: '订单 核对 READ<T>' }), ['a']);
  assert.deepEqual(select(items, { query: '[.*' }), []);
  assert.deepEqual(select(items, { query: ' 工作区  缓存 ' }), ['a']);
});
test('scope distinguishes the same file from its exact snapshot', () => {
  const items = [note('current'), note('changed', {}, { context: { ...context, snapshot: 'old' } }),
    note('diff', {}, { context: { ...context, snapshot: 'diff', kind: 1 } }), note('other', {}, { path: 'b.cs', context: { ...context, path: 'b.cs', snapshot: 'b' } })];
  assert.deepEqual(select(items, { scope: 'current' }), ['current']);
  assert.deepEqual(new Set(select(items, { scope: 'file' })), new Set(['current', 'changed', 'diff']));
  assert.match(noteStatus(items[1].anchor, doc), /上下文已变化/);
  assert.match(noteStatus(items[2].anchor, doc), /其他版本/);
});
test('renamed old-side notes match the comparison path as well as their original path', () => {
  const item = note('rename', {}, { path: 'old.cs', context: { ...context, path: 'new.cs', snapshot: 'rename', kind: 1 } });
  assert.deepEqual(select([item], { scope: 'file', query: 'old.cs new.cs' }, { path: 'new.cs' }), ['rename']);
});
test('repository isolation and absent document scope never expose unrelated notes', () => {
  const items = [note('a'), note('foreign', {}, { context: { ...context, repositoryId: 'other' } })];
  assert.deepEqual(select(items), ['a']);
  assert.deepEqual(select(items, {}, null, null), []);
  assert.deepEqual(select(items, { scope: 'current' }, {}), []);
  assert.deepEqual(select(items, { scope: 'file' }, {}), []);
});
test('sorting is stable, deterministic and does not mutate the original records', () => {
  const items = [note('a', { updated: '2026-10-28' }, { startLine: 8 }), note('b', { updated: '2026-10-29' }, { startLine: 4 }),
    note('c', { updated: '2026-10-27' }, { path: 'a.cs' })];
  assert.deepEqual(select(items), ['b', 'a', 'c']);
  assert.deepEqual(select(items, { sort: 'file' }), ['c', 'b', 'a']);
  assert.deepEqual(items.map(item => item.id), ['a', 'b', 'c']);
});
