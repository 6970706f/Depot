(() => {
  'use strict';

  const { getPref } = MediaFlow;

  const EMPTY = '—';

  function formatBytes(bytes) {
    if (bytes >= 1e12) return `${(bytes / 1e12).toFixed(2)} TB`;
    if (bytes >= 1e9) return `${(bytes / 1e9).toFixed(1)} GB`;
    if (bytes >= 1e6) return `${Math.round(bytes / 1e6)} MB`;
    return `${Math.round(bytes / 1e3)} KB`;
  }

  function formatSpeed(bytesPerSecond, { showZero = false } = {}) {
    if (!bytesPerSecond) return showZero ? `0 ${getPref('speedUnit')}` : EMPTY;
    if (getPref('speedUnit') === 'Mbit/s') return `${((bytesPerSecond * 8) / 1e6).toFixed(1)} Mbit/s`;
    if (bytesPerSecond < 1e6) return `${Math.round(bytesPerSecond / 1e3)} KB/s`;
    return `${(bytesPerSecond / 1e6).toFixed(1)} MB/s`;
  }

  function formatEta(seconds) {
    if (seconds === null || seconds === undefined) return EMPTY;
    if (seconds < 60) return '< 1 min';
    const minutes = Math.round(seconds / 60);
    if (minutes < 60) return `${minutes} min`;
    const hours = Math.floor(minutes / 60);
    return `${hours} h ${minutes % 60} min`;
  }

  function formatPercent(fraction) {
    return `${Math.floor(fraction * 100)}%`;
  }

  function plural(count, one, many = `${one}s`) {
    return `${count} ${count === 1 ? one : many}`;
  }

  Object.assign(MediaFlow, { EMPTY, formatBytes, formatSpeed, formatEta, formatPercent, plural });
})();
