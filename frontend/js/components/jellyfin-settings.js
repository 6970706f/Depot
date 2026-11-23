import { api } from '../api.js';
import { h } from '../dom.js';
import { field, showFieldError } from './form.js';
import { toast } from './toast.js';

// Settings > Jellyfin connection. The saved key never comes back from the server; leaving the
// field blank keeps it (for the same URL).
export function jellyfinSettingsPanel() {
  const url = field({ label: 'Server URL', name: 'url', placeholder: 'http://localhost:8096' });
  const apiKey = field({ label: 'API key', name: 'apiKey', type: 'password', autocomplete: 'off', hint: 'Create one in Jellyfin under Dashboard › API Keys.' });
  const fields = { url, apiKey };
  const result = h('p', { class: 'muted', role: 'status' });
  const test = h('button', { class: 'btn', type: 'button', onclick: () => run('test') }, 'Test connection');
  const save = h('button', { class: 'btn primary', type: 'submit' }, 'Save');

  const form = h('form', { class: 'panel', novalidate: true },
    h('header', {}, h('h2', { text: 'Jellyfin connection' }), h('span', { class: 'subtle', text: 'Shows your library and scans it after each import' })),
    h('div', { class: 'grid-2' }, url.el, apiKey.el),
    h('div', { class: 'row wrap' }, save, test, result));

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    run('save');
  });

  async function run(action) {
    const values = { url: url.value, apiKey: apiKey.value };
    test.disabled = save.disabled = true;
    result.className = 'muted';
    result.textContent = action === 'test' ? 'Testing…' : 'Saving…';
    showFieldError(fields, null);
    try {
      if (action === 'test') {
        const server = await api.testJellyfinSettings(values);
        result.textContent = `Connected to ${server.serverName} (Jellyfin ${server.version}).`;
      } else {
        apply(await api.saveJellyfinSettings(values));
        result.textContent = '';
        toast('Jellyfin settings saved.');
      }
    } catch (error) {
      if (showFieldError(fields, error)) {
        result.textContent = '';
      } else {
        result.className = 'danger-text';
        result.textContent = error.message;
      }
    } finally {
      test.disabled = save.disabled = false;
    }
  }

  function apply(settings) {
    url.input.value = settings.url ?? '';
    apiKey.input.value = '';
    apiKey.input.placeholder = settings.hasApiKey ? 'Saved. Leave blank to keep it.' : '';
  }

  api.jellyfinSettings().then(apply).catch((error) => {
    result.className = 'danger-text';
    result.textContent = error.message;
  });

  return form;
}
