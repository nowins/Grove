import { sideHeader } from './side-header.js';
import { GoToLine } from './go-to-line.js';
import './go-to-line.css';
import { recentFilesShortcut } from './recent-files-shortcut.js';
import { readingDirection, readingGesture } from './reading-navigation.js';
import { ContinuousDiff } from './continuous-diff.js';
import './continuous-diff.css';
import './node_modules/monaco-editor/esm/vs/nls/lang/zh-cn.js';
import * as monaco from 'monaco-editor/editor/editor.api.js';
import './node_modules/monaco-editor/esm/vs/base/browser/ui/codicons/codicon/codicon.css';
import 'monaco-editor/editor/contrib/find/browser/findController.js';
import 'monaco-editor/editor/contrib/folding/browser/folding.js';
import { prepareLanguage } from './language-definitions.js';
import { installNativeBridge } from './native-bridge.js';
import { MarkdownPreview } from './markdown-preview.js';
import './markdown-preview.css';

import { buildAlignment, nextChange } from './diff-layout.js';
import { registerDiffFolding, DiffFolding } from './diff-folding.js';
import { LocalCommentsView } from './comments.js';
import { DoubleShiftGesture } from './double-shift.js';
import { iconLabel } from './icons.js';
import { editorTheme } from './editor-theme.js';
import { editorTypography } from './editor-typography.js';
import { installTooltips } from './tooltips.js';
import { ToolFeedback } from './tool-feedback.js';
import './idea-font.css';
import { installFocusBoundary } from './focus-boundary.js';
import { DiffActions } from './diff-actions.js';
import { SourceMinimap } from './source-minimap.js';
import { DiffScrollMap, sourceBoundary, connectorPath } from './diff-scroll.js';
import './find-widget.css';
import { positionPopup } from './popup-position.js';
import { BlameMenu } from './blame-menu.js';
import { defaultBlameView, blameFields, blameFieldText } from './blame-fields.js';
import { canReuseSourceModel } from './source-update.js';
import { DocumentTransport } from './document-transport.js';
import { SourceTabs } from './source-tabs.js';
import { SourceEditing, renderedModelCount } from './source-editing.js';
import { SourceSymbols } from './source-symbols.js';

iconLabel(document.getElementById('previous-change'), 'up');
iconLabel(document.getElementById('next-change'), 'down');
iconLabel(document.querySelector('#diff-more summary'), 'more');
iconLabel(document.getElementById('stage-block'), 'plus', '暂存块');
iconLabel(document.getElementById('stage-lines'), 'plus', '暂存行');
iconLabel(document.getElementById('expand-all'), 'expand', '展开全部未变区域');
iconLabel(document.getElementById('collapse-all'), 'collapse', '折叠未变区域');
iconLabel(document.getElementById('toggle-minimap'), 'check', '显示代码缩略图');
iconLabel(document.getElementById('toggle-unchanged'), 'collapse');

self.MonacoEnvironment = { getWorker: () => new Worker(new URL('editor.worker.js', location.href)) };
monaco.editor.defineTheme('gitx', editorTheme);
const lineHeight = editorTypography.lineHeight, topPadding = 4;
const diffFolding = registerDiffFolding(monaco);
const options = {
  theme: 'gitx', readOnly: true, domReadOnly: true, automaticLayout: true,
  maxTokenizationLineLength: 20000,
  minimap: { enabled: false }, ...editorTypography,
  lineHeight, scrollBeyondLastLine: false, renderWhitespace: 'selection',
  padding: { top: topPadding, bottom: 14 }, contextmenu: false, links: false,
  find: { addExtraSpaceOnTop: false },
  unicodeHighlight: { ambiguousCharacters: false },
  stickyScroll: { enabled: false }, renderValidationDecorations: 'off',
  experimentalEditContextEnabled: false, wordWrap: 'off', accessibilitySupport: 'auto',
  lineNumbersMinChars: 3, lineDecorationsWidth: 0, glyphMargin: false, folding: false, overviewRulerLanes: 1,
  bracketPairColorization: { enabled: false }, guides: { indentation: false },
  // Both panes must have the same vertical scroll extent even when only one
  // has a horizontal scrollbar. Bottom padding already clears its 12 px track.
  scrollbar: { alwaysConsumeMouseWheel: false, horizontalScrollbarSize: 12, verticalScrollbarSize: 12,
    ignoreHorizontalScrollbarInContentHeight: true }
};
const left = monaco.editor.create(document.getElementById('old-editor'), { ...options, ariaLabel: '旧版本，只读' });
const right = monaco.editor.create(document.getElementById('new-editor'), { ...options, ariaLabel: '新版本，只读' });
const panes = [left, right];
const codeFontReady = document.fonts.load('14px GitXIdeaMono').catch(() => []).then(() => {
  monaco.editor.remeasureFonts(); panes.forEach(pane => pane.layout());
});
const marks = panes.map(pane => pane.createDecorationsCollection());
const activeMarks = panes.map(pane => pane.createDecorationsCollection());
let models = [null, null], zoneIds = [[], []];
let plan = null, selectedChange = -1, cursorRow = null, activePane = 1;
let folding = null, modelSequence = 0, navigation = Promise.resolve(), navigationEpoch = 0;
let syncing = false, applying = false, navigating = false, renderPending = false;
let generation = -1, loadSequence = 0, fetchController;
const toolFeedback = new ToolFeedback(document.getElementById('tool-feedback'));
installTooltips('#editor-tools button, #editor-tools summary, #editor-tools select');
let loading = false, pendingDirection = null, canStage = false, canRevert = false, canInclude = false;
let included = new Set(), fileIncluded = false, inclusionBusy = false, highlightMode = 'words', collapseUnchanged = false;
let blame = null;
let blameView = { ...defaultBlameView };
let showMinimap = true;
let scrollMap = null, expectedScrollTop = [null, null];
let sourceMode = false, sourcePath = '', readingEnabled = false;
let readingPublishedSide = -1, readingPublishedVersion = -1;
const send = value => window.invokeCSharpAction?.(JSON.stringify(value));
const continuousDiff = new ContinuousDiff({ host: document.getElementById('continuous-diff'), send });
document.getElementById('continuous-open').addEventListener('click', () => send({ type: 'continuous-diff', action: 'open', version: generation }));
installNativeBridge(window, () => {
  const version = generation;
  return error => { if (version === generation) send({ type: 'error', version, message: String(error) }); };
});
const sourceTabs = new SourceTabs({ getVersion: () => generation, send, isReady: () => !loading,
  beforeMenu: () => { goToLine.close(false); sourceSymbols.close(false); blameMenu.close(); more.open = false; } });
const markdown = new MarkdownPreview({ send, getVersion: () => generation, onSource: () => { panes.forEach(pane => pane.layout()); right.focus(); } });
const sourceSymbols = new SourceSymbols({ getState: () => ({ available: !loading && sourceMode && !plan && !!models[1],
  identity: `${generation}:${models[1]?.id}`, language: models[1]?.getLanguageId(), path: sourcePath, text: () => models[1]?.getValue() }),
  focus: () => panes[activePane].focus(), reveal: symbol => {
    activePane = 1;
    const range = models[1].validateRange({ startLineNumber: symbol.line, startColumn: symbol.column, endLineNumber: symbol.line, endColumn: symbol.column + symbol.length });
    right.setSelection(range); right.revealRangeInCenter(range, monaco.editor.ScrollType.Immediate);
  } });
const goToLine = new GoToLine({
  getState: () => ({ available: !loading && sourceMode && !plan && !!models[1] && !models[1].isDisposed(),
    version: generation, model: models[1], modelVersion: models[1]?.getVersionId(), path: sourcePath,
    lineCount: models[1]?.getLineCount(), line: right.getPosition()?.lineNumber ?? 1 }),
  beforeOpen: () => { sourceTabs.closeMenu(); sourceSymbols.close(false); more.open = false; blameMenu.close(); if (markdown.active) markdown.activate(false, false); },
  focus: () => { activePane = 1; right.focus(); },
  reveal: async line => {
    const model = models[1], version = generation;
    await right.getAction('editor.unfold')?.run({ selectionLines: [line - 1], levels: 99 });
    if (loading || generation !== version || models[1] !== model || model.isDisposed()) return;
    right.setPosition({ lineNumber: line, column: 1 });
    right.revealLineInCenter(line, monaco.editor.ScrollType.Immediate); right.focus();
  }
});
const blameMenu = new BlameMenu({ getState: () => ({ blame, loading, version: generation, blameView }), send, setView: setBlameView,
  focusEditor: () => panes[activePane].focus(), beforeOpen: () => { sourceTabs.closeMenu(); document.getElementById('diff-more').open = false; } });
const diffActions = new DiffActions({ panes, send, getState: () => ({ plan, canInclude, canRevert: canRevert && !inclusionBusy, loading, busy: inclusionBusy, generation, included,
  hidden: [0, 1].map(side => folding?.regions.filter(region => folding.collapsed.has(region.id)).map(region => ({ start: folding.start(region, side), end: folding.start(region, side) + region.count - 1 })) ?? []) }),
  selectLine: (side, line) => { activePane = side; panes[side].setPosition({ lineNumber: line, column: 1 }); panes[side].focus(); } });
const sourceMinimap = new SourceMinimap({ panes, reveal: async (side, line) => {
  const owner = modelSequence;
  if (folding) {
    const desired = new Set(folding.collapsed);
    folding.regions.forEach(region => { const start = folding.start(region, side); if (line >= start && line < start + region.count) desired.delete(region.id); });
    if (!folding.sameState(desired, folding.collapsed)) await folding.setState(desired, side, false);
  }
  if (owner !== modelSequence) return;
  activePane = side; panes[side].revealLineInCenter(line); panes[side].focus();
} });
let commentZone = null;
const comments = new LocalCommentsView({ panes, monaco, send,
  draftChanged: draft => sourceTabs.setDraft(draft),
  getState: () => ({ side: activePane, version: generation, loading }),
  showZone: value => {
    commentZone = value;
    const zones = [[], []];
    if (value) zones[value.anchor.side].push({ afterLineNumber: value.anchor.endLine, height: 11, comment: true });
    panes.forEach((pane, side) => installZones(pane, zones[side], side));
    scrollMap?.invalidate(); syncScrollFrom(activePane);
    scheduleConnectors();
  },
  reveal: async anchor => {
    const owner = modelSequence, currentFolding = folding;
    if (currentFolding) {
      const desired = new Set(currentFolding.collapsed);
      currentFolding.regions.forEach(region => {
        const start = currentFolding.start(region, anchor.side);
        if (anchor.endLine >= start && anchor.startLine < start + region.count) desired.delete(region.id);
      });
      if (!currentFolding.sameState(desired, currentFolding.collapsed)) await currentFolding.setState(desired, anchor.side, false);
    } else await panes[anchor.side].getAction('editor.unfold')?.run({ selectionLines: [anchor.endLine - 1], levels: 99 });
    if (owner !== modelSequence) return;
    activePane = anchor.side;
    panes[anchor.side].setSelection(new monaco.Range(anchor.startLine, 1, anchor.endLine, panes[anchor.side].getModel().getLineMaxColumn(anchor.endLine)));
    panes[anchor.side].revealLineNearTop(anchor.endLine);
  }
});

document.getElementById('fold-controls').append(comments.toolbar);

// The embedded page never exposes browser navigation/printing commands.
// Editor commands are explicit; ordinary clicks and selection cannot create notes.
document.addEventListener('contextmenu', event => event.preventDefault());
let canAnnotate = false;
panes.forEach((pane, side) => pane.onContextMenu(event => {
  const type = event.target.type, targets = monaco.editor.MouseTargetType;
  const gutter = [targets.GUTTER_LINE_NUMBERS, targets.GUTTER_GLYPH_MARGIN, targets.GUTTER_LINE_DECORATIONS].includes(type);
  if (loading || !event.target.position || !gutter && ![targets.CONTENT_TEXT, targets.CONTENT_EMPTY].includes(type)) return;
  event.event.preventDefault(); event.event.stopPropagation();
  const line = event.target.position.lineNumber, selection = pane.getSelection();
  activePane = side;
  if (!selection || line < selection.startLineNumber || line > selection.endLineNumber)
    pane.setPosition(event.target.position);
  const actions = [];
  if (gutter && canAnnotate && side === 1) actions.push({ label: blame ? '关闭 Git Blame 注解' : '使用 Git Blame 注解', icon: 'history',
    run: () => send({ type: 'blame', version: generation }) });
  if (comments.doc?.commentContext) actions.push({ label: '添加本地笔记', icon: 'comment', run: () => comments.begin(side) });
  blameMenu.showEditor(actions, { left: event.event.posx, right: event.event.posx, top: event.event.posy, bottom: event.event.posy });
}));

function blockRanges(block) {
  return panes.map((pane, side) => {
    const start = side === 0 ? block.oldStart : block.newStart, count = side === 0 ? block.oldCount : block.newCount;
    const total = side === 0 ? plan.oldLines.length : plan.newLines.length;
    return [sourceBoundary(pane, start, total, lineHeight) - pane.getScrollTop(),
      sourceBoundary(pane, start + count, total, lineHeight) - pane.getScrollTop()];
  });
}

function syncScrollFrom(side) {
  if (!scrollMap || applying || panes.some((pane, index) => pane.getModel() !== models[index])) return;
  const other = 1 - side;
  const top = scrollMap.target(panes, side, panes[side].getScrollTop(), panes[other].getScrollTop());
  if (Math.abs(panes[other].getScrollTop() - top) < .5) return;
  expectedScrollTop[other] = top;
  const previous = syncing; syncing = true;
  panes[other].setScrollTop(top, monaco.editor.ScrollType.Immediate);
  syncing = previous;
}

function changeFromCursor(direction) {
  const blocks = plan.blocks.map(block => ({ ...block, visualStart: activePane === 0 ? block.oldStart : block.newStart }));
  return nextChange(blocks, selectedChange, direction, cursorRow);
}

function scheduleConnectors() {
  if (renderPending) return;
  renderPending = true;
  requestAnimationFrame(() => {
    renderPending = false;
    const svg = document.getElementById('connectors');
    svg.replaceChildren();
    renderBlame();
    sourceMinimap.renderViewport();
    diffActions.render(blockRanges);
    if (!plan) return;
    const viewportHeight = svg.clientHeight;
    for (const block of plan.blocks) {
      const [[oldTop, oldBottom], [newTop, newBottom]] = blockRanges(block);
      if (Math.min(oldTop, newTop) > viewportHeight || Math.max(oldBottom, newBottom) < 0) continue;
      const polygon = document.createElementNS('http://www.w3.org/2000/svg', 'path');
      polygon.setAttribute('d', connectorPath(svg.clientWidth, oldTop, oldBottom, newTop, newBottom));
      const kind = plan.movedOldBlocks.has(block.index) ? 'moved' : block.oldCount === 0 ? 'added' : block.newCount === 0 ? 'deleted' : 'modified';
      polygon.setAttribute('class', `connector ${kind}${block.index === selectedChange ? ' active' : ''}`);
      svg.append(polygon);
    }
  });
}

function updateMinimaps() {
  sourceMinimap.update(plan, showMinimap);
}
panes.forEach((pane, side) => {
  let caretFrame = null, lastPosition = null, lastVersion = -1;
  const sendCaret = (position, navigation = false, force = false) => {
    if (!loading && position && (force || pane.hasWidgetFocus())) {
      send({ type: 'caret', version: generation, side, line: position.lineNumber, column: position.column, navigation: readingEnabled && navigation });
      readingPublishedSide = side; readingPublishedVersion = generation;
    }
  };
  const publishCaret = () => {
    if (caretFrame) return;
    const version = generation;
    caretFrame = requestAnimationFrame(() => {
      caretFrame = null;
      const position = pane.getPosition();
      if (version === generation) sendCaret(position);
    });
  };
  pane.onDidFocusEditorWidget(() => { activePane = side; publishCaret(); });
  pane.onDidChangeCursorPosition(event => {
    if (!loading && !applying && !folding?.busy && readingGesture(event)) {
      const force = readingEnabled && (!plan || side === activePane || event.source === 'mouse');
      if (lastVersion === generation && readingPublishedVersion === generation && readingPublishedSide === side) sendCaret(lastPosition, false, force);
      sendCaret(event.position, true, force);
    } else publishCaret();
    lastPosition = event.position; lastVersion = generation;
    if (!applying && !navigating && !folding?.busy && plan) {
      cursorRow = event.position.lineNumber - 1;
      selectedChange = -1;
      activeMarks.forEach(mark => mark.clear());
      updateCounter(); scheduleConnectors();
    }
  });
  pane.onDidScrollChange(event => {
    if (event.scrollTopChanged || event.scrollLeftChanged) blameMenu.close();
    sizeCommentCard();
    const expected = event.scrollTopChanged && expectedScrollTop[side] !== null && Math.abs(event.scrollTop - expectedScrollTop[side]) < 1.5;
    if (event.scrollTopChanged) expectedScrollTop[side] = null;
    if (!applying && !syncing && !folding?.busy && plan) {
      const other = panes[1 - side];
      if (event.scrollTopChanged && !expected) syncScrollFrom(side);
      if (event.scrollLeftChanged) {
        syncing = true; other.setScrollLeft(event.scrollLeft, monaco.editor.ScrollType.Immediate); syncing = false;
      }
    }
    scheduleConnectors();
  });
  pane.onDidLayoutChange(() => { scrollMap?.invalidate(); updateMinimaps(); sizeCommentCard(); scheduleConnectors(); });
});

function renderBlame() {
  const gutter = document.getElementById('blame-gutter'); gutter.replaceChildren();
  if (!blame || !models[1]) return;
  const top = right.getScrollTop();
  for (const range of right.getVisibleRanges()) {
    for (let line = range.startLineNumber; line <= range.endLineNumber; line++) {
      const info = blame[line - 1]; if (!info) continue;
      const row = document.createElement('div');
      const local = /^0+$/.test(info.sha);
      row.className = `blame-row${local ? ' local' : ''}`;
      row.dataset.line = String(line); row.tabIndex = 0;
      row.style.top = `${right.getTopForLineNumber(line) - top}px`;
      if (local) { const label = document.createElement('span'); label.className = 'blame-local'; label.textContent = '未提交改动'; row.append(label); }
      else for (const field of blameFields.filter(field => blameView[field])) {
        const value = document.createElement('span'); value.className = `blame-field ${field}`; value.textContent = blameFieldText(info, field); row.append(value);
      }
      row.title = `${info.sha}\n${info.author} · ${info.when}\n${info.summary}\n源行 ${info.originalLine}`;
      gutter.append(row);
    }
  }
}

function sizeBlameColumns() {
  if (!blame) return;
  const gutter = document.getElementById('blame-gutter'), context = document.createElement('canvas').getContext('2d');
  context.font = getComputedStyle(gutter).font;
  const widths = blameFields.filter(field => blameView[field]).map(field => {
    const values = [...new Set(blame.map(info => blameFieldText(info, field).slice(0, 80)))];
    return Math.ceil(Math.min(160, Math.max(36, ...values.map(value => context.measureText(value).width)))) + 4;
  });
  document.body.style.setProperty('--blame-columns', widths.map(width => `minmax(0,${width}fr)`).join(' '));
  document.body.style.setProperty('--blame-width', `${Math.min(widths.reduce((sum, width) => sum + width, 16) + (widths.length - 1) * 8, Math.max(72, document.documentElement.clientWidth * .45))}px`);
}
function setBlameView(value) {
  blameView = { ...defaultBlameView, ...value }; sizeBlameColumns();
  panes.forEach(pane => pane.layout()); renderBlame();
  if (blameMenu.opened && !blameMenu.submenu.hidden) blameMenu.refreshFields();
}
window.addEventListener('resize', sizeBlameColumns);

const blameGutter = document.getElementById('blame-gutter');
blameGutter.addEventListener('contextmenu', event => {
  const row = event.target.closest('.blame-row');
  if (!row) return;
  event.preventDefault();
  blameMenu.show(Number(row.dataset.line), { left: event.clientX, right: event.clientX, top: event.clientY, bottom: event.clientY });
});
blameGutter.addEventListener('keydown', event => {
  if (event.key !== 'ContextMenu' && !(event.shiftKey && event.key === 'F10')) return;
  const row = event.target.closest('.blame-row'); if (!row) return;
  event.preventDefault(); blameMenu.show(Number(row.dataset.line), row.getBoundingClientRect());
});

function installZones(pane, zones, side) {
  pane.changeViewZones(accessor => {
    zoneIds[side].forEach(id => accessor.removeZone(id));
    zoneIds[side] = zones.map(zone => {
      if (zone.comment) {
        const node = side === commentZone.anchor.side ? commentZone.node : document.createElement('div');
        if (side !== commentZone.anchor.side) node.className = 'comment-padding';
        else sizeCommentCard();
        const host = document.createElement('div'); host.className = 'gitx-comment-zone'; host.append(node);
        return accessor.addZone({ afterLineNumber: zone.afterLineNumber, heightInLines: zone.height,
          ordinal: zone.ordinal, domNode: host, suppressMouseDown: false });
      }
      const node = document.createElement('div');
      node.className = 'gitx-gap';
      node.setAttribute('aria-label', '此侧无对应行');
      if (zone.index >= 0) node.addEventListener('click', () => queueNavigation(() => selectChange(zone.index)));
      const margin = document.createElement('div');
      margin.className = 'gitx-gap-margin';
      return accessor.addZone({ afterLineNumber: zone.afterLineNumber, heightInLines: zone.height,
        ordinal: zone.ordinal, domNode: node, marginDomNode: margin, suppressMouseDown: true });
    });
  });
}

function sizeCommentCard() {
  if (!commentZone) return;
  const pane = panes[commentZone.anchor.side];
  // Zones span scrollWidth; cards use viewport width and remain readable
  // while long source lines scroll horizontally.
  commentZone.node.style.width = `${Math.max(0, pane.getLayoutInfo().contentWidth - 8)}px`;
  commentZone.node.style.transform = `translateX(${pane.getScrollLeft()}px)`;
}

function changedMarks(blocks, side) {
  if (highlightMode === 'none') return [];
  const result = [];
  const addedTexts = new Set(blocks.flatMap(block => block.rows.filter(row => row.newLine !== null).map(row => plan.newLines[row.newLine - 1].trim())));
  for (const block of blocks) {
    const count = side === 0 ? block.oldCount : block.newCount, start = side === 0 ? block.oldStart : block.newStart;
    const total = side === 0 ? plan.oldLines.length : plan.newLines.length;
    if (!count && total) {
      const line = Math.min(total, start + 1), kind = side === 0 ? 'added' : plan.movedOldBlocks.has(block.index) ? 'moved' : 'deleted';
      result.push({ range: new monaco.Range(line, 1, line, 1), options: { isWholeLine: true,
        className: `gitx-${kind}-boundary${start >= total ? '-end' : ''}` } });
    }
    for (const row of block.rows) {
      const line = side === 0 ? row.oldLine : row.newLine;
      if (line === null) continue;
      const kind = row.oldLine !== null && row.newLine !== null ? 'modified' : side === 0 ? 'deleted' : 'added';
      const moved = side === 0 && row.newLine === null && addedTexts.has(plan.oldLines[line - 1].trim());
      const color = kind === 'modified' ? '#5277a5' : side === 0 ? '#a95860' : '#528c64';
      result.push({ range: new monaco.Range(line, 1, line, 1), options: {
        isWholeLine: true, className: moved ? 'gitx-moved-line' : `gitx-${kind}-line`,
        overviewRuler: { color, position: monaco.editor.OverviewRulerLane.Full },
        minimap: { color, position: monaco.editor.MinimapPosition.Inline }
      } });
    }
    for (const change of block.inlineChanges) {
      if (highlightMode !== 'words') continue;
      const line = side === 0 ? change.oldLine : change.newLine;
      const spans = side === 0 ? change.oldWordSpans ?? change.oldSpans : change.newWordSpans ?? change.newSpans;
      for (const span of spans) result.push({ range: new monaco.Range(line, span.startColumn, line, span.endColumn),
        options: { inlineClassName: 'gitx-modified-text' } });
    }
  }
  return result;
}


const diffViews = new Map();
let displayedDiffKey = null;
let displayedSource = null, displayedSourceTab = null, sourcePublished = null;
const textEditing = new SourceEditing({createModel: (text, language) => monaco.editor.createModel(text,language), getVersion: () => generation, changed: updateTextEditing});
const saveButton = document.getElementById('source-save'), saveStatus = document.getElementById('source-save-state');
let markdownEditTimer;
function requestTextSave() {
  const s=textEditing.active;if(loading || !s || !s.info.canEdit || s.busy || composingSearch)return;
  send({type:'source-save',id:s.id,version:generation});
}
saveButton.addEventListener('click',requestTextSave);
function updateTextEditing() {
  const s=textEditing.active, editable=!loading && sourceMode && !plan && textEditing.editable(s), dirty=!!s&&textEditing.dirty(s);
  right.updateOptions({readOnly:!editable,domReadOnly:!editable,ariaLabel:editable?'工作区文本，可编辑':'文件，只读'});
  sourceTabs.setTextStates(textEditing.metadata());
  saveButton.hidden=!s;saveStatus.hidden=!s&&!sourcePublished?.editing?.reason;
  saveButton.disabled=!s || !dirty || s.busy || !s.info.canEdit || !!s.readOnlyReason || loading;
  saveButton.title=/Mac/.test(navigator.platform)?'保存文件 · ⌘S':'保存文件 · Ctrl+S';
  saveStatus.textContent=s?(s.error || s.readOnlyReason || (s.busy?'正在保存…':s.info.fingerprint!==s.disk?'磁盘已变化 · 草稿保留':dirty?'未保存':'已保存')+` · UTF-8${s.info.bom?' BOM':''} · ${s.info.newline}`):(sourcePublished?.editing?.reason??'');
  saveStatus.title=saveStatus.textContent;saveStatus.classList.toggle('attention',!!s&&(dirty||!!s.error||s.info.fingerprint!==s.disk));
  if(s)send({type:'source-edit-state',id:s.id,version:generation,editable:!!s.info.canEdit&&!s.readOnlyReason,dirty});
  if(s && textEditing.unsafe(s)) {
    canAnnotate=false;blame=null;document.body.classList.remove('blame-mode');blameMenu.close();renderBlame();
    if(comments.doc?.commentContext)comments.attach({...comments.doc,commentContext:null,blame:null});
  } else if(s && sourcePublished && sourcePublished.editing?.fingerprint===s.info.fingerprint) {
    if(comments.doc && !comments.doc.commentContext)comments.attach(sourcePublished);
  }
  clearTimeout(markdownEditTimer);
  if(s&&sourcePublished?.language==='markdown')markdownEditTimer=setTimeout(()=>{
    if(textEditing.active===s&&!loading)markdown.update({...sourcePublished,text:s.model.getValue(),isNotice:false},generation,s.id);
  },160);
}
window.addEventListener('beforeunload',()=>textEditing.dispose());
function display(doc, toolbar) {
  document.getElementById('continuous-open').hidden = !toolbar?.canContinuousDiff;
  continuousDiff.show(toolbar?.continuousDiff);
  sourcePublished=doc;
  const editing=textEditing.prepare(doc,toolbar);doc=editing.doc;

  const reuseSource = !plan && models[1] && toolbar?.sourceMode
    && canReuseSourceModel(displayedSource, doc, displayedSourceTab, toolbar.activeSourceTabId);
  if (displayedDiffKey && plan) {
    diffViews.delete(displayedDiffKey);
    diffViews.set(displayedDiffKey, { views: panes.map(pane => ({ selection: pane.getSelection(), top: pane.getScrollTop(), left: pane.getScrollLeft() })),
      expanded: folding?.regions.filter(region => !folding.collapsed.has(region.id)) ?? [], side: activePane });
    if (diffViews.size > 32) diffViews.delete(diffViews.keys().next().value);
  }
  const identity = doc.commentContext;
  sourceMode = !!toolbar?.sourceMode; sourcePath = doc.path ?? '';
  readingEnabled = !!toolbar?.readingNavigation;
  document.body.classList.toggle('file-history-diff', !!toolbar?.fileHistory);
  document.getElementById('source-history').hidden = !sourceMode;
  document.getElementById('source-history').disabled = !!doc.isNotice || !sourcePath;
  displayedDiffKey = doc.isDiff && identity ? JSON.stringify([identity.repositoryId, identity.path, identity.kind, identity.snapshot]) : null;
  const savedDiff = displayedDiffKey ? diffViews.get(displayedDiffKey) : null;
  blameMenu.close();
  const sourceView = sourceTabs.begin(toolbar, doc, () => right.getModel() ? right.saveViewState() : null);
  const path = toolbar?.path ?? doc.path ?? '选择文件', slash = path.lastIndexOf('/');
  document.getElementById('file-path').title = path;
  document.getElementById('file-parent').textContent = slash < 0 ? '' : path.slice(0, slash + 1);
  document.getElementById('file-name').textContent = path.slice(slash + 1);
  document.getElementById('tab-caption').textContent = doc.canInclude ? '提交:' : doc.isDiff ? '差异:' : '文件:';
  canAnnotate = !!toolbar?.canBlame;
  document.getElementById('diff-more').open = false;
  if (!reuseSource) comments.detach();
  comments.setSourceMode(!!toolbar?.sourceMode);
  blame = !doc.diff ? doc.blame : null;
  document.body.classList.toggle('blame-mode', !!blame);
  blameView = { ...defaultBlameView, ...toolbar?.blameView }; sizeBlameColumns();
  const revision = document.getElementById('source-revision'); revision.hidden = !doc.sourceRevision;
  revision.textContent = doc.sourceRevision ? `修订 ${doc.sourceRevision.slice(0, 8)} · 只读` : '';
  revision.title = doc.sourceRevision ?? '';
  displayedSource = toolbar?.sourceMode && !doc.diff && !doc.isNotice ? doc : null;
  displayedSourceTab = toolbar?.activeSourceTabId;
  if (reuseSource) {
    // Blame changes the gutter, not the source model. Keep tokenization,
    // folds, selection, find state and inline note zones intact.
    updateTextEditing(); panes.forEach(pane => pane.layout()); renderBlame();
    return Promise.resolve();
  }
  const expanded = savedDiff?.expanded ?? (doc.preserveView && folding ? folding.regions.filter(region => !folding.collapsed.has(region.id)) : []);
  canStage = !!doc.canStage;
  canInclude = !!doc.canInclude;
  included = new Set((toolbar?.includedLines ?? []).map(line => `${line.side}:${line.line}`));
  fileIncluded = toolbar && 'included' in toolbar ? toolbar.included : false;
  document.querySelector('.include-file-label').hidden = !canInclude;
  document.getElementById('new-label').style.display = canInclude ? 'none' : 'inline-flex';
  canRevert = (canStage || canInclude) && !!doc.diff?.old.exists && !!doc.diff?.new.exists;
  diffActions.reset();
  document.getElementById('stage-block').hidden = !canStage;
  document.getElementById('stage-lines').hidden = !canStage;
  const views = savedDiff?.views ?? (doc.preserveView ? panes.map(pane => ({ selection: pane.getSelection(), top: pane.getScrollTop(), left: pane.getScrollLeft() })) : null);
  const previousSide = activePane, focusEpoch = navigationEpoch;
  const findInput = doc.preserveView && (document.activeElement instanceof HTMLInputElement || document.activeElement instanceof HTMLTextAreaElement) && document.activeElement.closest('.find-widget')
    ? { start: document.activeElement.selectionStart, end: document.activeElement.selectionEnd } : null;
  applying = true;
  folding?.dispose(); folding = null;
  selectedChange = -1; cursorRow = null; activePane = savedDiff?.side ?? (doc.preserveView ? previousSide : 1);
  const previous = models;
  plan = doc.diff ? buildAlignment(doc.diff) : null;
  if (plan) {
    const added = new Set(plan.blocks.flatMap(block => block.rows.filter(row => row.newLine !== null).map(row => plan.newLines[row.newLine - 1].trim())));
    plan.movedOldBlocks = new Set(plan.blocks.filter(block => block.oldCount && !block.newCount && block.rows.every(row => added.has(plan.oldLines[row.oldLine - 1].trim()))).map(block => block.index));
  }
  scrollMap = plan ? new DiffScrollMap(plan, lineHeight, topPadding) : null; expectedScrollTop = [null, null];
  updateMinimaps();
  const notice = !!doc.isNotice;
  document.getElementById('document-notice').hidden = !notice;
  document.getElementById('document-notice').textContent = notice ? doc.text : '';
  document.getElementById('workspace').hidden = notice;
  document.body.classList.toggle('diff-mode', !!plan);
  marks.forEach(mark => mark.clear()); activeMarks.forEach(mark => mark.clear());
  panes.forEach((pane, side) => installZones(pane, [], side));
  // Keep source models free of alignment filler. Only view zones consume blank screen rows.
  const modelId = ++modelSequence;
  models = plan ? [monaco.editor.createModel(plan.oldLines.join('\n'), doc.language, monaco.Uri.parse(`gitx-diff://document/${modelId}/old`)),
    monaco.editor.createModel(plan.newLines.join('\n'), doc.language, monaco.Uri.parse(`gitx-diff://document/${modelId}/new`))]
    : [null, notice ? null : editing.model ?? monaco.editor.createModel(doc.text, doc.language)];
  models.forEach(model => model?.updateOptions({ bracketColorizationOptions: { enabled: false, independentColorPoolPerBracketType: false } }));
  sourceSymbols.update();
  if (plan) folding = new DiffFolding({ monaco, panes, models, plan, ranges: diffFolding.ranges,
    onLayout: () => { scrollMap?.invalidate(); updateFoldControls(); scheduleConnectors(); },
    syncScroll: syncScrollFrom,
    onCursorMoved: (line, side) => { cursorRow = line - 1; },
    onError: message => send({ type: 'error', message }) });
  panes.forEach((pane, side) => {
    pane.setModel(models[side]);
    pane.updateOptions({ readOnly: side === 0 || !textEditing.editable(), domReadOnly: side === 0 || !textEditing.editable() });
    const lines = plan ? (side === 0 ? plan.oldLines.length : plan.newLines.length) : null;
    pane.updateOptions({ lineNumbers: plan ? 'off' : 'on', folding: true,
      showFoldingControls: plan ? 'never' : 'mouseover', foldingHighlight: !plan, unfoldOnClickAfterEndOfLine: !plan });
  });
  previous.forEach(model => { if(model && !models.includes(model) && !textEditing.owns(model))model.dispose(); });
  textEditing.disposeRetired(models);updateTextEditing();
  if (plan) {
    sideHeader('old-label', doc.diff.old, doc.diff.new); sideHeader('new-label', doc.diff.new, doc.diff.old);
    installZones(left, [], 0); installZones(right, [], 1);
    marks[0].set(changedMarks(plan.blocks, 0)); marks[1].set(changedMarks(plan.blocks, 1));
    folding.mount(collapseUnchanged);
  }
  panes.forEach(pane => { pane.layout(); pane.setScrollPosition({ scrollTop: 0, scrollLeft: 0 }, monaco.editor.ScrollType.Immediate); });
  applying = false;
  updateCounter(); updateFoldControls(); scheduleConnectors();
  return Promise.resolve(folding?.pending).then(async () => {
    if (modelId !== modelSequence) return;
    if (views) {
      // A saved Monaco view also contains per-pane folding state. Replaying it
      // on a different diff can leave one side expanded. Restore only selection
      // and scrolling, and reconcile the new folds through the shared owner.
      if (folding) folding.busy = true;
      panes.forEach((pane, side) => { if (models[side] && views[side].selection) pane.setSelection(models[side].validateRange(views[side].selection)); });
      if (folding) {
        const desired = savedDiff ? new Set(folding.regions.map(region => region.id)) : new Set(folding.collapsed);
        folding.regions.forEach(region => {
          if (expanded.some(previous => previous.oldStart === region.oldStart && previous.newStart === region.newStart && previous.count === region.count)) desired.delete(region.id);
        });
        folding.regions.forEach(region => panes.forEach((pane, side) => {
          const selection = pane.getSelection(), start = folding.start(region, side);
          if (selection && selection.startLineNumber < start + region.count && selection.endLineNumber >= start) desired.delete(region.id);
        }));
        folding.busy = false;
        if (!folding.sameState(desired, folding.collapsed)) await folding.setState(desired, activePane, false);
      }
      if (modelId !== modelSequence) return;
      syncing = true;
      panes.forEach((pane, side) => pane.setScrollPosition({ scrollTop: views[side].top, scrollLeft: views[side].left }, monaco.editor.ScrollType.Immediate));
      syncing = false;
    }
    else if (sourceView && models[1]) right.restoreViewState(sourceView);
    else if (plan?.blocks.length) selectChange(0, false);
    if (findInput && focusEpoch === navigationEpoch && document.hasFocus()) {
      panes[activePane].trigger('refresh', 'actions.find', null);
      if (document.activeElement instanceof HTMLInputElement || document.activeElement instanceof HTMLTextAreaElement) document.activeElement.setSelectionRange(findInput.start, findInput.end);
    }
    comments.attach(doc);updateTextEditing();
  });
}

function updateFoldControls() {
  const disabled = !folding?.regions.length || folding.busy;
  document.getElementById('expand-all').disabled = disabled || folding.collapsed.size === 0;
  document.getElementById('collapse-all').disabled = disabled || folding.collapsed.size === folding.regions.length;
  const collapsed = !!folding?.collapsed.size;
  document.getElementById('toggle-unchanged').setAttribute('aria-pressed', String(collapsed));
  document.getElementById('toggle-unchanged').title = collapsed ? '展开未变化片段' : '折叠未变化片段';
}
document.getElementById('toggle-unchanged').addEventListener('click', () => {
  collapseUnchanged = !folding?.collapsed.size; folding?.setAll(collapseUnchanged, activePane);
});
document.getElementById('whitespace-mode').addEventListener('change', event => {
  toolFeedback.show('正在重新比较…');
  send({ type: 'whitespace', version: generation, value: Number(event.currentTarget.value) });
});
document.getElementById('highlight-mode').addEventListener('change', event => {
  highlightMode = event.currentTarget.value;
  if (plan) marks.forEach((mark, side) => mark.set(changedMarks(plan.blocks, side)));
  toolFeedback.show(event.currentTarget.selectedOptions[0].textContent);
});
document.getElementById('close-file').addEventListener('click', () => send({ type: 'close-document', version: generation }));
document.getElementById('include-file').addEventListener('change', () => { if (canInclude && !loading) send({ type: 'include-file', version: generation }); });
const more = document.getElementById('diff-more');
const morePopup = document.getElementById('fold-controls');
const moreSummary = more.querySelector('summary');
moreSummary.setAttribute('aria-haspopup', 'menu');
moreSummary.setAttribute('aria-controls', morePopup.id);
morePopup.setAttribute('role', 'menu');
for (const button of morePopup.querySelectorAll('button')) {
  const checked = button.getAttribute('aria-pressed');
  button.setAttribute('role', checked === null ? 'menuitem' : 'menuitemcheckbox');
  if (checked !== null) button.setAttribute('aria-checked', checked);
}
function moreItems() { return [...morePopup.querySelectorAll('button')].filter(button => !button.disabled && button.getClientRects().length); }
function closeMore(restoreFocus = false) { more.open = false; if (restoreFocus) moreSummary.focus(); }
function navigateMore(event) {
  if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key) || event.ctrlKey || event.altKey || event.shiftKey) return false;
  if (!more.open && event.target !== moreSummary) return false;
  event.preventDefault(); event.stopImmediatePropagation();
  more.classList.add('keyboard-open');
  more.open = true; positionMore();
  const buttons = moreItems(), current = buttons.indexOf(document.activeElement);
  const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1
    : current < 0 ? (event.key === 'ArrowUp' ? buttons.length - 1 : 0)
    : (current + (event.key === 'ArrowUp' ? -1 : 1) + buttons.length) % buttons.length;
  buttons[next]?.focus(); return true;
}
function positionMore() { if (more.open) positionPopup(morePopup, more.querySelector('summary').getBoundingClientRect(), true); }
moreSummary.addEventListener('click', event => {
  more.classList.toggle('keyboard-open', !event.detail);
  event.preventDefault(); more.open = !more.open;
  if (more.open) { blameMenu.close(); positionMore(); if (!event.detail) moreItems()[0]?.focus(); }
});
more.addEventListener('toggle', () => { moreSummary.setAttribute('aria-expanded', String(more.open)); if (more.open) { blameMenu.close(); positionMore(); } });
more.addEventListener('focusout', event => { if (event.relatedTarget && !more.contains(event.relatedTarget)) closeMore(); });
// Leaving the WebView for a native control has no DOM relatedTarget.
window.addEventListener('blur', () => closeMore());
const moreSizer = new ResizeObserver(positionMore); moreSizer.observe(morePopup);
window.addEventListener('beforeunload', () => moreSizer.disconnect(), { once: true });
window.addEventListener('resize', positionMore);
document.getElementById('toggle-minimap').addEventListener('click', event => {
  showMinimap = !showMinimap;
  event.currentTarget.setAttribute('aria-pressed', String(showMinimap));
  event.currentTarget.setAttribute('aria-checked', String(showMinimap));
  closeMore(true); updateMinimaps();
});
for (const [id, collapsed] of [['expand-all', false], ['collapse-all', true]]) {
  document.getElementById(id).addEventListener('click', () => { more.open = false; folding?.setAll(collapsed, activePane); panes[activePane].focus(); });
}
document.addEventListener('pointerdown', event => { if (!more.contains(event.target)) more.open = false; });
document.getElementById('previous-change').addEventListener('click', () => navigate(-1));
document.getElementById('next-change').addEventListener('click', () => navigate(1));
iconLabel(document.getElementById('source-history'), 'history', '文件历史');
document.getElementById('source-history').addEventListener('click', () => { if (!loading && sourceMode) send({ type: 'source-history', version: generation }); });
function stageSelection(wholeBlock) {
  if (!canStage || !plan || loading) return;
  const selection = panes[activePane].getSelection();
  if (!selection) return;
  let block = null;
  if (wholeBlock) {
    const line = panes[activePane].getPosition().lineNumber;
    const key = activePane === 0 ? 'oldLine' : 'newLine';
    block = selectedChange >= 0 ? selectedChange : plan.blocks.findIndex(item => item.rows.some(row => row[key] === line));
    if (block < 0) { send({ type: 'error', message: '请按 F7 选择变更块，或将光标移到改动行。' }); return; }
  }
  send({ type: 'stage-selection', version: generation, block, side: activePane, startLine: selection.startLineNumber,
    endLine: selection.endLineNumber - (selection.endColumn === 1 && selection.endLineNumber > selection.startLineNumber ? 1 : 0) });
}
function revertCurrentBlock() {
  if (!canRevert || !plan || loading) return;
  const line = panes[activePane].getPosition()?.lineNumber;
  const key = activePane === 0 ? 'oldLine' : 'newLine';
  const block = selectedChange >= 0 ? selectedChange : plan.blocks.findIndex(item => item.rows.some(row => row[key] === line));
  if (block < 0) { send({ type: 'error', message: '请按 F7 选择要回退的变更块，或将光标移到改动行。' }); return; }
  send({ type: 'revert-block', version: generation, block });
}
document.getElementById('stage-block').addEventListener('click', () => stageSelection(true));
document.getElementById('stage-lines').addEventListener('click', () => stageSelection(false));

function updateCounter() {
  const count = plan?.blocks.length ?? 0;
  const selected = plan?.blocks.filter(block => block.rows.some(row => included.has(row.newLine !== null ? `1:${row.newLine}` : `0:${row.oldLine}`))).length ?? 0;
  document.getElementById('change-count').textContent = `${count} 处差异${canInclude ? `，${selected} 处已包含` : ''}`;
  document.getElementById('change-count').title = count ? `共 ${count} 个变更块` : '没有文本差异';
  document.getElementById('previous-change').disabled = document.getElementById('next-change').disabled = loading || !count;
  document.getElementById('whitespace-mode').disabled = document.getElementById('highlight-mode').disabled = loading;
  const file = document.getElementById('include-file'); file.checked = fileIncluded === true; file.indeterminate = fileIncluded === null; file.disabled = loading || inclusionBusy || !canInclude;
}

const documentTransport = new DocumentTransport();
window.addEventListener('beforeunload', () => documentTransport.clear());
async function load(version) {
  if (version < generation) return;
  if (version !== generation) pendingDirection = null;
  generation = version;
  loading = true;
  sourceTabs.closeMenu();
  markdown.setLoading(true);
  goToLine.close(false);
  sourceSymbols.invalidate(); sourceSymbols.update();
  blameMenu.close(); more.open = false;
  scheduleConnectors();
  const request = ++loadSequence;
  toolFeedback.begin(request); updateCounter();
  fetchController?.abort(); fetchController = new AbortController();
  try {
    const payload = await documentTransport.read(version, { signal: fetchController.signal,
      isCurrent: () => request === loadSequence && version === generation });
    if (!payload) return;
    if (continuousDiff.manifest?.id !== payload.toolbar?.continuousDiff?.id) continuousDiff.close();
    // Keep layout measurable while the replacement models/folds/viewport are
    // prepared. Never paint their temporary line-1 state, including refreshes
    // that restore a saved viewport and navigation queued during a slow load.
    const workspace = document.getElementById('workspace');
    workspace.classList.add('document-preparing'); workspace.setAttribute('aria-busy', 'true');
    await Promise.all([codeFontReady, payload.document.isNotice ? null : prepareLanguage(payload.document.language)]);
    if (request !== loadSequence || version !== generation) return;
    const prepareSource = !payload.document.isNotice && !payload.document.isDiff && !payload.document.preserveView;
    if (prepareSource) {
      // Warm embedded languages before the model can cache uncolored tokens.
      // Window height bounds the initial viewport even while workspace is hidden.
      const rows = Math.ceil(document.documentElement.clientHeight / lineHeight) + 2;
      const limit = options.maxTokenizationLineLength;
      const prefix = payload.document.text.split('\n', rows).map(line => line.length < limit ? line : '').join('\n');
      await monaco.editor.colorize(prefix, payload.document.language, {});
      if (request !== loadSequence || version !== generation) return;
    }
    await display(payload.document, payload.toolbar);
    if (request !== loadSequence || version !== generation) return;
    markdown.update(textEditing.active ? {...payload.document,...textEditing.active.header,text:textEditing.active.model.getValue(),isNotice:false} : payload.document, version, payload.toolbar?.activeSourceTabId); markdown.setLoading(false);
    const revealSide = plan && payload.document.revealSide === 0 ? 0 : 1;
    if (models[revealSide] && !payload.document.preserveView && payload.document.revealLine) {
      const pane = panes[revealSide], model = models[revealSide];
      const line = Math.max(1, Math.min(model.getLineCount(), payload.document.revealLine));
      if (folding) {
        const desired = new Set(folding.collapsed);
        for (const region of folding.regions) {
          const start = folding.start(region, revealSide);
          if (line >= start && line < start + region.count) desired.delete(region.id);
        }
        if (!folding.sameState(desired, folding.collapsed)) await folding.setState(desired, revealSide, false);
      }
      await pane.getAction('editor.unfold')?.run({ selectionLines: [line - 1], levels: 99 });
      if (request !== loadSequence || version !== generation || models[revealSide] !== model) return;
      const column = payload.document.revealColumn ?? 1;
      pane.setSelection(model.validateRange({ startLineNumber: line, startColumn: column, endLineNumber: line, endColumn: column + (payload.document.revealLength ?? 0) }));
      pane.revealRangeInCenter(pane.getSelection(), monaco.editor.ScrollType.Immediate); activePane = revealSide;
    }
    if (prepareSource && models[1] && !payload.document.revealLine) {
      // A newly opened source file starts at the top. Prepare only that first
      // viewport before exposing it; leave the rest to background tokenization.
      // Do not synchronously scan the prefix of a restored, deep viewport.
      const model = models[1], endLine = right.getVisibleRanges().at(-1)?.endLineNumber;
      // Visible ranges omit the partially clipped bottom line.
      if (endLine) monaco.editor.colorizeModelLine(model, Math.min(model.getLineCount(), endLine + 1));
    }
    loading = false;updateTextEditing();
    toolFeedback.end(request); updateCounter();
    sourceSymbols.update();
    scheduleConnectors();
    comments.refresh();
    if (plan && pendingDirection !== null) {
      const finding = !!document.activeElement?.closest('.find-widget');
      const direction = pendingDirection; pendingDirection = null;
      selectChange(changeFromCursor(direction));
      if (finding) panes[activePane].trigger('keyboard', 'actions.find', null);
    }
    panes.forEach(pane => pane.render(true));
    workspace.classList.remove('document-preparing'); workspace.setAttribute('aria-busy', 'false');
    send({ type: 'loaded', version, models: renderedModelCount(payload.document, models, panes), hunks: plan?.blocks.length ?? 0 });
    if (continuousDiff.manifest && document.hasFocus()) continuousDiff.focus();
    const reading = readingPosition(version);
    if (reading) {
      send({ type: 'caret', version, side: reading.side, line: reading.line, column: reading.column });
      readingPublishedSide = reading.side; readingPublishedVersion = version;
    }
  } catch (error) {
    if (error.name !== 'AbortError' && request === loadSequence && version === generation) {
      loading = false; pendingDirection = null;
      toolFeedback.end(request, '读取失败 · 按 F5 重试'); updateCounter();
      sourceMode = false; sourceSymbols.update();
      markdown.update({ isNotice: true }, version); markdown.setLoading(false);
      const workspace = document.getElementById('workspace'), notice = document.getElementById('document-notice');
      workspace.hidden = true; workspace.classList.remove('document-preparing'); workspace.setAttribute('aria-busy', 'false');
      notice.textContent = `编辑器文档加载失败：${String(error)}`; notice.hidden = false;
      send({ type: 'error', version, message: String(error) });
    }
  }
}

function selectChange(index, focus = true) {
  if (!plan || index < 0) return;
  const block = plan.blocks[index];
  selectedChange = index; cursorRow = null; navigating = true;
  panes.forEach((pane, side) => {
    const start = side === 0 ? block.oldStart : block.newStart;
    const count = side === 0 ? block.oldCount : block.newCount;
    const total = side === 0 ? plan.oldLines.length : plan.newLines.length;
    const anchor = Math.max(1, Math.min(start + 1, total));
    pane.setPosition({ lineNumber: anchor, column: 1 });
    activeMarks[side].set(count ? [{ range: new monaco.Range(start + 1, 1, start + count, 1),
      options: { isWholeLine: true, linesDecorationsClassName: 'gitx-active-change' } }] : []);
  });
  syncing = true;
  const shift = Math.min(...panes.map(pane => pane.getLayoutInfo().height / 3));
  panes.forEach((pane, side) => {
    const start = side === 0 ? block.oldStart : block.newStart, total = side === 0 ? plan.oldLines.length : plan.newLines.length;
    pane.setScrollTop(Math.max(0, sourceBoundary(pane, start, total, lineHeight) - shift), monaco.editor.ScrollType.Immediate);
  });
  syncing = false;
  if ((activePane === 0 ? plan.oldLines.length : plan.newLines.length) === 0) activePane = 1 - activePane;
  if (focus) panes[activePane].focus();
  navigating = false; updateCounter(); scheduleConnectors();
}
function queueNavigation(action) {
  const owner = plan, epoch = navigationEpoch, version = generation;
  navigation = navigation.then(async () => {
    if (!owner || plan !== owner || epoch !== navigationEpoch) return;
    await folding?.pending;
    if (plan === owner && epoch === navigationEpoch) action();
  }).catch(error => send({ type: 'error', version, message: String(error) }));
}
function navigate(direction) {
  if (continuousDiff.manifest) return;
  // Transfer DOM focus synchronously even while a new document is loading.
  panes[activePane].focus();
  if (loading || !plan) { pendingDirection = direction; return; }
  queueNavigation(() => selectChange(changeFromCursor(direction)));
}
window.gitx = { load, navigate, readingPosition, goToLine: () => goToLine.open(), openSymbols: () => sourceSymbols.open(), revertBlock: revertCurrentBlock, setBlameView, setSourceTabs: toolbar => { sourceTabs.update(toolbar);textEditing.sync(toolbar);textEditing.disposeRetired(models);updateTextEditing(); },
  fileSessions: () => ({sessions:textEditing.metadata(),composing:composingSearch}), fileDraft:id=>textEditing.draft(id), fileRestore:(id,record)=>textEditing.restore(id,record),
  fileSaving:draft=>textEditing.saving(draft),fileSaved:result=>textEditing.saved(result),lockFiles:(ids,value)=>textEditing.lock(ids,value),lockDiskWrites:value=>textEditing.lockDiskWrites(value),
  captureDockView: () => ({ version: generation, path: sourcePath, views: panes.map(pane => pane.getModel() ? pane.saveViewState() : null), side: activePane, markdown: markdown.capture() }),
  restoreDockView: value => {
    if (typeof value === 'string') { try { value = JSON.parse(value); } catch { return; } }
    if (!value || value.version !== generation || value.path !== sourcePath || !Array.isArray(value.views)) return;
    value.views.forEach((view, side) => { if (view && panes[side]?.getModel()) panes[side].restoreViewState(view); });
    if (value.side === 0 || value.side === 1) activePane = value.side;
    markdown.restore(value.markdown);
  },
  openNotes: () => { if (markdown.active) markdown.activate(false, true); sourceSymbols.close(false); comments.openDrawer(); },
  setInclusion: state => { if (state.version !== generation) return; included = new Set(state.lines.map(line => `${line.side}:${line.line}`)); fileIncluded = state.included; inclusionBusy = !!state.busy; updateCounter(); scheduleConnectors(); },
  refreshComments: () => comments.refresh(), openCommentDraft: id => { if (markdown.active) markdown.activate(false, true); comments.focusDraft(id); }, focus: () => continuousDiff.manifest ? continuousDiff.focus() : markdown.active ? markdown.host.focus() : panes[activePane].focus() };
const doubleShift = new DoubleShiftGesture();
let composingSearch = false;
const shiftAllowed = event => !composingSearch && !event.isComposing && event.keyCode !== 229 && !event.ctrlKey && !event.altKey && !event.metaKey
  && !event.target.closest?.('.comment-ui textarea');
window.addEventListener('compositionstart', () => { composingSearch = true; doubleShift.reset(); }, true);
window.addEventListener('compositionend', () => { composingSearch = false; doubleShift.reset(); }, true);
window.addEventListener('pointerdown', () => doubleShift.reset(), true);
window.addEventListener('focusin', () => doubleShift.reset(), true);
window.addEventListener('blur', () => { composingSearch = false; doubleShift.reset(); goToLine.close(false); });
window.addEventListener('input', () => doubleShift.reset(), true);
window.addEventListener('keyup', event => {
  if (doubleShift.release(event.key === 'Shift', shiftAllowed(event), performance.now()))
    send({ type: 'code-search', filesOnly: true, quick: true, version: generation });
}, true);
window.addEventListener('keydown', event => {
  doubleShift.press(event.key === 'Shift', shiftAllowed(event), performance.now());
  if (event.isComposing) return;
  if(event.key.toLowerCase()==='s' && !event.shiftKey && !event.altKey && (/Mac|iPhone|iPad/.test(navigator.platform)?event.metaKey&&!event.ctrlKey:event.ctrlKey&&!event.metaKey)
      && !event.target.closest?.('.comment-ui textarea')) {
    event.preventDefault();event.stopImmediatePropagation();requestTextSave();return;
  }
  if (goToLine.handleKey(event)) return;
  if (right.hasTextFocus() && event.target.closest?.('.monaco-editor') && !event.target.closest('.comment-ui') && goToLine.handleShortcut(event, /Mac|iPhone|iPad/.test(navigator.platform))) return;
  if (markdown.active && event.key === 'Escape' && !event.shiftKey) {
    event.preventDefault(); event.stopImmediatePropagation(); markdown.activate(false, true); return;
  }
  if (event.key === 'F12' && event.shiftKey && !event.ctrlKey && !event.altKey) {
    event.preventDefault(); event.stopImmediatePropagation(); send({ type: 'reset-layout', version: generation }); return;
  }
  if (event.key === 'Escape' && event.shiftKey && !event.ctrlKey && !event.altKey) {
    event.preventDefault(); event.stopImmediatePropagation(); send({ type: 'hide-tool', version: generation }); return;
  }
  if (sourceSymbols.handleKey(event)) return;
  if (recentFilesShortcut(event, /Mac|iPhone|iPad/.test(navigator.platform))) {
    event.preventDefault(); event.stopImmediatePropagation(); send({ type: 'recent-files', version: generation }); return;
  }
  if (comments.handleKey(event)) return;
  if (navigateMore(event)) return;
  if (event.ctrlKey && !event.altKey && event.key === 'F4' && sourceTabs.closeActive()) { event.preventDefault(); event.stopImmediatePropagation(); return; }
  if (event.key === 'Escape' && blameMenu.opened) {
    event.preventDefault(); event.stopImmediatePropagation(); blameMenu.escape(); return;
  }
  if (event.key === 'Escape' && more.open) {
    event.preventDefault(); event.stopImmediatePropagation(); closeMore(true); return;
  }
  if (event.target instanceof HTMLTextAreaElement && event.target.closest('.comment-ui')) {
    // Plain textarea owns Enter, selection, undo and IME. Esc keeps the draft.
    if (event.key === 'Escape') { event.preventDefault(); panes[activePane].focus(); }
    return;
  }
  if (event.ctrlKey && event.shiftKey && !event.altKey && event.key.toLowerCase() === 'e') {
    event.preventDefault(); event.stopImmediatePropagation();
    send({ type: 'project-reveal', version: generation }); return;
  }
  if (event.ctrlKey && event.shiftKey && !event.altKey && ['f', 'n'].includes(event.key.toLowerCase())) {
    event.preventDefault(); event.stopImmediatePropagation();
    send({ type: 'code-search', filesOnly: event.key.toLowerCase() === 'n', version: generation }); return;
  }
  if (event.ctrlKey && !event.altKey && !event.shiftKey && event.key === 'F12') {
    event.preventDefault(); event.stopImmediatePropagation(); sourceSymbols.open(); return;
  }
  const reading = readingDirection(event, /Mac|iPhone|iPad/.test(navigator.platform));
  if (readingEnabled && reading && !document.activeElement?.closest('.find-widget input, .find-widget textarea')) {
    event.preventDefault(); event.stopImmediatePropagation(); send({ type: 'reading-history', version: generation, direction: reading }); return;
  }
  if (!readingEnabled && event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey && event.key === 'ArrowLeft' && !sourceMode) {
    event.preventDefault(); event.stopImmediatePropagation(); send({ type: 'history-back', version: generation }); return;
  }
  if (continuousDiff.manifest) {
    if (event.key === 'F5') { event.preventDefault(); send({ type: 'refresh' }); }
    else if (event.key === 'Escape') { event.preventDefault(); send({ type: 'focus-navigation', version: generation }); }
    // Find, folding and change navigation belong to the complete Monaco Diff.
    // Never run them against the detached editor underneath this read view.
    else if (event.key === 'F7' || event.ctrlKey && ['f', 'g'].includes(event.key.toLowerCase())) event.preventDefault();
    return;
  }
  if (event.key === 'F7') { event.preventDefault(); event.stopImmediatePropagation(); navigate(event.shiftKey ? -1 : 1); }
  else if (event.ctrlKey && event.altKey && event.key.toLowerCase() === 'r') { event.preventDefault(); event.stopImmediatePropagation(); revertCurrentBlock(); }
  else if (event.ctrlKey && !event.shiftKey && !event.altKey && event.key.toLowerCase() === 'f') {
    event.preventDefault(); event.stopImmediatePropagation();
    if (markdown.active) markdown.activate(false, true);
    panes[activePane].focus(); panes[activePane].trigger('keyboard', 'actions.find', null);
  }
  else if (event.key === 'F5') { event.preventDefault(); send({ type: 'refresh' }); }
  else if (event.ctrlKey && event.key.toLowerCase() === 'o') { event.preventDefault(); send({ type: 'open' }); }
  else if (event.ctrlKey && ['1', '2', '3'].includes(event.key)) { event.preventDefault(); send({ type: 'view', index: Number(event.key) - 1 }); }
  else if (event.key === 'Escape') {
    event.preventDefault(); event.stopImmediatePropagation();
    // Close Find before returning native focus; its close command focuses Monaco.
    // Do not let a queued F7 reclaim focus after the user has left the diff.
    navigationEpoch++;
    pendingDirection = null;
    const side = activePane;
    panes.forEach(pane => pane.trigger('keyboard', 'closeFindWidget', null));
    activePane = side;
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
    send({ type: 'focus-navigation', version: generation });
  }
}, true);
installFocusBoundary(direction => send({ type: 'focus-out', direction, version: generation }));
window.addEventListener('beforeunload', () => { continuousDiff.dispose(); goToLine.dispose(); sourceSymbols.dispose(); fetchController?.abort(); folding?.dispose(); diffFolding.registration.dispose(); panes.forEach(pane => pane.dispose()); models.forEach(model => model?.dispose()); });
display({ text: '打开本地 Git 仓库，然后选择文件。\n\n代码浏览 · 本地改动 · 提交历史\n\nCtrl+O 打开仓库    F5 刷新    F7 下一变更块', language: 'plaintext', isNotice: true });

function readingPosition(version) {
  const side = plan ? activePane : 1, pane = panes[side];
  if (!readingEnabled || loading || version !== generation || !models[side] || models[side].isDisposed() ||
      panes.some(editor => editor.inComposition) || goToLine.opened || sourceSymbols.opened ||
      document.activeElement?.closest('.comment-ui textarea, .comment-ui input, .find-widget input, .find-widget textarea')) return null;
  const position = pane.getPosition();
  return position ? { version, side, line: position.lineNumber, column: position.column } : null;
}
send({ type: 'ready' });
