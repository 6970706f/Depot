import { api } from '../api.js';
import { h, clear, icon } from '../dom.js';
import * as format from '../format.js';
import { field, showFieldError } from './form.js';
import { toast } from './toast.js';

const importModes = [
  { value: 'auto', label: 'Auto (hardlink, copy when not possible)' },
  { value: 'hardlink', label: 'Hardlink only' },
  { value: 'copy', label: 'Copy' },
];

// Settings > Media library: the folders Jellyfin reads, how files get there and where Depot
// finds qBittorrent's downloads.
export function librarySettingsPanel() {
  const movies = field({ label: 'Movies folder', name: 'moviesPath', hint: 'The folder of your Jellyfin Movies library. ~/ is the home of the user running Depot.' });
  const series = field({ label: 'Series folder', name: 'seriesPath', hint: 'The folder of your Jellyfin Shows library.' });
  const mode = field({ label: 'Import mode', name: 'importMode', options: importModes });
  const interval = field({ label: 'Check for finished downloads every (seconds)', name: 'pollingIntervalSeconds', type: 'number', min: 10, max: 3600 });
  let mappings = [];
  const mappingList = h('div', { class: 'mappings' });
  const addMapping = h('button', { class: 'btn', type: 'button', onclick: () => { mappings.push(mappingRow({})); renderMappings(); } },
    icon('plus'), 'Add mapping');
  const checkResult = h('div', { class: 'check' });
  const result = h('p', { class: 'muted', role: 'status' });
  const check = h('button', { class: 'btn', type: 'button', onclick: () => run('check') }, 'Check folders');
  const save = h('button', { class: 'btn primary', type: 'submit' }, 'Save');

  const form = h('form', { class: 'panel', novalidate: true },
    h('header', {}, h('h2', { text: 'Media library' }), h('span', { class: 'subtle', text: 'Where imported videos go' })),
    h('div', { class: 'grid-2' }, movies.el, series.el, mode.el, interval.el),
    h('div', { class: 'field' },
      h('label', { text: 'Path mappings' }),
      h('span', { class: 'hint', text: "Depot only reads downloads from these folders. Write each folder as qBittorrent shows it and as Depot sees it; with qBittorrent on this machine, use the same path on both sides." }),
      mappingList,
      h('div', {}, addMapping)),
    h('div', { class: 'row wrap' }, save, check, result),
    checkResult);

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    run('save');
  });

  function mappingRow({ qbittorrentPath = '', depotPath = '' }) {
    const remote = field({ label: 'In qBittorrent', name: 'qbittorrentPath', value: qbittorrentPath, placeholder: '/downloads' });
    const local = field({ label: 'As Depot sees it', name: 'depotPath', value: depotPath, placeholder: '/srv/media/downloads' });
    const row = { remote, local };
    row.el = h('div', { class: 'mapping' }, remote.el, local.el,
      h('button', {
        class: 'icon-btn danger', type: 'button', 'aria-label': 'Remove mapping',
        onclick: () => { mappings = mappings.filter((item) => item !== row); renderMappings(); },
      }, icon('trash')));
    return row;
  }

  function renderMappings() {
    clear(mappingList).append(...mappings.map((row) => row.el));
    if (!mappings.length) mappingList.append(h('p', { class: 'subtle', text: 'No mappings: nothing will be imported.' }));
  }

  function values() {
    return {
      moviesPath: movies.value,
      seriesPath: series.value,
      importMode: mode.value,
      pollingIntervalSeconds: Number(interval.value),
      pathMappings: mappings.map((row) => ({ qbittorrentPath: row.remote.value, depotPath: row.local.value })),
    };
  }

  function allFields() {
    const fields = { moviesPath: movies, seriesPath: series, importMode: mode, pollingIntervalSeconds: interval };
    mappings.forEach((row, index) => {
      fields[`pathMappings[${index}].qbittorrentPath`] = row.remote;
      fields[`pathMappings[${index}].depotPath`] = row.local;
    });
    return fields;
  }

  async function run(action) {
    check.disabled = save.disabled = true;
    result.className = 'muted';
    result.textContent = action === 'check' ? 'Checking…' : 'Saving…';
    showFieldError(allFields(), null);
    try {
      if (action === 'check') {
        renderCheck(await api.checkLibrarySettings(values()));
        result.textContent = '';
      } else {
        apply(await api.saveLibrarySettings(values()));
        result.textContent = '';
        toast('Media library settings saved.');
      }
    } catch (error) {
      if (showFieldError(allFields(), error)) {
        result.textContent = '';
      } else {
        result.className = 'danger-text';
        result.textContent = error.message;
      }
    } finally {
      check.disabled = save.disabled = false;
    }
  }

  function renderCheck(report) {
    clear(checkResult).append(
      h('p', { class: report.ready ? '' : 'danger-text', text: report.ready ? 'Ready to import.' : 'Imports are paused until every folder below works.' }),
      h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
        h('thead', {}, h('tr', {}, h('th', { text: 'Folder' }), h('th', { text: 'Path' }), h('th', { text: 'State' }), h('th', { class: 'num', text: 'Free' }))),
        h('tbody', {}, report.folders.map((folder) => h('tr', {},
          h('td', { text: folder.label }),
          h('td', {}, h('span', { class: 'mono', text: folder.resolvedPath })),
          h('td', { class: folder.problem ? 'danger-text' : '' }, folder.problem ?? (folder.role === 'downloads' ? 'Readable' : 'Writable')),
          h('td', { class: 'num', text: format.bytes(folder.availableBytes) })))))),
      report.notes.length ? h('ul', { class: 'notes muted' }, report.notes.map((note) => h('li', { text: note }))) : null);
  }

  function apply(settings) {
    movies.input.value = settings.moviesPath;
    series.input.value = settings.seriesPath;
    mode.input.value = settings.importMode;
    interval.input.value = settings.pollingIntervalSeconds;
    mappings = settings.pathMappings.map(mappingRow);
    renderMappings();
    renderCheck(settings.check);
    if (!settings.isSaved) result.textContent = 'Not saved yet: these are the defaults.';
  }

  api.librarySettings().then(apply).catch((error) => {
    result.className = 'danger-text';
    result.textContent = error.message;
  });

  return form;
}
