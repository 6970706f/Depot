(() => {
  'use strict';

  const SVG_NS = 'http://www.w3.org/2000/svg';
  const DOM_PROPERTIES = new Set(['value', 'checked', 'disabled', 'selected', 'textContent']);

  function h(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(props)) {
      if (value === null || value === undefined) continue;
      if (key.startsWith('on')) {
        node.addEventListener(key.slice(2).toLowerCase(), value);
      } else if (DOM_PROPERTIES.has(key)) {
        node[key] = value;
      } else if (value === false && !key.startsWith('aria-')) {
        continue;
      } else {
        node.setAttribute(key, value === true ? '' : String(value));
      }
    }
    node.append(...children.flat().filter((child) => child !== null && child !== undefined && child !== false));
    return node;
  }

  function icon(name, className = '') {
    const svg = document.createElementNS(SVG_NS, 'svg');
    svg.setAttribute('class', `icon ${className}`.trim());
    svg.setAttribute('aria-hidden', 'true');
    const use = document.createElementNS(SVG_NS, 'use');
    use.setAttribute('href', `#i-${name}`);
    svg.append(use);
    return svg;
  }

  function setIcon(svg, name) {
    svg.firstElementChild.setAttribute('href', `#i-${name}`);
  }

  // Skips no-op writes so polling doesn't clear a text selection inside a row.
  function setText(node, text) {
    if (node.textContent !== text) node.textContent = text;
  }

  // Reconciles rows by key. Existing rows are updated in place and only moved when
  // their position changes, so polling keeps focus and hover state intact.
  function keyedList(parent, key, create) {
    const rows = new Map();
    return (items) => {
      const seen = new Set();
      let cursor = parent.firstChild;
      for (const item of items) {
        const id = key(item);
        seen.add(id);
        let row = rows.get(id);
        if (!row) {
          row = create(item);
          rows.set(id, row);
        }
        row.update(item);
        if (row.el === cursor) cursor = cursor.nextSibling;
        else parent.insertBefore(row.el, cursor);
      }
      for (const [id, row] of rows) {
        if (seen.has(id)) continue;
        const hadFocus = row.el.contains(document.activeElement);
        row.el.remove();
        rows.delete(id);
        if (hadFocus) document.getElementById('view')?.focus();
      }
    };
  }

  Object.assign(MediaFlow, { h, icon, setIcon, setText, keyedList });
})();
