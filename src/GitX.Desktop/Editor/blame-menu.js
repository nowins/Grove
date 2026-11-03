import { iconLabel } from './icons.js';
import { positionPopup, positionSubmenu } from './popup-position.js';

export class BlameMenu {
  constructor({ getState, send, focusEditor, beforeOpen, setView }) {
    Object.assign(this, { getState, send, focusEditor, beforeOpen, setView });
    this.menu = document.createElement('div'); this.menu.className = 'blame-menu';
    this.menu.setAttribute('role', 'menu'); this.menu.setAttribute('aria-label', 'Git 注解操作');
    this.menu.hidden = true; document.body.append(this.menu);
    this.submenu = document.createElement('div'); this.submenu.className = 'blame-menu blame-view-menu';
    this.submenu.setAttribute('role', 'menu'); this.submenu.setAttribute('aria-label', '注解显示字段');
    this.submenu.hidden = true; document.body.append(this.submenu);
    document.addEventListener('pointerdown', event => { if (!this.menu.contains(event.target) && !this.submenu.contains(event.target)) this.close(); });
    window.addEventListener('resize', () => this.close());
    this.menu.addEventListener('pointerleave', event => { if (!this.submenu.contains(event.relatedTarget)) this.delayCloseView(); });
    this.submenu.addEventListener('pointerenter', () => clearTimeout(this.closeViewTimer));
    this.submenu.addEventListener('pointerleave', event => { if (!this.menu.contains(event.relatedTarget)) this.delayCloseView(); });
    for (const host of [this.menu, this.submenu]) host.addEventListener('keydown', event => {
      if (host === this.menu && event.key === 'ArrowRight' && document.activeElement === this.viewButton) {
        event.preventDefault(); this.openView(true); return;
      }
      if (host === this.submenu && event.key === 'ArrowLeft') { event.preventDefault(); this.closeView(true); return; }
      const buttons = [...host.querySelectorAll('button:not(:disabled)')];
      let index = buttons.indexOf(document.activeElement);
      if (event.key === 'ArrowDown') index = (index + 1) % buttons.length;
      else if (event.key === 'ArrowUp') index = (index - 1 + buttons.length) % buttons.length;
      else if (event.key === 'Home') index = 0;
      else if (event.key === 'End') index = buttons.length - 1;
      else if (event.key === 'Tab') { this.close(); return; }
      else return;
      event.preventDefault(); if (host === this.menu) this.closeView(); buttons[index]?.focus();
    });
  }
  get opened() { return !this.menu.hidden; }
  close(focus = false) { this.closeView(); this.menu.hidden = true; if (focus) this.focusEditor(); }
  closeView(focus = false) { clearTimeout(this.closeViewTimer); this.submenu.hidden = true; this.viewButton?.setAttribute('aria-expanded', 'false'); if (focus) this.viewButton?.focus(); }
  delayCloseView() { clearTimeout(this.closeViewTimer); this.closeViewTimer = setTimeout(() => this.closeView(), 200); }
  escape() { if (!this.submenu.hidden) this.closeView(true); else this.close(true); }
  openView(focus = false) {
    clearTimeout(this.closeViewTimer); this.refreshFields(); this.submenu.hidden = false;
    this.viewButton.setAttribute('aria-expanded', 'true'); positionSubmenu(this.submenu, this.viewButton.getBoundingClientRect());
    if (focus) this.submenu.querySelector('button:not(:disabled)')?.focus();
  }
  refreshFields() {
    const focused = document.activeElement?.dataset.field;
    this.submenu.replaceChildren();
    const settings = this.getState().blameView;
    const count = Object.values(settings).filter(Boolean).length;
    for (const [field, label] of [['revision', '修订号'], ['date', '日期'], ['author', '作者'], ['commitNumber', '提交序号']]) {
      const button = document.createElement('button'); button.type = 'button'; button.dataset.field = field;
      button.setAttribute('role', 'menuitemcheckbox'); button.setAttribute('aria-checked', String(settings[field]));
      iconLabel(button, settings[field] ? 'check' : 'blank', label);
      button.disabled = settings[field] && count === 1;
      if (button.disabled) button.title = '至少保留一个注解显示字段';
      button.addEventListener('click', () => {
        const current = this.getState(); if (current.loading || current.version !== this.version) return;
        const value = !current.blameView[field]; this.setView({ ...current.blameView, [field]: value });
        this.send({ type: 'blame-view', version: this.version, field, value }); this.refreshFields();
        this.submenu.querySelector(`[data-field="${field}"]`)?.focus();
      });
      this.submenu.append(button);
    }
    if (focused) this.submenu.querySelector(`[data-field="${focused}"]`)?.focus();
  }
  show(line, anchor) {
    const state = this.getState(), info = state.blame?.[line - 1];
    if (state.loading || !info) return;
    this.menu.setAttribute('aria-label', 'Git 注解操作');
    this.beforeOpen(); this.closeView(); this.version = state.version; this.menu.replaceChildren();
    const committed = /^[a-f0-9]{40}$/i.test(info.sha) && !/^0+$/.test(info.sha);
    for (const [action, label, icon, enabled] of [
      ['close', '关闭注解', 'close', true], ['diff', '显示此提交差异', 'chevrons-right', committed],
      ['view', '查看', 'blank', true], ['copy', '复制修订号', 'copy', committed],
      ['previous', '注解前一修订', 'history', !!info.hasPrevious], ['history', '查看文件历史', 'history', true]
    ]) {
      const button = document.createElement('button'); button.type = 'button'; button.setAttribute('role', 'menuitem');
      button.disabled = !enabled; iconLabel(button, icon, label);
      if (!enabled) button.title = action === 'previous' ? '此行没有更早的文件修订' : '此行尚未提交，没有所属提交';
      if (action === 'previous') { const separator = document.createElement('hr'); this.menu.append(separator); }
      if (action === 'view') {
        this.viewButton = button; button.setAttribute('aria-haspopup', 'menu'); button.setAttribute('aria-expanded', 'false');
        const arrow = document.createElement('span'); arrow.className = 'submenu-arrow'; iconLabel(arrow, 'right'); button.append(arrow);
        button.addEventListener('pointerenter', () => { button.focus(); this.openView(); });
        button.addEventListener('click', () => this.openView(true)); this.menu.append(button); continue;
      }
      button.addEventListener('pointerenter', () => { this.closeView(); button.focus(); });
      button.addEventListener('click', () => {
        this.close(true);
        const current = this.getState();
        if (!current.loading && current.version === state.version) this.send({ type: 'blame-action', action, line, version: state.version });
      });
      this.menu.append(button);
    }
    this.menu.hidden = false; positionPopup(this.menu, anchor);
    this.menu.querySelector('button:not(:disabled)')?.focus();
  }
  showEditor(actions, anchor) {
    if (this.getState().loading || !actions.length) return;
    this.menu.setAttribute('aria-label', '代码操作');
    this.beforeOpen(); this.closeView(); this.viewButton = null;
    const version = this.version = this.getState().version;
    this.menu.replaceChildren();
    for (const { label, icon, run } of actions) {
      const button = document.createElement('button'); button.type = 'button'; button.setAttribute('role', 'menuitem');
      iconLabel(button, icon, label);
      button.addEventListener('click', () => {
        this.close(true);
        const state = this.getState();
        if (!state.loading && state.version === version) run();
      });
      this.menu.append(button);
    }
    this.menu.hidden = false; positionPopup(this.menu, anchor);
    this.menu.querySelector('button')?.focus();
  }
}
