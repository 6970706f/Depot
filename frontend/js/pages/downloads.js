import { api } from '../api.js';
import { h, clear } from '../dom.js';
import { downloadTable } from '../components/download-table.js';
import { downloadActions } from '../components/download-actions.js';
import { emptyState, errorState, loadingState } from '../components/states.js';

const filters = [
  { id: 'all', label: 'All', test: () => true },
  { id: 'active', label: 'Active', test: (d) => ['downloading', 'queued', 'checking'].includes(d.status) },
  { id: 'paused', label: 'Paused', test: (d) => d.status === 'paused' },
  { id: 'completed', label: 'Completed', test: (d) => d.status === 'completed' },
  { id: 'error', label: 'Error', test: (d) => d.status === 'error' },
];

export default {
  title: 'Downloads',
  mount(view, { actions }) {
    let filter = 'all';
    let search = '';
    let downloads = null;

    const searchInput = h('input', {
      class: 'input search', type: 'search', placeholder: 'Search downloads', 'aria-label': 'Search downloads',
      oninput: () => { search = searchInput.value.trim().toLowerCase(); render(); },
    });
    const tabs = h('div', { class: 'tabs', role: 'group', 'aria-label': 'Filter by status' });
    const list = h('div', {}, loadingState());
    view.append(h('div', { class: 'page' }, h('div', { class: 'toolbar' }, searchInput, tabs), list));

    function renderTabs() {
      clear(tabs).append(...filters.map((item) => {
        const count = downloads ? downloads.filter(item.test).length : null;
        return h('button', {
          type: 'button', 'aria-pressed': String(item.id === filter),
          onclick: () => { filter = item.id; render(); },
        }, item.label, count != null ? h('span', { class: 'subtle', text: ` ${count}` }) : null);
      }));
    }

    function render() {
      renderTabs();
      if (!downloads) return;
      const test = filters.find((item) => item.id === filter).test;
      const visible = downloads.filter((d) => test(d) && (!search || d.name.toLowerCase().includes(search)));
      if (downloads.length === 0) {
        clear(list).append(emptyState('No downloads yet.'));
      } else if (visible.length === 0) {
        clear(list).append(emptyState('No downloads match.'));
      } else {
        clear(list).append(downloadTable(visible, { actions: downloadActions }));
      }
    }

    async function refresh() {
      try {
        downloads = await api.downloads();
        render();
      } catch (error) {
        downloads = null;
        renderTabs();
        clear(list).append(errorState("Downloads didn't load", error, refresh));
      }
    }

    refresh();
    return { refresh };
  },
};
