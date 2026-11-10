(() => {
  'use strict';

  const { formatRuntime, h, plural } = MediaFlow;

  const KIND_LABELS = { movie: 'Movie', series: 'Series', season: 'Season', episode: 'Episode', collection: 'Collection' };

  // The poster width fetched for a card; the grid's widest card on a large screen is about 200px,
  // and 320 keeps it sharp on high-density screens.
  const POSTER_WIDTH = 320;

  function itemHref(item) {
    return `#/library/item?id=${encodeURIComponent(item.id)}`;
  }

  function kindLabel(kind) {
    return KIND_LABELS[kind] ?? kind;
  }

  // "2021", "2021 · Series", or the number of titles of a collection.
  function cardMeta(item, showKind) {
    const parts = [];
    if (item.kind === 'collection') parts.push(plural(item.memberCount, 'title'));
    else if (item.year) parts.push(String(item.year));
    if (showKind && item.kind !== 'collection') parts.push(kindLabel(item.kind));
    return parts.join(' · ') || ' ';
  }

  // What the hover adds: rating, length or episodes, genres.
  function hoverFacts(item) {
    const facts = [];
    if (item.communityRating) facts.push(`★ ${item.communityRating.toFixed(1)}`);
    if (item.kind === 'series' && item.episodeCount) facts.push(plural(item.episodeCount, 'episode'));
    else if (item.runtimeSeconds) facts.push(formatRuntime(item.runtimeSeconds));
    if (item.officialRating) facts.push(item.officialRating);
    return facts;
  }

  // Shown while there's no poster, or when there is none: the title on a plain tile.
  function posterFallback(item) {
    return h('span', { class: 'media-fallback', 'aria-hidden': 'true' },
      h('span', { class: 'media-fallback-kind', textContent: kindLabel(item.kind) }),
      h('span', { class: 'media-fallback-title', textContent: item.name }));
  }

  // A collection without its own poster shows its members' posters.
  function mosaic(item, images) {
    const tiles = item.preview.map((member) => {
      const img = h('img', { alt: '', decoding: 'async' });
      images.item(img, member, 'primary', { width: 160 });
      return img;
    });
    return h('span', { class: 'media-mosaic', 'data-count': String(Math.min(tiles.length, 4)), 'aria-hidden': 'true' }, tiles);
  }

  // One title: its poster, name and year. The whole card is a link to its page.
  function mediaCard(item, { images, showKind = false } = {}) {
    const poster = h('span', { class: 'media-poster' });
    const fallback = posterFallback(item);
    poster.append(fallback);

    if (item.images.primary) {
      const img = h('img', { class: 'media-poster-img', alt: '', decoding: 'async' });
      poster.append(img);
      images.item(img, item, 'primary', { width: POSTER_WIDTH });
    } else if (item.kind === 'collection' && item.preview?.length) {
      poster.append(mosaic(item, images));
    }

    const facts = hoverFacts(item);
    const genres = item.genres.slice(0, 3).join(', ');
    if (facts.length || genres || item.isAnime) {
      poster.append(h('span', { class: 'media-hover', 'aria-hidden': 'true' },
        item.isAnime ? h('span', { class: 'media-tag', textContent: 'Anime' }) : null,
        facts.length ? h('span', { class: 'media-hover-facts', textContent: facts.join(' · ') }) : null,
        genres ? h('span', { class: 'media-hover-genres', textContent: genres }) : null));
    }

    const meta = cardMeta(item, showKind);
    return h('a', { class: 'media-card', href: itemHref(item), 'aria-label': [item.name, meta.trim()].filter(Boolean).join(', ') },
      poster,
      h('span', { class: 'media-card-title', textContent: item.name }),
      h('span', { class: 'media-card-meta', textContent: meta }));
  }

  // A responsive grid of cards: as many columns as fit.
  function createMediaGrid({ images, showKind = false, label }) {
    const el = h('ul', { class: 'media-grid', 'aria-label': label });
    const wrap = (item) => h('li', {}, mediaCard(item, { images, showKind }));
    return {
      el,
      set(items) {
        el.replaceChildren(...items.map(wrap));
      },
      append(items) {
        el.append(...items.map(wrap));
      },
    };
  }

  // The same shapes as the cards, while they load.
  function mediaGridSkeleton(count = 18, { row = false } = {}) {
    return h('div', { class: row ? 'media-row' : 'media-grid', role: 'status' },
      h('span', { class: 'sr-only', textContent: 'Loading the library' }),
      Array.from({ length: count }, () => h('div', { class: 'media-card media-card-skeleton', 'aria-hidden': 'true' },
        h('span', { class: 'media-poster' }),
        h('span', { class: 'skeleton-bar' }),
        h('span', { class: 'skeleton-bar skeleton-bar-short' }))));
  }

  // A single row of cards that scrolls sideways, for the overview's sections.
  function mediaRow(items, { images, showKind = false, label }) {
    return h('ul', { class: 'media-row', 'aria-label': label },
      items.map((item) => h('li', {}, mediaCard(item, { images, showKind }))));
  }

  Object.assign(MediaFlow, { createMediaGrid, itemHref, kindLabel, mediaCard, mediaGridSkeleton, mediaRow });
})();
