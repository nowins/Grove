import { selectedLines } from './comment-layout.js';
import './comments.css';
import { iconLabel } from './icons.js';
import { sameNoteContext as same, selectNotes, noteStatus } from './note-list.js';

const element = (tag, className, text) => {
  const node = document.createElement(tag); node.className = className ?? '';
  if (text !== undefined) node.textContent = text; return node;
};
const button = (text, action, icon) => {
  const node = element('button', '', text); node.type = 'button'; node.onclick = action;
  if (icon) iconLabel(node, icon, text); return node;
};
const range = a => `${a.side === 0 ? '旧侧' : '新侧'} · L${a.startLine}${a.endLine === a.startLine ? '' : '–' + a.endLine}`;

export class LocalCommentsView {
  constructor({ panes, monaco, send, getState, showZone, reveal, draftChanged }) {
    Object.assign(this, { panes, monaco, send, getState, showZone, reveal, draftChanged });
    this.items = []; this.draft = null; this.sequence = 0; this.active = null; this.visibleLimit = 50;
    this.marks = panes.map(pane => pane.createDecorationsCollection());
    this.toolbar = element('div', 'comment-toolbar comment-ui');
    this.add = button('行评论', () => this.begin()); iconLabel(this.add, 'plus', '行评论');
    this.add.title = '为光标行或选中的多行添加本地笔记；也可在代码区域右键记录';
    this.toggle = button('本地评论', () => { if (this.drawer.hidden) this.openDrawer(); else this.closeDrawer(); });
    iconLabel(this.toggle, 'comment', '本地评论');
    this.draftButton = button('返回草稿', () => this.openDraft(true), 'edit'); this.draftButton.hidden = true;
    this.draftButton.className = 'comment-draft-button';
    this.message = element('span', 'comment-message'); this.message.setAttribute('role', 'status');
    this.toolbar.append(this.add, this.toggle);
    document.getElementById('editor-messages').append(this.draftButton, this.message);
    document.getElementById('status').before(this.toolbar);
    // The draft belongs to the window, and must stay reachable outside More,
    // without pushing the diff toolbar out of a narrow editor.
    this.drawer = element('aside', 'comment-drawer comment-ui'); this.drawer.hidden = true;
    this.drawer.setAttribute('aria-label', '本地代码笔记');
    const head = element('div', 'comment-drawer-head');
    head.append(element('strong', '', '本地代码笔记'), button('关闭', () => this.closeDrawer(), 'close'));
    this.search = element('input', 'note-search'); this.search.type = 'search'; this.search.maxLength = 160;
    this.search.placeholder = '搜索笔记、文件路径或原代码…'; this.search.setAttribute('aria-label', '搜索本地笔记');
    const filters = element('div', 'note-filters');
    this.scope = element('select'); this.scope.setAttribute('aria-label', '笔记范围');
    for (const [value, label] of [['all', '整个仓库'], ['file', '当前文件'], ['current', '当前版本']]) { const option = element('option', '', label); option.value = value; this.scope.append(option); }
    this.sort = element('select'); this.sort.setAttribute('aria-label', '笔记排序');
    for (const [value, label] of [['updated', '最近更新'], ['file', '文件与行号']]) { const option = element('option', '', label); option.value = value; this.sort.append(option); }
    filters.append(this.scope, this.sort);
    this.count = element('div', 'note-count'); this.count.setAttribute('role', 'status');
    const apply = () => { this.visibleLimit = 50; this.renderList(); };
    this.search.oninput = () => { clearTimeout(this.searchDelay); this.searchDelay = setTimeout(apply, 150); };
    this.scope.onchange = this.sort.onchange = apply;
    const actions = element('div', 'comment-actions');
    this.copy = button('复制 Markdown', () => this.request('copy'), 'copy');
    this.export = button('导出 .md', () => this.request('export'), 'save');
    this.copy.title = '复制当前仓库全部已保存笔记，不受列表筛选影响';
    this.export.title = '导出当前仓库全部已保存笔记，不受列表筛选影响';
    this.record = button('记录选区', () => this.begin(), 'plus'); this.record.title = '为当前光标行或选中的多行添加本地笔记';
    actions.append(this.record, this.copy, this.export, button('刷新', () => this.request('list'), 'refresh'));
    this.draftSlot = element('div', 'comment-draft-slot');
    this.list = element('div', 'comment-list');
    this.drawer.append(head, this.search, filters, this.count, actions, this.draftSlot, this.list); document.body.append(this.drawer);
    const tools = document.getElementById('editor-header');
    const toolbarSize = new ResizeObserver(() => { this.drawer.style.top = `${tools.offsetTop + tools.offsetHeight}px`; });
    toolbarSize.observe(tools);
    window.addEventListener('beforeunload', () => { toolbarSize.disconnect(); clearTimeout(this.searchDelay); }, { once: true });
    this.park = element('div', 'comment-park'); this.park.hidden = true; document.body.append(this.park);
    panes.forEach((pane, side) => pane.onMouseDown(event => {
      if (!event.event.leftButton || event.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN || !event.target.position) return;
      const line = event.target.position.lineNumber;
      const item = this.items.find(c => same(c.anchor.context, this.doc?.commentContext) && c.anchor.side === side && line >= c.anchor.startLine && line <= c.anchor.endLine);
      if (item) this.open(item);
    }));
  }
  request(action, args = {}) { this.send({ type: 'comment', action, repositoryId: this.repositoryId, ...args }); }
  setSourceMode(value) {
    this.sourceMode = value;
    (value ? document.getElementById('source-note-slot') : this.toolbar).append(this.toggle);
    if (value) this.toggle.removeAttribute('role'); else this.toggle.setAttribute('role', 'menuitem');
    this.updateToggle();
  }
  updateToggle() { iconLabel(this.toggle, 'comment', `${this.sourceMode ? '笔记' : '本地评论'} ${this.items.length}`); this.toggle.title = '搜索和整理当前仓库的本地代码笔记'; }
  openDrawer() {
    document.getElementById('diff-more').open = false;
    this.drawer.hidden = false; this.request('list'); this.search.focus();
  }
  closeDrawer() { this.drawer.hidden = true; this.panes[this.getState().side].focus(); }
  handleKey(event) {
    if (this.drawer.hidden || !this.drawer.contains(event.target) || event.target instanceof HTMLTextAreaElement) return false;
    if (event.isComposing || event.keyCode === 229) return true;
    if (event.key === 'Escape') this.closeDrawer();
    else if (event.ctrlKey && !event.altKey && !event.shiftKey && event.key.toLowerCase() === 'f') { this.search.focus(); this.search.select(); }
    else if (event.key !== 'Enter' || event.target !== this.search) return false;
    event.preventDefault(); event.stopImmediatePropagation(); return true;
  }
  begin(side = this.getState().side, lines = null) {
    if (this.draft) { this.openDraft(true); return; }
    if (!this.doc?.commentContext || this.getState().loading) return;
    const selection = this.panes[side].getSelection(); if (!selection) return;
    this.request('begin', { version: this.getState().version, snapshot: this.doc.commentContext.snapshot, side, ...(lines ?? selectedLines(selection)) });
  }
  detach() {
    ++this.sequence;
    clearTimeout(this.searchDelay); this.preview = null;
    this.active = null;
    if (this.composer) this.park.append(this.composer);
    this.draftSlot.replaceChildren();
    this.showZone(null); this.marks.forEach(mark => mark.clear()); this.doc = null;
    this.items = []; this.repositoryId = null; this.list.replaceChildren(); this.updateToggle();
    this.copy.disabled = this.export.disabled = true;
  }
  attach(doc) {
    this.doc = doc; this.add.disabled = this.record.disabled = !doc.commentContext;
    this.drawMarks(); this.renderList();
    if (this.draft) this.openDraft(false);
  }
  async refresh() {
    const owner = ++this.sequence;
    try {
      const response = await fetch('comments', { cache: 'no-store' });
      const data = await response.json();
      if (owner !== this.sequence || data.version !== this.getState().version) return;
      if (data.repositoryId && data.repositoryId !== this.filterRepository) {
        this.filterRepository = data.repositoryId; this.search.value = ''; this.scope.value = 'all'; this.sort.value = 'updated'; this.visibleLimit = 50;
      }
      this.repositoryId = data.repositoryId; this.items = data.comments ?? []; this.busy = data.busy;
      this.message.textContent = data.message ?? ''; this.message.title = this.message.textContent;
      this.updateToggle(); this.record.disabled = this.busy || !this.doc?.commentContext;
      this.copy.disabled = this.export.disabled = this.busy || this.items.length === 0;
      const newDraft = data.draft && data.draft.id !== this.draft?.id;
      if (!data.draft && this.draft) { this.composer?.remove(); this.composer = null; this.draftSlot.replaceChildren(); this.showZone(null); this.active = null; }
      this.draft = data.draft?.id === this.draft?.id ? this.draft : data.draft;
      this.draftButton.hidden = !this.draft;
      if (this.draft) {
        const anchor = this.draft.anchor;
        iconLabel(this.draftButton, 'edit', `未保存评论 · ${anchor.path}:L${anchor.startLine}`);
        this.draftButton.title = `返回未保存的评论\n${anchor.context.root} / ${anchor.path}\n${range(anchor)} · ${anchor.label}`;
      }
      this.draftChanged?.(this.draft);
      if (newDraft) { this.createComposer(); this.openDraft(true); }
      if (this.composer) for (const control of this.composer.querySelectorAll('button,textarea')) control.disabled = !!this.busy;
      this.drawMarks(); this.renderList();
      if (this.active?.revision && !this.items.some(c => c.id === this.active.id && c.revision === this.active.revision)) { this.showZone(null); this.active = null; }
      if (this.preview && !this.draft && !this.items.some(c => c.id === this.preview.id && c.revision === this.preview.revision)) { this.preview = null; this.draftSlot.replaceChildren(); }
      const revealKey = data.reveal && `${data.version}:${data.reveal.id}:${data.reveal.revision}`;
      if (revealKey && revealKey !== this.lastReveal && !this.draft) {
        const item = this.items.find(item => item.id === data.reveal.id && item.revision === data.reveal.revision);
        if (item && same(item.anchor.context, this.doc?.commentContext)) { this.lastReveal = revealKey; await this.open(item); }
      }
    } catch (error) { if (owner === this.sequence) this.message.textContent = `评论读取失败：${error}`; }
  }
  drawMarks() {
    this.panes.forEach((pane, side) => {
      pane.updateOptions({ glyphMargin: !!this.doc?.commentContext && (!this.doc.diff || this.items.some(c => same(c.anchor.context, this.doc.commentContext) && c.anchor.side === side)) });
      this.marks[side].set(this.items.filter(c => same(c.anchor.context, this.doc?.commentContext) && c.anchor.side === side).map(c => ({
        range: new this.monaco.Range(c.anchor.startLine, 1, c.anchor.endLine, 1), options: {
          isWholeLine: true, glyphMarginClassName: 'gitx-comment-glyph', glyphMarginHoverMessage: { value: '本地行评论 · 点击查看' }
        }
      })));
    });
  }
  status(anchor) {
    return noteStatus(anchor, this.doc);
  }
  renderList() {
    const focused = this.list.contains(document.activeElement) ? document.activeElement.closest('[data-note-action]') : null;
    const focusKey = focused && [focused.dataset.noteId, focused.dataset.noteAction];
    this.scope.options[1].disabled = !this.doc?.path;
    this.scope.options[2].disabled = !this.doc?.commentContext;
    if (this.scope.selectedOptions[0].disabled) this.scope.value = 'all';
    const items = selectNotes(this.items, { query: this.search.value, scope: this.scope.value, sort: this.sort.value }, this.doc, this.repositoryId);
    this.list.replaceChildren();
    this.count.textContent = `${items.length} / ${this.items.length} 条笔记 · 仅保存在本机`;
    if (!items.length) this.list.append(element('p', 'comment-empty', this.items.length ? '没有匹配的笔记。请调整搜索或范围。' : '此仓库尚无本地笔记。选择代码行后记录。'));
    for (const item of items.slice(0, this.visibleLimit)) {
      const card = element('article', 'comment-list-item');
      card.append(element('strong', '', item.anchor.path), element('div', 'comment-meta', `${range(item.anchor)} · ${this.status(item.anchor)}\n更新 ${new Date(item.updated).toLocaleString()}`), element('p', 'comment-summary', item.body));
      const actions = element('div', 'comment-actions');
      actions.append(button(same(item.anchor.context, this.doc?.commentContext) ? '定位' : '查看快照', () => this.open(item), 'locate'),
        button('编辑', () => this.request('edit', { id: item.id, revision: item.revision }), 'edit'),
        button('删除', () => {
          actions.replaceChildren(button('确认删除', () => this.request('delete', { id: item.id, revision: item.revision })), button('取消', () => this.renderList()));
        }, 'trash'));
      if (item.anchor.context.kind === 0 && !same(item.anchor.context, this.doc?.commentContext)) {
        const source = button('打开当前源码', () => this.request('open-source', { id: item.id, revision: item.revision, version: this.getState().version }), 'locate');
        source.disabled = !!this.draft;
        source.title = this.draft ? '请先保存或取消当前草稿' : '核对完整版本后定位；内容变化时继续保留原快照';
        actions.append(source);
      }
      for (const [index, control] of [...actions.children].entries()) { control.dataset.noteId = item.id; control.dataset.noteAction = String(index); }
      if (this.busy) for (const b of actions.children) b.disabled = true;
      card.append(actions); this.list.append(card);
    }
    if (items.length > this.visibleLimit) {
      const more = button(`显示更多（剩余 ${items.length - this.visibleLimit} 条）`, () => { this.visibleLimit += 50; this.renderList(); });
      more.className = 'note-more'; this.list.append(more);
    }
    if (focusKey) {
      const next = [...this.list.querySelectorAll('[data-note-action]')].find(control => control.dataset.noteId === focusKey[0] && control.dataset.noteAction === focusKey[1]);
      (next ?? this.search).focus({ preventScroll: true });
    }
  }
  snippet(anchor) {
    const details = element('details', 'comment-snippet');
    details.append(element('summary', '', `原代码 · ${anchor.label} · ${anchor.context.revision.slice(0, 8)}`), element('pre', '', anchor.code)); details.open = true;
    return details;
  }
  async open(item) {
    if (this.draft) { this.openDraft(true); return; }
    this.preview = null; this.draftSlot.replaceChildren();
    const card = element('section', 'comment-card comment-ui');
    const head = element('div', 'comment-card-head');
    head.append(element('span', '', `本地评论 · ${range(item.anchor)}`), button('收起', () => {
      this.showZone(null); card.remove(); this.active = null;
      if (this.preview === item) { this.preview = null; this.draftSlot.replaceChildren(); }
    }, 'collapse'));
    card.append(head, element('div', 'comment-body', item.body));
    const foot = element('div', 'comment-card-foot');
    foot.append(element('span', '', new Date(item.updated).toLocaleString()), button('编辑', () => this.request('edit', { id: item.id, revision: item.revision }), 'edit'));
    card.append(foot);
    if (same(item.anchor.context, this.doc?.commentContext)) {
      this.active = item; this.drawer.hidden = true;
      await this.reveal(item.anchor);
      if (this.active !== item || !same(item.anchor.context, this.doc?.commentContext)) return;
      this.showZone({ anchor: item.anchor, node: card });
      this.panes[item.anchor.side].focus();
    } else {
      this.showZone(null); this.active = null; this.preview = item; this.drawer.hidden = false;
      this.draftSlot.replaceChildren(element('div', 'comment-meta', `${item.anchor.path} · ${this.status(item.anchor)}`), this.snippet(item.anchor), card);
    }
  }
  createComposer() {
    const draft = this.draft;
    const card = this.composer = element('section', 'comment-card comment-composer comment-ui');
    const head = element('div', 'comment-card-head', `${draft.original ? '编辑' : '新评论'} · ${range(draft.anchor)}`);
    const textarea = element('textarea'); textarea.placeholder = '记录问题或修改建议…'; textarea.maxLength = 16000;
    textarea.setAttribute('aria-label', '本地行评论内容'); textarea.value = draft.original?.body ?? '';
    textarea.addEventListener('compositionstart', () => { this.composing = true; });
    textarea.addEventListener('compositionend', () => { this.composing = false; });
    const foot = element('div', 'comment-card-foot');
    foot.append(element('span', '', 'Enter 换行 · 仅保存在本机'), button('取消', () => this.request('cancel')),
      button('保存评论', () => { if (!this.composing) this.request('save', { id: draft.id, body: textarea.value }); }, 'save'));
    card.append(head, textarea, foot);
    for (const type of ['mousedown', 'pointerdown', 'dblclick', 'wheel']) card.addEventListener(type, event => event.stopPropagation());
  }
  async openDraft(focus) {
    if (!this.draft || !this.composer) return;
    if (focus) document.getElementById('diff-more').open = false;
    const draft = this.draft;
    if (same(draft.anchor.context, this.doc?.commentContext)) {
      this.drawer.hidden = true;
      await this.reveal(draft.anchor);
      if (this.draft !== draft || !same(draft.anchor.context, this.doc?.commentContext)) return;
      this.active = draft; this.showZone({ anchor: draft.anchor, node: this.composer });
    } else {
      this.showZone(null); this.active = null;
      this.composer.style.width = ''; this.composer.style.transform = '';
      this.draftSlot.replaceChildren(element('div', 'comment-meta', `原位置草稿 · ${draft.anchor.context.root} / ${draft.anchor.path}`), this.snippet(draft.anchor), this.composer);
      if (focus) this.drawer.hidden = false;
    }
    // Monaco mounts view zones on its next render. Focusing a still-hidden zone
    // is ignored by Chromium, so wait for that render without touching internals.
    if (focus) requestAnimationFrame(() => requestAnimationFrame(() => {
      if (this.draft === draft && this.composer?.isConnected) this.composer.querySelector('textarea').focus();
    }));
  }
  async focusDraft(id) {
    await this.refresh();
    if (this.draft?.id !== id) return;
    document.getElementById('diff-more').open = false;
    await this.openDraft(true);
  }
}
