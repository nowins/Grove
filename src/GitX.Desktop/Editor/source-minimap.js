// A fixed-width source overview keeps the IDEA layout independent of Monaco's
// viewport-column-dependent minimap width. Navigation uses public source APIs.
export class SourceMinimap {
  constructor({ panes, reveal }) {
    Object.assign(this, { panes, reveal });
    this.hosts = ['old-map', 'new-map'].map(id => document.getElementById(id));
    this.sizes = [null, null];
    this.colors = [null, null];
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
    window.addEventListener('beforeunload', () => this.resize.disconnect(), { once: true });
  }
  update(plan, show) {
    const changed = this.plan !== plan; this.plan = plan;
    this.hosts.forEach(host => {
      const column = host.parentElement;
      const enabled = !!plan && show && column.clientWidth >= 500;
      column.classList.toggle('with-minimap', enabled);
      column.style.setProperty('--map-width', column.clientWidth >= 630 ? '145px' : '100px');
    });
    if (changed) { this.sizes = [null, null]; this.colors = [null, null]; this.draw(); this.scheduleColors(plan); }
  }
  scheduleColors(owner) {
    if (!owner) return;
    // Reuse the displayed model's public colorizer. Work in source order during
    // idle slices so token state (including multiline comments) stays correct.
    const idle = action => window.requestIdleCallback ? requestIdleCallback(action, { timeout: 250 }) : setTimeout(action, 16);
    let side = 0, line = 1; const colors = [[], []], models = [];
    const step = () => {
      if (owner !== this.plan) return;
      const deadline = performance.now() + 5;
      while (side < 2 && performance.now() < deadline) {
        const model = this.panes[side].getModel();
        if (!model || models[side] && models[side] !== model || model.isDisposed()) return;
        models[side] = model;
        const lines = side === 0 ? owner.oldLines : owner.newLines;
        if (line > lines.length) { this.colors[side] = colors[side]; this.sizes[side] = null; side++; line = 1; continue; }
        this.colorHost.innerHTML = monaco.editor.colorizeModelLine(model, line, 4);
        const runs = []; let offset = 0;
        for (const span of this.colorHost.querySelectorAll('span.mtk1, span[class*="mtk"]')) {
          const value = span.textContent.replaceAll('\u00a0', ' '), color = getComputedStyle(span).color;
          for (const match of value.matchAll(/\S+/g)) runs.push({ offset: offset + match.index, length: match[0].length, color });
          offset += value.length;
        }
        colors[side].push(runs); line++;
      }
      this.draw(); this.renderViewport();
      if (side < 2) idle(step); else this.colorHost.replaceChildren();
    };
    idle(step);
  }
  seek(event, side) {
    if (!this.plan || !this.sizes[side]) return;
    const owner = this.plan, host = this.hosts[side], lines = side === 0 ? owner.oldLines : owner.newLines;
    const y = event.clientY - host.getBoundingClientRect().top;
    const line = Math.max(1, Math.min(lines.length, Math.floor(y / this.sizes[side].row) + 1));
    this.pending = { side, line, owner };
    if (this.frame) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = null; const target = this.pending;
      if (target?.owner === this.plan) this.reveal(target.side, target.line);
    });
  }
  draw() {
    if (!this.plan) return;
    this.hosts.forEach((host, side) => {
      const width = host.clientWidth, height = host.clientHeight;
      if (!width || !height) return;
      const canvas = host.firstChild, ratio = devicePixelRatio || 1;
      if (this.sizes[side]?.width === width && this.sizes[side]?.height === height) return;
      canvas.width = width * ratio; canvas.height = height * ratio;
      canvas.style.width = `${width}px`; canvas.style.height = `${height}px`;
      const context = canvas.getContext('2d'); context.scale(ratio, ratio);
      context.clearRect(0, 0, width, height);
      const lines = side === 0 ? this.plan.oldLines : this.plan.newLines;
      const row = Math.min(2, Math.max(.05, (height - 4) / Math.max(1, lines.length)));
      this.sizes[side] = { width, height, row };
      let previousY = -1;
      lines.forEach((line, index) => {
        const y = Math.floor(index * row); if (y === previousY) return; previousY = y;
        const runs = this.colors[side]?.[index] ?? [...line.slice(0, 220).matchAll(/\S+/g)].map(match => ({ offset: match.index, length: match[0].length, color: '#787b83' }));
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
