(() => {
  'use strict';

  const {
    createImageLoader, createLibraryBar, createMediaGrid, createPanel, getJellyfinFilters, getJellyfinItems, getJellyfinSync, getSystemStatus,
    go, h, mediaGridSkeleton, plural, searchField, selectControl, setText, startJellyfinSync, toast,
  } = MediaFlow;

  const PAGE_SIZE = 60;

  const SORTS = [
    { value: 'name', label: 'A to Z' },
    { value: 'release', label: 'Newest releases' },
    { value: 'added', label: 'Recently added' },
  ];

  const TYPES = [
    { value: '', label: 'Movies and series' },
    { value: 'movie', label: 'Movies' },
    { value: 'series', label: 'Series' },
  ];

  // What to say when the library has nothing to show yet, and what to do about it.
  async function emptyLibraryState(noun) {
    const [system, sync] = await Promise.allSettled([getSystemStatus(), getJellyfinSync()]);
    if (system.status === 'fulfilled' && !system.value.jellyfin.configured) {
      return {
        key: 'not-configured',
        title: 'Jellyfin isn\'t set up',
        hint: 'Depot shows the library it reads from Jellyfin. Add the Jellyfin URL and an API key in Settings.',
        action: { label: 'Open Settings', onClick: () => go('settings') },
      };
    }
    if (sync.status === 'fulfilled' && sync.value.isRunning) {
      return { key: 'syncing', title: 'Syncing the library', hint: 'Titles appear here as soon as the sync saves them.' };
    }
    if (sync.status === 'fulfilled' && !sync.value.latestRun) {
      return {
        key: 'never-synced',
        title: 'The library hasn\'t been synced yet',
        hint: 'Sync now reads the movies and series of every Jellyfin library.',
        action: {
          label: 'Sync now',
          async onClick() {
            try {
              await startJellyfinSync();
              toast('Library sync started');
            } catch (error) {
              toast(error.message, { type: 'error' });
            }
          },
        },
      };
    }
    return { key: 'empty', title: `No ${noun} in the library`, hint: `Jellyfin has no ${noun}, as of the last sync.` };
  }

  // A page of posters for one category. kind and anime are the API's filters; types lets the
  // page choose between movies and series; filters says whether genre and year apply.
  function createBrowsePage({ title, route, noun, kind = null, anime = null, types = false, filters = true, showKind = false }) {
    return {
      title,
      keepParams: true,

      mount(root, { params, signal }) {
        const state = {
          search: params.search ?? '',
          type: types ? (TYPES.some((option) => option.value === params.type) ? params.type : '') : '',
          genre: params.genre ?? '',
          year: params.year ?? '',
          sort: SORTS.some((option) => option.value === params.sort) ? params.sort : 'name',
        };
        const images = createImageLoader(signal);
        const bar = createLibraryBar({ onFinished: () => reload() });
        const grid = createMediaGrid({ images, showKind: showKind || types, label: title });
        const panel = createPanel(title);
        const count = h('span', { class: 'media-count', role: 'status' });
        const more = h('button', { class: 'btn', type: 'button', textContent: 'Show more', hidden: true, onClick: () => loadMore() });
        const sentinel = h('div', { class: 'media-sentinel', 'aria-hidden': 'true' });
        const results = h('div', { class: 'media-results' }, grid.el, h('div', { class: 'media-more' }, more), sentinel);

        let page = 0;
        let total = 0;
        let loaded = 0;
        let loading = null;
        let generation = 0;

        function currentKind() {
          return types ? state.type || null : kind;
        }

        // The filters live in the address, so going back to the list finds them as they were.
        function remember() {
          const query = new URLSearchParams(Object.entries(state).filter(([key, value]) => value && !(key === 'sort' && value === 'name')));
          history.replaceState(null, '', `#/${route}${query.size ? `?${query}` : ''}`);
        }

        function isFiltered() {
          return Boolean(state.search || state.genre || state.year || state.type);
        }

        async function fetchPage(number) {
          return getJellyfinItems({
            kind: currentKind(), anime, search: state.search, genre: state.genre, year: state.year,
            sort: state.sort, page: number, pageSize: PAGE_SIZE,
          });
        }

        // From the first page again, after a filter changed or a sync finished.
        async function reload() {
          const mine = ++generation;
          remember();
          clear.hidden = !isFiltered();
          if (!loaded) panel.content(skeleton);
          let first;
          try {
            first = await fetchPage(1);
          } catch (error) {
            if (signal.aborted || mine !== generation) return;
            panel.error(error.message, () => reload());
            return;
          }
          if (signal.aborted || mine !== generation) return;
          page = 1;
          total = first.total;
          loaded = first.items.length;
          setText(count, `${plural(total, noun === 'collections' ? 'collection' : 'title')}`);
          if (!total) {
            if (isFiltered()) {
              panel.empty({
                key: `none:${JSON.stringify(state)}`,
                title: 'Nothing matches',
                hint: state.search ? `No ${noun} match “${state.search}” with these filters.` : `No ${noun} match these filters.`,
                action: { label: 'Clear filters', onClick: clearFilters },
              });
            } else {
              panel.empty(await emptyLibraryState(noun));
            }
            more.hidden = true;
            return;
          }
          grid.set(first.items);
          panel.content(results);
          more.hidden = loaded >= total;
          refreshFilters();
        }

        async function loadMore() {
          if (loading || loaded >= total) return;
          const mine = generation;
          more.setAttribute('aria-busy', 'true');
          loading = fetchPage(page + 1)
            .then((next) => {
              if (signal.aborted || mine !== generation) return;
              page += 1;
              loaded += next.items.length;
              grid.append(next.items);
              more.hidden = loaded >= total || next.items.length === 0;
            })
            .catch((error) => toast(error.message, { type: 'error' }))
            .finally(() => {
              loading = null;
              more.removeAttribute('aria-busy');
            });
          await loading;
        }

        // Scrolling near the end loads the next page; the button does the same by hand.
        const observer = new IntersectionObserver((entries) => {
          if (entries.some((entry) => entry.isIntersecting) && !more.hidden) loadMore();
        }, { rootMargin: '800px 0px' });
        observer.observe(sentinel);
        signal.addEventListener('abort', () => observer.disconnect());

        // Toolbar

        let searchTimer = null;
        const search = searchField({
          label: `Search ${noun}`, placeholder: `Search ${noun}`, value: state.search,
          onInput(event) {
            clearTimeout(searchTimer);
            searchTimer = setTimeout(() => {
              state.search = event.target.value.trim();
              reload();
            }, 250);
          },
        });

        const genreSelect = selectControl({ label: 'Genre', options: [{ value: '', label: 'All genres' }], value: '', onChange: (event) => change('genre', event.target.value) });
        const yearSelect = selectControl({ label: 'Year', options: [{ value: '', label: 'Any year' }], value: '', onChange: (event) => change('year', event.target.value) });
        const typeSelect = types ? selectControl({ label: 'Type', options: TYPES, value: state.type, onChange: (event) => change('type', event.target.value) }) : null;
        const sortSelect = selectControl({ label: 'Sort', options: SORTS, value: state.sort, onChange: (event) => change('sort', event.target.value) });
        const clear = h('button', { class: 'btn btn-quiet', type: 'button', textContent: 'Clear filters', hidden: true, onClick: clearFilters });

        function change(key, value) {
          state[key] = value;
          if (key === 'type') {
            // The genres and years on offer depend on the type.
            state.genre = '';
            state.year = '';
          }
          reload();
        }

        function clearFilters() {
          Object.assign(state, { search: '', genre: '', year: '', type: '' });
          search.querySelector('input').value = '';
          if (typeSelect) typeSelect.querySelector('select').value = '';
          reload();
        }

        // The genres and years that exist in this category, with their counts.
        let filtersKey = null;
        async function refreshFilters() {
          if (!filters) return;
          let available;
          try {
            available = await getJellyfinFilters({ kind: currentKind(), anime });
          } catch {
            return;
          }
          if (signal.aborted) return;
          const key = JSON.stringify([available, state.genre, state.year]);
          if (key === filtersKey) return;
          filtersKey = key;
          fillSelect(genreSelect, 'All genres', available.genres.map((genre) => ({ value: genre.value, label: `${genre.value} (${genre.count})` })), state.genre);
          fillSelect(yearSelect, 'Any year', available.years.map((year) => ({ value: String(year.value), label: `${year.value} (${year.count})` })), state.year);
        }

        function fillSelect(control, emptyLabel, options, value) {
          const select = control.querySelector('select');
          // A chosen value the library no longer has stays selectable, so the filter shows what's applied.
          const all = value && !options.some((option) => option.value === value) ? [...options, { value, label: value }] : options;
          select.replaceChildren(h('option', { value: '', textContent: emptyLabel }),
            ...all.map((option) => h('option', { value: option.value, textContent: option.label, selected: option.value === value })));
          select.value = value;
        }

        const toolbar = h('div', { class: 'toolbar media-toolbar' },
          search,
          typeSelect,
          filters ? genreSelect : null,
          filters ? yearSelect : null,
          clear,
          h('div', { class: 'toolbar-end' }, count, sortSelect));

        const skeleton = mediaGridSkeleton(18);
        root.append(bar.el, toolbar, panel.el);
        panel.el.classList.add('media-panel');
        reload();

        // Polling only checks the sync; the grid reloads when a sync finishes.
        return () => bar.refresh();
      },
    };
  }

  Object.assign(MediaFlow, {
    emptyLibraryState,
    libraryAllPage: createBrowsePage({ title: 'All media', route: 'library/all', noun: 'titles', types: true }),
    libraryMoviesPage: createBrowsePage({ title: 'Movies', route: 'library/movies', noun: 'movies', kind: 'movie' }),
    librarySeriesPage: createBrowsePage({ title: 'Series', route: 'library/series', noun: 'series', kind: 'series' }),
    libraryAnimePage: createBrowsePage({ title: 'Anime', route: 'library/anime', noun: 'anime', anime: true, types: true }),
    libraryCollectionsPage: createBrowsePage({ title: 'Collections', route: 'library/collections', noun: 'collections', kind: 'collection', filters: false }),
  });
})();
