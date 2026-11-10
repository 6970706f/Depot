(() => {
  'use strict';

  const {
    createImageLoader, createLibraryBar, createMediaGrid, createPanel, formatBytes, formatChannels, formatResolution, formatRuntime, getJellyfinItem,
    go, h, itemHref, kindLabel, plural, segmentedButtons,
  } = MediaFlow;

  const CATEGORY = {
    movie: { label: 'Movies', route: 'library/movies' },
    series: { label: 'Series', route: 'library/series' },
    season: { label: 'Series', route: 'library/series' },
    episode: { label: 'Series', route: 'library/series' },
    collection: { label: 'Collections', route: 'library/collections' },
  };

  // Only ids of the expected shape become links to other sites.
  const EXTERNAL = [
    { provider: 'Imdb', label: 'IMDb', pattern: /^tt\d+$/, url: (id) => `https://www.imdb.com/title/${id}/` },
    { provider: 'Tmdb', label: 'TMDB', pattern: /^\d+$/, url: (id, kind) => `https://www.themoviedb.org/${kind === 'movie' ? 'movie' : 'tv'}/${id}` },
  ];

  function category(item) {
    return item.isAnime && (item.kind === 'movie' || item.kind === 'series') ? { label: 'Anime', route: 'library/anime' } : CATEGORY[item.kind];
  }

  function breadcrumb(item) {
    const where = category(item);
    const parts = [h('a', { href: '#/library', textContent: 'Library' }), h('a', { href: `#/${where.route}`, textContent: where.label })];
    if (item.series) parts.push(h('a', { href: itemHref(item.series), textContent: item.series.name }));
    if (item.season && item.kind === 'episode') parts.push(h('a', { href: itemHref(item.season), textContent: item.season.name }));
    return h('nav', { class: 'media-breadcrumb', 'aria-label': 'Breadcrumb' },
      parts.flatMap((part, index) => (index ? [h('span', { 'aria-hidden': 'true', textContent: '/' }), part] : [part])));
  }

  function years(item) {
    if (item.kind !== 'series' || !item.year) return item.year ? String(item.year) : null;
    if (item.seriesStatus === 'Ended' && item.endDate) {
      const end = Number(item.endDate.slice(0, 4));
      return end > item.year ? `${item.year}–${end}` : String(item.year);
    }
    return item.seriesStatus === 'Continuing' ? `${item.year}–` : String(item.year);
  }

  function facts(item) {
    const list = [];
    if (item.kind === 'episode') {
      if (item.seasonNumber !== null && item.episodeNumber !== null) {
        list.push(`S${item.seasonNumber} · E${item.episodeNumber}${item.episodeNumberEnd ? `–${item.episodeNumberEnd}` : ''}`);
      }
      if (item.premiereDate) list.push(new Date(`${item.premiereDate}T00:00:00`).toLocaleDateString([], { dateStyle: 'medium' }));
    } else if (item.kind === 'collection') {
      // Jellyfin gives a collection the year and rating of one of its titles; the span of its titles says more.
      const spanYears = item.children.map((child) => child.year).filter(Boolean);
      if (spanYears.length) {
        const [first, last] = [Math.min(...spanYears), Math.max(...spanYears)];
        list.push(first === last ? String(first) : `${first}–${last}`);
      }
    } else {
      const span = years(item);
      if (span) list.push(span);
    }
    if (item.kind === 'series' && item.seriesStatus) list.push(item.seriesStatus);
    if ((item.kind === 'series' || item.kind === 'season') && item.episodeCount) list.push(plural(item.episodeCount, 'episode'));
    if (item.kind === 'collection') list.push(plural(item.children.length, 'title'));
    const runtime = formatRuntime(item.runtimeSeconds);
    if (runtime && item.kind !== 'series') list.push(runtime);
    if (item.officialRating && item.kind !== 'collection') list.push(item.officialRating);
    if (item.communityRating) list.push(`★ ${item.communityRating.toFixed(1)}`);
    if (item.productionLocations.length) list.push(item.productionLocations.slice(0, 2).join(', '));
    return list;
  }

  function peopleOf(item, kinds) {
    return item.people.filter((person) => kinds.includes(person.kind));
  }

  // "Directed by", "Written by", the collections it's in and links to IMDb and TMDB.
  function credits(item) {
    const rows = [];
    const directors = peopleOf(item, ['director']);
    const writers = peopleOf(item, ['writer']);
    if (directors.length) rows.push(['Directed by', directors.map((person) => person.name).join(', ')]);
    if (writers.length) rows.push(['Written by', writers.map((person) => person.name).join(', ')]);
    if (item.studios.length) rows.push(['Studio', item.studios.slice(0, 3).join(', ')]);
    const subtitles = subtitleLanguages(item);
    if (subtitles.length) rows.push(['Subtitles', subtitles.join(', ')]);

    const links = EXTERNAL
      .map((site) => ({ site, id: item.providerIds[site.provider] }))
      .filter(({ site, id }) => id && site.pattern.test(id) && item.kind !== 'collection' && item.kind !== 'season')
      .map(({ site, id }) => h('a', { href: site.url(id, item.kind), target: '_blank', rel: 'noopener noreferrer', textContent: site.label }));

    return h('div', { class: 'media-credits' },
      rows.length ? h('dl', {}, rows.flatMap(([term, value]) => [h('dt', { textContent: term }), h('dd', { textContent: value })])) : null,
      item.collections.length
        ? h('p', {}, 'Part of ', item.collections.flatMap((collection, index) => [index ? ', ' : null, h('a', { href: itemHref(collection), textContent: collection.name })]))
        : null,
      links.length ? h('p', { class: 'media-links' }, links) : null);
  }

  const languageNames = new Intl.DisplayNames([navigator.language, 'en'], { type: 'language', fallback: 'none' });

  // "eng" or "pt-BR" as a name, such as "English" or "Brazilian Portuguese"; the code itself when
  // the browser doesn't know it.
  function languageName(code) {
    try {
      return languageNames.of(code) ?? code;
    } catch {
      return code;
    }
  }

  // Every subtitle language across the item's files, once each.
  function subtitleLanguages(item) {
    const names = item.files.flatMap((file) => file.streams
      .filter((stream) => stream.kind === 'subtitle')
      .map((stream) => (stream.language ? languageName(stream.language) : stream.title ?? stream.codec)));
    return [...new Set(names.filter(Boolean))];
  }

  function hero(item, parent, images) {
    const backdropItem = item.images.backdrops.length ? item : parent?.images.backdrops.length ? parent : null;
    const backdrop = h('div', { class: 'media-hero-backdrop', 'aria-hidden': 'true' });
    if (backdropItem) {
      const img = h('img', { alt: '', decoding: 'async' });
      backdrop.append(img);
      images.item(img, backdropItem, 'backdrop', { width: 1920 });
    }

    // An episode's own picture is a still from it, so it gets a wide frame.
    const wide = item.kind === 'episode';
    const posterItem = item.images.primary ? item : parent;
    const poster = h('div', { class: `media-hero-poster${wide && item.images.primary ? ' media-hero-poster-wide' : ''}` },
      h('span', { class: 'media-fallback' }, h('span', { class: 'media-fallback-kind', textContent: kindLabel(item.kind) }), h('span', { class: 'media-fallback-title', textContent: item.name })));
    if (posterItem?.images.primary) {
      const img = h('img', { alt: `${item.name} poster`, decoding: 'async' });
      poster.append(img);
      images.item(img, posterItem, 'primary', { width: wide ? 720 : 480 });
    } else if (item.kind === 'collection' && item.children.some((child) => child.images.primary)) {
      const tiles = item.children.filter((child) => child.images.primary).slice(0, 4).map((child) => {
        const img = h('img', { alt: '', decoding: 'async' });
        images.item(img, child, 'primary', { width: 240 });
        return img;
      });
      poster.append(h('span', { class: 'media-mosaic', 'data-count': String(tiles.length) }, tiles));
    }

    const original = item.originalTitle && item.originalTitle !== item.name ? h('p', { class: 'media-original', textContent: item.originalTitle }) : null;
    return h('section', { class: 'media-hero' },
      backdrop,
      h('div', { class: 'media-hero-body' },
        poster,
        h('div', { class: 'media-hero-text' },
          breadcrumb(item),
          h('h2', { class: 'media-title', textContent: item.name }),
          original,
          h('p', { class: 'media-facts', textContent: facts(item).join(' · ') }),
          item.genres.length
            ? h('ul', { class: 'media-genres', 'aria-label': 'Genres' }, item.genres.map((genre) => h('li', {},
              h('a', { href: `#/${category(item).route === 'library/collections' ? 'library/all' : category(item).route}?genre=${encodeURIComponent(genre)}`, textContent: genre }))))
            : null,
          item.overview ? h('p', { class: 'media-overview-text', textContent: item.overview }) : h('p', { class: 'media-note', textContent: 'Jellyfin has no synopsis for this title.' }),
          credits(item))));
  }

  function cast(item, images) {
    const people = peopleOf(item, ['actor', 'guestStar']);
    if (!people.length) return null;
    return section('Cast', h('ul', { class: 'media-cast' }, people.map((person) => {
      const photo = h('span', { class: 'media-person-photo' }, h('span', { class: 'media-fallback-title', 'aria-hidden': 'true', textContent: initials(person.name) }));
      if (person.imageVersion) {
        const img = h('img', { alt: '', decoding: 'async' });
        photo.append(img);
        images.person(img, item.id, person, { width: 160 });
      }
      return h('li', { class: 'media-person' },
        photo,
        h('span', { class: 'media-person-name', textContent: person.name }),
        person.role ? h('span', { class: 'media-person-role', textContent: person.role }) : null);
    })));
  }

  function initials(name) {
    return name.split(/\s+/).filter(Boolean).slice(0, 2).map((word) => word[0]).join('').toUpperCase();
  }

  function section(title, body, extra) {
    return h('section', { class: 'media-section' },
      h('header', { class: 'section-header' }, h('h2', { textContent: title }), extra ?? null),
      body);
  }

  // The episodes of a season, each with its number, still, length and synopsis.
  function episodeList(episodes, images) {
    if (!episodes.length) return h('p', { class: 'media-note', textContent: 'No episodes of this season are in the library.' });
    return h('ol', { class: 'media-episodes' }, episodes.map((episode) => {
      const still = h('span', { class: 'media-episode-still' }, h('span', { class: 'media-fallback-title', 'aria-hidden': 'true', textContent: episode.episodeNumber ?? '' }));
      if (episode.images.primary) {
        const img = h('img', { alt: '', decoding: 'async' });
        still.append(img);
        images.item(img, episode, 'primary', { width: 320 });
      }
      const number = episode.episodeNumber === null ? '' : `${episode.episodeNumber}${episode.episodeNumberEnd ? `–${episode.episodeNumberEnd}` : ''}. `;
      const meta = [formatRuntime(episode.runtimeSeconds), episode.fileCount ? null : 'No file', episode.communityRating ? `★ ${episode.communityRating.toFixed(1)}` : null].filter(Boolean);
      return h('li', {},
        h('a', { class: 'media-episode', href: itemHref(episode) },
          still,
          h('span', { class: 'media-episode-text' },
            h('span', { class: 'media-episode-title' }, h('strong', { textContent: `${number}${episode.name}` }), meta.length ? h('span', { textContent: meta.join(' · ') }) : null),
            h('span', { class: 'media-episode-overview', textContent: episode.overview ?? 'No synopsis.' }))));
    }));
  }

  // Seasons as tabs; the episodes of the chosen one load on demand.
  function seasons(item, images, signal, selected) {
    if (!item.children.length) return section('Seasons', h('p', { class: 'media-note', textContent: 'No seasons of this series are in the library.' }));
    const body = h('div', { class: 'media-season-body' });
    let current = item.children.some((season) => String(season.id) === selected) ? selected : String(item.children[0].id);

    async function show(id) {
      current = id;
      tabs.setValue(id);
      history.replaceState(null, '', `#/library/item?id=${item.id}&season=${id}`);
      body.replaceChildren(h('p', { class: 'media-note', role: 'status', textContent: 'Loading episodes…' }));
      try {
        const season = await getJellyfinItem(id);
        if (signal.aborted || current !== id) return;
        body.replaceChildren(
          season.overview ? h('p', { class: 'media-season-overview', textContent: season.overview }) : null,
          episodeList(season.children, images));
      } catch (error) {
        if (signal.aborted || current !== id) return;
        body.replaceChildren(h('p', { class: 'media-note media-note-error', role: 'alert', textContent: error.message }),
          h('button', { class: 'btn', type: 'button', textContent: 'Retry', onClick: () => show(id) }));
      }
    }

    const tabs = segmentedButtons({
      label: 'Seasons',
      options: item.children.map((season) => ({ value: String(season.id), label: season.name, count: season.episodeCount ? h('span', { class: 'segment-count', textContent: ` ${season.episodeCount}` }) : null })),
      value: current,
      onChange: show,
      framed: true,
    });
    show(current);
    return section('Seasons', h('div', { class: 'media-seasons' }, h('div', { class: 'media-season-tabs' }, tabs.el), body));
  }

  function describeStream(stream) {
    if (stream.kind === 'video') {
      return [formatResolution(stream.width, stream.height), stream.codec?.toUpperCase(), stream.videoRange].filter(Boolean).join(' · ');
    }
    const extra = [stream.isDefault ? 'default' : null, stream.isForced ? 'forced' : null, stream.isExternal ? 'external file' : null].filter(Boolean);
    const base = stream.title ?? [stream.language, stream.codec?.toUpperCase(), formatChannels(stream.channels)].filter(Boolean).join(' · ');
    return extra.length ? `${base} (${extra.join(', ')})` : base;
  }

  // Each file with what Jellyfin found in it: a movie can come in several versions.
  function files(item) {
    if (!item.files.length) return null;
    return section(item.files.length > 1 ? `Files · ${item.files.length} versions` : 'File', h('div', { class: 'media-files' }, item.files.map((file) => {
      const streams = (kind) => file.streams.filter((stream) => stream.kind === kind).map(describeStream);
      const rows = [
        ['Video', streams('video')],
        ['Audio', streams('audio')],
        ['Subtitles', streams('subtitle')],
      ];
      const summary = [
        file.container?.toUpperCase(),
        file.sizeBytes ? formatBytes(file.sizeBytes) : null,
        file.bitrate ? `${(file.bitrate / 1e6).toFixed(1)} Mbps` : null,
        formatRuntime(file.runtimeSeconds),
      ].filter(Boolean).join(' · ');
      return h('article', { class: 'media-file' },
        h('header', {}, h('strong', { textContent: file.name ?? (item.files.length > 1 ? 'Version' : item.name) }), h('span', { textContent: summary })),
        h('dl', {}, rows.flatMap(([term, values]) => [
          h('dt', { textContent: term }),
          h('dd', {}, values.length ? h('ul', {}, values.map((value) => h('li', { textContent: value }))) : h('span', { class: 'media-note', textContent: 'None' })),
        ]),
        file.path ? [h('dt', { textContent: 'Path' }), h('dd', { class: 'media-path', textContent: file.path })] : null));
    })));
  }

  function members(item, images) {
    if (!item.children.length) return section('Titles', h('p', { class: 'media-note', textContent: 'None of this collection\'s titles are in the library.' }));
    const grid = createMediaGrid({ images, showKind: true, label: `${item.name} titles` });
    grid.set(item.children);
    return section(`Titles · ${item.children.length}`, grid.el);
  }

  function detailSkeleton() {
    return h('div', { class: 'media-hero media-hero-skeleton', role: 'status' },
      h('span', { class: 'sr-only', textContent: 'Loading' }),
      h('div', { class: 'media-hero-body' },
        h('div', { class: 'media-hero-poster' }),
        h('div', { class: 'media-hero-text' },
          h('span', { class: 'skeleton-bar' }), h('span', { class: 'skeleton-bar' }), h('span', { class: 'skeleton-bar skeleton-bar-short' }))));
  }

  const libraryItemPage = {
    title: 'Library',
    keepParams: true,

    mount(root, { params, signal, setTitle }) {
      const images = createImageLoader(signal);
      const bar = createLibraryBar({ onFinished: () => load() });
      const panel = createPanel('Title');
      root.append(bar.el, panel.el);
      panel.el.classList.add('media-panel');
      panel.content(detailSkeleton());

      const id = Number(params.id);

      async function load() {
        if (!Number.isInteger(id) || id <= 0) {
          panel.empty({ key: 'no-id', title: 'No title chosen', hint: 'Pick a movie or series in the library.', action: { label: 'Open the library', onClick: () => go('library') } });
          return;
        }
        let item;
        let parent = null;
        try {
          item = await getJellyfinItem(id);
          // Seasons and episodes borrow their series' backdrop and poster when they have none.
          if (item.series && (!item.images.backdrops.length || !item.images.primary)) {
            parent = await getJellyfinItem(item.series.id).catch(() => null);
          }
        } catch (error) {
          if (signal.aborted) return;
          if (error.code === 'item_not_found') {
            panel.empty({
              key: 'gone', title: 'This title isn\'t in the library anymore',
              hint: 'It was removed from Jellyfin, and the last sync removed it here too.',
              action: { label: 'Open the library', onClick: () => go('library') },
            });
            return;
          }
          panel.error(error.message, () => {
            panel.content(detailSkeleton());
            load();
          });
          return;
        }
        if (signal.aborted) return;
        setTitle(item.name);

        const body = [hero(item, parent, images)];
        if (item.kind === 'series') body.push(seasons(item, images, signal, params.season));
        if (item.kind === 'season') body.push(section('Episodes', episodeList(item.children, images)));
        if (item.kind === 'collection') body.push(members(item, images));
        body.push(cast(item, images), files(item));
        panel.content(h('div', { class: 'media-detail' }, body));
      }

      load();
      return () => bar.refresh();
    },
  };

  Object.assign(MediaFlow, { libraryItemPage });
})();
