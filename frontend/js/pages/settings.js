(() => {
  'use strict';

  const { createOrganizationSettings, createPanel, getPref, getSettings, h, icon, saveSettings, selectField, setIcon, setPref, setText, settingsSection: section, testConnection, textField, toast } = MediaFlow;

  function isHttpUrl(text) {
    try {
      return ['http:', 'https:'].includes(new URL(text).protocol);
    } catch {
      return false;
    }
  }

  const URL_HINT = 'The qBittorrent Web UI address, as Depot\'s server reaches it.';

  // With http:// the password and session travel unencrypted, which only matters once they
  // leave the machine.
  function urlHint(text) {
    let url;
    try {
      url = new URL(text);
    } catch {
      return URL_HINT;
    }
    const local = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
    return url.protocol === 'http:' && !local
      ? 'This address uses http://, so the password crosses the network unencrypted. Prefer https://, for example through a reverse proxy.'
      : URL_HINT;
  }

  function themeField() {
    const themes = [['black', 'Black'], ['graphite', 'Graphite'], ['light', 'Light']];
    return h('fieldset', { class: 'segmented segmented-framed' },
      h('legend', { class: 'sr-only', textContent: 'Theme' }),
      themes.map(([value, label]) => h('label', { class: 'segment' },
        h('input', {
          class: 'sr-only', type: 'radio', name: 'theme', value,
          checked: getPref('theme') === value,
          onChange: () => setPref('theme', value),
        }),
        label,
      )),
    );
  }

  function prefSelect(label, key, options, parse = String) {
    return selectField({
      label,
      value: String(getPref(key)),
      options,
      onChange: (event) => setPref(key, parse(event.target.value)),
    }).el;
  }

  function interfaceFields() {
    return [
      prefSelect('Row density', 'density', [
        { value: 'compact', label: 'Compact' },
        { value: 'comfortable', label: 'Comfortable' },
      ]),
      prefSelect('Speed units', 'speedUnit', [
        { value: 'MB/s', label: 'Megabytes per second (MB/s)' },
        { value: 'Mbit/s', label: 'Megabits per second (Mbit/s)' },
      ]),
      prefSelect('Refresh every', 'refreshSeconds', [
        { value: '1', label: '1 second' },
        { value: '2', label: '2 seconds' },
        { value: '5', label: '5 seconds' },
      ], Number),
      h('label', { class: 'check' },
        h('input', {
          type: 'checkbox',
          checked: getPref('shortcuts'),
          onChange: (event) => setPref('shortcuts', event.target.checked),
        }),
        'Use single-key shortcuts (A to add, / to search)',
      ),
    ];
  }

  // readValues returns what the backend should test; showFieldError marks a field the backend rejected.
  function createConnectionTest({ urlField, readValues, showFieldError }) {
    const glyph = icon('check');
    const message = h('span');
    const result = h('span', { class: 'settings-result', role: 'status' });
    const button = h('button', {
      class: 'btn', type: 'button',
      textContent: 'Test connection',
      async onClick() {
        if (button.getAttribute('aria-busy')) return;
        const url = urlField.input.value.trim();
        if (!isHttpUrl(url)) {
          urlField.setError('Enter a URL starting with http:// or https://.');
          urlField.input.focus();
          return;
        }
        urlField.setError(null);
        button.setAttribute('aria-busy', 'true');
        show('pending', 'Testing…');
        try {
          const { version } = await testConnection(readValues());
          show('ok', `Connected to qBittorrent ${version}`);
        } catch (error) {
          showFieldError(error);
          show('error', error.message);
        }
        button.removeAttribute('aria-busy');
      },
    });

    function show(state, text) {
      result.dataset.state = state;
      setIcon(glyph, state === 'error' ? 'alert' : 'check');
      setText(message, text);
      result.replaceChildren(...(state === 'pending' ? [message] : [glyph, message]));
    }

    return h('div', { class: 'settings-actions' }, button, result);
  }

  function createServerForm(settings) {
    const fields = {
      qbUrl: textField({ label: 'URL', value: settings.qbittorrent.url, type: 'url', hint: urlHint(settings.qbittorrent.url ?? '') }),
      qbUser: textField({ label: 'Username', value: settings.qbittorrent.username }),
      qbPassword: textField({
        label: 'Password', type: 'password', autocomplete: 'new-password',
        hint: settings.qbittorrent.hasPassword ? 'A password is saved on the server. Leave blank to keep it.' : 'No password saved yet.',
      }),
    };

    fields.qbUrl.input.addEventListener('input', () => fields.qbUrl.setHint(urlHint(fields.qbUrl.input.value.trim())));

    const submit = h('button', { class: 'btn btn-primary', type: 'submit', textContent: 'Save changes' });

    const fieldsByApiName = {
      'qbittorrent.url': fields.qbUrl,
      'qbittorrent.username': fields.qbUser,
      'qbittorrent.password': fields.qbPassword,
    };

    // Shows a backend validation error next to its field. Returns whether one was shown.
    function showFieldError(error) {
      const field = fieldsByApiName[error.field];
      if (!field) return false;
      field.setError(error.message);
      field.input.focus();
      return true;
    }

    // Reading the values for a new request clears the errors from the previous one.
    function readQbittorrent() {
      for (const field of Object.values(fieldsByApiName)) field.setError(null);
      return {
        url: fields.qbUrl.input.value.trim(),
        username: fields.qbUser.input.value.trim(),
        password: fields.qbPassword.input.value,
      };
    }

    function validate() {
      const valid = isHttpUrl(fields.qbUrl.input.value.trim());
      fields.qbUrl.setError(valid ? null : 'Enter a URL starting with http:// or https://.');
      if (!valid) fields.qbUrl.input.focus();
      return valid;
    }

    async function onSubmit(event) {
      event.preventDefault();
      if (submit.getAttribute('aria-busy') || !validate()) return;
      submit.setAttribute('aria-busy', 'true');
      try {
        const saved = await saveSettings({ qbittorrent: readQbittorrent() });
        fields.qbPassword.input.value = '';
        fields.qbPassword.setHint(saved.qbittorrent.hasPassword ? 'A password is saved on the server. Leave blank to keep it.' : 'No password saved yet.');
        toast('Settings saved');
      } catch (error) {
        if (!showFieldError(error)) toast(error.message, { type: 'error' });
      } finally {
        submit.removeAttribute('aria-busy');
      }
    }

    return h('form', { novalidate: true, onSubmit },
      section('qBittorrent connection', 'MediaFlow connects through its backend. The browser never talks to qBittorrent.',
        fields.qbUrl.el, fields.qbUser.el, fields.qbPassword.el,
        createConnectionTest({ urlField: fields.qbUrl, readValues: readQbittorrent, showFieldError })),
      h('div', { class: 'settings-save' },
        submit,
        h('span', { class: 'settings-save-note', textContent: 'Saved on the Depot server. The password is stored encrypted and never sent back.' })),
    );
  }

  const settingsPage = {
    title: 'Settings',

    mount(root, { signal }) {
      const panel = createPanel('Settings');
      let loaded = false;

      async function load() {
        let settings;
        try {
          settings = await getSettings();
        } catch (error) {
          if (signal.aborted) return;
          panel.error(error.message, () => {
            panel.loading(4);
            load();
          });
          return;
        }
        if (signal.aborted) return;
        loaded = true;
        panel.content(createServerForm(settings));
      }

      const organization = createOrganizationSettings(signal);

      root.append(
        h('div', { class: 'settings' },
          panel.el,
          organization.el,
          section('Theme', 'Applies right away and is saved in this browser.', themeField()),
          section('Interface', 'Applies right away and is saved in this browser.', ...interfaceFields()),
        ),
      );
      panel.loading(4);

      // The forms stay untouched by polling so it can't overwrite what's being typed.
      return () => Promise.all([loaded ? undefined : load(), organization.refresh()]);
    },
  };

  Object.assign(MediaFlow, { settingsPage });
})();
