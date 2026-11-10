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

  // A movie's or episode's length, such as "2 h 35 min" or "48 min".
  function formatRuntime(seconds) {
    if (!seconds) return null;
    const minutes = Math.round(seconds / 60);
    if (minutes < 60) return `${Math.max(minutes, 1)} min`;
    return minutes % 60 ? `${Math.floor(minutes / 60)} h ${minutes % 60} min` : `${minutes / 60} h`;
  }

  // "just now", "5 min ago", "3 h ago", "2 days ago", then the date.
  function formatAgo(value) {
    const seconds = (Date.now() - new Date(value).getTime()) / 1000;
    if (seconds < 60) return 'just now';
    if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
    if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`;
    if (seconds < 7 * 86400) return plural(Math.floor(seconds / 86400), 'day') + ' ago';
    return new Date(value).toLocaleDateString([], { dateStyle: 'medium' });
  }

  // A video's resolution as people name it: 4K, 1080p...
  function formatResolution(width, height) {
    if (!width && !height) return null;
    if (width >= 3800 || height >= 2100) return '4K';
    if (width >= 2500 || height >= 1400) return '1440p';
    if (width >= 1900 || height >= 1000) return '1080p';
    if (width >= 1200 || height >= 700) return '720p';
    return `${height}p`;
  }

  function formatChannels(channels) {
    return { 1: 'Mono', 2: 'Stereo', 6: '5.1', 8: '7.1' }[channels] ?? (channels ? `${channels} ch` : null);
  }

  Object.assign(MediaFlow, { EMPTY, formatBytes, formatSpeed, formatEta, formatPercent, plural, formatRuntime, formatAgo, formatResolution, formatChannels });
})();
