// Element helper: h('button', { class: 'btn', onclick }, 'Save').
// Props starting with "on" become listeners; null/false values are skipped.
export function h(tag, props = {}, ...children) {
  const element = document.createElement(tag);
  for (const [key, value] of Object.entries(props ?? {})) {
    if (value == null || value === false) continue;
    if (key.startsWith('on') && typeof value === 'function') {
      element.addEventListener(key.slice(2), value);
    } else if (key === 'class') {
      element.className = value;
    } else if (key === 'text') {
      element.textContent = value;
    } else if (key === 'dataset') {
      Object.assign(element.dataset, value);
    } else if (key in element && typeof value !== 'string') {
      element[key] = value;
    } else {
      element.setAttribute(key, value === true ? '' : value);
    }
  }
  append(element, children);
  return element;
}

function append(element, children) {
  for (const child of children.flat(Infinity)) {
    if (child == null || child === false) continue;
    element.append(child instanceof Node ? child : String(child));
  }
}

const iconPaths = {
  overview: 'M4 4h7v7H4zM13 4h7v4h-7zM13 10h7v10h-7zM4 13h7v7H4z',
  downloads: 'M12 4v11M7 10.5l5 5 5-5M4 20h16',
  imports: 'M3 5h6l2 3h10v11H3z',
  library: 'M4 4h5v16H4zM11 4h5v16h-5zM18 5l2.5.6-1.2 14.6-2.3-.4',
  settings: 'M4 7h9M17 7h3M4 17h3M11 17h9M15 5v4M9 15v4',
  plus: 'M12 5v14M5 12h14',
  pause: 'M9 5v14M15 5v14',
  play: 'M7 4.5v15l12-7.5z',
  retry: 'M4 5v5h5M5 10a8 8 0 1 1-1 4.5',
  trash: 'M4 7h16M9 7V4h6v3M6.5 7l1 13h9l1-13M10 11v5M14 11v5',
  close: 'M6 6l12 12M18 6L6 18',
  skip: 'M6 5l8 7-8 7zM18 5v14',
  film: 'M4 4h16v16H4zM8 4v16M16 4v16M4 9h4M4 15h4M16 9h4M16 15h4',
  tv: 'M3 7h18v12H3zM8 3l4 4 4-4',
};

export function icon(name) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('class', 'icon');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  path.setAttribute('d', iconPaths[name] ?? '');
  svg.append(path);
  return svg;
}

export function clear(element) {
  element.replaceChildren();
  return element;
}
