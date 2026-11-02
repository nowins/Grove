// Fixed overlays stay within the WebView, including buttons near either edge.
export function positionPopup(popup, anchor, alignEnd = false) {
  const margin = 6, width = document.documentElement.clientWidth, height = document.documentElement.clientHeight;
  popup.style.maxWidth = `${Math.max(0, width - margin * 2)}px`;
  popup.style.maxHeight = `${Math.max(0, height - margin * 2)}px`;
  const size = popup.getBoundingClientRect();
  const x = alignEnd ? anchor.right - size.width : anchor.left;
  const below = anchor.bottom + 3;
  const y = below + size.height <= height - margin ? below : anchor.top - size.height - 3;
  popup.style.left = `${Math.max(margin, Math.min(x, width - size.width - margin))}px`;
  popup.style.top = `${Math.max(margin, Math.min(y, height - size.height - margin))}px`;
}

export function positionSubmenu(popup, anchor) {
  const margin = 6, width = document.documentElement.clientWidth, height = document.documentElement.clientHeight;
  popup.style.maxWidth = `${Math.max(0, width - margin * 2)}px`;
  popup.style.maxHeight = `${Math.max(0, height - margin * 2)}px`;
  const size = popup.getBoundingClientRect();
  const x = anchor.right + size.width + margin <= width ? anchor.right + 1 : anchor.left - size.width - 1;
  popup.style.left = `${Math.max(margin, Math.min(x, width - size.width - margin))}px`;
  popup.style.top = `${Math.max(margin, Math.min(anchor.top, height - size.height - margin))}px`;
}
