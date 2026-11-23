// Every call to the Depot API goes through here. The browser never talks to qBittorrent or
// Jellyfin directly. Events: "change" after any write (refreshes the visible page) and
// "unauthorized" when the API asks for the access token.

export const apiEvents = new EventTarget();

const TOKEN_KEY = 'depot.token';

export class ApiError extends Error {
  constructor(message, { status = 0, code = null, field = null, errors = null } = {}) {
    super(message);
    this.status = status;
    this.code = code;
    this.field = field;
    this.errors = errors;
  }
}

function readToken() {
  try {
    return localStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}

export function setToken(token) {
  try {
    localStorage.setItem(TOKEN_KEY, token);
  } catch {
    // Storage blocked: the token lasts until the page is reloaded.
  }
  memoryToken = token;
  apiEvents.dispatchEvent(new Event('change'));
}

let memoryToken = readToken();

async function request(method, path, { json, form, signal, token = memoryToken, raw = false } = {}) {
  const headers = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  let body;
  if (json !== undefined) {
    headers['Content-Type'] = 'application/json';
    body = JSON.stringify(json);
  } else if (form) {
    body = form;
  }

  let response;
  try {
    response = await fetch(`/api/${path}`, { method, headers, body, signal });
  } catch (error) {
    if (error.name === 'AbortError') throw error;
    throw new ApiError("Can't reach Depot. Check that it's running.", { code: 'offline' });
  }

  if (!response.ok) {
    const error = await readError(response);
    if (error.status === 401 && token === memoryToken) apiEvents.dispatchEvent(new Event('unauthorized'));
    throw error;
  }
  if (method !== 'GET') apiEvents.dispatchEvent(new Event('change'));
  if (raw) return response;
  const type = response.headers.get('Content-Type') ?? '';
  return type.includes('json') ? response.json() : null;
}

// Problem details: { detail, code, errors: { field: [message] } }.
async function readError(response) {
  let problem = {};
  try {
    problem = await response.json();
  } catch {
    // Not JSON: fall back to the status line.
  }
  const errors = problem.errors ?? null;
  const field = errors ? Object.keys(errors)[0] : null;
  const message = (field && errors[field]?.[0]) || problem.detail || problem.title || `Depot answered ${response.status}.`;
  return new ApiError(message, { status: response.status, code: problem.code ?? null, field, errors });
}

// Reads made at the same moment from several places share one request.
const inFlight = new Map();
function shared(path) {
  if (!inFlight.has(path)) {
    const promise = request('GET', path).finally(() => inFlight.delete(path));
    inFlight.set(path, promise);
  }
  return inFlight.get(path);
}

export const api = {
  health: () => shared('health'),
  session: () => request('GET', 'session'),
  checkToken: (token) => request('GET', 'session', { token }),
  status: () => shared('status'),

  downloads: () => shared('downloads'),
  pauseDownload: (hash) => request('POST', `downloads/${encodeURIComponent(hash)}/pause`),
  resumeDownload: (hash) => request('POST', `downloads/${encodeURIComponent(hash)}/resume`),
  removeDownload: (hash, deleteFiles) => request('DELETE', `downloads/${encodeURIComponent(hash)}?deleteFiles=${deleteFiles}`),
  addMagnet: (input) => request('POST', 'downloads/magnet', { json: input }),
  addTorrentFile: (form) => request('POST', 'downloads/torrent', { form }),
  librarySeries: () => request('GET', 'library/series'),

  media: ({ kind, search, sort, page, pageSize }) =>
    request('GET', `media?${new URLSearchParams({ kind, search: search ?? '', sort, page, pageSize })}`),
  mediaCounts: () => shared('media/counts'),
  mediaItem: (id) => request('GET', `media/${encodeURIComponent(id)}`),
  // A blob: URL for an <img>; the caller keeps it for the page's lifetime.
  async mediaImage(id, type, width, tag) {
    const query = new URLSearchParams({ width });
    if (tag) query.set('tag', tag);
    const response = await request('GET', `media/${encodeURIComponent(id)}/images/${type}?${query}`, { raw: true });
    return URL.createObjectURL(await response.blob());
  },

  imports: (status) => request('GET', status ? `imports?status=${status}` : 'imports'),
  importStatus: () => shared('imports/status'),
  retryImport: (id) => request('POST', `imports/${id}/retry`),
  skipImport: (id) => request('POST', `imports/${id}/skip`),

  qbittorrentSettings: () => request('GET', 'settings/qbittorrent'),
  saveQbittorrentSettings: (values) => request('PUT', 'settings/qbittorrent', { json: values }),
  testQbittorrentSettings: (values) => request('POST', 'settings/qbittorrent/test', { json: values }),
  jellyfinSettings: () => request('GET', 'settings/jellyfin'),
  saveJellyfinSettings: (values) => request('PUT', 'settings/jellyfin', { json: values }),
  testJellyfinSettings: (values) => request('POST', 'settings/jellyfin/test', { json: values }),
  librarySettings: () => request('GET', 'settings/library'),
  saveLibrarySettings: (values) => request('PUT', 'settings/library', { json: values }),
  checkLibrarySettings: (values) => request('POST', 'settings/library/check', { json: values }),
};
