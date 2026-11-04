(() => {
  'use strict';

  const { h, icon } = MediaFlow;

  // Thin wrapper over native <dialog>, which provides the focus trap, Escape,
  // the backdrop and returning focus to the trigger.
  function openDialog({ title, body, footer, onSubmit, initialFocus }) {
    const close = () => dialog.close();
    const titleId = `dialog-title-${Date.now()}`;

    const form = h('form', { method: 'dialog', novalidate: true },
      h('header', { class: 'dialog-header' },
        h('h2', { class: 'dialog-title', id: titleId, textContent: title }),
        h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Close', onClick: close }, icon('close')),
      ),
      h('div', { class: 'dialog-body' }, body),
      h('footer', { class: 'dialog-footer' }, footer),
    );

    form.addEventListener('submit', (event) => {
      event.preventDefault();
      onSubmit?.();
    });

    const dialog = h('dialog', { class: 'dialog', 'aria-labelledby': titleId }, form);
    dialog.addEventListener('close', () => dialog.remove());
    dialog.addEventListener('mousedown', (event) => {
      if (event.target === dialog) close();
    });

    document.body.append(dialog);
    dialog.showModal();
    initialFocus?.focus();
    return { close, dialog };
  }

  function cancelButton(onClick, label = 'Cancel') {
    return h('button', { class: 'btn', type: 'button', onClick, textContent: label });
  }

  Object.assign(MediaFlow, { openDialog, cancelButton });
})();
