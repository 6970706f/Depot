import { api } from '../api.js';
import { h, clear } from '../dom.js';
import * as format from '../format.js';
import { mediaImage } from '../components/media-image.js';
import { emptyState, errorState, loadingState } from '../components/states.js';

const PAGE_SIZE = 48;

const sorts = [
  { value: 'name', label: 'Name' },
  { value: 'added', label: 'Recently added' },
  { value: 'year', label: 'Year' },
  { value: 'rating', label: 'Rating' },
];

function notConfigured(error) {
  return error.code === 'not_configured'
    ? emptyState(error.message, h('a', { class: 'btn primary', href: '#/settings' }, 'Open Settings'))
    : null;
}

function card(item) {
  return h('a', { class: 'media-card', href: `#/library/item/${item.id}` },
    mediaImage({ id: item.id, tag: item.primaryImageTag, alt: item.name, width: 320 }),
    h('span', { class: 'media-title', text: item.name }),
    h('span', { class: 'media-meta', text: [item.year, item.rating != null ? `★ ${item.rating}` : null].filter(Boolean).join(' · ') }));
}

// #/library/movies or #/library/series: a poster grid with search, sorting and more pages.
function mountGrid(view, kind, actions) {
  let sort = 'name';
  let search = '';
  let page = 1;
  let items = [];
  let total = 0;
  let searchTimer;
  let generation = 0;

  const tabs = h('div', { class: 'tabs', role: 'group', 'aria-label': 'Library' },
    h('a', { class: 'tab-link', href: '#/library/movies', 'aria-current': kind === 'movie' ? 'page' : null }, 'Movies'),
    h('a', { class: 'tab-link', href: '#/library/series', 'aria-current': kind === 'series' ? 'page' : null }, 'Series'));
  const counts = h('span', { class: 'subtle' });
  const searchInput = h('input', {
    class: 'input search', type: 'search', placeholder: kind === 'movie' ? 'Search movies' : 'Search series', 'aria-label': 'Search',
    oninput: () => {
      clearTimeout(searchTimer);
      searchTimer = setTimeout(() => { search = searchInput.value.trim(); reload(); }, 300);
    },
  });
  const sortSelect = h('select', { class: 'select sort', 'aria-label': 'Sort by', onchange: () => { sort = sortSelect.value; reload(); } },
    sorts.map((option) => h('option', { value: option.value, text: option.label })));
  const grid = h('div', { class: 'media-grid' });
  const more = h('div', { class: 'row end' });
  const body = h('div', {}, loadingState());
  view.append(h('div', { class: 'page' }, h('div', { class: 'toolbar' }, tabs, searchInput, sortSelect, counts), body));

  async function fetchPage() {
    const id = ++generation;
    try {
      const result = await api.media({ kind, search, sort, page, pageSize: PAGE_SIZE });
      if (id !== generation) return;
      items = page === 1 ? result.items : items.concat(result.items);
      total = result.total;
      render(result.items);
    } catch (error) {
      if (id !== generation) return;
      clear(body).append(notConfigured(error) ?? errorState("The library didn't load", error, fetchPage));
    }
  }

  function render(added) {
    counts.textContent = `${total} ${kind === 'movie' ? 'movies' : 'series'}`;
    if (page === 1) {
      clear(grid);
      if (items.length) clear(body).append(grid, more);
      else clear(body).append(emptyState(search ? 'Nothing matches this search.' : 'Jellyfin has nothing here yet.'));
    }
    grid.append(...added.map(card));
    clear(more);
    if (items.length < total) {
      more.append(h('button', { class: 'btn', type: 'button', onclick: (event) => { event.target.disabled = true; page += 1; fetchPage(); } },
        `Show more (${total - items.length} left)`));
    }
  }

  function reload() {
    page = 1;
    clear(body).append(loadingState());
    fetchPage();
  }

  fetchPage();
  return {};
}

function fileLine(file) {
  return h('div', { class: 'file' },
    h('span', { class: 'mono', text: file.path ?? '—' }),
    h('span', { class: 'subtle', text: [format.bytes(file.sizeBytes), file.container, file.video, ...file.audio].filter(Boolean).join(' · ') }),
    file.subtitles.length ? h('span', { class: 'subtle', text: `Subtitles: ${file.subtitles.join(', ')}` }) : null);
}

function season(item, entry) {
  return h('details', { class: 'season', open: item.seasons.length === 1 },
    h('summary', {}, h('span', { text: entry.name }), h('span', { class: 'subtle', text: ` ${entry.episodes.length} episode(s)` })),
    h('ol', { class: 'episodes' }, entry.episodes.map((episode) => h('li', { class: 'episode' },
      mediaImage({ id: episode.id, tag: episode.primaryImageTag, alt: '', width: 320, className: 'thumb' }),
      h('div', { class: 'episode-text' },
        h('strong', { text: `${episode.number != null ? `${episode.number}. ` : ''}${episode.name}` }),
        episode.runtimeMinutes ? h('span', { class: 'subtle', text: ` ${episode.runtimeMinutes} min` }) : null,
        episode.overview ? h('p', { class: 'muted', text: episode.overview }) : null,
        episode.file ? h('span', { class: 'mono subtle', text: episode.file.path ?? '' }) : null)))));
}

// #/library/item/<id>: one movie or series with its details.
function mountItem(view, id) {
  const body = h('div', { class: 'page' }, loadingState());
  view.append(body);

  async function load() {
    try {
      const item = await api.mediaItem(id);
      document.title = `${item.name} · Depot`;
      document.getElementById('page-title').textContent = item.name;
      const facts = [item.year, item.officialRating, item.runtimeMinutes ? `${item.runtimeMinutes} min` : null,
        item.rating != null ? `★ ${item.rating}` : null].filter(Boolean).join(' · ');
      const links = [
        item.providerIds.Imdb ? h('a', { href: `https://www.imdb.com/title/${encodeURIComponent(item.providerIds.Imdb)}/`, target: '_blank', rel: 'noreferrer' }, 'IMDb') : null,
        item.providerIds.Tmdb ? h('a', { href: `https://www.themoviedb.org/${item.kind === 'movie' ? 'movie' : 'tv'}/${encodeURIComponent(item.providerIds.Tmdb)}`, target: '_blank', rel: 'noreferrer' }, 'TMDB') : null,
      ].filter(Boolean);
      clear(body).append(
        h('a', { class: 'subtle', href: `#/library/${item.kind === 'movie' ? 'movies' : 'series'}`, text: `← ${item.kind === 'movie' ? 'Movies' : 'Series'}` }),
        item.backdropImageTag ? mediaImage({ id: item.id, type: 'backdrop', tag: item.backdropImageTag, width: 1920, alt: '', className: 'backdrop' }) : null,
        h('div', { class: 'item-head' },
          mediaImage({ id: item.id, tag: item.primaryImageTag, alt: item.name, width: 480 }),
          h('div', { class: 'item-text' },
            h('h2', { class: 'item-title', text: item.name }),
            item.originalTitle && item.originalTitle !== item.name ? h('p', { class: 'subtle', text: item.originalTitle }) : null,
            facts ? h('p', { class: 'muted', text: facts }) : null,
            item.genres.length ? h('div', { class: 'row wrap' }, item.genres.map((genre) => h('span', { class: 'tag', text: genre }))) : null,
            item.overview ? h('p', { class: 'overview', text: item.overview }) : null,
            item.studios.length ? h('p', { class: 'subtle', text: item.studios.join(', ') }) : null,
            links.length ? h('div', { class: 'row' }, links) : null)),
        item.people.length ? h('section', { class: 'panel' }, h('h2', { text: 'Cast and crew' }),
          h('ul', { class: 'people' }, item.people.map((person) => h('li', {},
            h('strong', { text: person.name }), h('span', { class: 'subtle', text: person.role ? ` ${person.role}` : ` ${person.type}` }))))) : null,
        item.seasons.length ? h('section', { class: 'panel' }, h('h2', { text: 'Seasons' }), item.seasons.map((entry) => season(item, entry))) : null,
        item.files.length ? h('section', { class: 'panel' }, h('h2', { text: item.files.length > 1 ? 'Files (versions)' : 'File' }), item.files.map(fileLine)) : null);
    } catch (error) {
      clear(body).append(notConfigured(error) ?? errorState("This title didn't load", error, load));
    }
  }

  load();
  return {};
}

// The Jellyfin library, read live. No periodic refresh: it would reset the grid while scrolling.
export default {
  title: 'Library',
  mount(view, { params, actions }) {
    if (params[0] === 'item' && params[1]) return mountItem(view, params[1]);
    return mountGrid(view, params[0] === 'series' ? 'series' : 'movie', actions);
  },
};
