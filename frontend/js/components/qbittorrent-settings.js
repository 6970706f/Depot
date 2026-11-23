import { api } from '../api.js';
import { h } from '../dom.js';
import { field, showFieldError } from './form.js';
import { toast } from './toast.js';

// Settings > qBittorrent connection. The saved password never comes back from the server;
// leaving the field blank keeps it (for the same URL).
export function qbittorrentSettingsPanel() {
  const url = field({ label: 'Web UI URL', name: 'url', placeholder: 'http://localhost:8080' });
  const username = field({ label: 'Username', name: 'username', autocomplete: 'username' });
  const password = field({ label: 'Password', name: 'password', type: 'password', autocomplete: 'new-password' });
  const fields = { url, username, password };
  const result = h('p', { class: 'muted', role: 'status' });
  const warning = h('p', { class: 'danger-text', hidden: true },
    'This address uses http:// outside this machine, so the password travels unencrypted.');
  const test = h('button', { class: 'btn', type: 'button', onclick: () => run('test') }, 'Test connection');
  const save = h('button', { class: 'btn primary', type: 'submit' }, 'Save');

  const form = h('form', { class: 'panel', novalidate: true },
    h('header', {}, h('h2', { text: 'qBittorrent connection' }), h('span', { class: 'subtle', text: 'Tools › Options › Web UI in qBittorrent' })),
    h('div', { class: 'grid-2' }, url.el, username.el, password.el),
    warning,
    h('div', { class: 'row wrap' }, save, test, result));

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    run('save');
  });
  url.input.addEventListener('input', updateWarning);

  function updateWarning() {
    try {
      const parsed = new URL(url.value.trim());
      warning.hidden = parsed.protocol !== 'http:' || ['localhost', '127.0.0.1', '[::1]'].includes(parsed.hostname);
    } catch {
      warning.hidden = true;
    }
  }

  async function run(action) {
    const values = { url: url.value, username: username.value, password: password.value };
    test.disabled = save.disabled = true;
    result.className = 'muted';
    result.textContent = action === 'test' ? 'Testing…' : 'Saving…';
    showFieldError(fields, null);
    try {
      if (action === 'test') {
        const { version } = await api.testQbittorrentSettings(values);
        result.textContent = `Connected to qBittorrent ${version}.`;
      } else {
        apply(await api.saveQbittorrentSettings(values));
        result.textContent = '';
        toast('qBittorrent settings saved.');
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
    username.input.value = settings.username ?? '';
    password.input.value = '';
    password.input.placeholder = settings.hasPassword ? 'Saved. Leave blank to keep it.' : '';
    updateWarning();
  }

  api.qbittorrentSettings().then(apply).catch((error) => {
    result.className = 'danger-text';
    result.textContent = error.message;
  });

  return form;
}
