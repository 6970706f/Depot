import { h } from '../dom.js';

// A modal <dialog> with a form. onSubmit may be async; the dialog stays open until it calls close().
export function openDialog({ title, body = [], footer = [], onSubmit, onClose, initialFocus, wide = false }) {
  const cancel = h('button', { class: 'btn ghost', type: 'button', onclick: () => close() }, 'Cancel');
  const form = h('form', { method: 'dialog', novalidate: true },
    h('h2', { text: title }),
    body,
    h('div', { class: 'row end' }, cancel, footer));
  const dialog = h('dialog', { class: 'dialog', style: wide ? 'width: min(40rem, calc(100vw - 2rem))' : null }, form);

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    onSubmit?.(event);
  });
  dialog.addEventListener('close', () => {
    dialog.remove();
    onClose?.();
  });

  document.body.append(dialog);
  dialog.showModal();
  initialFocus?.focus();

  function close() {
    if (dialog.open) dialog.close();
  }
  return { dialog, form, close };
}
