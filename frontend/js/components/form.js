(() => {
  'use strict';

  const { h, icon, setText } = MediaFlow;

  let fieldCounter = 0;

  function fieldShell({ label, optional, hint, control }) {
    const id = `field-${++fieldCounter}`;
    const hintId = `${id}-hint`;
    const errorId = `${id}-error`;
    const input = control.matches('input, select') ? control : control.querySelector('select');
    input.id = id;

    const error = h('p', { class: 'field-error', id: errorId, hidden: true });
    const hintNode = hint !== undefined ? h('p', { class: 'field-hint', id: hintId, textContent: hint }) : null;
    if (hintNode) input.setAttribute('aria-describedby', hintId);

    const el = h('div', { class: 'field' },
      h('label', { class: 'field-label', for: id }, label,
        optional ? h('span', { class: 'field-optional', textContent: ' (optional)' }) : null),
      control, hintNode, error,
    );

    return {
      el,
      input,
      setHint: (text) => hintNode && setText(hintNode, text),
      setError(message) {
        error.hidden = !message;
        error.textContent = message || '';
        input.setAttribute('aria-invalid', message ? 'true' : 'false');
        const described = [message ? errorId : null, hintNode ? hintId : null].filter(Boolean).join(' ');
        if (described) input.setAttribute('aria-describedby', described);
        else input.removeAttribute('aria-describedby');
      },
    };
  }

  function textField({ label, value = '', type = 'text', hint, placeholder, autocomplete = 'off', optional, name }) {
    const control = h('input', { class: 'input', type, value, placeholder, autocomplete, name, spellcheck: 'false' });
    return fieldShell({ label, optional, hint, control });
  }

  function selectControl({ options, value, name, label, className = '', onChange }) {
    const select = h('select', { name, 'aria-label': label, onChange },
      options.map((option) => h('option', { value: option.value, selected: option.value === value, textContent: option.label })),
    );
    return h('span', { class: `select ${className}`.trim() }, select);
  }

  function selectField({ label, options, value, hint, optional, name, onChange }) {
    const control = selectControl({ options, value, name, onChange });
    return fieldShell({ label, optional, hint, control });
  }

  // data-page-search is the target of the "/" keyboard shortcut.
  function searchField({ label, placeholder, value, onInput }) {
    return h('div', { class: 'search-field', role: 'search' },
      icon('search'),
      h('input', {
        class: 'input', type: 'search', placeholder, value, autocomplete: 'off',
        'aria-label': label, 'data-page-search': true, onInput,
      }),
    );
  }

  function segmentedButtons({ label, options, value, onChange, framed = false }) {
    const buttons = options.map((option) => {
      const node = h('button', {
        class: `segment${option.icon ? ' segment-icon' : ''}`,
        type: 'button',
        title: option.title,
        'aria-label': option.title,
        'aria-pressed': option.value === value,
        onClick: () => onChange(option.value),
      }, option.icon ?? option.label, option.count);
      return [option.value, node];
    });

    return {
      el: h('div', { class: `segmented${framed ? ' segmented-framed' : ''}`, role: 'group', 'aria-label': label },
        buttons.map(([, node]) => node)),
      setValue(next) {
        buttons.forEach(([optionValue, node]) => node.setAttribute('aria-pressed', String(optionValue === next)));
      },
    };
  }

  // A titled block of the settings page: heading and description on the left, fields on the right.
  function settingsSection(title, description, ...fields) {
    return h('section', { class: 'settings-section' },
      h('div', { class: 'settings-heading' },
        h('h2', { textContent: title }),
        description ? h('p', { textContent: description }) : null),
      h('div', { class: 'settings-fields' }, fields),
    );
  }

  Object.assign(MediaFlow, { textField, selectControl, selectField, searchField, segmentedButtons, settingsSection });
})();
