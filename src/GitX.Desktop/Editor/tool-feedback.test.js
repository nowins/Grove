import test from 'node:test';
import assert from 'node:assert/strict';
import { ToolFeedback } from './tool-feedback.js';

test('a fast document read never flashes a busy message', t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const element = { hidden: true }, feedback = new ToolFeedback(element);
  feedback.begin(1); t.mock.timers.tick(100); feedback.end(1); t.mock.timers.tick(500);
  assert.equal(element.hidden, true);
});

test('an old document completion cannot dismiss the current slow read', t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const element = { hidden: true }, feedback = new ToolFeedback(element);
  feedback.begin(1); t.mock.timers.tick(100); feedback.begin(2); feedback.end(1);
  t.mock.timers.tick(150);
  assert.equal(element.hidden, false); assert.equal(element.textContent, '正在读取…');
  feedback.end(2); assert.equal(element.hidden, true);
});

test('a new read clears old action feedback and cancels its dismissal timer', t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const element = { hidden: true }, feedback = new ToolFeedback(element);
  feedback.show('按行高亮'); t.mock.timers.tick(1900); feedback.begin(3);
  assert.equal(element.hidden, true);
  t.mock.timers.tick(150); t.mock.timers.tick(2000);
  assert.equal(element.hidden, false); assert.equal(element.textContent, '正在读取…');
});
