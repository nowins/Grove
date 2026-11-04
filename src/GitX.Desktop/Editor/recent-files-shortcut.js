// Exactly one platform primary modifier; never consume an IME composition.
export function recentFilesShortcut(event, mac) {
  return !event.defaultPrevented && !event.isComposing && event.keyCode !== 229 && !event.repeat &&
    event.key.toLowerCase() === 'e' && !event.altKey && !event.shiftKey &&
    (mac ? event.metaKey && !event.ctrlKey : event.ctrlKey && !event.metaKey);
}
