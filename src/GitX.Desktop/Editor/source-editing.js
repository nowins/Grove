// Working-tree drafts own their Monaco model until the tab closes. Neither a
// publication nor switching files is permission to replace unsaved text.
export function renderedModelCount(doc, models, panes) {
  if (doc.isNotice) return 0;
  return models.reduce((count, model, side) => count + (model && !model.isDisposed() && panes[side]?.getModel() === model ? 1 : 0), 0);
}
export class SourceEditing {
  constructor({ createModel, getVersion, changed = () => {} }) {
    Object.assign(this, { createModel, getVersion, changed }); this.sessions = new Map(); this.retired = new Set(); this.locks = new Map();
  }
  dirty(s) { return s.model.getAlternativeVersionId() !== s.saved; }
  owns(model) { return [...this.sessions.values()].some(s => s.model === model) || this.retired.has(model); }
  sync(toolbar) {
    const ids = new Set((toolbar?.sourceTabs ?? []).filter(t => t.localFile).map(t => t.id));
    for (const [id,s] of this.sessions) if (!ids.has(id)) { s.subscription.dispose(); this.retired.add(s.model); this.sessions.delete(id); if(this.active===s)this.active=null; }
  }
  disposeRetired(current) { for (const model of this.retired) if (!current.includes(model)) { model.dispose(); this.retired.delete(model); } }
  prepare(doc, toolbar) {
    this.sync(toolbar); this.active = null;
    const id = toolbar?.activeSourceTabId, tab = toolbar?.sourceTabs?.find(t => t.id === id);
    if (!toolbar?.sourceMode || !tab?.localFile || doc.diff || doc.sourceRevision) return { doc, model: null };
    let s = this.sessions.get(id); const info = doc.editing;
    if (s && info && info.rootKey !== s.info.rootKey) { s.subscription.dispose(); this.retired.add(s.model); this.sessions.delete(id); s = null; }
    if (!s && info?.canEdit && !doc.isNotice) {
      const model = this.createModel(doc.text.replace(/\r\n/g,'\n'),doc.language);
      s = { id, path: tab.path, model, info, disk: info.fingerprint, saved: model.getAlternativeVersionId(), header: { path: doc.path, language: doc.language }, busy: false, error: '' };
      s.subscription = model.onDidChangeContent(() => { this.changed(this); }); this.sessions.set(id,s);
    }
    if (!s) return { doc, model: null };
    this.active = s;
    if (!this.dirty(s) && !s.busy && info && !doc.isNotice) {
      // Clean files may follow disk; dirty models keep their baseline/undo.
      if (s.model.getValue() !== doc.text.replace(/\r\n/g,'\n')) s.model.setValue(doc.text.replace(/\r\n/g,'\n'));
      s.info = info; s.saved = s.model.getAlternativeVersionId(); s.error = '';
    }
    s.readOnlyReason = info && !info.canEdit ? info.reason || "文件当前只读" : doc.isNotice ? "磁盘文件不可读取；草稿保留为只读，可复制内容" : null;
    s.disk = info?.fingerprint ?? null;
    s.published = info?.fingerprint ?? null;
    const effective = { ...doc, ...s.header, text: s.model.getValue(), isNotice: false, editing: s.info };
    if (this.unsafe(s)) { effective.blame = null; effective.commentContext = null; }
    return { doc: effective, model: s.model };
  }
  unsafe(s = this.active) { return !!s && (this.dirty(s) || s.info.fingerprint !== s.published); }
  editable(s = this.active) { return !!s?.info.canEdit && !s.readOnlyReason && this.sessions.get(s.id)===s && !this.locks.get(s.id); }
  metadata() { return [...this.sessions.values()].map(s => ({ id:s.id,path:s.path,rootKey:s.info.rootKey,dirty:this.dirty(s),busy:s.busy,editable:this.editable(s),conflict:s.info.fingerprint!==s.disk })); }
  draft(id) {
    const s=this.sessions.get(id); if(!s || s.busy || !s.info.canEdit) return null;
    return { id:s.id, path:s.path, rootKey:s.info.rootKey, model:s.model.id, checkpoint:s.model.getAlternativeVersionId(), expected:s.info.fingerprint, text:s.model.getValue(), dirty:this.dirty(s) };
  }
  saving(draft) { const s=this.sessions.get(draft.id); if(!s || s.model.id!==draft.model) return false; s.busy=true;s.error='';this.changed(this);return true; }
  saved(result) {
    const s=this.sessions.get(result.id);if(!s || s.model.id!==result.model) return;
    s.busy=false;
    if(result.info) { s.info=result.info;s.saved=result.checkpoint;s.disk=result.info.fingerprint;s.error=''; }
    else s.error=result.error || '保存失败，草稿保留';
    this.changed(this);
  }
  lock(ids, value) { for(const id of ids){const count=(this.locks.get(id)||0)+(value?1:-1);if(count>0)this.locks.set(id,count);else this.locks.delete(id);}this.changed(this); }
  dispose() { for(const s of this.sessions.values()){s.subscription.dispose();s.model.dispose();}for(const m of this.retired)m.dispose();this.sessions.clear();this.retired.clear(); }
}
