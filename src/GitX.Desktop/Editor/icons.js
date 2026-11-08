// 16-unit paths shared visually with Resources/Icons.axaml and the prototype.
const paths = {
  blank: '',
  file: 'M3.5 2H9L12.5 5.5V14H3.5ZM9 2V5.5H12.5M6 8H10M6 10.5H10',
  structure: 'M2 3H5V6H2ZM9 7H12V10H9ZM9 12H12V15H9ZM3.5 6V13.5H9M3.5 8.5H9',
  'chevrons-right': 'M3 4L7 8L3 12M8 4L12 8L8 12',
  up: 'M8 13V3M4 7L8 3L12 7', down: 'M8 3V13M4 9L8 13L12 9',
  left: 'M13 8H3M7 4L3 8L7 12', right: 'M3 8H13M9 4L13 8L9 12',
  plus: 'M8 3V13M3 8H13', comment: 'M2.5 3.5H13.5V10.5H7.5L4.5 13V10.5H2.5Z',
  more: 'M3 8h1M7.5 8h1M12 8h1',
  close: 'M4 4L12 12M12 4L4 12',
  edit: 'M10.5 3L13 5.5L6 12.5H3.5V10Z',
  copy: 'M6.5 5H11.5Q13 5 13 6.5V11.5Q13 13 11.5 13H6.5Q5 13 5 11.5V6.5Q5 5 6.5 5ZM3 10.5V4Q3 3 4 3H9.5',
  save: 'M3 2.5H11L13.5 5V13.5H2.5V2.5ZM5 2.5V6H10V2.5M5 13.5V9H11V13.5',
  refresh: 'M13.5 5.5A5.6 5.6 0 1 0 13.5 10M13.5 2V5.5H10',
  locate: 'M8 2V4M8 12V14M2 8H4M12 8H14M12 8A4 4 0 1 1 4 8A4 4 0 1 1 12 8',
  trash: 'M3 4.5H13M6 4.5V2.5H10V4.5M4.5 4.5V13.5H11.5V4.5M6.5 7V11M9.5 7V11',
  check: 'M3.5 8.5L6.5 11.5L12.5 4.5',
  undo: 'M4 6H10A3 3 0 0 1 10 12H7M6.5 3.5L4 6L6.5 8.5',
  send: 'M2 3L14 8L2 13L4 8ZM4 8H14',
  expand: 'M5 6L8 3L11 6M5 10L8 13L11 10',
  collapse: 'M3 4H13M3 12H13M6 7L8 9L10 7',
  history: 'M13.8 8A5.8 5.8 0 1 1 2.2 8A5.8 5.8 0 1 1 13.8 8M8 4.8V8L10.3 9.6',
};
export function iconLabel(node, name, label = '') {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 16 16'); svg.setAttribute('class', 'ui-icon');
  svg.setAttribute('aria-hidden', 'true'); svg.setAttribute('focusable', 'false');
  const path = document.createElementNS(svg.namespaceURI, 'path');
  path.setAttribute('d', paths[name]); svg.append(path);
  node.replaceChildren(svg);
  if (label) { const text = document.createElement('span'); text.textContent = label; node.append(text); }
}
