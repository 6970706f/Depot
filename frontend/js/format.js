const units = ['B', 'KB', 'MB', 'GB', 'TB'];

export function bytes(value) {
  if (value == null) return '—';
  let size = value;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }
  return `${size.toFixed(size >= 100 || unit === 0 ? 0 : 1)} ${units[unit]}`;
}

export function speed(value) {
  return value ? `${bytes(value)}/s` : '—';
}

export function eta(seconds) {
  if (seconds == null) return '—';
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `${hours}h ${minutes % 60}m`;
  return `${Math.floor(hours / 24)}d`;
}

export function percent(progress) {
  return `${Math.floor((progress ?? 0) * 1000) / 10}%`;
}

export function dateTime(value) {
  return value ? new Date(value).toLocaleString() : '—';
}

export const statusLabels = {
  downloading: 'Downloading',
  queued: 'Queued',
  checking: 'Checking',
  paused: 'Paused',
  completed: 'Completed',
  error: 'Error',
};
