(() => {
  'use strict';

  const { createPanel, formatBytes, getOrganizationSettings, h, icon, saveOrganizationSettings, selectField, settingsSection, testOrganizationSettings, textField, toast } = MediaFlow;

  const IMPORT_MODES = [
    { value: 'auto', label: 'Auto: hardlink when possible, otherwise copy' },
    { value: 'hardlink', label: 'Hardlink only (fail instead of copying)' },
    { value: 'copy', label: 'Always copy' },
  ];

  const INTERVALS = [
    { value: '30', label: '30 seconds' },
    { value: '60', label: '1 minute' },
    { value: '300', label: '5 minutes' },
    { value: '900', label: '15 minutes' },
  ];

  const MAPPING_FIELD = /^pathMappings\[(\d+)\]\.(qbittorrentPath|depotPath)$/;

  // A saved interval that isn't one of the presets (set through the API) stays selectable.
  function intervalOptions(seconds) {
    return INTERVALS.some((option) => option.value === String(seconds))
      ? INTERVALS
      : [...INTERVALS, { value: String(seconds), label: `${seconds} seconds` }];
  }

  function createMappingRow(mapping, onRemove) {
    const remote = textField({ label: 'Folder in qBittorrent', value: mapping.qbittorrentPath, placeholder: '/downloads' });
    const local = textField({ label: 'Same folder for Depot', value: mapping.depotPath, placeholder: '~/containers/media/downloads' });
    const remove = h('button', {
      class: 'icon-btn icon-btn-danger', type: 'button', title: 'Remove mapping', 'aria-label': 'Remove mapping', onClick: onRemove,
    }, icon('trash'));
    return { el: h('div', { class: 'mapping-row' }, remote.el, local.el, remove), remote, local };
  }

  // One line per folder: ready or not, where it really is, and why it can't be used.
  function folderCheck(folder) {
    const ready = folder.problem === null;
    const free = folder.availableBytes === null ? null : `${formatBytes(folder.availableBytes)} free`;
    const access = folder.role === 'downloads' ? 'Readable' : 'Writable';
    return h('li', { class: 'folder-check', 'data-state': ready ? 'ok' : 'down' },
      h('span', { class: 'dot' }),
      h('div', { class: 'folder-check-text' },
        h('p', { class: 'folder-check-title' },
          h('strong', { textContent: folder.label }), ` · ${ready ? [access, free].filter(Boolean).join(', ') : 'Not ready'}`),
        h('p', { class: 'folder-check-path', textContent: folder.resolvedPath }),
        ready ? null : h('p', { class: 'folder-check-problem', textContent: folder.problem }),
      ),
    );
  }

  function renderDiagnostics(container, diagnostics) {
    container.replaceChildren(
      h('ul', { class: 'folder-checks' }, diagnostics.folders.map(folderCheck)),
      ...diagnostics.notes.map((note) => h('p', { class: 'folder-note', textContent: note })),
    );
  }

  function createOrganizationForm(settings) {
    const fields = {
      moviesPath: textField({
        label: 'Movies folder', value: settings.moviesPath,
        hint: 'The folder of your Jellyfin Movies library. ~/ is the home folder of the user running Depot.',
      }),
      seriesPath: textField({ label: 'Series folder', value: settings.seriesPath, hint: 'The folder of your Jellyfin Shows library.' }),
      importMode: selectField({
        label: 'How files reach the library', options: IMPORT_MODES, value: settings.importMode,
        hint: 'Hardlinks use no extra space but need the downloads and the library on the same filesystem. The originals always stay where qBittorrent seeds them.',
      }),
      pollingIntervalSeconds: selectField({
        label: 'Look for finished downloads every',
        options: intervalOptions(settings.pollingIntervalSeconds),
        value: String(settings.pollingIntervalSeconds),
      }),
    };

    const mappingList = h('div', { class: 'mapping-list' });
    let mappingRows = [];

    function addMapping(mapping = { qbittorrentPath: '', depotPath: '' }) {
      const row = createMappingRow(mapping, () => {
        mappingRows = mappingRows.filter((other) => other !== row);
        row.el.remove();
      });
      mappingRows.push(row);
      mappingList.append(row.el);
      return row;
    }

    settings.pathMappings.forEach((mapping) => addMapping(mapping));
    const addButton = h('button', { class: 'btn', type: 'button', onClick: () => addMapping().remote.input.focus() },
      icon('plus'), 'Add mapping');

    const diagnostics = h('div', { class: 'folder-diagnostics', 'aria-live': 'polite' });
    renderDiagnostics(diagnostics, settings.diagnostics);

    const testButton = h('button', { class: 'btn', type: 'button', textContent: 'Test folders', onClick: test });
    const submit = h('button', { class: 'btn btn-primary', type: 'submit', textContent: 'Save library settings' });

    function allFields() {
      return [...Object.values(fields), ...mappingRows.flatMap((row) => [row.remote, row.local])];
    }

    // The field a backend validation error is about, such as "pathMappings[1].depotPath".
    function fieldFor(name) {
      const mapping = MAPPING_FIELD.exec(name ?? '');
      if (mapping) {
        const row = mappingRows[Number(mapping[1])];
        return row && (mapping[2] === 'qbittorrentPath' ? row.remote : row.local);
      }
      return fields[name];
    }

    function showFieldError(error) {
      const field = fieldFor(error.field);
      if (!field) return false;
      field.setError(error.message);
      field.input.focus();
      return true;
    }

    // Reading the values for a new request clears the errors from the previous one.
    function read() {
      for (const field of allFields()) field.setError(null);
      return {
        moviesPath: fields.moviesPath.input.value.trim(),
        seriesPath: fields.seriesPath.input.value.trim(),
        importMode: fields.importMode.input.value,
        pollingIntervalSeconds: Number(fields.pollingIntervalSeconds.input.value),
        pathMappings: mappingRows.map((row) => ({
          qbittorrentPath: row.remote.input.value.trim(),
          depotPath: row.local.input.value.trim(),
        })),
      };
    }

    async function run(button, action) {
      if (button.getAttribute('aria-busy')) return;
      button.setAttribute('aria-busy', 'true');
      try {
        await action(read());
      } catch (error) {
        if (!showFieldError(error)) toast(error.message, { type: 'error' });
      } finally {
        button.removeAttribute('aria-busy');
      }
    }

    function test() {
      return run(testButton, async (values) => renderDiagnostics(diagnostics, await testOrganizationSettings(values)));
    }

    function onSubmit(event) {
      event.preventDefault();
      return run(submit, async (values) => {
        const saved = await saveOrganizationSettings(values);
        renderDiagnostics(diagnostics, saved.diagnostics);
        toast(saved.diagnostics.canOrganize
          ? 'Library settings saved'
          : 'Library settings saved. Some folders aren\'t ready yet, so imports wait; see the folder check.',
        saved.diagnostics.canOrganize ? {} : { type: 'error' });
      });
    }

    return h('form', { novalidate: true, onSubmit },
      settingsSection('Media library',
        'The videos of finished movies and series are filed here, in the folder chosen when adding them. Depot hardlinks or copies them: the downloaded files and the torrent stay as they are.',
        fields.moviesPath.el, fields.seriesPath.el, fields.importMode.el, fields.pollingIntervalSeconds.el),
      settingsSection('Path mappings',
        'The folders Depot may read downloads from: nothing outside them is imported. Map qBittorrent\'s download folder to the same folder as Depot sees it, such as /downloads to ~/containers/media/downloads, or to itself when qBittorrent runs on this machine.',
        mappingList, h('div', { class: 'settings-actions' }, addButton)),
      settingsSection('Folder check', 'Whether Depot can use each folder right now. Imports wait while any check fails.', diagnostics),
      h('div', { class: 'settings-save' },
        submit,
        testButton,
        h('span', { class: 'settings-save-note', textContent: 'Saved on the Depot server.' })),
    );
  }

  // The media library part of the settings page, loaded once: refreshes never overwrite what
  // is being typed.
  function createOrganizationSettings(signal) {
    const panel = createPanel('Media library settings');
    let loaded = false;

    async function load() {
      let settings;
      try {
        settings = await getOrganizationSettings();
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
      panel.content(createOrganizationForm(settings));
    }

    panel.loading(4);
    return { el: panel.el, refresh: () => (loaded ? undefined : load()) };
  }

  Object.assign(MediaFlow, { createOrganizationSettings });
})();
