(() => {
  'use strict';

  const { createPanel, getOrganizationJobs, getOrganizationStatus, h, icon, keyedList, retryOrganizationJob, segmentedButtons, setText, skipOrganizationJob, toast } = MediaFlow;

  const FILTERS = [
    { value: 'all', label: 'All' },
    { value: 'attention', label: 'Failed' },
    { value: 'waiting', label: 'Waiting' },
    { value: 'completed', label: 'Imported' },
    { value: 'skipped', label: 'Skipped' },
  ];

  const FILTER_STATUSES = {
    attention: ['failed'],
    waiting: ['discovered', 'processing'],
    completed: ['completed'],
    skipped: ['skipped'],
  };

  const STATUS = {
    discovered: { label: 'Waiting', icon: 'list' },
    processing: { label: 'Importing', icon: 'arrow-down' },
    completed: { label: 'Imported', icon: 'check' },
    failed: { label: 'Failed', icon: 'alert' },
    skipped: { label: 'Skipped', icon: 'close' },
  };

  // Kept outside mount so the filter survives moving between pages.
  const state = { filter: 'all' };

  function inFilter(job, filter) {
    return filter === 'all' || FILTER_STATUSES[filter].includes(job.status);
  }

  // The library choice made when the download was added.
  function describeTarget(target) {
    if (target.mediaType === 'movie') return `Movie · ${target.title}`;
    return `Series · ${target.title} · ${target.season === 0 ? 'Specials' : `Season ${target.season}`}`;
  }

  function fileName(path) {
    return path.slice(path.lastIndexOf('/') + 1);
  }

  function describeImport(job) {
    if (job.status !== 'completed') return null;
    if (job.importedWith === 'hardlink') return 'Imported as a hardlink: no extra space used.';
    if (job.importedWith === 'copy') return 'Imported as a copy.';
    return null;
  }

  function createJobRow() {
    const el = h('li', { class: 'job' });
    let signature = null;
    const busy = new Set();

    async function run(kind, button, action) {
      if (busy.has(kind)) return;
      busy.add(kind);
      button.setAttribute('aria-busy', 'true');
      try {
        await action();
      } catch (error) {
        toast(error.message, { type: 'error' });
      } finally {
        busy.delete(kind);
        button.removeAttribute('aria-busy');
      }
    }

    function actions(job) {
      const buttons = [];
      if (job.canRetry) {
        const retry = h('button', {
          class: 'btn', type: 'button', textContent: 'Retry',
          onClick: () => run('retry', retry, async () => {
            await retryOrganizationJob(job.id);
            toast('Queued again');
          }),
        });
        buttons.push(retry);
      }
      if (job.canSkip) {
        const skip = h('button', {
          class: 'btn', type: 'button', textContent: 'Skip',
          onClick: () => run('skip', skip, async () => {
            await skipOrganizationJob(job.id);
            toast('Skipped. No file was changed or deleted.');
          }),
        });
        buttons.push(skip);
      }
      return buttons.length ? h('div', { class: 'job-actions' }, buttons) : null;
    }

    function render(job) {
      const status = STATUS[job.status];
      const destination = `${job.destinationPath}${job.destinationIsFinal ? '' : ' (planned)'}`;

      el.dataset.status = job.status;
      el.replaceChildren(
        h('div', { class: 'job-main' },
          h('p', { class: 'job-title' },
            h('strong', { textContent: fileName(job.relativePath) }),
            h('span', { class: 'job-kind', textContent: describeTarget(job.target) })),
          h('p', { class: 'job-line' }, h('span', { class: 'job-label', textContent: 'From' }), `${job.torrentName} › ${job.relativePath}`),
          h('p', { class: 'job-line' }, h('span', { class: 'job-label', textContent: 'To' }), destination),
          job.message ? h('p', { class: 'job-message', 'data-kind': job.status === 'failed' ? 'error' : 'info', textContent: job.message }) : null,
          [describeImport(job)].filter(Boolean).map((note) => h('p', { class: 'job-note', textContent: note })),
        ),
        h('div', { class: 'job-side' },
          h('span', { class: 'status', 'data-status': job.status }, icon(status.icon), status.label),
          actions(job),
        ),
      );
    }

    return {
      el,
      // Redrawn only when the job changed, so refreshes keep focus on its buttons.
      update(job) {
        const next = JSON.stringify(job);
        if (next === signature) return;
        const hadFocus = el.contains(document.activeElement);
        signature = next;
        render(job);
        if (hadFocus) el.querySelector('button')?.focus();
      },
    };
  }

  function describeWorker(status) {
    const lastRun = status.lastRunAt
      ? ` Last check at ${new Date(status.lastRunAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}.`
      : '';
    switch (status.state) {
      case 'paused':
      case 'error':
        return { kind: 'error', text: status.message ?? 'Organization stopped.' };
      case 'running':
        return { kind: 'info', text: status.message ?? 'Working...' };
      case 'starting':
        return { kind: 'info', text: 'Starting...' };
      default:
        return { kind: 'info', text: `Finished movies and series go to the folder you chose when adding them.${lastRun}` };
    }
  }

  const libraryPage = {
    title: 'Library',

    mount(root, { signal }) {
      let jobs = null;
      let status = null;
      const panel = createPanel('Library');
      const counts = Object.fromEntries(FILTERS.map(({ value }) => [value, h('span', { class: 'segment-count' })]));
      const workerText = h('p', { class: 'library-status', role: 'status' });

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

      const list = h('ul', { class: 'job-list', 'aria-label': 'Organization jobs' });
      const renderRows = keyedList(list, (job) => job.id, createJobRow);

      function render() {
        if (status) {
          const worker = describeWorker(status);
          setText(workerText, worker.text);
          workerText.dataset.kind = worker.kind;
        }
        if (!jobs) return;

        for (const { value } of FILTERS) {
          setText(counts[value], String(jobs.filter((job) => inFilter(job, value)).length));
        }

        const visible = jobs.filter((job) => inFilter(job, state.filter));
        if (!jobs.length) {
          panel.empty({
            key: 'none',
            title: 'Nothing to organize yet',
            hint: 'When you add a movie or series, its videos show up here once the download finishes.',
          });
        } else if (!visible.length) {
          panel.empty({ key: 'filtered', title: 'No jobs match this filter', hint: 'Choose another filter to see more.' });
        } else {
          panel.content(list);
          renderRows(visible);
        }
      }

      async function refresh() {
        const [nextJobs, nextStatus] = await Promise.allSettled([getOrganizationJobs(), getOrganizationStatus()]);
        if (signal.aborted) return;
        if (nextStatus.status === 'fulfilled') status = nextStatus.value;
        if (nextJobs.status === 'fulfilled') {
          jobs = nextJobs.value;
        } else if (!jobs) {
          // Polling failures keep the last list on screen; the top bar reports the outage.
          panel.error(nextJobs.reason.message, () => {
            panel.loading();
            refresh();
          });
          return;
        }
        render();
      }

      root.append(
        h('div', { class: 'toolbar' }, filters.el),
        h('div', { class: 'library-status-bar' }, workerText),
        panel.el,
      );
      panel.loading();
      return refresh;
    },
  };

  Object.assign(MediaFlow, { libraryPage });
})();
