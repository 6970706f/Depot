import { api } from '../api.js';
import { h, clear } from '../dom.js';
import * as format from '../format.js';
import { downloadTable } from '../components/download-table.js';
import { downloadActions } from '../components/download-actions.js';
import { emptyState, errorState, loadingState } from '../components/states.js';

const active = (d) => ['downloading', 'queued', 'checking'].includes(d.status);

function stat(label, value) {
  return h('div', { class: 'stat' }, h('span', { class: 'label', text: label }), h('span', { class: 'value', text: value }));
}

export default {
  title: 'Overview',
  mount(view) {
    const body = h('div', { class: 'page' }, loadingState());
    view.append(body);

    // Jellyfin's totals, when it's set up; the page works without them.
    const library = h('div', { class: 'stats' });
    async function refreshLibrary() {
      try {
        const counts = await api.mediaCounts();
        clear(library).append(
          h('a', { class: 'stat link', href: '#/library/movies' }, h('span', { class: 'label', text: 'Movies' }), h('span', { class: 'value', text: counts.movies })),
          h('a', { class: 'stat link', href: '#/library/series' }, h('span', { class: 'label', text: 'Series' }), h('span', { class: 'value', text: counts.series })),
          stat('Episodes', counts.episodes));
      } catch {
        clear(library);
      }
    }

    async function refresh() {
      try {
        const downloads = await api.downloads();
        const running = downloads.filter(active);
        clear(body).append(
          h('div', { class: 'stats' },
            stat('Active', running.length),
            stat('Completed', downloads.filter((d) => d.status === 'completed').length),
            stat('Download speed', format.speed(downloads.reduce((sum, d) => sum + d.downloadSpeed, 0))),
            stat('Upload speed', format.speed(downloads.reduce((sum, d) => sum + d.uploadSpeed, 0)))),
          h('section', { class: 'panel' },
            h('header', {}, h('h2', { text: 'Active downloads' }), h('a', { class: 'subtle', href: '#/downloads', text: 'All downloads' })),
            running.length ? downloadTable(running, { compact: true, actions: downloadActions }) : emptyState('Nothing downloading right now.')));
        body.append(library);
      } catch (error) {
        clear(body).append(error.code === 'not_configured'
          ? emptyState(error.message, h('a', { class: 'btn primary', href: '#/settings' }, 'Open Settings'))
          : errorState("Downloads didn't load", error, refresh));
      }
    }

    refresh();
    refreshLibrary();
    return { refresh };
  },
};
