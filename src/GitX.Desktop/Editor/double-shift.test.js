import test from 'node:test';
import assert from 'node:assert/strict';
import { DoubleShiftGesture } from './double-shift.js';
const tap = (gesture, time, duration = 40, allowed = true) => { gesture.press(true, allowed, time); return gesture.release(true, allowed, time + duration); };
test('double Shift requires two isolated taps and fires once per pair', () => {
  const g = new DoubleShiftGesture(); assert.equal(tap(g, 0), false); assert.equal(tap(g, 100), true);
  assert.equal(tap(g, 200), false); assert.equal(tap(g, 300), true);
});
test('timeout, held Shift and repeats cannot trigger search', () => {
  const g = new DoubleShiftGesture(); assert.equal(tap(g, 0), false); assert.equal(tap(g, 600), false); assert.equal(tap(g, 700), true);
  assert.equal(tap(g, 1000, 400), false); assert.equal(tap(g, 1450), false);
  g.press(true, true, 1600); g.press(true, true, 1610); assert.equal(g.release(true, true, 1650), false);
  assert.equal(tap(g, 1700), false);
});
test('typing and Shift chords interrupt a pending first tap', () => {
  for (const shift of [false, true]) {
    const g = new DoubleShiftGesture(); tap(g, 0); g.press(shift, false, 80); g.release(shift, false, 100);
    assert.equal(tap(g, 150), false); assert.equal(tap(g, 250), true);
  }
});
test('composition, blur, pointer and focus resets require a fresh pair', () => {
  const g = new DoubleShiftGesture(); tap(g, 0); g.reset(); assert.equal(tap(g, 100), false);
  g.press(true, false, 160); assert.equal(g.release(true, true, 200), false); assert.equal(tap(g, 250), false);
  assert.equal(tap(g, 350), true);
});
test('release timing is inclusive and invalid keyup or reversed time is rejected', () => {
  const g = new DoubleShiftGesture(); assert.equal(g.release(true, true, 0), false);
  tap(g, 0); assert.equal(tap(g, 500), true);
  tap(g, 1000); assert.equal(tap(g, 1530), false);
  g.reset(); tap(g, 100); assert.equal(tap(g, 0), false);
});
