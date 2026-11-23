import { h } from '../dom.js';

let nextId = 0;

// A labelled input, select or textarea with a hint and an error line.
export function field({ label, name, type = 'text', value = '', hint, placeholder, options, rows, autocomplete, required, min, max, list }) {
  const id = `field-${++nextId}`;
  const errorId = `${id}-error`;
  let input;
  if (options) {
    input = h('select', { class: 'select', id, name },
      options.map((option) => h('option', { value: option.value, text: option.label, selected: option.value === value })));
  } else if (rows) {
    input = h('textarea', { class: 'textarea', id, name, rows, placeholder, spellcheck: 'false' });
    input.value = value ?? '';
  } else {
    input = h('input', { class: 'input', id, name, type, placeholder, autocomplete, required, min, max, list, spellcheck: 'false' });
    input.value = value ?? '';
  }
  const error = h('span', { class: 'error', id: errorId, hidden: true });
  const el = h('div', { class: 'field' },
    h('label', { for: id, text: label }),
    input,
    hint ? h('span', { class: 'hint', text: hint }) : null,
    error);

  function setError(message) {
    error.textContent = message ?? '';
    error.hidden = !message;
    if (message) {
      input.setAttribute('aria-invalid', 'true');
      input.setAttribute('aria-describedby', errorId);
    } else {
      input.removeAttribute('aria-invalid');
      input.removeAttribute('aria-describedby');
    }
  }

  return { el, input, setError, get value() { return input.value; } };
}

// Shows an ApiError next to the field it is about, or returns false when no field matches.
export function showFieldError(fields, error) {
  for (const item of Object.values(fields)) item.setError(null);
  const target = error?.field && fields[error.field];
  if (!target) return false;
  target.setError(error.message);
  target.input.focus();
  return true;
}
