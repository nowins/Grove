const rowHeight = 22, maxFiles = 8, maxBytes = 16 * 1024 * 1024;
export function rowWindow(count, top, height) {
  const start = Math.max(0, Math.min(count, Math.floor(top / rowHeight) - 8));
  return { start, end: Math.min(count, start + Math.ceil(height / rowHeight) + 16) };
}
export function fileBytes(file) {
  return file.rows.reduce((sum, row) => sum + 64 + (row.oldText.length + row.newText.length) * 2, 0);
}
function node(tag, className, text) {
  const element = document.createElement(tag); element.className = className;
  if (text !== undefined) element.textContent = text; return element;
}

// A single scroll surface. Only captured file indices are sent to the server;
// no Monaco model, editor command, save token or comment anchor is created here.
export class ContinuousDiff {
  constructor({ host, send, retryDelay = 1000, fetchFile = (manifest, index, signal) => fetch(
    `continuous-diff?id=${encodeURIComponent(manifest.id)}&version=${manifest.version}&index=${index}`, { signal, cache: 'no-store' }) }) {
    Object.assign(this, { host, send, fetchFile, retryDelay }); this.epoch = 0; this.scrollEpoch = 0; this.active = 0; this.pending = new Set(); this.timers = new Set();
    this.scroll = () => {
      if (this.host.scrollTop !== this.ownScrollTop) this.restoreAnchor = null;
      this.ownScrollTop = this.host.scrollTop; this.scrollEpoch++; this.schedule();
    };
    host.addEventListener('scroll', this.scroll, { passive: true });
    this.resize = new ResizeObserver(this.scroll); this.resize.observe(host);
    this.unload = () => this.close(); window.addEventListener('beforeunload', this.unload);
  }
  show(manifest) {
    if (this.manifest?.id === manifest?.id) { this.schedule(); return; }
    const preserving = manifest?.scope && manifest.scope === this.manifest?.scope;
    const saved = preserving ? this.restoreAnchor ?? this.captureAnchor() : null;
    const anchor = saved ? { ...saved, placed: false } : null;
    const heights = new Map(preserving ? this.items.map(item => [item.entry.path, item.count]) : []);
    this.close(); if (!manifest) return;
    this.manifest = manifest; this.host.hidden = false; document.body.classList.add('continuous-diff-mode');
    const toolbar = node('div', 'cd-toolbar');
    toolbar.append(node('strong', '', '连续 Diff'), node('span', '', `${manifest.title} · ${manifest.whitespace} · 按行高亮 · 只读`));
    const range = node('span', 'cd-range', manifest.files.length ? `${manifest.offset + 1}–${manifest.offset + manifest.files.length} / ${manifest.totalFiles} 个文件` : '0 个文件');
    toolbar.append(range);
    const page = (text, offset, disabled) => {
      const button = node('button', '', text); button.disabled = disabled;
      button.addEventListener('click', () => { if (this.manifest === manifest) this.send({ type: 'continuous-diff', action: 'page', id: manifest.id, offset, version: manifest.version }); }); return button;
    };
    toolbar.append(page('上一页文件', Math.max(0, manifest.offset - 200), manifest.offset === 0),
      page('下一页文件', manifest.offset + 200, manifest.offset + manifest.files.length >= manifest.totalFiles));
    const picker = node('select', 'cd-picker'); picker.setAttribute('aria-label', '定位连续 Diff 文件');
    manifest.files.forEach(entry => { const option = node('option', '', entry.path); option.value = entry.index; picker.append(option); });
    picker.addEventListener('change', () => {
      if (this.manifest !== manifest) return;
      this.restoreAnchor = null;
      this.items.find(item => item.entry.index === Number(picker.value))?.section.scrollIntoView({ block: 'start' });
    });
    toolbar.append(picker);
    this.host.append(toolbar);
    this.items = manifest.files.map(entry => {
      const section = node('section', 'cd-file'); section.dataset.index = entry.index;
      const header = node('header', 'cd-file-header');
      const path = node('span', 'cd-path', entry.oldPath && entry.oldPath !== entry.path ? `${entry.oldPath} → ${entry.path}` : entry.path);
      path.title = path.textContent;
      const open = node('button', '', '打开完整 Diff'); open.setAttribute('aria-label', `打开 ${entry.path} 的完整 Diff`);
      const item = { entry, section, header, data: null, count: heights.get(entry.path) ?? 0, bytes: 0, range: '', evictedAt: -1, error: '', retries: 0, retryAt: 0 };
      open.addEventListener('click', () => this.open(item, item.data?.new?.exists === false ? 0 : 1, 1));
      header.append(path, node('span', 'cd-status', entry.status), open);
      const sides = node('div', 'cd-sides'); const body = node('div', 'cd-body'); const rows = node('div', 'cd-rows');
      rows.style.height = `${Math.max(5, item.count) * rowHeight}px`;
      body.append(rows); item.body = body; item.rows = rows; item.sides = sides;
      const footer = node('div', 'cd-footer'); const message = node('span', '', '按需读取磁盘或固定修订；不含未保存编辑。');
      const retry = node('button', '', '重新读取'); retry.addEventListener('click', () => {
        if (this.manifest !== manifest || !this.items.includes(item)) return;
        item.controller?.abort(); item.controller = null;
        if (item.retryTimer) { clearTimeout(item.retryTimer); this.timers.delete(item.retryTimer); item.retryTimer = null; }
        // Preserve scroll geometry, but release the previous capture and its
        // line links before a replacement can fail or finish out of order.
        item.rows.replaceChildren(); item.sides.replaceChildren(); item.range = ''; item.message.textContent = '等待重新读取…';
        item.data = null; item.bytes = 0; item.error = ''; item.evictedAt = -1; item.retries = 0; item.retryAt = 0; this.scrollEpoch++; this.schedule();
      }); footer.append(message, retry); item.message = message;
      section.append(header, sides, body, footer); this.host.append(section); return item;
    });
    if (!this.items.length) this.host.append(node('p', 'cd-empty', '当前比较范围没有文件差异。'));
    this.restoreAnchor = anchor;
    this.moveScroll(anchor ? this.host.scrollTop : 0); this.schedule();
  }
  open(item, side, line) {
    if (!this.manifest || !this.items.includes(item)) return;
    this.send({ type: 'continuous-diff', action: 'file', id: this.manifest.id, index: item.entry.index,
      side, line, version: this.manifest.version });
  }
  focus() { this.host.focus(); }
  readingTop() { return this.host.getBoundingClientRect().top + this.host.firstElementChild.getBoundingClientRect().height + 34; }
  captureAnchor() {
    if (!this.manifest || !this.items.length) return null;
    const top = this.readingTop();
    const item = this.items.find(item => item.section.getBoundingClientRect().bottom > top) ?? this.items.at(-1);
    const row = Math.max(0, Math.min(item.count - 1, Math.floor((top - item.rows.getBoundingClientRect().top) / rowHeight)));
    const value = item.data?.rows[row];
    const side = value?.oldLine ? 0 : 1, line = side === 0 ? value?.oldLine : value?.newLine;
    const index = this.items.indexOf(item);
    return { path: item.entry.path, index: item.entry.index, side, line,
      rowOffset: item.rows.getBoundingClientRect().top + row * rowHeight - top,
      fileOffset: item.section.getBoundingClientRect().top - top,
      fallback: [...this.items.slice(index + 1), ...this.items.slice(0, index).reverse()].map(item => item.entry.path) };
  }
  moveScroll(top) { this.host.scrollTop = top; this.ownScrollTop = this.host.scrollTop; }
  restoreView() {
    const anchor = this.restoreAnchor; if (!anchor) return;
    let item = this.items.find(item => item.entry.path === anchor.path);
    if (!item) {
      item = anchor.fallback.map(path => this.items.find(item => item.entry.path === path)).find(Boolean);
      if (!item) { this.restoreAnchor = null; return; }
      anchor.path = item.entry.path; anchor.line = null; anchor.fileOffset = 0;
    }
    const top = this.readingTop();
    if (!anchor.placed) {
      this.moveScroll(this.host.scrollTop + item.section.getBoundingClientRect().top - top - anchor.fileOffset);
      anchor.placed = true;
    }
    if (!item.data) return;
    if (anchor.line) {
      const field = anchor.side === 0 ? 'oldLine' : 'newLine';
      let row = item.data.rows.findIndex(value => value[field] >= anchor.line);
      if (row < 0) row = Math.max(0, item.data.rows.length - 1);
      this.moveScroll(this.host.scrollTop + item.rows.getBoundingClientRect().top + row * rowHeight - top - anchor.rowOffset);
    }
    this.restoreAnchor = null;
  }
  schedule() {
    if (!this.manifest || this.frame) return;
    this.frame = requestAnimationFrame(() => { this.frame = null; this.update(); });
  }
  anchor(action) {
    const top = this.readingTop();
    const anchor = this.items.find(item => item.section.getBoundingClientRect().bottom > top);
    const before = anchor?.section.getBoundingClientRect().top;
    action();
    if (anchor) this.moveScroll(this.host.scrollTop + anchor.section.getBoundingClientRect().top - before);
  }
  update() {
    if (!this.manifest) return;
    this.restoreView();
    this.host.style.setProperty('--cd-toolbar-height', `${this.host.firstElementChild.getBoundingClientRect().height}px`);
    const viewport = this.host.getBoundingClientRect();
    const rectangles = new Map(this.items.map(item => [item, item.section.getBoundingClientRect()]));
    const distance = item => {
      const rect = rectangles.get(item);
      return Math.max(viewport.top - rect.bottom, rect.top - viewport.bottom, 0);
    };
    const candidates = this.items.filter(item => distance(item) < 250).sort((a, b) => distance(a) - distance(b)).slice(0, maxFiles);
    const wanted = new Set(candidates);
    for (const item of this.items) {
      if (!wanted.has(item) && item.rows.childElementCount) item.rows.replaceChildren();
      if (!wanted.has(item) && item.controller) item.controller.abort();
      if (!item.rows.childElementCount) item.range = '';
    }
    for (const item of candidates) {
      if (!item.data && !item.error && !item.controller && item.evictedAt !== this.scrollEpoch && performance.now() >= item.retryAt && this.active < 2) this.read(item);
      this.render(item, viewport);
    }
    let cached = this.items.filter(item => item.data);
    let bytes = cached.reduce((sum, item) => sum + item.bytes, 0);
    cached.sort((a, b) => distance(b) - distance(a));
    while (cached.length > maxFiles || bytes > maxBytes && cached.length > 1) {
      const item = cached.shift(); bytes -= item.bytes; item.data = null; item.bytes = 0;
      item.rows.replaceChildren(); item.range = ''; item.evictedAt = this.scrollEpoch;
      item.message.textContent = '内容已释放；滚动到此处或选择“重新读取”。';
    }
  }
  read(item) {
    const owner = this.manifest, epoch = this.epoch, controller = new AbortController();
    this.active++; this.pending.add(controller); item.controller = controller; item.message.textContent = '正在读取…';
    Promise.resolve().then(() => this.fetchFile(owner, item.entry.index, controller.signal)).then(async response => {
      if (controller.signal.aborted || epoch !== this.epoch || this.manifest !== owner) return;
      if (response.status === 429 && item.retries++ < 5) {
        const seconds = Number(response.headers?.get('Retry-After'));
        const delay = Number.isFinite(seconds) && seconds > 0 ? Math.min(5000, seconds * 1000) : this.retryDelay;
        item.retryAt = performance.now() + delay; item.message.textContent = '读取繁忙，稍后自动重试…';
        const timer = setTimeout(() => { this.timers.delete(timer); if (item.retryTimer === timer) item.retryTimer = null; if (epoch === this.epoch) this.schedule(); }, delay);
        item.retryTimer = timer; this.timers.add(timer); return;
      }
      if (!response.ok) throw new Error(response.status === 404 ? '比较范围已失效，请重新打开连续 Diff。' : '读取失败，请重试。');
      const data = await response.json();
      if (controller.signal.aborted || epoch !== this.epoch || this.manifest !== owner) return;
      if (data.id !== owner.id || data.index !== item.entry.index || !Array.isArray(data.rows)) throw new Error('文件响应与当前比较范围不符。');
      this.anchor(() => {
        item.data = data; item.bytes = fileBytes(data); item.count = data.rows.length; item.range = ''; item.retries = 0; item.retryAt = 0;
        item.rows.style.height = `${Math.max(5, item.count) * rowHeight}px`;
        item.sides.replaceChildren(...[data.old, data.new].map(side => node('span', '', side ?
          `${side.label} · ${side.path} · ${side.format}${side.exists ? side.endsWithNewline ? ' · 末尾有换行' : ' · 末尾无换行' : ''}` : '无法显示文本差异')));
        item.message.textContent = [data.notice, data.truncated ? '本文件显示前 6,000 行；完整内容请打开完整 Diff。' : '行号可双击定位；不含未保存编辑。'].filter(Boolean).join(' ');
      });
      this.restoreView();
    }).catch(error => {
      if (!controller.signal.aborted && epoch === this.epoch) { item.error = String(error.message || error); item.message.textContent = item.error; }
    }).finally(() => {
      this.pending.delete(controller);
      if (epoch !== this.epoch) return;
      this.active--; if (item.controller === controller) item.controller = null; this.schedule();
    });
  }
  render(item, viewport) {
    if (!item.data) return;
    const rect = item.rows.getBoundingClientRect();
    const { start, end } = rowWindow(item.count, viewport.top - rect.top, viewport.height);
    const key = `${start}:${end}`; if (item.range === key) return; item.range = key;
    const fragment = document.createDocumentFragment();
    for (let index = start; index < end; index++) {
      const value = item.data.rows[index], row = node('div', `cd-row cd-${value.kind}`); row.style.top = `${index * rowHeight}px`;
      for (const side of [0, 1]) {
        const line = side === 0 ? value.oldLine : value.newLine;
        const number = node('span', 'cd-number', line ?? '');
        if (line) { number.title = `双击打开${side === 0 ? '旧' : '新'}侧第 ${line} 行`; number.addEventListener('dblclick', () => this.open(item, side, line)); }
        const text = node('code', 'cd-code', side === 0 ? value.oldText : value.newText);
        row.append(number, text);
      }
      fragment.append(row);
    }
    item.rows.replaceChildren(fragment);
  }
  close() {
    this.epoch++; this.active = 0; this.manifest = null;
    this.restoreAnchor = null;
    for (const controller of this.pending) controller.abort(); this.pending.clear();
    for (const timer of this.timers) clearTimeout(timer); this.timers.clear();
    if (this.frame) cancelAnimationFrame(this.frame); this.frame = null;
    this.items = []; this.host.replaceChildren(); this.host.hidden = true; document.body.classList.remove('continuous-diff-mode');
  }
  dispose() {
    this.close(); this.host.removeEventListener('scroll', this.scroll); this.resize.disconnect(); window.removeEventListener('beforeunload', this.unload);
  }
}
