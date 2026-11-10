(() => {
  'use strict';

  const { createPanel, describeSyncRun, getJellyfinItems, getJellyfinLibraries, getJellyfinSettings, getJellyfinSync, h, icon, loadJellyfinImage, plural, saveJellyfinSettings, setIcon, setText, settingsSection, startJellyfinSync, testJellyfinConnection, textField, toast } = MediaFlow;

  const URL_HINT = 'The Jellyfin address as Depot\'s server reaches it, such as http://localhost:8096.';
  const KEY_SAVED = 'A key is saved on the server. Leave blank to keep it.';
  const KEY_MISSING = 'Create one in Jellyfin under Dashboard > API Keys.';
  const RECENT_COUNT = 8;

  const LIBRARY_KINDS = { movies: 'Movies', shows: 'Shows', mixed: 'Movies and shows', collections: 'Collections', other: 'Not synced' };

  function isHttpUrl(text) {
    try {
      return ['http:', 'https:'].includes(new URL(text).protocol);
    } catch {
      return false;
    }
  }

  // With http:// the key travels unencrypted, which only matters once it leaves the machine.
  function urlHint(text) {
    let url;
    try {
      url = new URL(text);
    } catch {
      return URL_HINT;
    }
    const local = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
    return url.protocol === 'http:' && !local
      ? 'This address uses http://, so the API key crosses the network unencrypted. Prefer https://, for example through a reverse proxy.'
      : URL_HINT;
  }

  function formatTime(value) {
    return value ? new Date(value).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }) : '';
  }

  // The form keeps what's typed: polling never touches it. onSaved says whether a key is saved.
  function createConnectionForm(settings, onSaved) {
    const fields = {
      url: textField({ label: 'URL', value: settings.url, type: 'url', hint: urlHint(settings.url ?? '') }),
      apiKey: textField({
        label: 'API key', type: 'password', autocomplete: 'new-password',
        hint: settings.hasApiKey ? KEY_SAVED : KEY_MISSING,
      }),
    };
    fields.url.input.addEventListener('input', () => fields.url.setHint(urlHint(fields.url.input.value.trim())));

    const fieldsByApiName = { 'jellyfin.url': fields.url, 'jellyfin.apiKey': fields.apiKey };

    function showFieldError(error) {
      const field = fieldsByApiName[error.field];
      if (!field) return false;
      field.setError(error.message);
      field.input.focus();
      return true;
    }

    // Reading the values for a new request clears the errors from the previous one.
    function read() {
      for (const field of Object.values(fields)) field.setError(null);
      return { url: fields.url.input.value.trim(), apiKey: fields.apiKey.input.value.trim() };
    }

    function validate() {
      const valid = isHttpUrl(fields.url.input.value.trim());
      fields.url.setError(valid ? null : 'Enter a URL starting with http:// or https://.');
      if (!valid) fields.url.input.focus();
      return valid;
    }

    const glyph = icon('check');
    const message = h('span');
    const result = h('span', { class: 'settings-result', role: 'status' });

    function showResult(state, text) {
      result.dataset.state = state;
      setIcon(glyph, state === 'error' ? 'alert' : 'check');
      setText(message, text);
      result.replaceChildren(...(state === 'pending' ? [message] : [glyph, message]));
    }

    const testButton = h('button', {
      class: 'btn', type: 'button', textContent: 'Test connection',
      async onClick() {
        if (testButton.getAttribute('aria-busy') || !validate()) return;
        testButton.setAttribute('aria-busy', 'true');
        showResult('pending', 'Testing…');
        try {
          const { serverName, version } = await testJellyfinConnection(read());
          showResult('ok', `Connected to ${serverName} (Jellyfin ${version})`);
        } catch (error) {
          showFieldError(error);
          showResult('error', error.message);
        }
        testButton.removeAttribute('aria-busy');
      },
    });

    const submit = h('button', { class: 'btn btn-primary', type: 'submit', textContent: 'Save Jellyfin settings' });

    async function onSubmit(event) {
      event.preventDefault();
      if (submit.getAttribute('aria-busy') || !validate()) return;
      submit.setAttribute('aria-busy', 'true');
      try {
        const saved = await saveJellyfinSettings(read());
        fields.apiKey.input.value = '';
        fields.apiKey.setHint(saved.hasApiKey ? KEY_SAVED : KEY_MISSING);
        onSaved(saved.hasApiKey);
        toast('Jellyfin settings saved');
      } catch (error) {
        if (!showFieldError(error)) toast(error.message, { type: 'error' });
      } finally {
        submit.removeAttribute('aria-busy');
      }
    }

    return h('form', { novalidate: true, onSubmit },
      settingsSection('Jellyfin connection',
        'Jellyfin is where Depot reads the library\'s metadata and images from. The browser never talks to Jellyfin and never sees the key.',
        fields.url.el, fields.apiKey.el,
        h('div', { class: 'settings-actions' }, testButton, result)),
      h('div', { class: 'settings-save' },
        submit,
        h('span', { class: 'settings-save-note', textContent: 'Saved on the Depot server. The key is stored encrypted and never sent back.' })),
    );
  }

  function libraryLine(library) {
    const counts = library.kind === 'other'
      ? 'Not synced: only movies and shows are kept'
      : [
        library.movies ? plural(library.movies, 'movie') : null,
        library.series ? plural(library.series, 'series', 'series') : null,
        library.episodes ? plural(library.episodes, 'episode') : null,
      ].filter(Boolean).join(', ') || 'Empty';
    const synced = library.lastSyncedAt ? `Synced ${formatTime(library.lastSyncedAt)}` : 'Not read completely yet';
    return h('li', { class: 'folder-check', 'data-state': 'ok' },
      h('span', { class: 'dot' }),
      h('div', { class: 'folder-check-text' },
        h('p', { class: 'folder-check-title' }, h('strong', { textContent: library.name }), ` · ${LIBRARY_KINDS[library.kind] ?? library.kind} · ${counts}`),
        h('p', { class: 'folder-check-path', textContent: `${library.locations.join(', ')} · ${synced}` }),
      ));
  }

  // The sync status, the libraries and the newest posters, redrawn on every refresh but only
  // where something changed.
  function createSyncPanel() {
    let configured = false;
    let running = false;
    let lastKey = null;
    let recentKey = null;
    let posterUrls = [];

    const state = h('p', { class: 'folder-check-title' });
    const summary = h('p', { class: 'folder-check-path' });
    const message = h('p', { class: 'folder-check-problem', hidden: true });
    const errors = h('ul', { class: 'sync-errors' });
    const status = h('div', { class: 'folder-check', 'data-state': 'ok', role: 'status' },
      h('span', { class: 'dot' }), h('div', { class: 'folder-check-text' }, state, summary, message, errors));
    const libraries = h('ul', { class: 'folder-checks' });
    const recent = h('ul', { class: 'poster-strip', 'aria-label': 'Recently added' });

    const button = h('button', {
      class: 'btn', type: 'button', textContent: 'Sync now',
      async onClick() {
        if (button.disabled) return;
        button.setAttribute('aria-busy', 'true');
        try {
          await startJellyfinSync();
          toast('Library sync started');
        } catch (error) {
          toast(error.message, { type: 'error' });
        } finally {
          button.removeAttribute('aria-busy');
        }
      },
    });

    function updateButton() {
      button.disabled = !configured || running;
      button.title = configured ? '' : 'Save the Jellyfin settings first.';
    }

    function renderRun(sync) {
      const run = sync.latestRun;
      running = sync.isRunning;
      updateButton();
      if (!run) {
        status.dataset.state = 'off';
        setText(state, configured ? 'Never synced' : 'Not set up');
        setText(summary, configured
          ? 'Sync now reads every movie and show library from Jellyfin. Syncs only run when you start them.'
          : 'Save the Jellyfin URL and API key above, then sync.');
        message.hidden = true;
        errors.replaceChildren();
        return;
      }
      const { state: runState, label, detail, failed } = describeSyncRun(run);
      status.dataset.state = runState;
      setText(state, `${label}${run.status === 'running' ? '…' : ''}`);
      setText(summary, detail);
      message.hidden = !(failed && run.message);
      setText(message, run.message ?? '');
      const errorKey = JSON.stringify(run.errors);
      if (errors.dataset.key !== errorKey) {
        errors.dataset.key = errorKey;
        errors.replaceChildren(...run.errors.map((error) => h('li', { class: 'folder-check-problem', textContent: `${error.library}: ${error.message}` })));
      }
    }

    function renderLibraries(list) {
      const key = JSON.stringify(list);
      if (key === lastKey) return;
      lastKey = key;
      libraries.replaceChildren(...list.map(libraryLine));
      libraries.hidden = list.length === 0;
    }

    // Posters come through Depot as blob: URLs (the token can't ride on an <img> request).
    // They're fetched again only after a sync changed something.
    async function renderRecent(page, signature) {
      if (signature === recentKey) return;
      recentKey = signature;
      const items = page.items;
      const urls = await Promise.all(items.map((item) => loadJellyfinImage(item, 'primary', { width: 160 }).catch(() => null)));
      if (recentKey !== signature) {
        urls.forEach((url) => url && URL.revokeObjectURL(url));
        return;
      }
      posterUrls.forEach((url) => URL.revokeObjectURL(url));
      posterUrls = urls.filter(Boolean);
      recent.replaceChildren(...items.map((item, index) => h('li', { class: 'poster', title: item.name },
        urls[index]
          ? h('img', { src: urls[index], alt: '', width: 80, height: 120, loading: 'lazy' })
          : h('span', { class: 'poster-missing', 'aria-hidden': 'true', textContent: item.name.slice(0, 1) }),
        h('span', { class: 'poster-name', textContent: item.year ? `${item.name} (${item.year})` : item.name }),
      )));
      recent.hidden = items.length === 0;
    }

    async function refresh() {
      const [sync, list, page] = await Promise.allSettled([
        getJellyfinSync(), getJellyfinLibraries(), getJellyfinItems({ sort: 'added', pageSize: RECENT_COUNT }),
      ]);
      if (sync.status === 'fulfilled') renderRun(sync.value);
      if (list.status === 'fulfilled') renderLibraries(list.value);
      if (page.status === 'fulfilled' && sync.status === 'fulfilled') {
        const run = sync.value.latestRun;
        await renderRecent(page.value, `${run?.id}:${run?.finishedAt}:${page.value.total}`);
      }
    }

    recent.hidden = true;
    libraries.hidden = true;
    return {
      el: [
        settingsSection('Library sync',
          'Depot keeps its own copy of the Jellyfin library: titles, seasons, episodes, files and their technical details. Images aren\'t copied; Depot fetches and caches them as they\'re shown.',
          status, h('div', { class: 'settings-actions' }, button)),
        settingsSection('Libraries', 'What the last sync found, per Jellyfin library.', libraries,
          h('p', { class: 'folder-note', textContent: 'Recently added' }), recent),
      ],
      refresh,
      setConfigured(value) {
        configured = value;
        updateButton();
      },
    };
  }

  // The Jellyfin part of the settings page: the form loads once, the sync panel on every refresh.
  function createJellyfinSettings(signal) {
    const panel = createPanel('Jellyfin settings');
    const sync = createSyncPanel();
    let loaded = false;

    async function load() {
      let settings;
      try {
        settings = await getJellyfinSettings();
      } catch (error) {
        if (signal.aborted) return;
        panel.error(error.message, () => {
          panel.loading(3);
          load();
        });
        return;
      }
      if (signal.aborted) return;
      loaded = true;
      sync.setConfigured(settings.hasApiKey);
      panel.content(h('div', {}, createConnectionForm(settings, sync.setConfigured), ...sync.el));
    }

    panel.loading(3);
    return {
      el: panel.el,
      refresh: async () => {
        if (!loaded) await load();
        if (loaded && !signal.aborted) await sync.refresh();
      },
    };
  }

  Object.assign(MediaFlow, { createJellyfinSettings });
})();
