import { api } from '../api.js';
import { h, clear, icon } from '../dom.js';
import * as format from '../format.js';
import { emptyState, errorState, loadingState } from '../components/states.js';
import { toast } from '../components/toast.js';

const filters = [
  { id: null, label: 'All' },
  { id: 'waiting', label: 'Waiting' },
  { id: 'failed', label: 'Failed' },
  { id: 'completed', label: 'Imported' },
  { id: 'skipped', label: 'Skipped' },
];

const statusLabels = {
  waiting: 'Waiting', importing: 'Importing', completed: 'Imported', failed: 'Failed', skipped: 'Skipped',
};

const workerLabels = {
  idle: 'Idle', running: 'Working', paused: 'Paused', error: 'Error',
};

function placement(job) {
  return job.mediaType === 'movie' ? `Movie · ${job.title}` : `${job.title} · ${job.season === 0 ? 'Specials' : `Season ${job.season}`}`;
}

async function act(button, action, message) {
  button.disabled = true;
  try {
    await action();
    toast(message);
  } catch (error) {
    toast(error.message, { error: true });
    button.disabled = false;
  }
}

function actions(job) {
  const buttons = [];
  if (job.status === 'failed' || job.status === 'skipped') {
    const retry = h('button', { class: 'icon-btn', type: 'button', title: 'Retry', 'aria-label': 'Retry',
      onclick: () => act(retry, () => api.retryImport(job.id), `Retrying ${job.fileName}.`) }, icon('retry'));
    buttons.push(retry);
  }
  if (job.status === 'waiting' || job.status === 'failed') {
    const skip = h('button', { class: 'icon-btn', type: 'button', title: 'Skip (no file is changed)', 'aria-label': 'Skip',
      onclick: () => act(skip, () => api.skipImport(job.id), `Skipped ${job.fileName}.`) }, icon('skip'));
    buttons.push(skip);
  }
  return buttons;
}

// One job per video of a finished download: where it goes, its state and why.
export default {
  title: 'Imports',
  mount(view) {
    let filter = null;
    const worker = h('div', { class: 'panel worker' });
    const tabs = h('div', { class: 'tabs', role: 'group', 'aria-label': 'Filter by state' });
    const list = h('div', {}, loadingState());
    view.append(h('div', { class: 'page' }, worker, tabs, list));

    function renderWorker(status) {
      const { state, message, lastRunAt } = status.worker;
      clear(worker).append(
        h('div', { class: 'row wrap' },
          h('span', { class: state === 'error' || state === 'paused' ? 'tag danger' : 'tag', text: workerLabels[state] ?? state }),
          h('span', { class: state === 'error' || state === 'paused' ? 'danger-text' : 'muted', text: message ?? 'Depot checks qBittorrent for finished downloads on the interval set in Settings.' }),
          lastRunAt ? h('span', { class: 'subtle', text: `Last check ${format.dateTime(lastRunAt)}` }) : null));
      clear(tabs).append(...filters.map((item) => h('button', {
        type: 'button', 'aria-pressed': String(item.id === filter),
        onclick: () => { filter = item.id; refresh(); },
      }, item.label, h('span', { class: 'subtle', text: ` ${item.id ? status.counts[item.id] ?? 0 : Object.values(status.counts).reduce((a, b) => a + b, 0)}` }))));
    }

    function renderJobs(jobs) {
      if (!jobs.length) {
        clear(list).append(emptyState(filter ? 'No imports in this state.' : 'Nothing imported yet. Finished downloads added as Movie or Series show up here.'));
        return;
      }
      clear(list).append(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
        h('thead', {}, h('tr', {}, h('th', { text: 'Video' }), h('th', { text: 'Destination' }), h('th', { text: 'State' }), h('th', { class: 'actions' }))),
        h('tbody', {}, jobs.map((job) => h('tr', { dataset: { id: job.id } },
          h('td', {}, h('div', { class: 'cell-title' },
            h('span', { class: 'name', text: job.fileName }),
            h('span', { class: 'meta', text: `${job.torrentName} · ${format.bytes(job.sizeBytes)}${job.subtitleCount ? ` · ${job.subtitleCount} subtitle(s)` : ''}` }))),
          h('td', {}, h('div', { class: 'cell-title' },
            h('span', { text: placement(job) }),
            h('span', { class: 'meta mono', text: job.destination }))),
          h('td', {},
            h('span', { class: job.status === 'failed' ? 'tag danger' : 'tag', text: statusLabels[job.status] ?? job.status }),
            job.usedMode ? h('span', { class: 'subtle', text: ` ${job.usedMode}` }) : null,
            job.message ? h('p', { class: job.status === 'failed' ? 'danger-text job-message' : 'muted job-message', text: job.message }) : null),
          h('td', { class: 'actions' }, actions(job))))))));
    }

    async function refresh() {
      try {
        const [status, jobs] = await Promise.all([api.importStatus(), api.imports(filter)]);
        renderWorker(status);
        renderJobs(jobs);
      } catch (error) {
        clear(list).append(errorState("Imports didn't load", error, refresh));
      }
    }

    refresh();
    return { refresh };
  },
};
