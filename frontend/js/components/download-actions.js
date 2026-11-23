import { api } from '../api.js';
import { h, icon } from '../dom.js';
import { openDialog } from './dialog.js';
import { toast } from './toast.js';

function iconButton(name, label, onclick, extra = '') {
  return h('button', { class: `icon-btn ${extra}`, type: 'button', title: label, 'aria-label': label, onclick }, icon(name));
}

async function run(button, action, done) {
  button.disabled = true;
  try {
    const result = await action();
    if (done) toast(done(result));
  } catch (error) {
    toast(error.message, { error: true });
  } finally {
    button.disabled = false;
  }
}

// Pause, resume (retry for a torrent in error) and remove, for one row.
export function downloadActions(download) {
  const buttons = [];
  if (['downloading', 'queued', 'checking'].includes(download.status)) {
    const pause = iconButton('pause', 'Pause', () => run(pause, () => api.pauseDownload(download.hash)));
    buttons.push(pause);
  } else if (download.status === 'paused') {
    const resume = iconButton('play', 'Resume', () => run(resume, () => api.resumeDownload(download.hash)));
    buttons.push(resume);
  } else if (download.status === 'error') {
    const retry = iconButton('retry', 'Retry', () => run(retry, () => api.resumeDownload(download.hash)));
    buttons.push(retry);
  }
  buttons.push(iconButton('trash', 'Remove', () => openRemoveDialog(download), 'danger'));
  return buttons;
}

// Removing always says what happens to the files: nothing is deleted by default.
export function openRemoveDialog(download) {
  const keep = h('input', { type: 'radio', name: 'files', value: 'keep', checked: true });
  const remove = h('input', { type: 'radio', name: 'files', value: 'delete' });
  const submit = h('button', { class: 'btn danger', type: 'submit' }, 'Remove');
  const library = download.library
    ? 'Videos already imported into the library stay there.'
    : null;
  const dialog = openDialog({
    title: `Remove ${download.name}?`,
    body: [
      h('div', { class: 'field' },
        h('label', { class: 'row' }, keep, 'Keep the downloaded files on the server'),
        h('label', { class: 'row danger-text' }, remove, 'Delete the downloaded files')),
      library ? h('p', { class: 'muted', text: library }) : null,
    ],
    footer: [submit],
    async onSubmit() {
      submit.disabled = true;
      try {
        const removed = await api.removeDownload(download.hash, remove.checked);
        dialog.close();
        toast(removed.filesDeleted ? `Removed ${removed.name} and its files.` : `Removed ${removed.name}. Its files were kept.`);
      } catch (error) {
        toast(error.message, { error: true });
        submit.disabled = false;
      }
    },
  });
}
