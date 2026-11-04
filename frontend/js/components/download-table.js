(() => {
  'use strict';

  const { EMPTY, formatBytes, formatEta, formatPercent, formatSpeed, h, icon, keyedList, runDownloadAction, setIcon, setText } = MediaFlow;

  const STATUS = {
    downloading: { label: 'Downloading', icon: 'arrow-down', rank: 0 },
    checking: { label: 'Checking', icon: 'retry', rank: 0 },
    queued: { label: 'Queued', icon: 'list', rank: 0 },
    error: { label: 'Error', icon: 'alert', rank: 1 },
    paused: { label: 'Paused', icon: 'pause', rank: 2 },
    completed: { label: 'Completed', icon: 'check', rank: 3 },
  };

  const PRIMARY_ACTION = {
    downloading: { kind: 'pause', label: 'Pause', icon: 'pause' },
    checking: { kind: 'pause', label: 'Pause', icon: 'pause' },
    queued: { kind: 'pause', label: 'Pause', icon: 'pause' },
    paused: { kind: 'resume', label: 'Resume', icon: 'play' },
    error: { kind: 'retry', label: 'Retry', icon: 'retry' },
  };

  const COLUMNS = {
    name: { label: 'Name', className: 'col-name' },
    progress: { label: 'Progress', className: 'col-progress num align-end' },
    down: { label: 'Down', className: 'col-down num align-end' },
    up: { label: 'Up', className: 'col-up num align-end' },
    eta: { label: 'ETA', className: 'col-eta num align-end' },
    size: { label: 'Size', className: 'col-size num align-end' },
    status: { label: 'Status', className: 'col-status' },
    actions: { label: 'Actions', className: 'col-actions', visuallyHidden: true, unsortable: true },
  };

  const COMPARATORS = {
    added: (a, b) => a.addedAt.localeCompare(b.addedAt),
    name: (a, b) => a.name.localeCompare(b.name),
    progress: (a, b) => a.progress - b.progress,
    down: (a, b) => a.downSpeed - b.downSpeed,
    up: (a, b) => a.upSpeed - b.upSpeed,
    eta: (a, b) => (a.etaSeconds ?? Infinity) - (b.etaSeconds ?? Infinity),
    size: (a, b) => a.size - b.size,
    status: (a, b) => STATUS[a.status].rank - STATUS[b.status].rank,
  };

  function sortDownloads(items, { key, direction }) {
    const sign = direction === 'asc' ? 1 : -1;
    return [...items].sort((a, b) => sign * COMPARATORS[key](a, b) || a.name.localeCompare(b.name));
  }

  function createHeader(columnKeys, sort, onSort) {
    const cells = columnKeys.map((key) => {
      const column = COLUMNS[key];
      const th = h('th', { class: column.className, role: 'columnheader', scope: 'col' });
      if (column.visuallyHidden) {
        th.append(h('span', { class: 'sr-only', textContent: column.label }));
      } else if (onSort && !column.unsortable) {
        const arrow = icon('arrow-up');
        th.append(h('button', { class: 'sort-btn', type: 'button', onClick: () => onSort(key) }, column.label, arrow));
        return { key, th, arrow };
      } else {
        th.textContent = column.label;
      }
      return { key, th };
    });

    const update = (current) => {
      for (const { key, th, arrow } of cells) {
        if (!arrow) continue;
        const active = current?.key === key;
        if (active) th.setAttribute('aria-sort', current.direction === 'asc' ? 'ascending' : 'descending');
        else th.removeAttribute('aria-sort');
        setIcon(arrow, active && current.direction === 'desc' ? 'arrow-down' : 'arrow-up');
      }
    };
    update(sort);

    return {
      el: h('thead', { role: 'rowgroup' }, h('tr', { role: 'row' }, cells.map(({ th }) => th))),
      update,
    };
  }

  // Where the videos go once the download finishes; null for a download only kept on the server.
  function describeLibrary(library) {
    if (!library) return null;
    if (library.mediaType === 'movie') return `Movie · ${library.title}`;
    return `${library.title} · ${library.season === 0 ? 'Specials' : `S${library.season}`}`;
  }

  function setCellValue(cell, text) {
    setText(cell, text);
    const isEmpty = text === EMPTY;
    cell.classList.toggle('cell-dim', isEmpty);
    cell.classList.toggle('cell-empty', isEmpty);
  }

  function createRow(columnKeys) {
    const name = h('span', { class: 'cell-name' });
    const note = h('span', { class: 'cell-note', hidden: true });
    const library = h('span', { class: 'cell-meta', hidden: true });
    const percent = h('span');
    const bar = h('progress', { class: 'row-progress', max: 1 });
    const statusIcon = icon('pause');
    const statusLabel = h('span');
    const status = h('span', { class: 'status' }, statusIcon, statusLabel);

    const textCell = (key) => h('td', { class: COLUMNS[key].className, role: 'cell' });
    const down = textCell('down');
    const up = textCell('up');
    const eta = textCell('eta');
    const size = textCell('size');

    let current;
    const busy = new Set();

    async function run(kind, button) {
      if (busy.has(kind)) return;
      busy.add(kind);
      button.setAttribute('aria-busy', 'true');
      await runDownloadAction(kind, current);
      busy.delete(kind);
      button.removeAttribute('aria-busy');
    }

    // A single primary button changes role with status, so keyboard focus survives
    // Pause becoming Resume.
    const primaryIcon = icon('pause');
    const primary = h('button', {
      class: 'icon-btn', type: 'button',
      onClick: () => run(PRIMARY_ACTION[current.status].kind, primary),
    }, primaryIcon);
    const remove = h('button', {
      class: 'icon-btn icon-btn-danger', type: 'button', title: 'Remove',
      onClick: () => run('remove', remove),
    }, icon('trash'));

    const cells = {
      name: h('td', { class: COLUMNS.name.className, role: 'cell' }, name, library, note),
      progress: h('td', { class: COLUMNS.progress.className, role: 'cell' }, percent, bar),
      down, up, eta, size,
      status: h('td', { class: COLUMNS.status.className, role: 'cell' }, status),
      actions: h('td', { class: COLUMNS.actions.className, role: 'cell' },
        h('div', { class: 'row-actions' }, primary, remove)),
    };

    const el = h('tr', { role: 'row' }, columnKeys.map((key) => cells[key]));

    function update(item) {
      current = item;
      el.dataset.status = item.status;

      setText(name, item.name);
      name.title = item.name;
      note.hidden = !item.error;
      note.title = item.error ?? '';
      setText(note, item.error ?? '');

      const target = describeLibrary(item.library);
      library.hidden = !target;
      library.title = target ? `Goes to your library as ${target} once finished` : '';
      setText(library, target ?? '');

      setText(percent, formatPercent(item.progress));
      bar.value = item.progress;
      bar.setAttribute('aria-label', `Progress of ${item.name}`);

      setCellValue(down, formatSpeed(item.downSpeed));
      setCellValue(up, formatSpeed(item.upSpeed));
      setCellValue(eta, formatEta(item.etaSeconds));
      setText(size, formatBytes(item.size));

      const statusInfo = STATUS[item.status];
      status.dataset.status = item.status;
      setText(statusLabel, statusInfo.label);
      setIcon(statusIcon, statusInfo.icon);

      const action = PRIMARY_ACTION[item.status];
      primary.hidden = !action;
      if (action) {
        primary.title = action.label;
        primary.setAttribute('aria-label', `${action.label} ${item.name}`);
        setIcon(primaryIcon, action.icon);
      }
      remove.setAttribute('aria-label', `Remove ${item.name}`);
    }

    return { el, update };
  }

  // columnKeys picks which columns a table shows. Pass onSort for sortable headers.
  function createDownloadTable({ label, columnKeys, sort, onSort }) {
    const header = createHeader(columnKeys, sort, onSort);
    const body = h('tbody', { role: 'rowgroup' });
    const renderRows = keyedList(body, (item) => item.id, () => createRow(columnKeys));
    const el = h('div', { class: 'table-wrap' },
      h('table', { class: 'data-table', role: 'table', 'aria-label': label }, header.el, body));

    return { el, render: renderRows, updateSort: header.update };
  }

  Object.assign(MediaFlow, { STATUS, sortDownloads, createDownloadTable });
})();
