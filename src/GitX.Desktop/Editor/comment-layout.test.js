import test from 'node:test';
import assert from 'node:assert/strict';
import { commentZones, selectedLines } from './comment-layout.js';

test('source selection excludes end at column one', () => {
  assert.deepEqual(selectedLines({ startLineNumber: 2, endLineNumber: 5, endColumn: 1 }), { start: 2, end: 4 });
  assert.deepEqual(selectedLines({ startLineNumber: 2, endLineNumber: 2, endColumn: 1 }), { start: 2, end: 2 });
});
test('paired comments split opposite insertion/deletion gaps without moving source rows', () => {
  for (const side of [0, 1]) {
    const gap = { afterLineNumber: 2, height: 4, index: 0 };
    const plan = side === 1 ? { oldZones: [gap], newZones: [] } : { oldZones: [], newZones: [gap] };
    const zones = commentZones(plan, side, 4, 11);
    const other = zones[1 - side];
    assert.deepEqual(other.map(z => [z.afterLineNumber, z.height, !!z.comment]), [[2, 2, false], [2, 11, true], [2, 2, false]]);
    assert.equal(zones[side][0].afterLineNumber, 4);
    assert.equal(other.filter(z => !z.comment).reduce((n, z) => n + z.height, 0), 4);
    assert.ok(other[0].ordinal < other[1].ordinal && other[1].ordinal < other[2].ordinal);
  }
});
test('comments align after earlier gaps, before gaps, at EOF and at empty side', () => {
  for (const [plan, side, line, expected] of [
    [{ oldZones: [{ afterLineNumber: 2, height: 3 }], newZones: [] }, 0, 4, [4, 7]],
    [{ oldZones: [{ afterLineNumber: 2, height: 3 }], newZones: [] }, 1, 1, [1, 1]],
    [{ oldZones: [{ afterLineNumber: 0, height: 3 }], newZones: [{ afterLineNumber: 3, height: 1 }] }, 1, 3, [0, 3]],
  ]) {
    const result = commentZones(plan, side, line, 11);
    assert.deepEqual(result.map(zones => zones.find(z => z.comment).afterLineNumber), expected);
    assert.equal(result[0].find(z => z.comment).height, result[1].find(z => z.comment).height);
  }
});
