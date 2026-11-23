import { h } from '../dom.js';

export function toast(message, { error = false } = {}) {
  const element = h('div', { class: error ? 'toast error' : 'toast', role: error ? 'alert' : 'status', text: message });
  document.getElementById('toasts').append(element);
  setTimeout(() => element.remove(), error ? 7000 : 3500);
}
