// A fixed-width source overview keeps the IDEA layout independent of Monaco's
// viewport-column-dependent minimap width. Navigation uses public source APIs.
export class SourceMinimap {
  constructor({ panes, reveal }) {
    Object.assign(this, { panes, reveal });
    this.hosts = ['old-map', 'new-map'].map(id => document.getElementById(id));
    this.sizes = [null, null];
    this.colors = [new Map(), new Map()];
    this.colorHost = document.createElement('div'); this.colorHost.className = 'monaco-editor';
    this.colorHost.style.cssText = 'position:fixed;visibility:hidden;pointer-events:none;left:-10000px;top:0';
    document.body.append(this.colorHost);
    this.hosts.forEach((host, side) => {
      const canvas = document.createElement('canvas'), viewport = document.createElement('div');
      viewport.className = 'map-viewport'; host.append(canvas, viewport);
      host.setAttribute('aria-label', `${side === 0 ? '旧' : '新'}侧代码缩略图`);
      host.addEventListener('pointerdown', event => { host.setPointerCapture(event.pointerId); this.seek(event, side); });
      host.addEventListener('pointermove', event => { if (host.hasPointerCapture(event.pointerId)) this.seek(event, side); });
      host.addEventListener('pointerup', event => { if (host.hasPointerCapture(event.pointerId)) host.releasePointerCapture(event.pointerId); });
    });
    this.resize = new ResizeObserver(() => { this.draw(); this.renderViewport(); });
    this.hosts.forEach(host => this.resize.observe(host));
    window.addEventListener('beforeunload', () => { this.resize.disconnect(); this.cancelColors(); }, { once: true });
  }
  update(plan, show) {
    const changed = this.plan !== plan; this.plan = plan;
    this.hosts.forEach(host => {
      const column = host.parentElement;
      const enabled = !!plan && show && column.clientWidth >= 500;
      column.classList.toggle('with-minimap', enabled);
      column.style.setProperty('--map-width', column.clientWidth >= 630 ? '145px' : '100px');
    });
    if (changed) {
      // A queued pointer target must not retain the previous document's lines.
      this.pending = null;
      this.cancelColors(); this.sizes = [null, null]; this.colors = [new Map(), new Map()];
    }
    this.draw(); this.renderViewport();
  }
  cancelColors() {
    if (this.idle != null) {
      if (window.requestIdleCallback) window.cancelIdleCallback(this.idle); else clearTimeout(this.idle);
    }
    this.idle = null; this.colorJob = null; this.colorHost.replaceChildren();
  }
  scheduleColors(owner) {
    if (!owner) return;
    // Cache only rasterized rows, not the whole document. The public colorizer
    // advances token state to each sampled line, including multiline comments.
    const job = this.colorJob = { side: 0, index: 0, models: [], styles: new Map() };
    const idle = () => { this.idle = window.requestIdleCallback ? window.requestIdleCallback(step, { timeout: 250 }) : setTimeout(step, 16); };
    const step = () => {
      if (job !== this.colorJob || owner !== this.plan) return;
      this.idle = null;
      const deadline = performance.now() + 5;
      while (job.side < 2 && performance.now() < deadline) {
        const side = job.side, size = this.sizes[side];
        if (!size || job.index >= size.samples.length) {
          if (size) size.dirty = true;
          job.side++; job.index = 0; continue;
        }
        const model = this.panes[side].getModel();
        if (!model || job.models[side] && job.models[side] !== model || model.isDisposed()) { this.cancelColors(); return; }
        job.models[side] = model;
        const index = size.samples[job.index++];
        this.colorHost.innerHTML = monaco.editor.colorizeModelLine(model, index + 1, 4);
        const runs = []; let offset = 0;
        for (const span of this.colorHost.querySelectorAll('span.mtk1, span[class*="mtk"]')) {
          if (offset >= size.columns) break;
          const value = span.textContent.slice(0, size.columns - offset).replaceAll('\u00a0', ' ');
          let color = job.styles.get(span.className);
          if (!color) { color = getComputedStyle(span).color; job.styles.set(span.className, color); }
          for (const match of value.matchAll(/\S+/g)) runs.push({ offset: offset + match.index, length: match[0].length, color });
          offset += value.length;
        }
        this.colors[side].set(index, runs);
      }
      this.draw(); this.renderViewport();
      if (job !== this.colorJob) return;
      if (job.side < 2) idle(); else { this.colorJob = null; this.colorHost.replaceChildren(); }
    };
    if (this.sizes.some(size => size?.samples.length)) idle(); else this.colorJob = null;
  }
  seek(event, side) {
    if (!this.plan || !this.sizes[side]) return;
    const owner = this.plan, host = this.hosts[side], lines = side === 0 ? owner.oldLines : owner.newLines;
    const y = event.clientY - host.getBoundingClientRect().top;
    const line = Math.max(1, Math.min(lines.length, Math.floor(y / this.sizes[side].row) + 1));
    this.pending = { side, line, owner };
    if (this.frame) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = null; const target = this.pending; this.pending = null;
      if (target?.owner === this.plan) this.reveal(target.side, target.line);
    });
  }
  draw() {
    let changed = false;
    this.hosts.forEach((host, side) => {
      const enabled = this.plan && host.parentElement.classList.contains('with-minimap');
      const width = enabled ? host.clientWidth : 0, height = enabled ? host.clientHeight : 0;
      const canvas = host.firstChild, ratio = devicePixelRatio || 1;
      if (!width || !height) {
        if (this.sizes[side]) changed = true;
        this.sizes[side] = null; this.colors[side].clear();
        // Release the canvas backing store as well as its per-row color cache.
        if (canvas.width || canvas.height) { canvas.width = 0; canvas.height = 0; }
        return;
      }
      const lines = side === 0 ? this.plan.oldLines : this.plan.newLines;
      let size = this.sizes[side];
      if (size?.width !== width || size?.height !== height || size?.ratio !== ratio) {
        const row = Math.min(2, Math.max(.05, (height - 4) / Math.max(1, lines.length)));
        const samples = []; let previousY = -1;
        for (let index = 0; index < lines.length; index++) {
          const y = Math.floor(index * row);
          if (y >= height) break;
          if (y !== previousY) { samples.push(index); previousY = y; }
        }
        size = this.sizes[side] = { width, height, ratio, row, samples, columns: Math.min(220, Math.ceil((width - 3) / .65)), dirty: true };
        this.colors[side].clear(); changed = true;
      }
      if (!size.dirty) return;
      size.dirty = false;
      canvas.width = width * ratio; canvas.height = height * ratio;
      canvas.style.width = `${width}px`; canvas.style.height = `${height}px`;
      const context = canvas.getContext('2d'); context.scale(ratio, ratio);
      context.clearRect(0, 0, width, height);
      const { row } = size;
      size.samples.forEach(index => {
        const y = Math.floor(index * row);
        const runs = this.colors[side].get(index) ?? [...lines[index].slice(0, size.columns).matchAll(/\S+/g)].map(match => ({ offset: match.index, length: match[0].length, color: '#787b83' }));
        for (const run of runs) {
          if (run.offset > 220) break;
          context.fillStyle = run.color;
          const x = 3 + run.offset * .65;
          if (x < width) context.fillRect(x, y, Math.min(width - x, run.length * .65), Math.max(1, row - .5));
        }
      });
      for (const block of this.plan.blocks) {
        const start = side === 0 ? block.oldStart : block.newStart, count = side === 0 ? block.oldCount : block.newCount;
        context.fillStyle = count && block.oldCount && block.newCount ? '#46617999' : side === 0 ? '#86575c88' : '#528c6488';
        if (count) context.fillRect(0, start * row, width, Math.max(2, count * row));
      }
    });
    if (changed) { this.cancelColors(); this.scheduleColors(this.plan); }
  }
  renderViewport() {
    if (!this.plan) return;
    this.hosts.forEach((host, side) => {
      if (!this.sizes[side]) return;
      const ranges = this.panes[side].getVisibleRanges(); if (!ranges.length) return;
      const viewport = host.lastChild, row = this.sizes[side].row;
      viewport.style.top = `${(ranges[0].startLineNumber - 1) * row}px`;
      viewport.style.height = `${Math.max(10, (ranges.at(-1).endLineNumber - ranges[0].startLineNumber + 1) * row)}px`;
    });
  }
}
import * as monaco from 'monaco-editor/editor/editor.api.js';
