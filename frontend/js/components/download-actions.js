(() => {
  'use strict';

  const { openRemoveDialog, pauseDownload, resumeDownload, retryDownload, toast } = MediaFlow;

  const ACTIONS = {
    pause: { call: pauseDownload, done: 'Download paused' },
    resume: { call: resumeDownload, done: 'Download resumed' },
    retry: { call: retryDownload, done: 'Download retried' },
  };

  async function runDownloadAction(kind, item) {
    if (kind === 'remove') {
      openRemoveDialog(item);
      return;
    }
    const action = ACTIONS[kind];
    try {
      await action.call(item.id);
      toast(action.done);
    } catch (error) {
      toast(error.message, { type: 'error' });
    }
  }

  Object.assign(MediaFlow, { runDownloadAction });
})();
