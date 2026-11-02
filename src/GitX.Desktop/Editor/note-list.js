export const sameNoteContext = (a, b) => !!(a && b && a.repositoryId && a.snapshot && a.repositoryId === b.repositoryId && a.snapshot === b.snapshot);

export function noteFileMatches(anchor, document) {
  const path = document?.commentContext?.path || document?.path;
  return !!path && (anchor.path === path || anchor.context.path === path);
}

export function noteStatus(anchor, document) {
  if (sameNoteContext(anchor.context, document?.commentContext)) return '当前版本';
  if (noteFileMatches(anchor, document)) return anchor.context.kind === document?.commentContext?.kind
    ? '上下文已变化 · 保留原快照' : '同一文件的其他版本 · 原快照';
  return '其他文件或比较版本 · 原快照';
}

export function selectNotes(items, { query = '', scope = 'all', sort = 'updated' }, document, repositoryId) {
  if (!repositoryId) return [];
  const parts = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const filtered = items.filter(item => {
    if (item.anchor.context.repositoryId !== repositoryId) return false;
    if (scope === 'file' && !noteFileMatches(item.anchor, document)) return false;
    if (scope === 'current' && !sameNoteContext(item.anchor.context, document?.commentContext)) return false;
    if (!parts.length) return true;
    const fields = [item.body, item.anchor.path, item.anchor.context.path, item.anchor.code, item.anchor.label].map(value => (value ?? '').toLowerCase());
    return parts.every(part => fields.some(field => field.includes(part)));
  });
  const updated = item => Date.parse(item.updated) || 0;
  return filtered.sort((a, b) => (sort === 'file' ? a.anchor.path.localeCompare(b.anchor.path) || a.anchor.startLine - b.anchor.startLine : 0)
    || updated(b) - updated(a) || a.anchor.path.localeCompare(b.anchor.path) || a.anchor.startLine - b.anchor.startLine || a.id.localeCompare(b.id));
}
