import { editorTypography } from './editor-typography.js';
import { iconLabel } from './icons.js';
import { installTooltips } from './tooltips.js';

const keyFor = row => row.newLine !== null ? `1:${row.newLine}` : `0:${row.oldLine}`;

// Source numbers and inclusion controls flank the centre, as in IDEA. They use
// public source geometry: alignment gaps and folds never gain fake source lines.
export class DiffActions {
  constructor({ panes, send, getState, selectLine }) {
    Object.assign(this, { panes, send, getState, selectLine });
    this.gutters = ['old-gutter', 'new-gutter'].map(id => document.getElementById(id));
    this.blocks = document.getElementById('block-actions');
    this.numbers = [new Map(), new Map()]; this.buttons = new Map(); this.deletedChecks = new Map();
    installTooltips('#block-actions button, .source-line input');
  }
  reset() {
    this.numbers.forEach(rows => { for (const row of rows.values()) row.remove(); rows.clear(); });
    this.blocks.replaceChildren(); this.buttons.clear(); this.plan = null;
    for (const row of this.deletedChecks.values()) row.remove(); this.deletedChecks.clear();
  }
  render(bounds) {
    const { plan, canInclude, canRevert, loading, busy, generation, included, hidden } = this.getState();
    if (!plan) { this.reset(); return; }
    if (this.plan !== plan) {
      this.plan = plan; this.changes = [new Map(), new Map()]; this.kinds = [new Map(), new Map()];
      for (const block of plan.blocks) for (const row of block.rows) {
        const side = row.newLine !== null ? 1 : 0, line = side === 1 ? row.newLine : row.oldLine;
        this.changes[side].set(line, keyFor(row));
        if (row.oldLine !== null) this.kinds[0].set(row.oldLine, row.newLine !== null ? 'modified' : plan.movedOldBlocks.has(block.index) ? 'moved' : 'deleted');
        if (row.newLine !== null) this.kinds[1].set(row.newLine, row.oldLine !== null ? 'modified' : 'added');
      }
    }
    const valid = () => { const current = this.getState(); return current.plan === plan && current.generation === generation && !current.loading && !current.busy; };
    this.panes.forEach((pane, side) => {
      const visible = new Set(), total = side === 0 ? plan.oldLines.length : plan.newLines.length;
      for (const range of pane.getVisibleRanges()) for (let line = range.startLineNumber; line <= Math.min(total, range.endLineNumber); line++) {
        const folded = hidden?.[side].find(region => line >= region.start && line <= region.end);
        if (folded) { line = folded.end; continue; }
        visible.add(line);
        let row = this.numbers[side].get(line);
        if (!row) {
          row = document.createElement('div'); row.className = `source-line ${this.kinds[side].get(line) ?? ''}`;
          const number = document.createElement('span'); number.textContent = line; row.append(number);
          const change = this.changes[side].get(line);
          if (side === 1 && change && canInclude) {
            const check = document.createElement('input'); check.type = 'checkbox';
            const label = `包含${side === 0 ? '删除的旧' : '新'}侧第 ${line} 行到提交`;
            check.setAttribute('aria-label', label); check.dataset.tooltip = label;
            check.addEventListener('change', () => {
              if (valid()) this.send({ type: 'include-line', version: generation, side, line, include: check.checked });
            });
            row.append(check);
          } else if (side === 1) { const gap = document.createElement('i'); gap.className = 'empty-check'; row.append(gap); }
          number.addEventListener('click', () => { if (valid()) this.selectLine(side, line); });
          this.gutters[side].append(row); this.numbers[side].set(line, row);
        }
        const check = row.querySelector('input');
        if (check) { check.checked = included.has(this.changes[side].get(line)); check.disabled = loading || busy; }
        row.style.top = `${pane.getTopForLineNumber(line) - pane.getScrollTop()}px`;
      }
      for (const [line, row] of this.numbers[side]) if (!visible.has(line)) { row.remove(); this.numbers[side].delete(line); }
    });
    const deletions = new Set();
    if (canInclude) for (const block of plan.blocks) {
      if (block.newCount !== 0 || !block.oldCount) continue;
      const top = bounds(block)[1][0];
      if (top < -editorTypography.lineHeight || top >= this.gutters[1].clientHeight) continue;
      const index = block.index, lines = block.rows.filter(row => row.oldLine !== null && row.newLine === null).map(row => row.oldLine);
      deletions.add(index); let row = this.deletedChecks.get(index);
      if (!row) {
        row = document.createElement('div'); row.className = `source-line deletion-marker${plan.movedOldBlocks.has(index) ? ' moved' : ''}`;
        const check = document.createElement('input'); check.type = 'checkbox';
        const label = `包含旧侧第 ${lines[0]}–${lines.at(-1)} 行的删除到提交`;
        check.setAttribute('aria-label', label); check.dataset.tooltip = label;
        check.addEventListener('change', () => { if (valid()) this.send({ type: 'include-deletion', version: generation, block: index, include: check.checked }); });
        row.append(check); this.gutters[1].append(row); this.deletedChecks.set(index, row);
      }
      const check = row.firstChild, count = lines.filter(line => included.has(`0:${line}`)).length;
      check.checked = count === lines.length; check.indeterminate = count > 0 && count < lines.length; check.disabled = loading || busy;
      row.style.top = `${top}px`;
    }
    for (const [line, row] of this.deletedChecks) if (!deletions.has(line)) { row.remove(); this.deletedChecks.delete(line); }
    const visible = new Set();
    if (canRevert && !loading) for (const block of plan.blocks) {
      const [top, end] = bounds(block)[0], bottom = Math.max(top + editorTypography.lineHeight, end); if (bottom < 0 || top >= this.blocks.clientHeight) continue;
      visible.add(block.index); let button = this.buttons.get(block.index);
      if (!button) {
        button = document.createElement('button'); button.type = 'button';
        button.dataset.tooltip = '回退 · Ctrl+Alt+R'; button.setAttribute('aria-label', `回退第 ${block.index + 1} 处差异`);
        iconLabel(button, 'chevrons-right');
        button.addEventListener('click', () => { if (valid()) this.send({ type: 'revert-block', version: generation, block: block.index }); });
        this.blocks.append(button); this.buttons.set(block.index, button);
      }
      button.style.top = `${Math.max(0, top)}px`;
    }
    for (const [index, button] of this.buttons) if (!visible.has(index)) { button.remove(); this.buttons.delete(index); }
  }
}
