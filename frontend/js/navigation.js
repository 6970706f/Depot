// Hash routing and sidebar behaviour. A static server needs no rewrite rules.
(() => {
  'use strict';

  const DEFAULT_ROUTE = 'overview';

  function go(route, params = {}) {
    const query = new URLSearchParams(params).toString();
    location.hash = `#/${route}${query ? `?${query}` : ''}`;
  }

  function parseHash(routes) {
    const [path, query] = location.hash.replace(/^#\/?/, '').split('?');
    return {
      route: routes.includes(path) ? path : DEFAULT_ROUTE,
      params: Object.fromEntries(new URLSearchParams(query)),
    };
  }

  function initNavigation({ routes, onRoute }) {
    const links = document.querySelectorAll('.nav-link');
    const announce = () => {
      const { route, params } = parseHash(routes);
      links.forEach((link) => {
        if (link.dataset.route === route) link.setAttribute('aria-current', 'page');
        else link.removeAttribute('aria-current');
      });
      onRoute(route, params);
    };

    window.addEventListener('hashchange', announce);
    initMobileMenu();
    initSkipLink();
    announce();
  }

  function initMobileMenu() {
    const sidebar = document.getElementById('sidebar');
    const scrim = document.getElementById('scrim');
    const button = document.getElementById('menu-btn');
    const workspace = document.querySelector('.workspace');

    const setOpen = (open) => {
      sidebar.classList.toggle('is-open', open);
      scrim.hidden = !open;
      workspace.inert = open;
      button.setAttribute('aria-expanded', String(open));
      button.setAttribute('aria-label', open ? 'Close navigation' : 'Open navigation');
    };

    button.addEventListener('click', () => {
      setOpen(true);
      sidebar.querySelector('.nav-link').focus();
    });
    const close = () => {
      setOpen(false);
      button.focus();
    };

    scrim.addEventListener('click', close);
    sidebar.addEventListener('click', (event) => {
      if (event.target.closest('a')) close();
    });
    document.addEventListener('keydown', (event) => {
      if (event.key === 'Escape' && sidebar.classList.contains('is-open')) close();
    });
  }

  // Following "#view" would be read as a route change.
  function initSkipLink() {
    document.getElementById('skip-link').addEventListener('click', (event) => {
      event.preventDefault();
      document.getElementById('view').focus();
    });
  }

  Object.assign(MediaFlow, { DEFAULT_ROUTE, go, initNavigation });
})();
