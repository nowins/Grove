import test from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import { WorkspaceNotice } from './workspace-notice.js';

function fixture(mac = false) {
  const dom = new JSDOM('<!doctype html><div id="notice"></div><div id="tools"></div>');
  globalThis.document = dom.window.document;
  const state = { version: 7, ready: true }, sent = [];
  const host = document.getElementById('notice'), tools = document.getElementById('tools');
  const view = new WorkspaceNotice({ host, tools, isMac: mac, getVersion: () => state.version, isReady: () => state.ready, send: message => sent.push(message) });
  return { host, tools, view, sent, state, close() { dom.window.close(); delete globalThis.document; } };
}

for (const mac of [false, true]) test(`${mac ? 'Mac' : 'Windows'} empty workspace has real search and recent actions with current publication identity`, () => {
  const t = fixture(mac);
  try {
    t.view.update({ path: '', isNotice: true, text: '选择文件查看内容。' }, { workspaceActions: true, emptyWorkspace: true });
    assert.equal(t.tools.hidden, true); assert.equal(t.host.hidden, false);
    assert.equal(t.host.querySelectorAll('[role=tab]').length, 0);
    const buttons = t.host.querySelectorAll('button');
    assert.equal(buttons.length, 3); assert.equal(t.host.querySelector('p'), null);
    assert.equal(buttons[2].querySelector('kbd').textContent, mac ? '⌘E' : 'Ctrl+E');
    t.view.focus(); assert.equal(document.activeElement, t.host);
    buttons[0].click(); buttons[1].click(); buttons[2].click();
    assert.deepEqual(t.sent, [{ type: 'code-search', filesOnly: true, quick: true, version: 7 }, { type: 'code-search', filesOnly: false, version: 7 }, { type: 'recent-files', version: 7 }]);
  } finally { t.close(); }
});

test('loading, old publication and retired action elements cannot send workspace commands', () => {
  const t = fixture();
  try {
    const doc = { path: '', isNotice: true, text: '选择文件' }, toolbar = { workspaceActions: true };
    t.view.update(doc, toolbar); const old = t.host.querySelector('button');
    t.state.ready = false; old.click(); t.state.ready = true;
    t.state.version++; old.click(); t.view.update(doc, toolbar); old.click();
    assert.deepEqual(t.sent, []);
    t.host.querySelector('button').click(); assert.equal(t.sent[0].version, 8);
    t.view.update({ path: 'actual.txt', text: 'code' }, toolbar);
    assert.equal(t.host.hidden, true); assert.equal(t.tools.hidden, false);
    assert.equal(t.host.children.length, 0);
  } finally { t.close(); }
});

test('standalone notices have no unsupported workspace actions and file notices retain file tools', () => {
  const t = fixture();
  try {
    t.view.update({ path: '', isNotice: true, text: '<img src=x onerror=alert(1)>' }, {});
    assert.equal(t.host.querySelectorAll('button, img').length, 0);
    assert.equal(t.host.textContent, '<img src=x onerror=alert(1)>');
    t.view.update({ path: 'image.png', isNotice: true, text: '二进制文件' }, { workspaceActions: true, emptyWorkspace: true });
    assert.equal(t.tools.hidden, false); assert.equal(t.host.querySelectorAll('button').length, 0);
    assert.equal(t.host.classList.contains('workspace-empty'), false);
    assert.equal(t.host.textContent, '二进制文件');
  } finally { t.close(); }
});

test('continuous Diff uses its own view without an empty workspace or notice', () => {
  const t = fixture();
  try {
    t.view.update({ path: '', isNotice: true, text: '连续 Diff' }, { workspaceActions: true, continuousDiff: { id: 'scope' } });
    assert.equal(t.view.active, false); assert.equal(t.host.hidden, true); assert.equal(t.tools.hidden, true);
    assert.equal(t.host.children.length, 0);
  } finally { t.close(); }
});

test('actual notices remain visible instead of being mistaken for the neutral empty state', () => {
  const t = fixture();
  try {
    t.view.update({ path: '', isNotice: true, text: '仓库已刷新，请重新打开连续 Diff。' }, { workspaceActions: true });
    assert.equal(t.host.querySelector('p').textContent, '仓库已刷新，请重新打开连续 Diff。');
  } finally { t.close(); }
});
