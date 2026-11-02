// Reuse only an unchanged source in the same repository, revision and tab.
// A refresh with different bytes, another comparison or a notice replaces it.
export function canReuseSourceModel(previous, next, previousTab, nextTab) {
  if (!previous || !next?.preserveView || !nextTab || previousTab !== nextTab
    || previous.isNotice || next.isNotice || previous.isDiff || next.isDiff || previous.diff || next.diff
    || previous.path !== next.path || previous.sourceRevision !== next.sourceRevision
    || previous.language !== next.language || previous.text !== next.text) return false;
  const before = previous.commentContext, after = next.commentContext;
  return !!before && !!after && before.repositoryId === after.repositoryId && before.path === after.path
    && before.kind === after.kind && before.snapshot === after.snapshot;
}
