// The only data access point for the UI. Every function calls the Depot backend, which
// owns all traffic to qBittorrent; pages never talk to the network themselves.
(() => {
  'use strict';

  // field names the input the error is about; code is the backend's error code, such as "already_added".
  class ApiError extends Error {
    constructor(message, { field = null, code = null } = {}) {
      super(message);
      this.name = 'ApiError';
      this.field = field;
      this.code = code;
    }
  }

  // Fires after any change, so the visible page can refresh without knowing who made it.
  const apiEvents = new EventTarget();

  // The Depot API serves this page, so it's called on the page's own origin, whatever address
  // the page was opened from (localhost, or the server's name on the network).
  const REQUEST_TIMEOUT_MS = 5000;

  const API_UNREACHABLE = location.protocol === 'file:'
    ? 'Open MediaFlow from the Depot server\'s address, such as http://localhost:5080, not as a file.'
    : `Can't reach the Depot API at ${location.origin}. Check that it's running, then retry.`;

  // Every API call carries Depot's access token. It's kept in this browser once entered; the
  // in-memory copy covers browsers where storage is blocked.
  const TOKEN_KEY = 'mediaflow.token';
  let accessToken = null;
  try {
    accessToken = localStorage.getItem(TOKEN_KEY);
  } catch {
    // Storage can be unavailable; the token is then asked for on every visit.
  }

  function storeAccessToken(token) {
    accessToken = token;
    try {
      localStorage.setItem(TOKEN_KEY, token);
    } catch {
      // Kept in memory only.
    }
  }

  // Calls the real backend. Every failure becomes an ApiError with a message the UI can show as is.
  // Send either json (an object) or form (FormData, for uploads; the browser sets its content type).
  // A 401 also dispatches "unauthorized" on apiEvents, so the app can ask for the access token.
  async function request(path, { method = 'GET', json, form, timeoutMs = REQUEST_TIMEOUT_MS, token = accessToken } = {}) {
    const headers = { Accept: 'application/json' };
    if (json !== undefined) headers['Content-Type'] = 'application/json';
    if (token) headers.Authorization = `Bearer ${token}`;

    let response;
    try {
      response = await fetch(path, {
        method,
        headers,
        body: form ?? (json === undefined ? undefined : JSON.stringify(json)),
        signal: AbortSignal.timeout(timeoutMs),
      });
    } catch {
      throw new ApiError(API_UNREACHABLE);
    }
    if (!response.ok) {
      const error = await toApiError(response);
      if (response.status === 401 && token === accessToken) apiEvents.dispatchEvent(new Event('unauthorized'));
      throw error;
    }
    // 202 and 204 answers have no body.
    const text = await response.text();
    if (!text) return null;
    try {
      return JSON.parse(text);
    } catch {
      throw new ApiError('The Depot API sent a response that could not be read.');
    }
  }

  // Reads the backend's problem details: the first validation error names its field,
  // and other failures carry a readable detail.
  async function toApiError(response) {
    try {
      const problem = await response.json();
      const [field, messages] = Object.entries(problem.errors ?? {})[0] ?? [];
      if (field && messages?.length) return new ApiError(messages[0], { field });
      if (problem.detail) return new ApiError(problem.detail, { code: problem.code ?? null });
    } catch {
      // Not a problem details body; fall back to the status code.
    }
    if (response.status === 413) return new ApiError('The file is larger than the Depot API accepts.');
    if (response.status === 429) return new ApiError('Too many requests in a short time. Wait a moment, then try again.');
    return new ApiError(`The Depot API is running but answered with an error (HTTP ${response.status}).`);
  }

  // Checks a token against the API before keeping it. Rejects with the API's reason.
  async function signIn(token) {
    const trimmed = token.trim();
    if (!trimmed) throw new ApiError('Enter the access token.');
    await request('/api/status', { token: trimmed });
    storeAccessToken(trimmed);
    apiEvents.dispatchEvent(new Event('change'));
  }

  // GET /api/health. Resolves only when the API reports itself operational.
  function getHealth() {
    return shared('health', async () => {
      const health = await request('/api/health');
      if (health?.status !== 'healthy' && health?.status !== 'degraded') {
        throw new ApiError('The Depot API reported a problem with its health check.');
      }
      return health;
    });
  }

  // Callers of the same read share one request while it's in flight. Each refresh asks for
  // the same data from several places (the page, the navigation badge, the status bar), and
  // this keeps it to one request per endpoint.
  const inFlight = new Map();

  function shared(key, load) {
    if (!inFlight.has(key)) {
      inFlight.set(key, load().finally(() => inFlight.delete(key)));
    }
    return inFlight.get(key);
  }

  // Maps a download from the API to the shape the pages use.
  function toUiDownload(download) {
    return {
      id: download.hash,
      name: download.name,
      status: download.status,
      size: download.sizeBytes,
      downloaded: download.downloadedBytes,
      progress: download.progress,
      downSpeed: download.downloadSpeed,
      upSpeed: download.uploadSpeed,
      etaSeconds: download.etaSeconds,
      category: download.category,
      destination: download.savePath,
      addedAt: download.addedAt,
      // { mediaType, title, season } when the videos go to the library once finished, otherwise null.
      library: download.library,
      // qBittorrent reports that a torrent failed, but not why.
      error: download.status === 'error' ? 'qBittorrent reported an error. Check this torrent in qBittorrent.' : null,
    };
  }

  // GET /api/downloads
  // An empty list means qBittorrent has no torrents; a failure to reach it rejects instead.
  function getDownloads() {
    return shared('downloads', async () => (await request('/api/downloads')).map(toUiDownload));
  }

  const IN_PROGRESS = new Set(['downloading', 'queued', 'checking']);

  // Summarizes GET /api/downloads for the overview and the navigation badge.
  async function getStats() {
    const downloads = await getDownloads();
    const count = (status) => downloads.filter((item) => item.status === status).length;
    const completed = downloads.filter((item) => item.status === 'completed');
    return {
      active: downloads.filter((item) => IN_PROGRESS.has(item.status)).length,
      paused: count('paused'),
      failed: count('error'),
      completed: completed.length,
      completedSize: completed.reduce((total, item) => total + item.size, 0),
      seeding: downloads.filter((item) => item.upSpeed > 0).length,
      downSpeed: downloads.reduce((total, item) => total + item.downSpeed, 0),
      upSpeed: downloads.reduce((total, item) => total + item.upSpeed, 0),
    };
  }

  // GET /api/status
  // { qbittorrent: { configured, connected, version, error }, jellyfin: { configured, connected, serverName, version, error } }
  function getSystemStatus() {
    return shared('status', () => request('/api/status'));
  }

  // Actions on downloads make the backend wait on qBittorrent more than once (a lookup, then the
  // command), up to 10 seconds each, so they get longer than the usual request timeout.
  const ACTION_TIMEOUT_MS = 25000;

  // POST /api/downloads/torrent with a file, otherwise POST /api/downloads/magnet. Resolves to { hash, name }.
  // mediaType is 'movie', 'series' or 'other'; title is the movie's or series' folder in the
  // library and season the series' season. 'other' is only saved on the server.
  // Errors about the magnet link, a duplicate included, are reported on the "source" field.
  // Errors about a file have no field of their own, so they are reported for the whole form.
  async function addDownload({ magnet = '', file = null, mediaType = 'other', title = null, season = null }) {
    const library = mediaType === 'other'
      ? { mediaType }
      : { mediaType, title, season: mediaType === 'series' ? season : null };
    let added;
    try {
      if (file) {
        const form = new FormData();
        form.append('file', file);
        for (const [key, value] of Object.entries(library)) {
          if (value !== null && value !== undefined) form.append(key, String(value));
        }
        added = await request('/api/downloads/torrent', { method: 'POST', form, timeoutMs: ACTION_TIMEOUT_MS });
      } else {
        added = await request('/api/downloads/magnet', {
          method: 'POST',
          json: { magnet, ...library },
          timeoutMs: ACTION_TIMEOUT_MS,
        });
      }
    } catch (error) {
      if (!file && error instanceof ApiError && (error.field === 'magnet' || error.code === 'already_added')) {
        throw new ApiError(error.message, { field: 'source', code: error.code });
      }
      throw error;
    }
    apiEvents.dispatchEvent(new Event('change'));
    return added;
  }

  // GET /api/library/series: [{ name, seasons: [1, 2] }], the series folders already in the
  // library with the seasons that have a folder.
  function getLibrarySeries() {
    return request('/api/library/series');
  }

  // The backend answers once qBittorrent reports the new state, so the refresh that follows shows it.
  async function runCommand(id, command) {
    const download = await request(`/api/downloads/${encodeURIComponent(id)}/${command}`, {
      method: 'POST',
      timeoutMs: ACTION_TIMEOUT_MS,
    });
    apiEvents.dispatchEvent(new Event('change'));
    return toUiDownload(download);
  }

  // POST /api/downloads/{hash}/pause
  function pauseDownload(id) {
    return runCommand(id, 'pause');
  }

  // POST /api/downloads/{hash}/resume
  function resumeDownload(id) {
    return runCommand(id, 'resume');
  }

  // qBittorrent retries a torrent in error when it's started again.
  function retryDownload(id) {
    return runCommand(id, 'resume');
  }

  // DELETE /api/downloads/{hash}?deleteFiles=true|false
  // Removing the torrent and deleting its files are separate outcomes. The backend requires
  // deleteFiles, so files are never deleted, or kept, by omission.
  // Resolves to { hash, name, filesDeleted, savePath }.
  async function removeDownload(id, deleteFiles) {
    if (typeof deleteFiles !== 'boolean') throw new ApiError('Choose whether to delete the downloaded files.');
    const removed = await request(`/api/downloads/${encodeURIComponent(id)}?deleteFiles=${deleteFiles}`, {
      method: 'DELETE',
      timeoutMs: ACTION_TIMEOUT_MS,
    });
    apiEvents.dispatchEvent(new Event('change'));
    return removed;
  }

  // The backend reports the field by its own name ("url"); the UI needs to know the service too.
  function withServiceField(service, error) {
    if (error instanceof ApiError && error.field) {
      return new ApiError(error.message, { field: `${service}.${error.field}` });
    }
    return error;
  }

  // GET /api/settings/qbittorrent. Resolves to { qbittorrent: { url, username, hasPassword } }.
  async function getSettings() {
    return { qbittorrent: await request('/api/settings/qbittorrent') };
  }

  // PUT /api/settings/qbittorrent
  // Secrets travel one way: the backend stores them and only ever reports hasPassword.
  async function saveSettings({ qbittorrent }) {
    let saved;
    try {
      saved = await request('/api/settings/qbittorrent', { method: 'PUT', json: qbittorrent });
    } catch (error) {
      throw withServiceField('qbittorrent', error);
    }
    // New settings can change the connection status shown everywhere.
    apiEvents.dispatchEvent(new Event('change'));
    return { qbittorrent: saved };
  }

  // POST /api/settings/qbittorrent/test
  // Tests the values in the form, before they're saved. A blank password uses the stored one.
  async function testConnection(values) {
    try {
      // The backend waits up to 10 seconds for qBittorrent, so allow longer than usual.
      return await request('/api/settings/qbittorrent/test', { method: 'POST', json: values, timeoutMs: 15000 });
    } catch (error) {
      throw withServiceField('qbittorrent', error);
    }
  }

  // GET /api/organization/settings: the folders, import mode, path mappings and a check of
  // every folder (diagnostics).
  function getOrganizationSettings() {
    return request('/api/organization/settings');
  }

  // PUT /api/organization/settings. Validation errors name their field, such as "moviesPath"
  // or "pathMappings[0].depotPath".
  async function saveOrganizationSettings(settings) {
    const saved = await request('/api/organization/settings', { method: 'PUT', json: settings });
    apiEvents.dispatchEvent(new Event('change'));
    return saved;
  }

  // POST /api/organization/settings/test: checks the folders without saving.
  function testOrganizationSettings(settings) {
    return request('/api/organization/settings/test', { method: 'POST', json: settings });
  }

  // GET /api/organization/status: worker state and job counts per status.
  function getOrganizationStatus() {
    return shared('organization-status', () => request('/api/organization/status'));
  }

  // GET /api/organization/jobs, newest first.
  function getOrganizationJobs() {
    return shared('organization-jobs', () => request('/api/organization/jobs'));
  }

  async function runJobAction(id, action) {
    const job = await request(`/api/organization/jobs/${id}/${action}`, { method: 'POST' });
    apiEvents.dispatchEvent(new Event('change'));
    return job;
  }

  function retryOrganizationJob(id) {
    return runJobAction(id, 'retry');
  }

  // Only marks the job as skipped; no file is deleted.
  function skipOrganizationJob(id) {
    return runJobAction(id, 'skip');
  }

  // POST /api/organization/jobs/{id}/classify: where a series video goes, then queues it.
  // placement is { kind: 'main', season } (season 0 for a special) or
  // { kind: 'extra', extrasFolder }.
  async function classifyOrganizationJob(id, placement) {
    const job = await request(`/api/organization/jobs/${id}/classify`, { method: 'POST', json: placement });
    apiEvents.dispatchEvent(new Event('change'));
    return job;
  }

  // POST /api/organization/downloads/{hash}/confirm: accepts the suggested placement of every
  // video of the download that waits for review.
  async function confirmOrganizationDownload(hash) {
    const jobs = await request(`/api/organization/downloads/${encodeURIComponent(hash)}/confirm`, { method: 'POST' });
    apiEvents.dispatchEvent(new Event('change'));
    return jobs;
  }

  // Jellyfin. The browser never talks to Jellyfin either, and never sees its API key: settings
  // report only hasApiKey, and images come through Depot.

  // GET /api/settings/jellyfin: { url, hasApiKey }.
  function getJellyfinSettings() {
    return request('/api/settings/jellyfin');
  }

  // PUT /api/settings/jellyfin with { url, apiKey }. A blank key keeps the saved one, but only
  // for the same URL. Errors name "jellyfin.url" or "jellyfin.apiKey".
  async function saveJellyfinSettings(values) {
    let saved;
    try {
      saved = await request('/api/settings/jellyfin', { method: 'PUT', json: values });
    } catch (error) {
      throw withServiceField('jellyfin', error);
    }
    apiEvents.dispatchEvent(new Event('change'));
    return saved;
  }

  // POST /api/settings/jellyfin/test: { serverName, version } for the values in the form.
  async function testJellyfinConnection(values) {
    try {
      // The backend makes two requests of up to 15 seconds each.
      return await request('/api/settings/jellyfin/test', { method: 'POST', json: values, timeoutMs: 35000 });
    } catch (error) {
      throw withServiceField('jellyfin', error);
    }
  }

  // GET /api/jellyfin/libraries: Depot's copy of each library, with item counts.
  function getJellyfinLibraries() {
    return shared('jellyfin-libraries', () => request('/api/jellyfin/libraries'));
  }

  // GET /api/jellyfin/items: { items, total, page, pageSize }. Filters: kind (movie, series,
  // season, episode), libraryId, search, sort (name, added, year), page, pageSize.
  function getJellyfinItems(query = {}) {
    const params = new URLSearchParams(Object.entries(query).filter(([, value]) => value !== null && value !== undefined && value !== ''));
    const path = `/api/jellyfin/items${params.size ? `?${params}` : ''}`;
    return shared(path, () => request(path));
  }

  // GET /api/jellyfin/items/{id}: metadata, files with their streams, and the seasons or episodes.
  function getJellyfinItem(id) {
    return request(`/api/jellyfin/items/${encodeURIComponent(id)}`);
  }

  // GET /api/jellyfin/sync: { isRunning, latestRun }.
  function getJellyfinSync() {
    return shared('jellyfin-sync', () => request('/api/jellyfin/sync'));
  }

  // POST /api/jellyfin/sync: queues a sync and resolves to the run. A sync already running is
  // an ApiError with code "sync_running".
  async function startJellyfinSync() {
    const run = await request('/api/jellyfin/sync', { method: 'POST' });
    apiEvents.dispatchEvent(new Event('change'));
    return run;
  }

  // Resolves to a blob: URL for an item's image, or null when it has none. images is the item's
  // images object; the version makes the address cacheable for good. The caller revokes the URL
  // (URL.revokeObjectURL) once it stops showing it.
  // kind is 'primary' (the poster), 'backdrop', 'thumb' or 'logo'.
  async function loadJellyfinImage(item, kind, { width, index = 0 } = {}) {
    const version = kind === 'backdrop' ? item.images.backdrops[index] : item.images[kind];
    if (!version) return null;
    const params = new URLSearchParams({ v: version });
    if (width) params.set('width', String(width));
    if (index) params.set('index', String(index));
    let response;
    try {
      response = await fetch(`/api/jellyfin/items/${encodeURIComponent(item.id)}/images/${kind}?${params}`, {
        headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {},
        signal: AbortSignal.timeout(20000),
      });
    } catch {
      throw new ApiError(API_UNREACHABLE);
    }
    if (response.status === 404) return null;
    if (!response.ok) throw await toApiError(response);
    return URL.createObjectURL(await response.blob());
  }

  Object.assign(MediaFlow, { ApiError, apiEvents, signIn, getHealth, getDownloads, getStats, getSystemStatus, addDownload, pauseDownload, resumeDownload, retryDownload, removeDownload, getSettings, saveSettings, testConnection, getOrganizationSettings, saveOrganizationSettings, testOrganizationSettings, getOrganizationStatus, getOrganizationJobs, retryOrganizationJob, skipOrganizationJob, classifyOrganizationJob, confirmOrganizationDownload, getLibrarySeries, getJellyfinSettings, saveJellyfinSettings, testJellyfinConnection, getJellyfinLibraries, getJellyfinItems, getJellyfinItem, getJellyfinSync, startJellyfinSync, loadJellyfinImage });
})();
