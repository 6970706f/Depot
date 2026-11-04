(() => {
  'use strict';

  const { h, icon } = MediaFlow;

  const DURATION = { info: 4000, error: 7000 };
  const MAX_VISIBLE = 3;

  function toast(message, { type = 'info' } = {}) {
    const region = document.getElementById('toasts');
    const dismiss = () => node.remove();
    const node = h('div', { class: 'toast', 'data-type': type, role: type === 'error' ? 'alert' : null },
      icon(type === 'error' ? 'alert' : 'check'),
      h('span', { class: 'toast-text', textContent: message }),
      h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Dismiss', onClick: dismiss }, icon('close')),
    );
    region.append(node);
    while (region.children.length > MAX_VISIBLE) region.firstElementChild.remove();
    setTimeout(dismiss, DURATION[type]);
  }

  Object.assign(MediaFlow, { toast });
})();
