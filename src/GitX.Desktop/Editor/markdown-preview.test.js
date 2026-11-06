import test from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import { renderMarkdown, markdownLink, MarkdownPreview } from './markdown-preview.js';

function render(text, path = 'docs/guide.md', version = 7) {
  const window = new JSDOM('<!doctype html><html><body></body></html>', { url: 'http://127.0.0.1:1234/capability/index.html' }).window;
  const content = window.document.createElement('article'); content.append(renderMarkdown(text, { window, path, version }));
  return { window, content };
}

test('common Markdown, Chinese headings, duplicate anchors and fenced code render as readable content', () => {
  const { content, window } = render('# 安装 Grove\n\n## 安装 Grove\n\n**粗体** *斜体* ~~删除~~ `inline`\n\n> quote\n\n- [x] done\n- [ ] todo\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n```ts\nconst tag = "<img onerror=bad()>";\n```');
  assert.equal(content.querySelector('h1').id, 'md-安装-grove'); assert.equal(content.querySelector('h2').id, 'md-安装-grove-1');
  assert.equal(content.querySelectorAll('strong,em,del').length, 3); assert.equal(content.querySelectorAll('table tbody tr').length, 1);
  assert.equal(content.querySelector('pre code').textContent, 'const tag = "<img onerror=bad()>";\n');
  const boxes = [...content.querySelectorAll('input')]; assert.equal(boxes.length, 2); assert.ok(boxes.every(box => box.disabled && box.type === 'checkbox')); assert.ok(boxes[0].checked);
  window.close();
});

test('raw HTML is inert text, including bridge calls, DOM clobbering, CSS, SVG and resource attributes', () => {
  const attack = '<script>window.invokeCSharpAction("bad")</script>\n<img src="https://tracker.invalid/x" onerror="bad()"><iframe srcdoc="<script>bad()</script>"></iframe>\n<form id="workspace"><input name="invokeCSharpAction"></form><style>body{display:none}</style><svg onload="bad()"></svg>';
  const { content, window } = render(attack);
  assert.equal(content.querySelectorAll('script,img,iframe,form,style,svg,[id=workspace],[name],[onload],[onerror],[srcdoc]').length, 0);
  assert.ok(content.textContent.includes('window.invokeCSharpAction')); window.close();
});

test('unsafe and encoded links cannot produce executable or filesystem navigation', () => {
  const hrefs = ['javascript:alert(1)', 'data:text/html,bad', 'file:///C:/secret', '//tracker.invalid', '/absolute', '..%2F..%2Fsecret', '%2e%2e/%2e%2e/secret', 'a%5Cb', 'a%00b', '../.git/config', 'https://user:password@example.invalid', 'mailto:a@example.invalid', 'bad%xx', 'https:example.invalid', 'https://example.invalid/\u0085hidden', ' https://example.invalid', 'https://example.invalid '];
  for (const href of hrefs) assert.equal(markdownLink('docs/guide.md', href), null, href);
  const { content, window } = render('[bad](javascript:alert%281%29) [file](file:///secret) [safe](https://example.invalid/guide)');
  assert.equal(content.querySelectorAll('a').length, 1); assert.equal(content.querySelector('a').getAttribute('href'), '#');
  assert.equal(content.querySelector('a').dataset.mdLink, 'https://example.invalid/guide'); window.close();
});

test('local resources resolve from document directory; assets carry the immutable published version', () => {
  assert.deepEqual(markdownLink('docs/guide.md', '../README.md#使用'), { kind: 'local', path: 'README.md', fragment: '使用', href: '../README.md#使用' });
  assert.equal(markdownLink('docs/guide.md', '%E4%B8%AD%E6%96%87.md').path, 'docs/中文.md');
  const { content, window } = render('![local](images/a.png) ![remote](https://tracker.invalid/a.png) ![vector](diagram.svg) ![escape](../../secret.png)');
  const images = [...content.querySelectorAll('img')]; assert.equal(images.length, 1);
  assert.equal(images[0].getAttribute('src'), 'markdown-image?version=7&href=images%2Fa.png');
  assert.equal(content.querySelectorAll('.markdown-image-unavailable').length, 3); assert.ok(!content.innerHTML.includes('src="https:')); window.close();
});

test('preview/source mode and scroll survive tab changes, search reveals source, and loading blocks stale links', () => {
  const window = new JSDOM('<div id="markdown-mode"><button id="markdown-source"></button><button id="markdown-rendered"></button></div><section id="markdown-preview"><article id="markdown-content"></article></section><div id="workspace"></div>').window;
  const previous = { window: globalThis.window, document: globalThis.document };
  globalThis.window = window; globalThis.document = window.document;
  try {
    let focused = 0; const sent = [], preview = new MarkdownPreview({ send: value => sent.push(value), getVersion: () => 7, onSource: () => focused++ });
    const a = { path: 'a.md', language: 'markdown', text: '# A\n\n[next](b.md)' }, b = { path: 'b.md', language: 'markdown', text: '# B' };
    preview.update(a, 7, 'a'); assert.ok(preview.active); preview.host.scrollTop = 120;
    preview.update(b, 8, 'b'); preview.update(a, 9, 'a'); assert.equal(preview.host.scrollTop, 120);
    preview.sourceButton.click(); assert.ok(!preview.active); assert.equal(focused, 1); assert.ok(preview.host.hidden); assert.ok(!window.document.getElementById('workspace').hidden);
    preview.update(b, 10, 'b'); preview.update(a, 11, 'a'); assert.ok(!preview.active);
    preview.previewButton.click(); preview.setLoading(true); preview.content.querySelector('a').click(); assert.equal(sent.length, 0);
    preview.setLoading(false); preview.content.querySelector('a').click(); assert.deepEqual(sent[0], { type: 'markdown-open', version: 7, href: 'b.md' });
    preview.update({ path: 'b.md', language: 'plaintext', isNotice: true }, 12, 'b'); assert.equal(preview.pending.path, 'b.md');
    preview.update(b, 12, 'b'); assert.ok(preview.active);
    preview.update({ ...a, revealLine: 2 }, 13, 'a'); assert.ok(!preview.active);
    preview.update({ ...a, isDiff: true }, 14); assert.ok(preview.controls.hidden); assert.ok(!preview.active);
  } finally { globalThis.window = previous.window; globalThis.document = previous.document; window.close(); }
});

test('source updates defer parsing until preview, using only the latest text and resource version', () => {
  const window = new JSDOM('<div id="markdown-mode"><button id="markdown-source"></button><button id="markdown-rendered"></button></div><section id="markdown-preview"><article id="markdown-content"></article></section><div id="workspace"></div>').window;
  const previous = { window: globalThis.window, document: globalThis.document };
  globalThis.window = window; globalThis.document = window.document;
  try {
    const preview = new MarkdownPreview({ send() {}, getVersion: () => 57, onSource() {} });
    preview.update({ path: 'a.md', language: 'markdown', text: '# Original' }, 7, 'a');
    preview.host.scrollTop = 120; preview.sourceButton.click();
    // Browsers clamp scrollTop when the hidden DOM has been cleared.
    preview.host.scrollTop = 0;
    let reads = 0;
    for (let version = 8; version <= 57; version++) preview.update({ path: 'a.md', language: 'markdown',
      get text() { reads++; return `# Draft ${version}\n\n![local](a.png)`; } }, version, 'a');
    assert.equal(reads, 0); assert.equal(preview.content.childNodes.length, 0);
    assert.equal(preview.active, false);
    preview.previewButton.click();
    assert.equal(preview.host.scrollTop, 120);
    assert.equal(reads, 1); assert.equal(preview.content.querySelector('h1').textContent, 'Draft 57');
    assert.equal(preview.content.querySelector('img').getAttribute('src'), 'markdown-image?version=57&href=a.png');
    const heading = preview.content.querySelector('h1');
    preview.sourceButton.click(); preview.previewButton.click();
    assert.equal(reads, 1); assert.equal(preview.content.querySelector('h1'), heading);
    preview.update({ path: 'b.md', language: 'markdown', text: '# B', revealLine: 1 }, 58, 'b');
    assert.equal(preview.content.childNodes.length, 0); assert.equal(preview.active, false);
    preview.previewButton.click(); assert.equal(preview.content.querySelector('h1').textContent, 'B');
    preview.update({ path: 'notice', language: 'plaintext', isNotice: true }, 59);
    preview.activate(true); assert.equal(preview.content.childNodes.length, 0); assert.equal(preview.active, false);
  } finally { globalThis.window = previous.window; globalThis.document = previous.document; window.close(); }
});
