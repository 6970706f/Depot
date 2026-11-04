(() => {
  'use strict';

  const { getDownloads, go, h, icon, openAddDialog } = MediaFlow;

  const MAX_RESULTS = 8;

  const BASE_COMMANDS = [
    { title: 'Add download', kind: 'Action', icon: 'plus', run: openAddDialog },
    { title: 'Overview', kind: 'Page', icon: 'overview', run: () => go('overview') },
    { title: 'Downloads', kind: 'Page', icon: 'downloads', run: () => go('downloads') },
    { title: 'Library', kind: 'Page', icon: 'folder', run: () => go('library') },
    { title: 'Settings', kind: 'Page', icon: 'settings', run: () => go('settings') },
  ];

  // A failed load just leaves the palette with pages and actions.
  async function loadContentCommands() {
    try {
      const downloads = await getDownloads();
      return downloads.map(({ name }) => ({ title: name, kind: 'Download', icon: 'downloads', run: () => go('downloads', { q: name }) }));
    } catch {
      return [];
    }
  }

  let openPalette = null;

  function toggleCommandPalette() {
    if (openPalette) openPalette.close();
    else showPalette();
  }

  function showPalette() {
    let commands = BASE_COMMANDS;
    let results = [];
    let selected = 0;

    const input = h('input', {
      class: 'palette-input', type: 'text', placeholder: 'Search downloads and pages',
      role: 'combobox', 'aria-expanded': 'true', 'aria-controls': 'palette-list',
      'aria-autocomplete': 'list', 'aria-label': 'Search', autocomplete: 'off', spellcheck: 'false',
    });
    const list = h('ul', { class: 'palette-list', id: 'palette-list', role: 'listbox', 'aria-label': 'Results' });
    const dialog = h('dialog', { class: 'dialog palette', 'aria-label': 'Search' }, input, list);

    function runSelected(index = selected) {
      const command = results[index];
      if (!command) return;
      dialog.close();
      command.run();
    }

    function select(index) {
      selected = index;
      [...list.children].forEach((node, position) => {
        node.setAttribute('aria-selected', String(position === selected));
        if (position === selected) {
          input.setAttribute('aria-activedescendant', node.id);
          node.scrollIntoView({ block: 'nearest' });
        }
      });
    }

    function renderResults() {
      const query = input.value.trim().toLowerCase();
      results = commands.filter((command) => command.title.toLowerCase().includes(query)).slice(0, MAX_RESULTS);
      selected = 0;
      input.removeAttribute('aria-activedescendant');

      if (!results.length) {
        list.replaceChildren(h('li', { class: 'palette-empty', role: 'presentation', textContent: `Nothing matches "${input.value.trim()}".` }));
        return;
      }
      list.replaceChildren(...results.map((command, index) => h('li', {
        class: 'palette-item', id: `palette-item-${index}`, role: 'option', 'aria-selected': 'false',
        onClick: () => runSelected(index),
        onMousemove: () => selected !== index && select(index),
      },
        icon(command.icon),
        h('span', { class: 'palette-item-title truncate', textContent: command.title }),
        h('span', { class: 'palette-item-kind', textContent: command.kind }),
      )));
      select(0);
    }

    input.addEventListener('input', renderResults);
    input.addEventListener('keydown', (event) => {
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        if (!results.length) return;
        const step = event.key === 'ArrowDown' ? 1 : -1;
        select((selected + step + results.length) % results.length);
      } else if (event.key === 'Enter') {
        event.preventDefault();
        runSelected();
      }
    });
    dialog.addEventListener('mousedown', (event) => {
      if (event.target === dialog) dialog.close();
    });
    dialog.addEventListener('close', () => {
      dialog.remove();
      openPalette = null;
    });

    document.body.append(dialog);
    dialog.showModal();
    openPalette = dialog;
    renderResults();

    loadContentCommands().then((contentCommands) => {
      commands = [...BASE_COMMANDS, ...contentCommands];
      if (input.value.trim()) renderResults();
    });
  }

  Object.assign(MediaFlow, { toggleCommandPalette });
})();
