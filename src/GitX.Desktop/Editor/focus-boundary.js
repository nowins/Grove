// WebView2 does not consistently return HWND focus when Tab leaves the document.
// Keep normal browser tab order (including Find and comment inputs) inside it,
// and explicitly hand off only at the two document boundaries.
export function installFocusBoundary(leave) {
  const markers = [-1, 1].map(direction => {
    const marker = document.createElement('span');
    marker.tabIndex = 0;
    marker.style.cssText = 'position:fixed;width:1px;height:1px;overflow:hidden;opacity:0;pointer-events:none';
    marker.addEventListener('focus', event => {
      if (event.relatedTarget && !markers.includes(event.relatedTarget)) { leave(direction); return; }
      // Entering from native controls: skip the marker, not the page's controls.
      const targets = [...document.querySelectorAll('button,input,textarea,select,summary,[tabindex]')]
        .filter(element => !markers.includes(element) && element.tabIndex >= 0 && !element.disabled
          && element.getClientRects().length && getComputedStyle(element).visibility !== 'hidden');
      (direction < 0 ? targets[0] : targets.at(-1))?.focus();
    });
    return marker;
  });
  document.body.prepend(markers[0]); document.body.append(markers[1]);
}
