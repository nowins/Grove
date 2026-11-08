// The Mac modifier combination leaves Option-arrow word movement intact.
export function readingDirection(event, mac) {
  if (event.isComposing || event.keyCode === 229 || event.shiftKey || event.ctrlKey ||
      !event.altKey || !!event.metaKey !== !!mac) return 0;
  return event.key === 'ArrowLeft' ? -1 : event.key === 'ArrowRight' ? 1 : 0;
}

// Monaco setPosition/setSelection (including Find) use API/NotSet, while
// mouse jumps use Explicit. Load/restore calls are suppressed by the caller.
// Keyboard arrows, typing, undo and redo only update the current place.
export function readingGesture(event) {
  return event.reason === 3 && event.source === 'mouse' ||
    [0, 3].includes(event.reason) && ['api', 'grove.navigate'].includes(event.source);
}
