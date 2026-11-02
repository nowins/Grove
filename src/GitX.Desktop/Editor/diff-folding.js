import { unchangedRegions, visibleAnchor } from './diff-layout.js';
import { iconLabel } from './icons.js';

// Only supported Monaco APIs are used: providers, editor actions, view zones and
// source-line geometry. No hidden-area setter or folding controller internals.
export function registerDiffFolding(monaco) {
  const ranges = new WeakMap();
  const registration = monaco.languages.registerFoldingRangeProvider({ scheme: 'gitx-diff' }, {
    provideFoldingRanges: model => ranges.get(model) ?? []
  });
  return { registration, ranges };
}

export class DiffFolding {
  constructor({ monaco, panes, models, plan, ranges, onLayout, onCursorMoved, onError, syncScroll }) {
    Object.assign(this, { monaco, panes, models, plan, onLayout, onCursorMoved, onError, syncScroll });
    this.regions = unchangedRegions(plan);
    this.collapsed = new Set();
    this.zoneIds = [[], []];
    this.zones = [[], []];
    this.buttons = [[], []];
    this.pending = Promise.resolve();
    this.busy = false;
    this.disposed = false;
    models.forEach((model, side) => ranges.set(model, this.regions.map(region => {
      const start = this.start(region, side);
      return { start: start - 1, end: start + region.count - 1, kind: monaco.languages.FoldingRangeKind.Region };
    })));
    this.subscriptions = panes.map((pane, side) => pane.onDidChangeHiddenAreas(() => {
      if (this.disposed || this.busy || pane.getModel() !== models[side]) return;
      // Find / Go to Line / built-in fold commands can reveal or fold one side.
      // Mirror its actual public geometry so search never leaves the panes apart.
      const desired = this.readState(side);
      if (!this.sameState(desired, this.collapsed)) this.setState(desired, side, false);
    }));
  }

  start(region, side) { return side === 0 ? region.oldStart : region.newStart; }
  sameState(a, b) { return a.size === b.size && [...a].every(id => b.has(id)); }
  readState(side) {
    const pane = this.panes[side];
    return new Set(this.regions.filter(region => {
      const start = this.start(region, side);
      return pane.getTopForLineNumber(start - 1) === pane.getTopForLineNumber(start + region.count - 1);
    }).map(region => region.id));
  }

  mount(collapse = false) {
    this.panes.forEach((pane, side) => pane.changeViewZones(accessor => {
      this.zoneIds[side] = this.regions.map(region => {
        const node = document.createElement('div');
        node.className = 'gitx-fold-zone';
        node.dataset.expanded = 'true';
        const button = document.createElement('button');
        button.type = 'button';
        // Keep Monaco's drag-selection pointer capture from swallowing the click.
        button.addEventListener('pointerdown', event => event.stopPropagation());
        button.addEventListener('mousedown', event => event.stopPropagation());
        button.addEventListener('click', () => {
          const desired = new Set(this.collapsed);
          if (desired.has(region.id)) desired.delete(region.id); else desired.add(region.id);
          this.setState(desired, side, true);
        });
        node.append(button);
        this.buttons[side].push({ region, button });
        const zone = { afterLineNumber: this.start(region, side) - 1,
          heightInLines: 0, domNode: node, showInHiddenAreas: true, suppressMouseDown: false };
        this.zones[side].push(zone);
        return accessor.addZone(zone);
      });
    }));
    // A fresh source model has no hidden lines. Running editor.unfold here
    // needlessly waits for Monaco's asynchronous folding provider, leaving
    // the viewport at line 1 before the initial change can be selected.
    if (collapse && this.regions.length) this.setAll(true, 1, false);
    else { this.updateButtons(); this.onLayout(); }
  }

  setAll(collapse, side, focus = true) {
    this.setState(new Set(collapse ? this.regions.map(region => region.id) : []), side, focus);
  }

  setState(desired, side, focus) {
    this.pending = this.pending.then(async () => {
      if (this.disposed || this.panes.some((pane, i) => pane.getModel() !== this.models[i])) return;
      const source = this.panes[side];
      const line = source.getVisibleRanges()[0]?.startLineNumber ?? 1;
      const delta = source.getTopForLineNumber(line) - source.getScrollTop();
      const scrollLeft = source.getScrollLeft();
      this.busy = true;
      this.updateButtons();
      this.onLayout();
      try {
        await Promise.all(this.panes.map(async (pane, index) => {
          const position = pane.getPosition();
          if (position) {
            const visibleLine = visibleAnchor(position.lineNumber, index, this.regions, desired);
            if (visibleLine !== position.lineNumber) {
              pane.setPosition({ lineNumber: visibleLine, column: 1 });
              if (side === index) this.onCursorMoved(visibleLine, side);
            }
          }
          // run() waits for the provider's folding ranges to be ready, even on
          // first load. Explicit levels make the commands idempotent.
          for (const collapse of [false, true]) {
            const selectionLines = this.regions.filter(region => desired.has(region.id) === collapse)
              .map(region => this.start(region, index) - 2);
            if (selectionLines.length) await pane.getAction(collapse ? 'editor.fold' : 'editor.unfold').run({ selectionLines, levels: 1 });
            if (this.disposed || pane.getModel() !== this.models[index]) return;
          }
        }));
        if (this.disposed) return;
        const oldState = this.readState(0), newState = this.readState(1);
        if (!this.sameState(oldState, desired) || !this.sameState(newState, desired))
          throw new Error('未变区域折叠状态不一致，请重新打开文件。');
        this.collapsed = desired;
        this.panes.forEach((pane, side) => pane.changeViewZones(accessor => this.zones[side].forEach((zone, i) => {
          zone.heightInLines = desired.has(this.regions[i].id) ? 1 : 0;
          zone.domNode.dataset.expanded = String(zone.heightInLines === 0);
          accessor.layoutZone(this.zoneIds[side][i]);
        })));
        const anchor = visibleAnchor(line, side, this.regions, desired);
        const top = Math.max(0, source.getTopForLineNumber(anchor) - delta);
        if (this.syncScroll) {
          source.setScrollPosition({ scrollTop: top, scrollLeft }, this.monaco.editor.ScrollType.Immediate);
          this.syncScroll(side);
        } else this.panes.forEach(pane => pane.setScrollPosition({ scrollTop: top, scrollLeft }, this.monaco.editor.ScrollType.Immediate));
        if (focus) source.focus();
      } catch (error) {
        if (!this.disposed) this.onError(String(error));
      } finally {
        if (!this.disposed) {
          this.busy = false;
          this.updateButtons();
          this.onLayout();
        }
      }
    });
    return this.pending;
  }

  updateButtons() {
    for (const buttons of this.buttons) for (const { region, button } of buttons) {
      const collapsed = this.collapsed.has(region.id);
      iconLabel(button, collapsed ? 'expand' : 'collapse', `${collapsed ? '展开' : '收起'} ${region.count} 行未修改代码`);
      button.title = `原 ${region.oldStart}–${region.oldStart + region.count - 1} / 新 ${region.newStart}–${region.newStart + region.count - 1}`;
      button.setAttribute('aria-expanded', String(!collapsed));
      button.disabled = this.busy;
    }
  }

  dispose() {
    this.disposed = true;
    this.subscriptions.forEach(subscription => subscription.dispose());
    this.panes.forEach((pane, side) => pane.changeViewZones(accessor => this.zoneIds[side].forEach(id => accessor.removeZone(id))));
  }
}
