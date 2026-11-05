import { filterSymbols } from './source-symbols-core.js';
import { structureGrammarFor } from './language-info.js';
import { iconLabel } from './icons.js';
import './source-symbols.css';

const element = (tag, className, text) => {
  const node = document.createElement(tag); node.className = className;
  if (text !== undefined) node.textContent = text; return node;
};

export class SourceSymbols {
  constructor({ getState, reveal, focus }) {
    Object.assign(this, { getState, reveal, focus }); this.sequence = 0; this.items = []; this.active = 0;
    this.button = document.getElementById('source-symbols'); iconLabel(this.button, 'structure', '结构');
    this.button.onclick = () => this.open();
    this.overlay = element('div', 'source-symbol-overlay'); this.overlay.hidden = true;
    this.dialog = element('section', 'source-symbol-dialog'); this.dialog.setAttribute('role', 'dialog'); this.dialog.setAttribute('aria-modal', 'true');
    this.dialog.setAttribute('aria-labelledby', 'source-symbol-title');
    const header = element('div', 'source-symbol-head');
    this.title = element('strong', '', '当前文件结构'); this.title.id = 'source-symbol-title';
    this.closeButton = element('button', 'source-symbol-close'); this.closeButton.type = 'button'; iconLabel(this.closeButton, 'close');
    this.closeButton.title = '关闭 · Esc'; this.closeButton.setAttribute('aria-label', '关闭文件结构'); this.closeButton.onclick = () => this.close();
    header.append(this.title, this.closeButton);
    this.input = element('input', 'source-symbol-query'); this.input.type = 'text'; this.input.maxLength = 160;
    this.input.placeholder = '筛选类、函数、方法或所属类型…'; this.input.setAttribute('aria-label', '筛选当前文件符号');
    this.input.setAttribute('role', 'combobox'); this.input.setAttribute('aria-autocomplete', 'list'); this.input.setAttribute('aria-controls', 'source-symbol-list');
    this.input.setAttribute('aria-expanded', 'true');
    this.list = element('div', 'source-symbol-list'); this.list.id = 'source-symbol-list'; this.list.setAttribute('role', 'listbox'); this.list.setAttribute('aria-label', '当前文件符号');
    this.status = element('div', 'source-symbol-status'); this.status.setAttribute('role', 'status');
    this.dialog.append(header, this.input, this.list, this.status); this.overlay.append(this.dialog); document.body.append(this.overlay);
    this.input.oninput = () => { this.active = 0; this.render(); };
    this.overlay.onpointerdown = event => { if (event.target === this.overlay) this.close(); };
  }
  get opened() { return !this.overlay.hidden; }
  update() {
    const state = this.getState(); this.button.hidden = !state.available;
    this.button.disabled = !state.available || !structureGrammarFor(state.language, state.path);
    this.button.title = this.button.disabled ? '当前文件结构支持 Java、C#、Python、JavaScript 和 TypeScript' : '当前文件结构 · Ctrl+F12';
  }
  invalidate() {
    ++this.sequence;
    this.close(false); this.items = []; this.filtered = []; this.result = null; this.identity = null;
    // Rows also close over symbols from the invalidated document.
    this.list.replaceChildren();
  }
  stop() {
    clearTimeout(this.watchdog); this.watchdog = null;
    this.worker?.terminate(); this.worker = null; this.pending = false;
  }
  close(restoreFocus = true) {
    if (!this.opened) return;
    this.overlay.hidden = true; this.input.removeAttribute('aria-activedescendant');
    if (this.pending) { ++this.sequence; this.stop(); this.identity = null; }
    if (restoreFocus) this.focus();
  }
  open() {
    const state = this.getState();
    if (!state.available || !structureGrammarFor(state.language, state.path)) return;
    if (this.opened) { this.input.focus(); this.input.select(); return; }
    this.overlay.hidden = false; this.title.textContent = `文件结构 · ${state.path}`;
    this.input.value = ''; this.input.focus(); this.active = 0;
    if (this.identity === state.identity) { this.render(); return; }
    this.items = []; this.error = null; this.result = null; this.identity = state.identity; this.pending = true; this.render();
    const id = ++this.sequence;
    const current = () => this.opened && id === this.sequence && state.identity === this.getState().identity;
    const fail = message => { if (!current()) return; this.stop(); this.identity = null; this.error = message; this.render(); };
    try {
      this.worker ??= new Worker(new URL('source-symbols.worker.js', location.href), { type: 'module' });
      this.worker.onmessage = ({ data }) => {
        if (!current() || data.id !== id) return;
        clearTimeout(this.watchdog); this.watchdog = null; this.pending = false;
        if (data.error) { fail(data.error); return; }
        this.result = data; this.items = data.symbols; this.render();
      };
      this.worker.onerror = event => { event.preventDefault(); fail('结构解析器未能加载，请重试。'); };
      this.watchdog = setTimeout(() => fail('结构解析耗时过长，请重试或查看较小的文件。'), 5000);
      this.worker.postMessage({ id, language: state.language, path: state.path, text: state.text() });
    } catch { fail('结构解析器未能启动，请重试。'); }
  }
  render() {
    const filtered = filterSymbols(this.items, this.input.value); this.filtered = filtered;
    this.active = Math.max(0, Math.min(this.active, filtered.length - 1)); this.list.replaceChildren();
    // The parsed result is bounded; keep the UI bounded independently while
    // still filtering against every extracted declaration.
    const visible = filtered.slice(0, 500);
    this.active = Math.min(this.active, Math.max(0, visible.length - 1));
    for (const [index, symbol] of visible.entries()) {
      const row = element('div', 'source-symbol-row'); row.id = `source-symbol-${index}`; row.setAttribute('role', 'option'); row.setAttribute('aria-selected', String(index === this.active));
      row.title = `${symbol.kind} · ${symbol.label}\n${symbol.container || '顶层'} · ${symbol.line}:${symbol.column}`;
      const kind = element('span', `source-symbol-kind ${symbol.glyph === 'm' || symbol.glyph === 'c' || symbol.glyph === 'f' ? 'method' : ''}`, symbol.glyph);
      kind.setAttribute('aria-label', symbol.kind);
      const label = element('span', 'source-symbol-label', symbol.label); label.style.paddingLeft = `${Math.min(symbol.depth, 4) * 10}px`;
      row.append(kind, label, element('span', 'source-symbol-container', symbol.container), element('span', 'source-symbol-line', `:${symbol.line}`));
      row.onpointerdown = event => event.preventDefault(); row.onclick = () => this.choose(symbol); this.list.append(row);
    }
    const active = this.list.children[this.active];
    if (active) { this.input.setAttribute('aria-activedescendant', active.id); active.scrollIntoView({ block: 'nearest' }); }
    else this.input.removeAttribute('aria-activedescendant');
    this.status.textContent = this.pending ? '正在解析语法结构…' : this.error ??
      `${filtered.length} 项${filtered.length > 500 ? ' · 仅显示前 500 项，请筛选' : ''}${this.result?.truncated ? ' · 结构过大，结果已截断' : ''}${this.result?.hasErrors ? ' · 文件有语法错误，结构可能不完整' : ''} · Enter 定位 · Esc 返回`;
    this.list.setAttribute('aria-busy', String(this.pending));
  }
  choose(symbol) {
    const state = this.getState();
    if (!this.opened || !state.available || state.identity !== this.identity || this.pending) return;
    this.close(false); this.reveal(symbol); this.focus();
  }
  handleKey(event) {
    if (!this.opened) return false;
    if (event.isComposing || event.keyCode === 229) return true;
    const maximum = Math.min(this.filtered?.length ?? 0, 500) - 1;
    if (event.key === 'Escape') this.close();
    else if (event.key === 'ArrowDown') { this.active = Math.min(maximum, this.active + 1); this.render(); }
    else if (event.key === 'ArrowUp') { this.active = Math.max(0, this.active - 1); this.render(); }
    else if (event.key === 'Enter') {
      if (document.activeElement === this.closeButton) this.close();
      else if (this.filtered?.[this.active]) this.choose(this.filtered[this.active]);
    }
    else if (event.key === 'Tab') { if (document.activeElement === this.input) this.closeButton.focus(); else this.input.focus(); }
    else if ((event.ctrlKey && ['f', 'n'].includes(event.key.toLowerCase())) || event.key === 'F12' || event.key === 'F5' || event.key === 'F7') { this.close(); return false; }
    else return false;
    event.preventDefault(); event.stopImmediatePropagation(); return true;
  }
  dispose() { this.stop(); this.overlay.remove(); }
}
