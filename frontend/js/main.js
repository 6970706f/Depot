// App shell: hash routing, page mounting and the periodic refresh of the visible page.
import { api, apiEvents } from './api.js';
import { h, icon, clear } from './dom.js';
import { openSignIn } from './components/sign-in.js';
import { renderServices } from './components/service-status.js';
import { openAddDialog } from './components/add-dialog.js';

const REFRESH_MS = 2000;
const STATUS_MS = 5000;

const pages = [
  { path: 'overview', label: 'Overview', icon: 'overview', load: () => import('./pages/overview.js') },
  { path: 'downloads', label: 'Downloads', icon: 'downloads', load: () => import('./pages/downloads.js') },
  { path: 'imports', label: 'Imports', icon: 'imports', load: () => import('./pages/imports.js') },
  { path: 'library', label: 'Library', icon: 'library', load: () => import('./pages/library.js') },
  { path: 'settings', label: 'Settings', icon: 'settings', load: () => import('./pages/settings.js') },
];

const view = document.getElementById('view');
const nav = document.getElementById('nav');
const title = document.getElementById('page-title');
const actions = document.getElementById('topbar-actions');
const sidebar = document.getElementById('sidebar');

let current = null;
let mountId = 0;

function renderNav() {
  clear(nav).append(...pages.map((page) =>
    h('a', { href: `#/${page.path}`, dataset: { path: page.path } },
      icon(page.icon), h('span', { text: page.label }), h('span', { class: 'count', hidden: true }))));
}

// A red number next to a page in the menu, such as failed imports.
function setNavCount(path, count) {
  const badge = nav.querySelector(`a[data-path="${path}"] .count`);
  if (!badge) return;
  badge.textContent = count || '';
  badge.hidden = !count;
}

// "#/library/movies/123" → { page: library, params: ['movies', '123'] }
function parseHash() {
  const [path, ...params] = location.hash.replace(/^#\/?/, '').split('/').map(decodeURIComponent);
  const page = pages.find((candidate) => candidate.path === path) ?? pages[0];
  return { page, params: page.path === path ? params : [] };
}

async function navigate() {
  const { page, params } = parseHash();
  const id = ++mountId;
  current?.instance?.unmount?.();
  current = null;

  for (const link of nav.querySelectorAll('a')) {
    if (link.dataset.path === page.path) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  }
  sidebar.classList.remove('open');

  const module = (await page.load()).default;
  if (id !== mountId) return;
  clear(view);
  clear(actions);
  title.textContent = module.title;
  document.title = `${module.title} · Depot`;
  const instance = module.mount(view, { params, actions });
  current = { page, instance };
}

function refreshCurrent() {
  if (document.visibilityState === 'visible') current?.instance?.refresh?.();
}

renderNav();
window.addEventListener('hashchange', navigate);
apiEvents.addEventListener('change', refreshCurrent);
apiEvents.addEventListener('unauthorized', openSignIn);
document.getElementById('menu-btn').addEventListener('click', () => sidebar.classList.toggle('open'));
setInterval(refreshCurrent, REFRESH_MS);
navigate();
// Asks for the token right away instead of on the first page that needs it.
api.session().catch(() => {});

// The service dots in the top bar, and a banner while Depot itself doesn't answer.
const services = document.getElementById('services');
const banner = document.getElementById('banner');
async function refreshStatus() {
  if (document.visibilityState !== 'visible') return;
  try {
    const status = await api.status();
    banner.hidden = true;
    renderServices(services, { qBittorrent: status.qBittorrent, jellyfin: status.jellyfin });
    const imports = await api.importStatus();
    setNavCount('imports', imports.counts.failed);
  } catch (error) {
    if (error.code === 'offline') {
      banner.textContent = error.message;
      banner.hidden = false;
    }
  }
}
refreshStatus();
setInterval(refreshStatus, STATUS_MS);
apiEvents.addEventListener('change', refreshStatus);

// "Add download" lives in the top bar on every page; "a" opens it too.
const addButton = h('button', { class: 'btn primary', type: 'button', onclick: openAddDialog }, icon('plus'), 'Add download');
document.querySelector('.topbar').insertBefore(addButton, services);
document.addEventListener('keydown', (event) => {
  const typing = event.target.closest?.('input, textarea, select, [contenteditable]');
  if (event.key === 'a' && !typing && !event.ctrlKey && !event.metaKey && !event.altKey && !document.querySelector('dialog[open]')) {
    event.preventDefault();
    openAddDialog();
  }
});
