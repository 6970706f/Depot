(() => {
  'use strict';

  const { apiEvents, describeServices, downloadsPage, getHealth, getOrganizationStatus, getPref, getStats, getSystemStatus, h, initNavigation, libraryPage, onPrefChange, openAddDialog, openSignInDialog, overviewPage, setText, settingsPage, toggleCommandPalette } = MediaFlow;

  const PAGES = {
    overview: overviewPage,
    downloads: downloadsPage,
    library: libraryPage,
    settings: settingsPage,
  };

  const view = document.getElementById('view');
  const pageTitle = document.getElementById('page-title');

  let pageController = null;
  let refreshPage = null;
  let isFirstRoute = true;

  function showPage(route, params) {
    pageController?.abort();
    pageController = new AbortController();

    const page = PAGES[route];
    const root = h('div', { class: 'page' });
    view.replaceChildren(root);
    view.scrollTop = 0;
    refreshPage = page.mount(root, { params, signal: pageController.signal });

    pageTitle.textContent = page.title;
    document.title = `${page.title} - MediaFlow`;
    if (!isFirstRoute) pageTitle.focus();
    isFirstRoute = false;

    // Search params seed the page once. Dropping them lets the same search run again later.
    if (Object.keys(params).length) history.replaceState(null, '', `#/${route}`);
    refreshNow();
  }

  // System status

  // The tooltip carries the detail, such as why a connection failed.
  function serviceItem({ name, state, label, detail }) {
    return h('li', { class: 'service', 'data-state': state, title: `${name}: ${detail ?? label}` },
      h('span', { class: 'dot' }),
      h('span', { class: 'rail-hidden', textContent: name }),
      h('span', { class: 'service-state rail-hidden', textContent: label }),
    );
  }

  function renderSystemStatus(status, unauthorized) {
    const services = describeServices(status);
    document.getElementById('services').replaceChildren(...services.map(serviceItem));

    // The first service that needs attention names the overall state.
    const problem = services.find((service) => service.state === 'down');
    let label = 'All connected';
    if (unauthorized) label = 'Access token needed';
    else if (status === null) label = 'Backend unreachable';
    else if (problem) label = `${problem.name} ${problem.label.toLowerCase()}`;

    // The tooltip carries the reason, so a failure can be understood without opening Settings.
    const reason = status !== null && problem?.detail ? ` ${problem.detail}` : '';
    const indicator = document.getElementById('system-indicator');
    indicator.dataset.state = problem ? 'down' : 'ok';
    indicator.title = `${label}.${reason} Open settings.`;
    indicator.setAttribute('aria-label', `System status: ${label}. Open settings.`);
    indicator.replaceChildren(
      h('span', { class: 'dot' }),
      h('span', { class: 'indicator-label', textContent: label }),
    );
  }

  // Shown above every page while the real API is unavailable; hidden again once it answers.
  function renderApiAlert(message) {
    const alert = document.getElementById('api-alert');
    alert.hidden = message === null;
    if (message === null) {
      alert.replaceChildren();
      return;
    }
    alert.replaceChildren(
      h('p', { class: 'api-alert-text' },
        h('strong', { textContent: 'Depot API unavailable. ' }),
        message,
      ),
      h('button', { class: 'btn', type: 'button', onClick: retryApi, textContent: 'Retry' }),
    );
  }

  // Marks the button busy so a retry that fails again still visibly happened.
  async function retryApi(event) {
    const button = event.currentTarget;
    button.setAttribute('aria-busy', 'true');
    try {
      await refreshNow();
    } finally {
      button.removeAttribute('aria-busy');
    }
  }

  let lastStatusKey = null;

  async function refreshSystem() {
    const [health, system, stats, organization] = await Promise.allSettled([
      getHealth(), getSystemStatus(), getStats(), getOrganizationStatus(),
    ]);
    const apiError = health.status === 'rejected' ? health.reason.message : null;
    // A failed status call is itself the status to report.
    const status = apiError === null && system.status === 'fulfilled' ? system.value : null;
    if (stats.status === 'fulfilled') {
      setText(document.getElementById('nav-download-count'), stats.value.active ? String(stats.value.active) : '');
    }
    if (organization.status === 'fulfilled') {
      // Imports that wait for a person: failed, or held for review.
      const waiting = organization.value.counts.failed + (organization.value.counts.needsReview ?? 0);
      setText(document.getElementById('nav-library-count'), waiting ? String(waiting) : '');
    }

    const unauthorized = system.status === 'rejected' && system.reason?.code === 'unauthorized';

    const key = JSON.stringify({ apiError, status, unauthorized });
    if (key !== lastStatusKey) {
      lastStatusKey = key;
      renderSystemStatus(status, unauthorized);
      renderApiAlert(apiError);
    }
  }

  // Polling. One refresh runs at a time; a change during a refresh queues one more.

  let refreshing = false;
  let refreshQueued = false;

  async function refreshNow() {
    if (refreshing) {
      refreshQueued = true;
      return;
    }
    refreshing = true;
    try {
      await Promise.all([refreshPage?.(), refreshSystem()]);
    } finally {
      refreshing = false;
    }
    if (refreshQueued) {
      refreshQueued = false;
      refreshNow();
    }
  }

  let pollTimer = null;

  function schedulePoll() {
    clearTimeout(pollTimer);
    pollTimer = setTimeout(async () => {
      if (!document.hidden) await refreshNow();
      schedulePoll();
    }, getPref('refreshSeconds') * 1000);
  }

  // Top bar and keyboard

  function initTopbar() {
    document.getElementById('add-btn').addEventListener('click', openAddDialog);
    document.getElementById('search-btn').addEventListener('click', toggleCommandPalette);
    if (/Mac|iPhone|iPad/.test(navigator.platform)) {
      document.getElementById('search-kbd').textContent = '⌘K';
    }
  }

  function isTypingTarget(target) {
    return target.closest?.('input, select, textarea, [contenteditable]');
  }

  function initShortcuts() {
    document.addEventListener('keydown', (event) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        const otherDialogOpen = document.querySelector('dialog[open]:not(.palette)');
        if (!otherDialogOpen) toggleCommandPalette();
        return;
      }

      if (!getPref('shortcuts') || event.ctrlKey || event.metaKey || event.altKey) return;
      if (isTypingTarget(event.target) || document.querySelector('dialog[open]')) return;

      if (event.key === 'a') {
        event.preventDefault();
        openAddDialog();
      } else if (event.key === '/') {
        event.preventDefault();
        const pageSearch = document.querySelector('[data-page-search]');
        if (pageSearch) pageSearch.focus();
        else toggleCommandPalette();
      }
    });
  }

  initTopbar();
  initShortcuts();
  apiEvents.addEventListener('change', refreshNow);
  apiEvents.addEventListener('unauthorized', openSignInDialog);
  onPrefChange((key) => {
    if (key === 'refreshSeconds') schedulePoll();
    if (key === 'speedUnit') refreshNow();
  });
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) refreshNow();
  });
  initNavigation({ routes: Object.keys(PAGES), onRoute: showPage });
  schedulePoll();

  // Tells boot.js the app started. Without this mark it replaces the page with an explanation.
  document.documentElement.dataset.app = 'ready';
})();
