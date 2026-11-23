import { h } from '../dom.js';

export function loadingState(text = 'Loading…') {
  return h('div', { class: 'state' }, text);
}

export function emptyState(text, ...actions) {
  return h('div', { class: 'state' }, h('p', { text }), actions.length ? h('div', { class: 'row' }, actions) : null);
}

export function errorState(title, error, retry) {
  return h('div', { class: 'state error', role: 'alert' },
    h('p', { text: title }),
    h('p', { class: 'muted', text: error?.message ?? String(error) }),
    retry ? h('button', { class: 'btn', type: 'button', onclick: retry }, 'Retry') : null);
}
