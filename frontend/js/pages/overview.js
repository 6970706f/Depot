(() => {
  'use strict';

  const { EMPTY, STATUS, createDownloadTable, createPanel, describeServices, formatBytes, formatSpeed, getDownloads, getStats, getSystemStatus, h, openAddDialog, plural, setText } = MediaFlow;

  const MAX_ACTIVE_ROWS = 8;
  const TABLE_COLUMNS = ['name', 'progress', 'down', 'eta', 'status', 'actions'];

  function createMetric(label) {
    const value = h('dd', { class: 'metric-value', textContent: EMPTY });
    const note = h('dd', { class: 'metric-note', textContent: ' ' });
    return {
      el: h('div', { class: 'metric' }, h('dt', { textContent: label }), value, note),
      set(nextValue, nextNote) {
        setText(value, nextValue);
        setText(note, nextNote || ' ');
      },
    };
  }

  function sectionHeader(title, href, linkLabel) {
    return h('header', { class: 'section-header' },
      h('h2', { textContent: title }),
      h('a', { class: 'section-link', href, textContent: linkLabel }),
    );
  }

  function pausedAndFailedNote({ paused, failed }) {
    const parts = [];
    if (paused) parts.push(`${paused} paused`);
    if (failed) parts.push(`${failed} failed`);
    return parts.join(', ');
  }

  // One row per integrated service, with the reason when it isn't connected.
  function createConnectionList() {
    const el = h('ul', { class: 'connection-list' });
    let lastKey = null;
    return {
      el,
      // Redrawn only when something changed, so polling never disturbs a text selection.
      render(services) {
        const key = JSON.stringify(services);
        if (key === lastKey) return;
        lastKey = key;
        el.replaceChildren(...services.map(({ name, state, label, detail }) =>
          h('li', { class: 'connection', 'data-state': state },
            h('span', { class: 'dot' }),
            h('div', { class: 'connection-text' },
              h('p', { class: 'connection-title' }, h('strong', { textContent: name }), ` · ${label}`),
              detail ? h('p', { class: 'connection-detail', textContent: detail }) : null,
            ),
          )));
      },
    };
  }

  const overviewPage = {
    title: 'Overview',

    mount(root, { signal }) {
      const metrics = {
        active: createMetric('Active downloads'),
        down: createMetric('Download speed'),
        up: createMetric('Upload speed'),
        completed: createMetric('Completed downloads'),
      };
      const table = createDownloadTable({ label: 'Active downloads', columnKeys: TABLE_COLUMNS });
      const connections = createConnectionList();
      const tablePanel = createPanel('Downloads');
      const connectionsPanel = createPanel('Connections');

      root.append(
        h('dl', { class: 'metrics' }, Object.values(metrics).map((metric) => metric.el)),
        h('div', { class: 'overview-columns' },
          h('section', { 'aria-label': 'Active downloads' },
            sectionHeader('Active downloads', '#/downloads', 'View all'),
            tablePanel.el),
          h('section', { class: 'overview-side', 'aria-label': 'Connections' },
            sectionHeader('Connections', '#/settings', 'Settings'),
            connectionsPanel.el),
        ),
      );

      tablePanel.loading(5);
      connectionsPanel.loading(2);

      const retry = () => {
        tablePanel.loading(5);
        refresh();
      };

      // The summary never shows numbers it can't vouch for: when the downloads can't be read,
      // the metrics go blank and the table shows why, until the next refresh succeeds.
      async function refresh() {
        const [downloads, stats, status] = await Promise.allSettled([getDownloads(), getStats(), getSystemStatus()]);
        if (signal.aborted) return;

        if (status.status === 'fulfilled') {
          connectionsPanel.content(connections.el);
          connections.render(describeServices(status.value));
        } else {
          connectionsPanel.error(status.reason.message, retry);
        }

        if (downloads.status === 'fulfilled' && stats.status === 'fulfilled') {
          renderMetrics(stats.value);
          renderActive(downloads.value);
        } else {
          const failure = downloads.status === 'rejected' ? downloads.reason : stats.reason;
          for (const metric of Object.values(metrics)) metric.set(EMPTY, 'Unavailable');
          tablePanel.error(failure.message, retry);
        }
      }

      function renderMetrics(stats) {
        metrics.active.set(String(stats.active), pausedAndFailedNote(stats));
        metrics.down.set(formatSpeed(stats.downSpeed, { showZero: true }), stats.active ? `Across ${plural(stats.active, 'download')}` : 'Idle');
        metrics.up.set(formatSpeed(stats.upSpeed, { showZero: true }), stats.seeding ? `Across ${plural(stats.seeding, 'torrent')}` : 'Idle');
        metrics.completed.set(String(stats.completed), stats.completed ? `${formatBytes(stats.completedSize)} on disk` : '');
      }

      function renderActive(downloads) {
        const active = downloads
          .filter((item) => item.status !== 'completed')
          .sort((a, b) => STATUS[a.status].rank - STATUS[b.status].rank || b.addedAt.localeCompare(a.addedAt))
          .slice(0, MAX_ACTIVE_ROWS);

        if (!active.length) {
          tablePanel.empty({
            title: 'Nothing is downloading',
            hint: 'Add a magnet link or .torrent file to start.',
            action: { label: 'Add download', icon: 'plus', onClick: openAddDialog },
          });
          return;
        }
        tablePanel.content(table.el);
        table.render(active);
      }

      return refresh;
    },
  };

  Object.assign(MediaFlow, { overviewPage });
})();
