import { api } from '../api.js';
import { h, clear } from '../dom.js';
import { openDialog } from './dialog.js';
import { field, showFieldError } from './form.js';
import { toast } from './toast.js';

const NEW_SERIES = '\u0000new';

function segmented(name, options, value, onchange) {
  return h('div', { class: 'segmented', role: 'radiogroup' }, options.map((option) =>
    h('label', {},
      h('input', { type: 'radio', name, value: option.value, checked: option.value === value, onchange: () => onchange(option.value) }),
      option.label)));
}

// Asks what the download is (Movie, Series or Other) and where it goes, then takes a magnet link
// or a .torrent file.
export function openAddDialog() {
  let type = 'movie';
  let source = 'magnet';
  let series = [];

  const title = field({ label: "Movie's name", name: 'title', placeholder: 'Dune (2021)', hint: 'The folder created in the movies library. Adding the year helps Jellyfin find it.' });
  const seriesSelect = field({ label: 'Series', name: 'series', options: [{ value: NEW_SERIES, label: 'New series…' }] });
  const seriesName = field({ label: "New series' name", name: 'seriesName', placeholder: 'The Last of Us' });
  const season = field({ label: 'Season', name: 'season', type: 'number', min: 0, max: 999, value: '1', hint: '0 for specials.' });
  const magnet = field({ label: 'Magnet link', name: 'magnet', rows: 3, placeholder: 'magnet:?xt=urn:btih:…' });
  const file = field({ label: '.torrent file', name: 'file', type: 'file' });
  file.input.accept = '.torrent,application/x-bittorrent';
  const fields = { title, season, magnet, file };

  const target = h('div', { class: 'field-group' });
  const sourceBox = h('div', { class: 'field-group' });
  const general = h('p', { class: 'danger-text', role: 'alert', hidden: true });
  const submit = h('button', { class: 'btn primary', type: 'submit' }, 'Add download');

  function renderTarget() {
    clear(target);
    if (type === 'movie') target.append(title.el);
    if (type === 'series') {
      target.append(seriesSelect.el);
      if (seriesSelect.value === NEW_SERIES) target.append(seriesName.el);
      target.append(season.el);
    }
    if (type === 'other') target.append(h('p', { class: 'muted', text: 'The download stays where qBittorrent saves it. Depot never moves it to the library.' }));
  }

  function renderSource() {
    clear(sourceBox).append(source === 'magnet' ? magnet.el : file.el);
  }

  function suggestSeason() {
    const chosen = series.find((entry) => entry.name === seriesSelect.value);
    const seasons = chosen?.seasons ?? [];
    season.input.value = seasons.length ? Math.max(...seasons) : 1;
    season.el.querySelector('.hint').textContent = seasons.length
      ? `Already in the library: ${seasons.map((n) => (n === 0 ? 'Specials' : `Season ${n}`)).join(', ')}. 0 for specials.`
      : '0 for specials.';
  }

  seriesSelect.input.addEventListener('change', () => { suggestSeason(); renderTarget(); });

  api.librarySeries().then((list) => {
    series = list;
    clear(seriesSelect.input).append(
      ...list.map((entry) => h('option', { value: entry.name, text: entry.name })),
      h('option', { value: NEW_SERIES, text: 'New series…' }));
    suggestSeason();
    renderTarget();
  }).catch(() => {
    // Without the list, a new series can still be typed.
  });

  const dialog = openDialog({
    title: 'Add download',
    wide: true,
    body: [
      h('div', { class: 'field' }, h('label', { text: 'What is it?' }),
        segmented('mediaType', [
          { value: 'movie', label: 'Movie' },
          { value: 'series', label: 'Series' },
          { value: 'other', label: 'Other' },
        ], type, (value) => { type = value; renderTarget(); })),
      target,
      h('div', { class: 'field' }, h('label', { text: 'Source' }),
        segmented('source', [
          { value: 'magnet', label: 'Magnet link' },
          { value: 'file', label: '.torrent file' },
        ], source, (value) => { source = value; renderSource(); })),
      sourceBox,
      general,
    ],
    footer: [submit],
    initialFocus: title.input,
    async onSubmit() {
      general.hidden = true;
      showFieldError(fields, null);
      seriesName.setError(null);
      const choice = { mediaType: type };
      if (type === 'movie') choice.title = title.value;
      if (type === 'series') {
        choice.title = seriesSelect.value === NEW_SERIES ? seriesName.value : seriesSelect.value;
        choice.season = season.value === '' ? null : Number(season.value);
      }

      submit.disabled = true;
      try {
        let added;
        if (source === 'magnet') {
          added = await api.addMagnet({ magnet: magnet.value, ...choice });
        } else {
          const form = new FormData();
          if (file.input.files[0]) form.append('file', file.input.files[0]);
          for (const [key, value] of Object.entries(choice)) if (value != null) form.append(key, value);
          added = await api.addTorrentFile(form);
        }
        dialog.close();
        toast(`Added ${added.name}.`);
      } catch (error) {
        const target = error.field === 'title' && type === 'series' && seriesSelect.value === NEW_SERIES ? seriesName : null;
        if (target) {
          target.setError(error.message);
        } else if (!showFieldError(fields, error)) {
          general.textContent = error.message;
          general.hidden = false;
        }
      } finally {
        submit.disabled = false;
      }
    },
  });

  renderTarget();
  renderSource();
  title.input.focus();
}
