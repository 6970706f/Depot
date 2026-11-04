(() => {
  'use strict';

  const {
    classifyOrganizationJob, confirmOrganizationDownload, createPanel, getOrganizationJobs, getOrganizationStatus, h, icon, keyedList,
    retryOrganizationJob, segmentedButtons, selectControl, setText, skipOrganizationJob, toast,
  } = MediaFlow;

  const FILTERS = [
    { value: 'all', label: 'All' },
    { value: 'review', label: 'Review' },
    { value: 'attention', label: 'Failed' },
    { value: 'waiting', label: 'Waiting' },
    { value: 'completed', label: 'Imported' },
    { value: 'skipped', label: 'Skipped' },
  ];

  const FILTER_STATUSES = {
    review: ['needsReview'],
    attention: ['failed'],
    waiting: ['discovered', 'processing'],
    completed: ['completed'],
    skipped: ['skipped'],
  };

  const STATUS = {
    discovered: { label: 'Waiting', icon: 'list' },
    needsReview: { label: 'Needs review', icon: 'alert' },
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

  // The folders Jellyfin reads series extras from (ExtrasFolders.Names on the server).
  const EXTRAS_FOLDERS = ['extras', 'featurettes', 'behind the scenes', 'deleted scenes', 'trailers', 'interviews'];

  // Where a series video goes inside the series folder; null for a movie.
  function describePlacement(job) {
    if (job.target.mediaType !== 'series') return null;
    const { kind, season, extrasFolder } = job.placement;
    if (kind === 'extra') return `Extra · ${extrasFolder}`;
    return season === 0 ? 'Special · Season 0' : `Episode · Season ${season}`;
  }

  // Episode, special or extra, with its season or extras folder, as a small form. Submitting
  // imports the video there on the next pass.
  function createPlacementEditor(job, { onSave, onCancel }) {
    const { kind, season, extrasFolder } = job.placement;
    const initialRole = kind === 'extra' ? 'extra' : season === 0 ? 'special' : 'episode';
    const role = selectControl({
      label: 'Import as',
      value: initialRole,
      options: [
        { value: 'episode', label: 'Episode' },
        { value: 'special', label: 'Special (season 0)' },
        { value: 'extra', label: 'Extra' },
      ],
      onChange: update,
    });
    const seasonInput = h('input', {
      class: 'input placement-season', type: 'number', min: '1', max: '999', inputmode: 'numeric',
      value: String(season && season > 0 ? season : job.target.season || 1), 'aria-label': 'Season',
    });
    const folder = selectControl({
      label: 'Extras folder',
      value: extrasFolder ?? 'extras',
      options: EXTRAS_FOLDERS.map((name) => ({ value: name, label: name })),
    });
    const save = h('button', { class: 'btn btn-primary', type: 'submit', textContent: 'Import as this' });

    function update() {
      const chosen = role.querySelector('select').value;
      seasonInput.hidden = chosen !== 'episode';
      folder.hidden = chosen !== 'extra';
    }

    const el = h('form', {
      class: 'placement-editor',
      onSubmit: (event) => {
        event.preventDefault();
        const chosen = role.querySelector('select').value;
        const placement = chosen === 'extra'
          ? { kind: 'extra', extrasFolder: folder.querySelector('select').value }
          : { kind: 'main', season: chosen === 'special' ? 0 : Number(seasonInput.value) };
        onSave(placement, save);
      },
    },
      role, seasonInput, folder, save,
      h('button', { class: 'btn', type: 'button', textContent: 'Cancel', onClick: onCancel }),
    );
    update();
    return el;
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
    let current = null;
    let editing = false;
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
      if (job.status === 'needsReview') {
        const confirm = h('button', {
          class: 'btn btn-primary', type: 'button', textContent: 'Confirm',
          onClick: () => run('classify', confirm, async () => {
            await classifyOrganizationJob(job.id, job.placement);
            toast(`Queued as ${describePlacement(job)}`);
          }),
        });
        buttons.push(confirm);
      }
      if (job.status === 'needsReview' && job.reviewInDownload > 1) {
        const confirmAll = h('button', {
          class: 'btn', type: 'button', textContent: `Confirm all ${job.reviewInDownload}`,
          title: 'Accept the suggestion for every video of this download that waits for review',
          onClick: () => run('confirm-all', confirmAll, async () => {
            const confirmed = await confirmOrganizationDownload(job.torrentHash);
            toast(`Queued ${confirmed.length} video(s) as suggested`);
          }),
        });
        buttons.push(confirmAll);
      }
      if (job.canClassify && !editing) {
        buttons.push(h('button', {
          class: 'btn', type: 'button', textContent: 'Change',
          onClick: () => {
            editing = true;
            render(current);
            el.querySelector('.placement-editor select')?.focus();
          },
        }));
      }
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
      current = job;
      if (!job.canClassify) editing = false;
      const status = STATUS[job.status];
      const destination = `${job.destinationPath}${job.destinationIsFinal ? '' : ' (planned)'}`;
      const placement = describePlacement(job);
      const editor = editing
        ? createPlacementEditor(job, {
          onSave: (chosen, button) => run('classify', button, async () => {
            await classifyOrganizationJob(job.id, chosen);
            editing = false;
            toast('Queued for import there');
          }),
          onCancel: () => {
            editing = false;
            render(current);
          },
        })
        : null;

      el.dataset.status = job.status;
      el.replaceChildren(
        h('div', { class: 'job-main' },
          h('p', { class: 'job-title' },
            h('strong', { textContent: fileName(job.relativePath) }),
            h('span', { class: 'job-kind', textContent: describeTarget(job.target) })),
          h('p', { class: 'job-line' }, h('span', { class: 'job-label', textContent: 'From' }), `${job.torrentName} › ${job.relativePath}`),
          placement ? h('p', { class: 'job-line' }, h('span', { class: 'job-label', textContent: 'As' }),
            job.status === 'needsReview' ? `${placement} (suggested)` : placement) : null,
          h('p', { class: 'job-line' }, h('span', { class: 'job-label', textContent: 'To' }), destination),
          job.message ? h('p', { class: 'job-message', 'data-kind': job.status === 'failed' ? 'error' : 'info', textContent: job.message }) : null,
          [describeImport(job)].filter(Boolean).map((note) => h('p', { class: 'job-note', textContent: note })),
          editor,
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
        // An open editor is only redrawn when the job changed, so polling keeps what's typed.
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
          // Each row knows how many videos of its download wait for review, to offer
          // confirming them together.
          const reviewCounts = new Map();
          for (const job of jobs) {
            if (job.status === 'needsReview') reviewCounts.set(job.torrentHash, (reviewCounts.get(job.torrentHash) ?? 0) + 1);
          }
          panel.content(list);
          renderRows(visible.map((job) => ({ ...job, reviewInDownload: reviewCounts.get(job.torrentHash) ?? 0 })));
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
