import { h } from '../dom.js';
import * as format from '../format.js';

// Rows keyed by hash, rebuilt on each refresh. actions(download) returns the cells' buttons.
export function downloadTable(downloads, { actions = () => [], compact = false } = {}) {
  return h('div', { class: 'table-wrap' },
    h('table', { class: 'table' },
      h('thead', {}, h('tr', {},
        h('th', { text: 'Name' }),
        h('th', { text: 'Status' }),
        compact ? null : h('th', { class: 'num', text: 'Size' }),
        h('th', { class: 'num', text: 'Down' }),
        compact ? null : h('th', { class: 'num', text: 'Up' }),
        h('th', { class: 'num', text: 'ETA' }),
        h('th', { class: 'actions' }))),
      h('tbody', {}, downloads.map((download) => row(download, actions(download), compact)))));
}

function row(download, actions, compact) {
  const status = format.statusLabels[download.status] ?? download.status;
  return h('tr', { dataset: { hash: download.hash } },
    h('td', {},
      h('div', { class: 'cell-title' },
        h('span', { class: 'name', text: download.name }),
        h('span', { class: 'meta' }, metaFor(download)),
        h('div', { class: download.status === 'error' ? 'progress error' : 'progress' },
          h('span', { style: `width: ${Math.min(100, download.progress * 100)}%` })))),
    h('td', {}, h('span', { class: download.status === 'error' ? 'tag danger' : 'tag', text: status }),
      download.status === 'completed' ? null : h('span', { class: 'subtle', text: ` ${format.percent(download.progress)}` })),
    compact ? null : h('td', { class: 'num', text: format.bytes(download.sizeBytes) }),
    h('td', { class: 'num', text: format.speed(download.downloadSpeed) }),
    compact ? null : h('td', { class: 'num', text: format.speed(download.uploadSpeed) }),
    h('td', { class: 'num', text: format.eta(download.etaSeconds) }),
    h('td', { class: 'actions' }, actions));
}

function metaFor(download) {
  const parts = [];
  if (download.library) {
    parts.push(download.library.mediaType === 'movie'
      ? `Movie · ${download.library.title}`
      : `${download.library.title} · S${download.library.season}`);
  } else if (download.category) {
    parts.push(download.category);
  }
  parts.push(format.bytes(download.sizeBytes));
  return parts.join(' · ');
}
