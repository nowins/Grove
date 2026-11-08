export class WorkspaceNotice {
  constructor({ host, tools, getVersion, send, isReady = () => true, isMac = /Mac|iPhone|iPad/.test(navigator.platform) }) {
    Object.assign(this, { host, tools, getVersion, send, isReady, isMac });
    host.tabIndex = -1;
  }
  update(doc, toolbar) {
    this.active = !!doc.isNotice && !doc.path && !toolbar?.continuousDiff;
    this.host.hidden = !doc.isNotice || !!toolbar?.continuousDiff;
    this.tools.hidden = this.active || !!toolbar?.continuousDiff;
    this.host.classList.toggle('workspace-empty', this.active);
    this.host.replaceChildren();
    if (this.host.hidden) return;
    const message = document.createElement('p'); message.textContent = doc.text ?? '';
    this.host.append(message);
    if (!this.active || !toolbar?.workspaceActions) return;
    const version = this.getVersion(), actions = document.createElement('div'); actions.className = 'workspace-empty-actions';
    for (const [label, shortcut, command] of [
      ['搜索文件', '双击 Shift', { type: 'code-search', filesOnly: true, quick: true }],
      ['最近文件', this.isMac ? '⌘E' : 'Ctrl+E', { type: 'recent-files' }]
    ]) {
      const button = document.createElement('button'); button.type = 'button';
      const name = document.createElement('span'); name.textContent = label;
      const keys = document.createElement('kbd'); keys.textContent = shortcut;
      button.append(name, keys);
      button.addEventListener('click', () => {
        if (this.active && this.host.contains(button) && version === this.getVersion() && this.isReady()) this.send({ ...command, version });
      });
      actions.append(button);
    }
    this.host.append(actions);
  }
  focus() {
    if (this.active && !this.host.hidden) (this.host.querySelector('button') ?? this.host).focus({ preventScroll: true });
  }
}
