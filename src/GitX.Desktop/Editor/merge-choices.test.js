import test from 'node:test';
import assert from 'node:assert/strict';
import { initialChoice, resolvedChoice, canApplyAutomatically, decide } from './merge-choices.js';
const conflict = { isConflict:true, leftText:'ours\n', rightText:'theirs\n' };
test('accepting one conflict side leaves the other pending; ignoring it preserves the accepted text', () => {
  const left = decide(conflict, initialChoice(conflict), 'left');
  assert.equal(left.text, 'ours\n'); assert.equal(resolvedChoice(left.choice), false);
  const ignored = decide(conflict, left.choice, 'ignore-right');
  assert.equal(ignored.text, undefined); assert.equal(resolvedChoice(ignored.choice), true);
  assert.equal(decide(conflict, ignored.choice, 'right'), null);
});
test('whole-block resolution and accepting both have distinct outcomes', () => {
  const left = decide(conflict, initialChoice(conflict), 'resolve-left');
  assert.equal(resolvedChoice(left.choice), true); assert.equal(left.choice.right, 'ignored');
  const right = decide(conflict, initialChoice(conflict), 'right');
  assert.equal(decide(conflict, right.choice, 'left').text, 'ours\ntheirs\n');
  assert.equal(decide({...conflict,leftText:'',rightText:'no newline'},initialChoice(conflict),'both').text,'no newline');
});
test('automatic clean changes are incremental and skip explicit decisions', () => {
  const clean = {isConflict:false,leftChanged:false,rightChanged:true,leftText:'base',rightText:'incoming',automaticText:'incoming'};
  const initial = initialChoice(clean);
  assert.equal(canApplyAutomatically(clean, initial), true);
  const accepted = decide(clean, initial, 'automatic');
  assert.equal(accepted.text, 'incoming'); assert.equal(resolvedChoice(accepted.choice), true);
  assert.equal(decide(clean, accepted.choice, 'automatic'), null);
  const ignored = decide(clean, initial, 'ignore-right');
  assert.equal(decide(clean, ignored.choice, 'automatic'), null);
  assert.equal(decide(conflict, initialChoice(conflict), 'automatic'), null);
  assert.equal(canApplyAutomatically({...clean,baseText:'base'}, initial, 'manual edit'), false);
  assert.equal(decide(clean, initial, 'both').text, 'incoming');
  assert.equal(decide({...clean,leftChanged:true,rightChanged:false,leftText:'ours',rightText:'base'},initialChoice({...clean,leftChanged:true,rightChanged:false}),'both').text,'ours');
});
