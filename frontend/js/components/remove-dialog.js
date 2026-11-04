(() => {
  'use strict';

  const { cancelButton, formatBytes, h, openDialog, removeDownload, setText, toast } = MediaFlow;

  function choice({ value, title, hint, warning, checked, onChange }) {
    return h('label', { class: 'choice' },
      h('input', { type: 'radio', name: 'remove-mode', value, checked, onChange }),
      h('span', { class: 'choice-title', textContent: title }),
      h('span', { class: 'choice-hint', textContent: hint }),
      warning ? h('span', { class: 'choice-warning', textContent: warning }) : null,
    );
  }

  // Removing a torrent and deleting its files are different outcomes, so the user picks
  // one explicitly. The default keeps files, and the confirm button names the result.
  // Deleting files also needs a separate acknowledgement before the button works.
  function openRemoveDialog(item) {
    let deleteFiles = false;
    let submitting = false;
    // A torrent still fetching its metadata has no known size yet.
    const amount = item.size > 0 ? formatBytes(item.size) : 'everything this torrent downloaded';

    const error = h('p', { class: 'dialog-error', role: 'alert', hidden: true });
    const confirm = h('button', { class: 'btn btn-primary', type: 'submit', textContent: 'Remove from qBittorrent' });

    const acknowledge = h('input', { type: 'checkbox', onChange: updateConfirm });
    const acknowledgement = h('label', { class: 'check', hidden: true },
      acknowledge,
      `I understand that ${amount} in ${item.destination} will be permanently deleted.`,
    );

    function updateConfirm() {
      confirm.disabled = deleteFiles && !acknowledge.checked;
    }

    const setMode = (nextDeleteFiles) => {
      deleteFiles = nextDeleteFiles;
      confirm.className = `btn ${deleteFiles ? 'btn-danger' : 'btn-primary'}`;
      setText(confirm, deleteFiles ? 'Remove and delete files' : 'Remove from qBittorrent');
      acknowledgement.hidden = !deleteFiles;
      // Switching away and back asks again, so a stale tick never confirms a deletion.
      acknowledge.checked = false;
      updateConfirm();
    };

    const choices = h('fieldset', { class: 'choice-list' },
      h('legend', { class: 'sr-only', textContent: 'What should happen to the downloaded files?' }),
      choice({
        value: 'keep', checked: true, onChange: () => setMode(false),
        title: 'Remove from qBittorrent',
        hint: `Stops the torrent. Files stay in ${item.destination}.`,
      }),
      choice({
        value: 'delete', onChange: () => setMode(true),
        title: 'Remove and delete files',
        hint: `Stops the torrent and deletes ${amount} from ${item.destination}.`,
        warning: "Deleted files can't be recovered.",
      }),
    );

    const cancel = cancelButton(() => handle.close());

    const handle = openDialog({
      title: `Remove ${item.name}?`,
      body: [choices, acknowledgement, error],
      footer: [cancel, confirm],
      initialFocus: cancel,
      async onSubmit() {
        if (submitting || (deleteFiles && !acknowledge.checked)) return;
        submitting = true;
        confirm.setAttribute('aria-busy', 'true');
        try {
          const removed = await removeDownload(item.id, deleteFiles);
          handle.close();
          toast(removed.filesDeleted
            ? `Removed ${removed.name}. Its files were deleted from ${removed.savePath}.`
            : `Removed ${removed.name} from qBittorrent. Its files are still in ${removed.savePath}.`);
        } catch (failure) {
          error.textContent = failure.message;
          error.hidden = false;
        } finally {
          submitting = false;
          confirm.removeAttribute('aria-busy');
        }
      },
    });
  }

  Object.assign(MediaFlow, { openRemoveDialog });
})();
