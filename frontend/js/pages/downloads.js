(() => {
  'use strict';

  const { createDownloadTable, createPanel, getDownloads, h, openAddDialog, searchField, segmentedButtons, selectControl, setText, sortDownloads } = MediaFlow;

  const COLUMNS = ['name', 'progress', 'down', 'up', 'eta', 'size', 'status', 'actions'];

  const FILTERS = [
    { value: 'all', label: 'All' },
    { value: 'downloading', label: 'Downloading' },
    { value: 'paused', label: 'Paused' },
    { value: 'completed', label: 'Completed' },
    { value: 'error', label: 'Error' },
  ];

  // Queued and checking torrents are still on their way, so they count as downloading.
  const FILTER_STATUSES = { downloading: ['downloading', 'queued', 'checking'] };

  function inFilter(item, filter) {
    return (FILTER_STATUSES[filter] ?? [filter]).includes(item.status);
  }

  // First click on a text column sorts A to Z, on a numeric column largest first.
  const ASCENDING_FIRST = new Set(['name', 'eta', 'status']);

  // Headers are visually hidden on phones, so this select takes over there.
  const MOBILE_SORTS = [
    { value: 'added:desc', label: 'Newest first' },
    { value: 'name:asc', label: 'Name' },
    { value: 'progress:desc', label: 'Progress' },
    { value: 'down:desc', label: 'Speed' },
    { value: 'size:desc', label: 'Size' },
    { value: 'status:asc', label: 'Status' },
  ];

  // Kept outside mount so filters survive moving between pages.
  const state = { query: '', filter: 'all', sort: { key: 'added', direction: 'desc' } };

  function matches(item, query) {
    return item.name.toLowerCase().includes(query);
  }

  const downloadsPage = {
    title: 'Downloads',

    mount(root, { signal, params }) {
      if (params.q !== undefined) {
        state.query = params.q;
        state.filter = 'all';
      }

      let items = null;
      const panel = createPanel('Downloads');
      const counts = Object.fromEntries(FILTERS.map(({ value }) => [value, h('span', { class: 'segment-count' })]));

      const filters = segmentedButtons({
        label: 'Filter by status',
        value: state.filter,
        options: FILTERS.map((filter) => ({ ...filter, count: counts[filter.value] })),
        onChange(value) {
          state.filter = value;
          filters.setValue(value);
          render();
        },
      });

      const search = searchField({
        label: 'Search downloads',
        placeholder: 'Search by name',
        value: state.query,
        onInput(event) {
          state.query = event.target.value;
          render();
        },
      });

      const mobileSort = selectControl({
        label: 'Sort downloads',
        className: 'sort-mobile',
        options: MOBILE_SORTS,
        value: `${state.sort.key}:${state.sort.direction}`,
        onChange(event) {
          const [key, direction] = event.target.value.split(':');
          applySort({ key, direction });
        },
      });

      const table = createDownloadTable({
        label: 'Downloads',
        columnKeys: COLUMNS,
        sort: state.sort,
        onSort(key) {
          const direction = state.sort.key === key
            ? (state.sort.direction === 'asc' ? 'desc' : 'asc')
            : (ASCENDING_FIRST.has(key) ? 'asc' : 'desc');
          applySort({ key, direction });
        },
      });

      function applySort(sort) {
        state.sort = sort;
        table.updateSort(sort);
        render();
      }

      function clearFilters() {
        state.query = '';
        state.filter = 'all';
        search.querySelector('input').value = '';
        filters.setValue('all');
        render();
      }

      function render() {
        if (!items) return;
        const query = state.query.trim().toLowerCase();
        const searched = items.filter((item) => matches(item, query));

        for (const { value } of FILTERS) {
          const count = value === 'all' ? searched.length : searched.filter((item) => inFilter(item, value)).length;
          setText(counts[value], String(count));
        }

        const visible = state.filter === 'all' ? searched : searched.filter((item) => inFilter(item, state.filter));

        if (!items.length) {
          panel.empty({
            key: 'none',
            title: 'No downloads yet',
            hint: 'Add a magnet link or .torrent file to start.',
            action: { label: 'Add download', icon: 'plus', onClick: openAddDialog },
          });
        } else if (!visible.length) {
          panel.empty({
            key: 'filtered',
            title: 'No downloads match these filters',
            hint: 'Change the search or status filter to see more.',
            action: { label: 'Clear filters', onClick: clearFilters },
          });
        } else {
          panel.content(table.el);
          table.render(sortDownloads(visible, state.sort));
        }
      }

      async function refresh() {
        let next;
        try {
          next = await getDownloads();
        } catch (error) {
          // Polling failures keep the last data on screen; the top bar reports the outage.
          if (signal.aborted || items) return;
          panel.error(error.message, () => {
            panel.loading();
            refresh();
          });
          return;
        }
        if (signal.aborted) return;
        items = next;
        render();
      }

      root.append(
        h('div', { class: 'toolbar' }, search, filters.el, h('div', { class: 'toolbar-end' }, mobileSort)),
        panel.el,
      );
      panel.loading();

      return refresh;
    },
  };

  Object.assign(MediaFlow, { downloadsPage });
})();
