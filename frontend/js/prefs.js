// Interface preferences for this browser. Server settings live behind api.js.
(() => {
  'use strict';

  const STORAGE_KEY = 'mediaflow.prefs';

  const DEFAULTS = {
    theme: 'black',
    density: 'compact',
    speedUnit: 'MB/s',
    refreshSeconds: 2,
    shortcuts: true,
  };

  const listeners = new Set();
  const prefs = { ...DEFAULTS, ...load() };

  function load() {
    try {
      return JSON.parse(localStorage.getItem(STORAGE_KEY)) || {};
    } catch {
      return {};
    }
  }

  function save() {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(prefs));
    } catch {
      // Without storage, preferences last for this page load only.
    }
  }

  function applyToDocument() {
    const { dataset } = document.documentElement;
    dataset.theme = prefs.theme;
    dataset.density = prefs.density;
    dataset.shortcuts = prefs.shortcuts ? 'on' : 'off';
  }

  function getPref(key) {
    return prefs[key];
  }

  function setPref(key, value) {
    prefs[key] = value;
    save();
    applyToDocument();
    listeners.forEach((listener) => listener(key));
  }

  function onPrefChange(listener) {
    listeners.add(listener);
  }

  applyToDocument();

  Object.assign(MediaFlow, { getPref, setPref, onPrefChange });
})();
