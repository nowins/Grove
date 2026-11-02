import { iconLabel } from './icons.js';

export class SourceTabs {
  constructor({ getVersion, send }) {
    Object.assign(this, { getVersion, send }); this.views = new Map();
    this.host = document.getElementById('source-tabs'); this.fallback = document.getElementById('file-path');
    this.resize = new ResizeObserver(() => this.revealActive());
    this.resize.observe(this.host);
    window.addEventListener('beforeunload', () => this.resize.disconnect(), { once: true });
    this.host.addEventListener('wheel', event => {
      if (this.host.scrollWidth <= this.host.clientWidth || Math.abs(event.deltaX) >= Math.abs(event.deltaY)) return;
      event.preventDefault(); this.host.scrollLeft += event.deltaY;
    }, { passive: false });
    this.host.addEventListener('keydown', event => {
      const buttons = [...this.host.querySelectorAll('[role=tab]')], current = document.activeElement.closest('[role=tab]');
      const index = buttons.indexOf(current); if (index < 0) return;
      let next;
      if (event.key === 'ArrowRight') next = (index + 1) % buttons.length;
      else if (event.key === 'ArrowLeft') next = (index - 1 + buttons.length) % buttons.length;
      else if (event.key === 'Home') next = 0;
      else if (event.key === 'End') next = buttons.length - 1;
      else return;
      event.preventDefault(); buttons[next].focus(); buttons[next].click();
    });
  }
  begin(toolbar, doc, capture) {
    if (this.displayedId) { const state = capture(); if (state) this.views.set(this.displayedId, state); }
    this.update(toolbar);
    this.displayedId = doc.isNotice ? null : this.activeId;
    return !doc.preserveView && !doc.revealLine ? this.views.get(this.displayedId) : null;
  }
  command(action, id) {
    const version = this.getVersion(); if (version >= 0) this.send({ type: 'source-tab', action, id, version });
  }
  closeActive() { if (this.activeId && !this.host.hidden) { this.command('close', this.activeId); return true; } return false; }
  revealActive() {
    if (this.host.hidden || !this.host.clientWidth) return;
    const active = this.host.querySelector('.source-tab.active');
    if (!active) return;
    const host = this.host.getBoundingClientRect(), tab = active.getBoundingClientRect();
    // Scroll this strip only; scrollIntoView may also move an ancestor.
    if (tab.left < host.left) this.host.scrollLeft -= host.left - tab.left;
    else if (tab.right > host.right) this.host.scrollLeft += tab.right - host.right;
  }
  setDraft(draft) { this.draft = draft; this.markDraft(); }
  markDraft() {
    for (const button of this.host.querySelectorAll('[role=tab]')) {
      const tab = this.tabs?.find(tab => tab.id === button.dataset.id);
      const pending = !!tab && !tab.label && !tab.revision && this.draft?.anchor.context.kind === 0 && tab.path === this.draft.anchor.context.path;
      button.classList.toggle('has-comment-draft', pending);
      const description = pending ? `未保存评论 · L${this.draft.anchor.startLine}` : '';
      button.setAttribute('aria-label', description ? `${button.textContent} · ${description}` : button.textContent);
      button.title = button.dataset.description + (description ? '\n' + description : '');
    }
  }
  update(toolbar) {
    const tabs = toolbar?.sourceTabs ?? [], ids = new Set(tabs.map(tab => tab.id));
    this.tabs = tabs;
    for (const id of this.views.keys()) if (!ids.has(id)) this.views.delete(id);
    this.activeId = toolbar?.activeSourceTabId;
    const focused = document.activeElement.closest('[role=tab]')?.dataset.id;
    const focusInTabs = this.host.contains(document.activeElement);
    this.host.hidden = !tabs.length; this.fallback.hidden = !this.host.hidden;
    this.host.replaceChildren();
    for (const tab of tabs) {
      const title = tab.path.slice(tab.path.lastIndexOf('/') + 1) + (tab.label ? ` (${tab.label}${tab.revision ? ' · ' + tab.revision.slice(0, 8) : ''})` : tab.revision ? ` (${tab.revision.slice(0, 8)})` : '');
      const item = document.createElement('div'); item.className = 'source-tab'; item.dataset.id = tab.id;
      const button = document.createElement('button'); button.type = 'button'; button.dataset.id = tab.id;
      button.id = `source-tab-${tab.id}`; button.setAttribute('aria-controls', 'workspace');
      button.setAttribute('role', 'tab'); button.setAttribute('aria-selected', String(tab.id === this.activeId));
      button.tabIndex = tab.id === this.activeId || !this.activeId && tab === tabs[0] ? 0 : -1;
      button.textContent = title; button.title = `${tab.path}\n${tab.label ?? '工作区'}${tab.revision ? ` · 修订 ${tab.revision}` : ''} · 只读`;
      button.dataset.description = button.title;
      button.addEventListener('click', () => this.command('select', tab.id));
      const close = document.createElement('button'); close.type = 'button'; close.className = 'close-source-tab'; close.tabIndex = -1;
      close.setAttribute('aria-label', `关闭 ${title}`); close.title = '关闭标签 · Ctrl+F4'; iconLabel(close, 'close');
      close.addEventListener('click', () => this.command('close', tab.id));
      item.addEventListener('auxclick', event => { if (event.button === 1) { event.preventDefault(); this.command('close', tab.id); } });
      item.classList.toggle('active', tab.id === this.activeId); item.append(button, close); this.host.append(item);
    }
    this.markDraft();
    const target = [...this.host.querySelectorAll('[role=tab]')].find(button => button.dataset.id === (focused && ids.has(focused) ? focused : this.activeId));
    if (focusInTabs) target?.focus({ preventScroll: true });
    this.revealActive();
    const workspace = document.getElementById('workspace');
    if (this.activeId) { workspace.setAttribute('role', 'tabpanel'); workspace.setAttribute('aria-labelledby', `source-tab-${this.activeId}`); }
    else { workspace.removeAttribute('role'); workspace.removeAttribute('aria-labelledby'); }
  }
}
