import { iconLabel } from './icons.js';
import { parseLineInput, goToLineShortcut, sameLineContext } from './go-to-line-core.js';

export class GoToLine {
  constructor({ getState, reveal, focus, beforeOpen = () => {} }) {
    Object.assign(this, { getState, reveal, focus, beforeOpen });
    this.overlay = document.createElement('div'); this.overlay.className = 'source-symbol-overlay goto-line-overlay'; this.overlay.hidden = true;
    this.dialog = document.createElement('section'); this.dialog.className = 'source-symbol-dialog goto-line-dialog'; this.dialog.setAttribute('role', 'dialog'); this.dialog.setAttribute('aria-modal', 'true'); this.dialog.setAttribute('aria-labelledby', 'goto-line-title');
    const heading = document.createElement('div'); heading.className = 'source-symbol-head';
    const title = document.createElement('strong'); title.id = 'goto-line-title'; title.textContent = '跳转到行'; heading.append(title);
    const close = document.createElement('button'); close.className = 'source-symbol-close'; iconLabel(close, 'close'); close.setAttribute('aria-label', '取消跳转'); close.onclick = () => this.close(true); heading.append(close);
    this.input = document.createElement('input'); this.input.className = 'source-symbol-query'; this.input.type = 'text'; this.input.inputMode = 'numeric'; this.input.maxLength = 16; this.input.setAttribute('aria-label', '目标行号'); this.input.setAttribute('aria-describedby', 'goto-line-status');
    this.status = document.createElement('div'); this.status.id = 'goto-line-status'; this.status.className = 'source-symbol-status'; this.status.setAttribute('role', 'status');
    this.dialog.append(heading, this.input, this.status); this.overlay.append(this.dialog); document.body.append(this.overlay);
    this.input.addEventListener('input', () => { this.input.removeAttribute('aria-invalid'); this.status.textContent = this.rangeText(); });
    this.overlay.addEventListener('pointerdown', event => { if (event.target === this.overlay) this.close(true); });
    this.closeButton = close;
    this.overlay.addEventListener('keydown', event => this.handleKey(event), true);
  }
  handleKey(event) {
    if (!this.opened) return false;
    if (event.isComposing || event.keyCode === 229) return true;
    if (event.key === 'Escape' || event.key === 'Enter' || event.key === 'Tab') {
      event.preventDefault(); event.stopImmediatePropagation();
      if (event.key === 'Escape' || event.key === 'Enter' && event.target === this.closeButton) this.close(true);
      else if (event.key === 'Enter') this.accept();
      else (document.activeElement === this.input ? this.closeButton : this.input).focus();
    }
    // The line-number form owns typing; editor-level shortcuts cannot move a
    // document while its modal input is active. Ordinary input remains native.
    return true;
  }
  get opened() { return !this.overlay.hidden; }
  rangeText() { return `1–${this.context?.lineCount ?? 1} 行 · Enter 跳转 · Esc 取消`; }
  open() {
    const state = this.getState(); if (!state.available) return false;
    this.beforeOpen();
    this.context = { ...state }; this.input.value = String(state.line ?? 1); this.input.removeAttribute('aria-invalid'); this.status.textContent = this.rangeText();
    this.overlay.hidden = false; this.input.focus(); this.input.select(); return true;
  }
  handleShortcut(event, mac) {
    if (!goToLineShortcut(event, mac) || !this.getState().available) return false;
    event.preventDefault(); event.stopImmediatePropagation(); return this.open();
  }
  accept() {
    if (!sameLineContext(this.context, this.getState())) { this.close(false); return false; }
    const parsed = parseLineInput(this.input.value, this.context.lineCount);
    if (parsed.error) { this.status.textContent = parsed.error; this.input.setAttribute('aria-invalid', 'true'); this.input.focus(); return false; }
    this.close(false); this.reveal(parsed.line); this.focus(); return true;
  }
  close(restore = false) {
    if (!this.opened) return;
    const current = sameLineContext(this.context, this.getState()); this.overlay.hidden = true; this.context = null;
    if (restore && current) this.focus();
  }
  dispose() { this.close(false); this.overlay.remove(); }
}
