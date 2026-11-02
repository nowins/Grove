import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DiffScrollMap, sourceBoundary, connectorPath } from './diff-scroll.js';

const h = 23, padding = 4;
function fixture(oldCount = 40, newCount = 45, blocks = [{ oldStart: 8, oldCount: 0, newStart: 8, newCount: 5 }]) {
  const panes = [oldCount, newCount].map(count => ({
    getTopForLineNumber: line => padding + (line - 1) * h,
    getLayoutInfo: () => ({ height: 230 }), getScrollHeight: () => padding + count * h + 14
  }));
  const map = new DiffScrollMap({ oldLines: Array(oldCount), newLines: Array(newCount), blocks }, h);
  map.layout(panes); return { map, panes };
}

test('continuous source boundaries preserve insertion endpoints in both directions', () => {
  const { map } = fixture();
  const y = line => padding + line * h;
  assert.equal(map.transfer(0, y(7)), y(7));
  assert.equal(map.transfer(0, y(8)), y(8));
  assert.equal(map.transfer(0, y(9)), y(14));
  for (const line of [9, 10, 11, 12, 13]) assert.equal(map.transfer(1, y(line)), y(8));
  assert.equal(map.transfer(1, y(14)), y(9));
});

test('deletions and unequal replacements use real source ranges without padding', () => {
  const { map } = fixture(45, 40, [{ oldStart: 8, oldCount: 5, newStart: 8, newCount: 0 }]);
  assert.equal(map.transfer(0, padding + 11 * h), padding + 8 * h);
  assert.equal(map.transfer(1, padding + 9 * h), padding + 14 * h);
  const replacement = fixture(40, 42, [{ oldStart: 8, oldCount: 2, newStart: 8, newCount: 4 }]).map;
  assert.equal(replacement.transfer(1, padding + 11 * h), padding + 10 * h);
  assert.equal(replacement.transfer(1, padding + 13 * h), padding + 11 * h);
});

test('scroll synchronization maps the upper third and preserves fractional line phase', () => {
  const { map, panes } = fixture();
  const top = 300.25;
  const forward = map.target(panes, 0, top, 0);
  assert.equal(forward, top + 5 * h);
  assert.equal(map.target(panes, 1, forward, 0), top);
  assert.equal(map.target(panes, 0, 0, 0), 0);
});

test('the follower does not oscillate backwards as an inserted block crosses the anchor', () => {
  const { map, panes } = fixture();
  let current = 0;
  for (let top = 140; top < 220; top += .5) {
    const next = map.target(panes, 1, top, current);
    assert.ok(next >= current, `${top}: ${next} < ${current}`); current = next;
  }
});

test('document ends and empty files clamp to the follower scroll extent', () => {
  const { map, panes } = fixture();
  assert.equal(map.target(panes, 0, 10000, 0), panes[1].getScrollHeight() - 230);
  const empty = fixture(0, 20, [{ oldStart: 0, oldCount: 0, newStart: 0, newCount: 20 }]);
  assert.equal(empty.map.target(empty.panes, 1, 200, 0), 0);
  assert.equal(sourceBoundary(empty.panes[0], 0, 0, h), padding);
  assert.equal(sourceBoundary(panes[0], 40, 40, h), padding + 40 * h);
});

test('folding and comment geometry is refreshed instead of retaining stale pixel offsets', () => {
  const { map, panes } = fixture();
  const before = map.transfer(0, padding + 9 * h);
  panes[1].getTopForLineNumber = line => padding + (line - 1) * h + 11 * h;
  map.invalidate(); map.layout(panes);
  assert.equal(map.transfer(0, padding + 9 * h), before + 11 * h);
});

test('connectors have two cubic boundaries and retain endpoints outside the viewport', () => {
  const path = connectorPath(32, -100, -100, 50, 250);
  assert.equal((path.match(/ C/g) ?? []).length, 2);
  assert.ok(path.startsWith('M0 -101 '));
  assert.ok(path.includes('32 50 L32 250'));
  assert.ok(path.endsWith('0 -100 Z'));
  assert.ok(!path.includes('NaN'));
});
