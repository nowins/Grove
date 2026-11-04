import { iconLabel } from './icons.js';
import { positionPopup } from './popup-position.js';

// A menu targets the tab under the pointer, independently of the active file.
// Capture the publication and tab set so a late command cannot close new tabs.
export class SourceTabMenu {
  constructor({ getState, run, focusTab, beforeOpen = () => {} }) {
    Object.assign(this, { getState, run, focusTab, beforeOpen });
    this.menu = document.createElement('div');
    this.menu.className = 'blame-menu source-tab-menu';
    this.menu.setAttribute('role', 'menu'); this.menu.hidden = true;
    document.body.append(this.menu);
    this.abort = new window.AbortController();
    const options = { signal: this.abort.signal };
    document.addEventListener('pointerdown', event => { if (!this.menu.contains(event.target)) this.close(); }, options);
    document.addEventListener('contextmenu', event => { if (!this.menu.contains(event.target)) this.close(); }, options);
    document.addEventListener('scroll', event => { if (!this.menu.contains(event.target)) this.close(); }, { ...options, capture: true });
    window.addEventListener('resize', () => this.close(), options);
    window.addEventListener('blur', () => this.close(), options);
    // Installed before editor shortcuts; menu navigation must not move source.
    window.addEventListener('keydown', event => this.key(event), { ...options, capture: true });
    this.menu.addEventListener('contextmenu', event => { event.preventDefault(); event.stopPropagation(); }, options);
  }
  get opened() { return !this.menu.hidden; }
  signature(tabs) { return tabs.map(tab => tab.id).join('|'); }
  current() {
    const state = this.getState(), context = this.context;
    return context && state.available && state.version === context.version
      && this.signature(state.tabs) === context.signature && state.tabs.some(tab => tab.id === context.id);
  }
  close(restore = false) {
    if (!this.opened) return;
    const id = this.context?.id, inside = this.menu.contains(document.activeElement);
    this.menu.hidden = true; this.context = null;
    if (restore) this.focusTab(id);
    else if (inside) document.activeElement.blur();
  }
  show(id, anchor) {
    this.close();
    const state = this.getState(), tab = state.tabs.find(tab => tab.id === id);
    if (!state.available || state.version < 0 || !tab) return false;
    this.beforeOpen();
    this.context = { id, version: state.version, signature: this.signature(state.tabs) };
    this.menu.setAttribute('aria-label', `文件标签操作：${tab.path}`);
    this.menu.replaceChildren();
    const local = tab.localFile ?? (!tab.revision && !tab.label);
    const reveal = state.platform === 'mac' ? '在 Finder 中显示' : state.platform === 'windows' ? '在资源管理器中显示' : '在文件管理器中显示';
    for (const [action, label, enabled, icon] of [
      ['close', '关闭标签', true, 'close'],
      ['close-others', '关闭其他标签', state.tabs.length > 1, 'blank'],
      ['close-all', '关闭全部标签', true, 'blank'],
      ['reveal', reveal, local, 'blank'],
      ['terminal', '在终端中打开', local, 'blank']
    ]) {
      if (action === 'reveal') this.menu.append(document.createElement('hr'));
      const button = document.createElement('button'); button.type = 'button';
      button.dataset.action = action; button.setAttribute('role', 'menuitem');
      button.disabled = !enabled; button.setAttribute('aria-disabled', String(!enabled));
      if (!enabled) button.title = action === 'close-others' ? '没有其他打开的标签' : '历史或比较快照没有可直接打开的本地文件；请使用项目树。';
      iconLabel(button, icon, label);
      button.addEventListener('pointerenter', () => { if (!button.disabled) button.focus(); });
      button.addEventListener('click', () => {
        const valid = this.current(), context = this.context;
        this.close(valid);
        if (valid && enabled) this.run(action, context.id, context.version);
      });
      this.menu.append(button);
    }
    this.menu.hidden = false; positionPopup(this.menu, anchor);
    this.menu.querySelector('button:not(:disabled)')?.focus();
    return true;
  }
  key(event) {
    if (!this.opened) return;
    if (event.isComposing || event.keyCode === 229) return;
    const buttons = [...this.menu.querySelectorAll('button:not(:disabled)')];
    let index = buttons.indexOf(document.activeElement);
    if (event.key === 'Escape' || event.key === 'Tab') {
      this.close(true);
      if (event.key === 'Tab') return;
    } else if (event.key === 'ArrowDown') { index = (index + 1) % buttons.length; buttons[index]?.focus(); }
    else if (event.key === 'ArrowUp') { index = (index - 1 + buttons.length) % buttons.length; buttons[index]?.focus(); }
    else if (event.key === 'Home') buttons[0]?.focus();
    else if (event.key === 'End') buttons.at(-1)?.focus();
    else if (event.key === 'Enter' || event.key === ' ') { if (buttons.includes(document.activeElement)) document.activeElement.click(); }
    else if (event.metaKey || event.ctrlKey || event.altKey) { this.close(); return; }
    event.preventDefault(); event.stopImmediatePropagation();
  }
  dispose() { this.close(); this.abort.abort(); this.menu.remove(); }
}
