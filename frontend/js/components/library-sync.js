(() => {
  'use strict';

  const { formatAgo, getJellyfinSync, getSystemStatus, h, plural, setText, startJellyfinSync, toast } = MediaFlow;

  const RUN_STATES = {
    running: 'Syncing',
    succeeded: 'Synced',
    completedWithErrors: 'Synced with errors',
    failed: 'Sync failed',
    interrupted: 'Sync interrupted',
  };

  // A sync run from GET /api/jellyfin/sync in words: { state, label, detail, failed }. Shared by
  // the library pages and Settings.
  function describeSyncRun(run) {
    if (!run) return { state: 'off', label: 'Never synced', detail: null, failed: false };
    const label = RUN_STATES[run.status] ?? run.status;
    if (run.status === 'running') {
      const where = run.currentLibrary ? `Reading ${run.currentLibrary}` : run.message;
      const libraries = run.librariesTotal
        ? ` · ${run.librariesCompleted + run.librariesFailed} of ${plural(run.librariesTotal, 'library', 'libraries')}`
        : '';
      return { state: 'ok', label, detail: `${where}${libraries} · ${plural(run.itemsSeen, 'item')} so far`, failed: false };
    }
    const counts = [`${run.itemsAdded} added`, `${run.itemsUpdated} updated`, `${run.itemsRemoved} removed`];
    if (run.itemsSkipped) counts.push(`${run.itemsSkipped} unreadable`);
    const failed = run.status !== 'succeeded';
    return {
      state: failed ? 'down' : 'ok',
      label,
      detail: `${formatAgo(run.finishedAt)} · ${counts.join(', ')}`,
      failed,
      message: failed ? run.message : null,
    };
  }

  // The bar above every library page: the sync's state and progress, its last result and
  // errors, the "Sync now" action, and a notice when Jellyfin can't be reached (the pages then
  // show the copy from the last sync). onFinished runs when a sync ends while the page is open.
  function createLibraryBar({ onFinished } = {}) {
    let lastRun = undefined;

    const dot = h('span', { class: 'dot' });
    const label = h('strong');
    const detail = h('span', { class: 'library-bar-detail' });
    const progress = h('span', { class: 'library-bar-progress', hidden: true }, h('span'));
    const errors = h('details', { class: 'library-bar-errors', hidden: true });
    const notice = h('p', { class: 'library-bar-notice', role: 'status', hidden: true });

    const button = h('button', {
      class: 'btn', type: 'button', textContent: 'Sync now',
      async onClick() {
        if (button.disabled) return;
        button.disabled = true;
        try {
          await startJellyfinSync();
          toast('Library sync started');
          // Shown at once: the next status check may still be one that started before the sync.
          status.dataset.state = 'ok';
          setText(label, 'Syncing');
          setText(detail, 'Starting…');
          setText(button, 'Syncing…');
        } catch (error) {
          toast(error.message, { type: 'error' });
          await refresh();
        }
      },
    });

    const status = h('div', { class: 'library-bar-status', role: 'status' }, dot, h('p', {}, label, ' ', detail));
    const el = h('div', { class: 'library-bar' },
      h('div', { class: 'library-bar-main' }, status, button),
      progress, errors, notice);

    function renderNotice(jellyfin) {
      if (jellyfin && !jellyfin.configured) {
        notice.hidden = false;
        notice.title = '';
        notice.replaceChildren('Jellyfin isn\'t set up. ', h('a', { href: '#/settings', textContent: 'Add its URL and API key in Settings' }), '.');
        return;
      }
      if (jellyfin && !jellyfin.connected) {
        notice.hidden = false;
        notice.title = jellyfin.error ?? '';
        notice.replaceChildren('Jellyfin is unavailable, so this is the library as of the last sync.');
        return;
      }
      notice.hidden = true;
    }

    async function refresh() {
      const [system, sync] = await Promise.allSettled([getSystemStatus(), getJellyfinSync()]);
      const jellyfin = system.status === 'fulfilled' ? system.value.jellyfin : null;
      renderNotice(jellyfin);

      if (sync.status === 'rejected') {
        el.dataset.state = 'down';
        status.dataset.state = 'down';
        setText(label, 'Sync status unavailable');
        setText(detail, sync.reason.message);
        button.disabled = true;
        return;
      }

      const { isRunning, latestRun } = sync.value;
      const described = describeSyncRun(latestRun);
      status.dataset.state = isRunning ? 'ok' : described.state;
      el.dataset.state = described.failed ? 'down' : 'ok';
      setText(label, isRunning ? 'Syncing' : described.label);
      setText(detail, described.detail ?? (jellyfin?.configured === false ? 'Set up Jellyfin to sync' : 'Sync now reads the library from Jellyfin'));
      button.disabled = isRunning || jellyfin?.configured === false;
      setText(button, isRunning ? 'Syncing…' : 'Sync now');

      progress.hidden = !isRunning || !latestRun?.librariesTotal;
      if (!progress.hidden) {
        const done = (latestRun.librariesCompleted + latestRun.librariesFailed) / latestRun.librariesTotal;
        progress.firstChild.style.width = `${Math.round(done * 100)}%`;
      }

      const runErrors = !isRunning && latestRun ? latestRun.errors : [];
      const errorKey = JSON.stringify([described.message, runErrors]);
      if (errors.dataset.key !== errorKey) {
        errors.dataset.key = errorKey;
        errors.hidden = !described.message && runErrors.length === 0;
        errors.replaceChildren(
          h('summary', { textContent: described.message ?? plural(runErrors.length, 'error') }),
          h('ul', {}, runErrors.map((error) => h('li', {}, h('strong', { textContent: error.library }), `: ${error.message}`))));
      }

      // A sync that ended since the last look changed the library.
      const runKey = latestRun ? `${latestRun.id}:${latestRun.finishedAt}` : null;
      if (lastRun !== undefined && !isRunning && runKey !== lastRun) onFinished?.();
      lastRun = runKey;
    }

    return { el, refresh };
  }

  Object.assign(MediaFlow, { createLibraryBar, describeSyncRun });
})();
