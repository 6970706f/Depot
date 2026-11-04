(() => {
  'use strict';

  const { h, icon } = MediaFlow;

  // A region showing exactly one of: loading, empty, error or its content.
  // Nodes are swapped only when the state changes, not on every poll.
  function createPanel(label) {
    const el = h('div', { class: 'page-body' });
    let current = null;

    function show(key, build) {
      if (current === key) return;
      current = key;
      el.replaceChildren(build());
    }

    return {
      el,
      loading: (rows = 6) => show('loading', () => skeleton(label, rows)),
      content: (node) => show(node, () => node),
      error(message, onRetry) {
        show(`error:${message}`, () => stateView({
          kind: 'error', title: `${label} didn't load`, hint: message,
          action: { label: 'Retry', onClick: onRetry },
        }));
      },
      empty({ key = 'empty', title, hint, action }) {
        show(`empty:${key}`, () => stateView({ kind: 'empty', title, hint, action }));
      },
    };
  }

  function stateView({ kind, title, hint, action }) {
    return h('div', { class: 'state', 'data-kind': kind, role: kind === 'error' ? 'alert' : null },
      h('p', { class: 'state-title', textContent: title }),
      hint ? h('p', { class: 'state-hint', textContent: hint }) : null,
      action ? h('button', { class: 'btn', type: 'button', onClick: action.onClick },
        action.icon ? icon(action.icon) : null, action.label) : null,
    );
  }

  function skeleton(label, rows) {
    const row = () => h('div', { class: 'skeleton-row' },
      h('span', { class: 'skeleton-bar' }), h('span', { class: 'skeleton-bar' }), h('span', { class: 'skeleton-bar' }));
    return h('div', { role: 'status' },
      h('span', { class: 'sr-only', textContent: `Loading ${label.toLowerCase()}` }),
      Array.from({ length: rows }, row),
    );
  }

  Object.assign(MediaFlow, { createPanel });
})();
