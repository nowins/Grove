import { Marked } from 'marked';
import createDOMPurify from 'dompurify';

const escape = value => String(value ?? '').replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);

export function markdownLink(documentPath, href) {
  if (typeof href !== 'string' || href.length > 4096 || href !== href.trim() || /[\u0000-\u001f\u007f-\u009f\\]/.test(href)) return null;
  if (/^https?:/i.test(href)) {
    if (!/^https?:\/\//i.test(href)) return null;
    try { const url = new URL(href); return url.username || url.password ? null : { kind: 'external', href: url.href }; } catch { return null; }
  }
  try {
    const hash = href.indexOf('#'), rawPath = hash < 0 ? href : href.slice(0, hash);
    const path = decodeURIComponent(rawPath), fragment = hash < 0 ? '' : decodeURIComponent(href.slice(hash + 1));
    if (/^[\/]|[:?\\\u0000-\u001f\u007f-\u009f]/.test(path) || /[\u0000-\u001f\u007f-\u009f]/.test(fragment)) return null;
    const parts = [];
    for (const part of (path ? documentPath.slice(0, documentPath.lastIndexOf('/') + 1) + path : documentPath).split('/')) {
      if (!part || part === '.') continue;
      if (part === '..') { if (!parts.length) return null; parts.pop(); }
      else {
        if (/^\.git$/i.test(part) || /[. ]$|[:\\<>"|?*]/.test(part) || /^(?:con|prn|aux|nul|com\d|lpt\d)(?:\.|$)/i.test(part)) return null;
        parts.push(part);
      }
    }
    return parts.length ? { kind: 'local', path: parts.join('/'), fragment, href } : null;
  } catch { return null; }
}

export function renderMarkdown(text, { window, path, version }) {
  const parser = new Marked({ gfm: true, breaks: false, renderer: {
    // Raw HTML is displayed as text; no source-controlled HTML ever gains
    // access to the editor bridge, CSS, forms, or network resource attributes.
    html({ text }) { return escape(text); },
    link({ href, title, tokens }) { return `<a data-md-link="${escape(href)}"${title ? ` title="${escape(title)}"` : ''}>${this.parser.parseInline(tokens)}</a>`; },
    image({ href, text }) { return `<span data-md-image="${escape(href)}">${escape(text)}</span>`; }
  } });
  const purify = createDOMPurify(window);
  const fragment = purify.sanitize(parser.parse(text.replace(/^\uFEFF/, '')), {
    RETURN_DOM_FRAGMENT: true, ALLOW_DATA_ATTR: false, ALLOW_ARIA_ATTR: false,
    ALLOWED_TAGS: ['p', 'br', 'hr', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'ul', 'ol', 'li', 'blockquote', 'pre', 'code', 'em', 'strong', 'del', 'a', 'span', 'table', 'thead', 'tbody', 'tr', 'th', 'td', 'input'],
    ALLOWED_ATTR: ['title', 'start', 'align', 'type', 'checked', 'disabled', 'data-md-link', 'data-md-image']
  });
  const counts = new Map();
  for (const heading of fragment.querySelectorAll('h1,h2,h3,h4,h5,h6')) {
    const slug = heading.textContent.normalize('NFKC').toLowerCase().replace(/[^\p{L}\p{N}\p{M}\s_-]/gu, '').trim().replace(/\s/g, '-');
    const count = counts.get(slug) ?? 0; counts.set(slug, count + 1);
    heading.id = `md-${slug}${count ? `-${count}` : ''}`; heading.tabIndex = -1;
  }
  for (const input of fragment.querySelectorAll('input')) { input.type = 'checkbox'; input.disabled = true; input.tabIndex = -1; }
  for (const anchor of fragment.querySelectorAll('a')) {
    const link = markdownLink(path, anchor.dataset.mdLink);
    if (!link) { anchor.replaceWith(...anchor.childNodes); continue; }
    anchor.href = '#'; anchor.title ||= link.kind === 'external' ? `在浏览器中打开：${link.href}` : link.href;
  }
  for (const placeholder of fragment.querySelectorAll('[data-md-image]')) {
    const href = placeholder.dataset.mdImage, link = markdownLink(path, href), alt = placeholder.textContent;
    if (link?.kind !== 'local' || link.fragment || !/\.(png|jpe?g|gif|webp)$/i.test(link.path)) {
      placeholder.removeAttribute('data-md-image'); placeholder.className = 'markdown-image-unavailable';
      placeholder.textContent = `[图片未加载${alt ? `：${alt}` : ''}]`; placeholder.title = '只加载仓库内 PNG、JPEG、GIF 或 WebP；远程图片不会自动请求。'; continue;
    }
    const image = window.document.createElement('img'); image.alt = alt; image.loading = 'lazy'; image.decoding = 'async';
    image.src = `markdown-image?version=${version}&href=${encodeURIComponent(href)}`;
    image.addEventListener('error', () => { placeholder.className = 'markdown-image-unavailable'; placeholder.textContent = `[图片不可用${alt ? `：${alt}` : ''}]`; }, { once: true });
    placeholder.removeAttribute('data-md-image'); placeholder.replaceChildren(image);
  }
  return fragment;
}

export class MarkdownPreview {
  constructor({ send, getVersion, onSource }) {
    Object.assign(this, { send, getVersion, onSource }); this.states = new Map();
    this.host = document.getElementById('markdown-preview'); this.content = document.getElementById('markdown-content');
    this.controls = document.getElementById('markdown-mode'); this.sourceButton = document.getElementById('markdown-source'); this.previewButton = document.getElementById('markdown-rendered');
    this.sourceButton.addEventListener('click', () => this.activate(false, true)); this.previewButton.addEventListener('click', () => this.activate(true, true));
    this.host.addEventListener('keydown', event => { if (event.key === 'Escape') { event.preventDefault(); this.activate(false, true); } });
    this.content.addEventListener('click', event => {
      const anchor = event.target.closest('a[data-md-link]'); if (!anchor) return;
      event.preventDefault(); if (this.loading || event.button !== 0) return;
      const link = markdownLink(this.doc.path, anchor.dataset.mdLink); if (!link) return;
      if (link.kind === 'local' && link.path === this.doc.path && link.fragment) { this.reveal(link.fragment); return; }
      if (link.kind === 'local') this.pending = { ...link, revision: this.doc.sourceRevision ?? null };
      this.send({ type: 'markdown-open', version: this.getVersion(), href: link.href });
    });
  }
  remember() {
    if (!this.key) return;
    this.states.delete(this.key); this.states.set(this.key, this.capture());
    if (this.states.size > 32) this.states.delete(this.states.keys().next().value);
  }
  update(doc, version, tabId = null) {
    this.remember(); this.doc = doc; this.loading = false;
    this.enabled = doc.language === 'markdown' && !doc.isDiff && !doc.isNotice;
    this.key = this.enabled ? JSON.stringify([tabId, doc.path, doc.sourceRevision ?? null]) : null;
    this.controls.hidden = !this.enabled;
    const state = this.states.get(this.key), pending = this.enabled && this.pending?.path === doc.path && this.pending.revision === (doc.sourceRevision ?? null) ? this.pending : null;
    this.content.replaceChildren();
    if (this.enabled) this.content.append(renderMarkdown(doc.text, { window, path: doc.path, version }));
    this.activate(this.enabled && (pending || !doc.revealLine && state?.active !== false), false);
    this.host.scrollTop = state?.scrollTop ?? 0;
    if (pending) { this.pending = null; if (pending.fragment) this.reveal(pending.fragment); else this.host.focus({ preventScroll: true }); }
    else if (!doc.isNotice && this.pending?.path === doc.path) this.pending = null;
  }
  setLoading(value) { this.loading = value; this.host.setAttribute('aria-busy', String(value)); this.sourceButton.disabled = this.previewButton.disabled = value; }
  activate(value, focus = false) {
    this.active = !!value && this.enabled;
    this.host.hidden = !this.active; document.getElementById('workspace').hidden = this.active || !!this.doc?.isNotice;
    document.body.classList.toggle('markdown-preview-mode', this.active);
    this.sourceButton.setAttribute('aria-pressed', String(!this.active)); this.previewButton.setAttribute('aria-pressed', String(this.active));
    if (focus) { if (this.active) this.host.focus(); else this.onSource(); }
  }
  reveal(fragment) {
    const heading = document.getElementById(`md-${fragment}`);
    if (heading && this.content.contains(heading)) { heading.scrollIntoView({ block: 'start' }); heading.focus({ preventScroll: true }); }
  }
  capture() { return { active: !!this.active, scrollTop: this.host.scrollTop }; }
  restore(state) { if (state && this.enabled) { this.activate(!!state.active); this.host.scrollTop = Math.max(0, Number(state.scrollTop) || 0); } }
}
