(() => {
  'use strict';

  const {
    EMPTY, createImageLoader, createLibraryBar, createPanel, emptyLibraryState, formatAgo, formatBytes, getJellyfinItems, getJellyfinStats,
    h, mediaGridSkeleton, mediaRow, plural,
  } = MediaFlow;

  const ROW_SIZE = 14;

  function metric(label, value, note) {
    return h('div', { class: 'metric' },
      h('dt', { textContent: label }),
      h('dd', { class: 'metric-value', textContent: value }),
      h('dd', { class: 'metric-note', textContent: note || ' ' }));
  }

  // One bar per row, all in the same grey: a single measure needs no colour to tell rows apart.
  // The value is written next to each bar; hovering shows it with its share of the total.
  function barList(label, rows) {
    const largest = Math.max(1, ...rows.map((row) => row.value));
    const total = rows.reduce((sum, row) => sum + row.value, 0) || 1;
    return h('figure', { class: 'media-bars' },
      h('figcaption', { textContent: label }),
      h('ul', {}, rows.map((row) => h('li', {
        class: 'media-bar',
        title: `${row.label}: ${row.value.toLocaleString()} (${Math.round((row.value / total) * 100)}%)`,
      },
      h('span', { class: 'media-bar-label', textContent: row.label }),
      h('span', { class: 'media-bar-track' }, barFill(row.value / largest)),
      h('span', { class: 'media-bar-value', textContent: row.value.toLocaleString() })))));
  }

  function barFill(fraction) {
    const fill = h('span', { class: 'media-bar-fill' });
    fill.style.width = `${Math.max(fraction * 100, fraction > 0 ? 1 : 0)}%`;
    return fill;
  }

  function section(title, link, linkLabel, body) {
    return h('section', { class: 'media-section', 'aria-label': title },
      h('header', { class: 'section-header' },
        h('h2', { textContent: title }),
        link ? h('a', { class: 'section-link', href: link, textContent: linkLabel }) : null),
      body);
  }

  const libraryOverviewPage = {
    title: 'Library',

    mount(root, { signal }) {
      const images = createImageLoader(signal);
      const bar = createLibraryBar({ onFinished: () => load() });
      const panel = createPanel('Library');
      root.append(bar.el, panel.el);
      panel.el.classList.add('media-panel');
      panel.content(mediaGridSkeleton(14, { row: true }));

      let generation = 0;

      async function load() {
        const mine = ++generation;
        let results;
        try {
          results = await Promise.all([
            getJellyfinStats(),
            getJellyfinItems({ sort: 'added', pageSize: ROW_SIZE }),
            getJellyfinItems({ sort: 'release', pageSize: ROW_SIZE }),
            getJellyfinItems({ kind: 'collection', sort: 'name', pageSize: ROW_SIZE }),
          ]);
        } catch (error) {
          if (signal.aborted || mine !== generation) return;
          panel.error(error.message, () => {
            panel.loading();
            load();
          });
          return;
        }
        if (signal.aborted || mine !== generation) return;
        const [stats, added, released, collections] = results;
        const titles = stats.movies + stats.series;
        if (!titles) {
          panel.empty(await emptyLibraryState('titles'));
          return;
        }

        const anime = stats.animeMovies + stats.animeSeries;
        const metrics = h('dl', { class: 'metrics' },
          metric('Titles', titles.toLocaleString(), stats.lastSyncedAt ? `Synced ${formatAgo(stats.lastSyncedAt)}` : ''),
          metric('Movies', stats.movies.toLocaleString(), stats.animeMovies ? `${stats.animeMovies} anime` : ''),
          metric('Series', stats.series.toLocaleString(), stats.series ? `${plural(stats.seasons, 'season')}${stats.animeSeries ? `, ${stats.animeSeries} anime` : ''}` : ''),
          metric('Episodes', stats.episodes ? stats.episodes.toLocaleString() : EMPTY,
            stats.files ? `${plural(stats.files, 'file')}, ${formatBytes(stats.sizeBytes)}` : ''));

        // Anime is shown on its own, so the three parts add up to every title.
        const categories = [
          { label: 'Movies', value: stats.movies - stats.animeMovies },
          { label: 'Series', value: stats.series - stats.animeSeries },
          { label: 'Anime', value: anime },
        ];
        const charts = h('div', { class: 'media-charts' },
          barList('By category', categories),
          stats.topGenres.length
            ? barList('Top genres', stats.topGenres.map((genre) => ({ label: genre.value, value: genre.count })))
            : null);

        panel.content(h('div', { class: 'media-overview' },
          metrics,
          section('Distribution', null, null, charts),
          section('Recently added', '#/library/all?sort=added', 'View all', mediaRow(added.items, { images, showKind: true, label: 'Recently added' })),
          section('Newest releases', '#/library/all?sort=release', 'View all', mediaRow(released.items, { images, showKind: true, label: 'Newest releases' })),
          collections.total
            ? section(`Collections · ${collections.total}`, '#/library/collections', 'View all', mediaRow(collections.items, { images, label: 'Collections' }))
            : section('Collections', null, null, h('p', { class: 'media-note', textContent: 'No collections in Jellyfin yet. Collections made in Jellyfin show up here after the next sync.' })),
        ));
      }

      load();
      return () => bar.refresh();
    },
  };

  Object.assign(MediaFlow, { libraryOverviewPage });
})();
