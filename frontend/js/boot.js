// Runs before first paint so a saved theme never flashes the default one.
// Also creates the MediaFlow namespace, the app's only global. Every script below
// adds what it exports to it.
window.MediaFlow = {};

(function () {
  try {
    var prefs = JSON.parse(localStorage.getItem('mediaflow.prefs')) || {};
    var root = document.documentElement;
    if (prefs.theme) root.dataset.theme = prefs.theme;
    if (prefs.density) root.dataset.density = prefs.density;
  } catch (e) {
    // Storage can be unavailable; the defaults on <html> still apply.
  }

  // Deferred scripts run before DOMContentLoaded, so app.js has marked itself ready by
  // now unless a script failed. Replacing the page keeps dead buttons from looking usable.
  document.addEventListener('DOMContentLoaded', function () {
    if (document.documentElement.dataset.app === 'ready') return;

    var message = document.createElement('main');
    message.className = 'boot-message';

    var title = document.createElement('h1');
    title.textContent = "MediaFlow didn't start";
    message.append(title);

    var detail = document.createElement('p');
    detail.textContent = 'One of its scripts failed to load. Check the browser console, then reload the page.';
    message.append(detail);

    document.body.replaceChildren(message);
  });
})();
