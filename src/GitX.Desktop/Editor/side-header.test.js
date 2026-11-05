import test from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import { sideHeader } from './side-header.js';

for (const id of ['old-label', 'new-label']) test(`${id} preserves filename, full path, revision and format as separate identities`, () => {
  const dom = new JSDOM(`<span id="${id}"></span><span id="${id}-path"></span><span id="${id === 'old-label' ? 'old-format' : 'new-format'}"></span>`);
  globalThis.document = dom.window.document;
  try {
    const side = { exists: true, text: 'text', label: 'abcdef12', path: '目录/very/deep/same.ts', lineEnding: 'CRLF', endsWithNewline: false };
    sideHeader(id, side, { ...side, lineEnding: 'LF' });
    assert.equal(document.querySelector('.side-revision').textContent, 'abcdef12');
    assert.equal(document.querySelector('.side-filename').textContent, 'same.ts');
    assert.equal(document.querySelector('.side-directory').textContent, '目录/very/deep/');
    assert.equal(document.getElementById(`${id}-path`).title, side.path);
    assert.match(document.getElementById(id).title, /末尾无换行/);
    assert.equal(document.querySelector('.attention').textContent, 'CRLF');
    sideHeader(id, { ...side, exists: false, path: 'same.ts', label: '父提交' }, side);
    assert.equal(document.querySelector('.side-directory').textContent, '');
    assert.equal(document.querySelector('.side-filename').textContent, 'same.ts');
    assert.equal(document.querySelector('.side-revision').textContent, '父提交');
    assert.equal(document.querySelector('.attention'), null);
  } finally { delete globalThis.document; dom.window.close(); }
});
