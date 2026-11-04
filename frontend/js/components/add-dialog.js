(() => {
  'use strict';

  const { addDownload, cancelButton, getLibrarySeries, h, icon, openDialog, selectControl, setText, textField, toast } = MediaFlow;

  // Mirrors the backend's check so most mistakes are caught before sending. The backend
  // validates again and has the final word.
  const MAGNET_PATTERN = /^magnet:\?(?:[^#]*&)?xt=urn:(?:btih:(?:[a-f0-9]{40}|[a-z2-7]{32})|btmh:1220[a-f0-9]{64})(?:&|$)/i;
  // Same limit as the backend (TorrentFile.MaxSizeBytes).
  const MAX_TORRENT_FILE_BYTES = 10 * 1024 * 1024;
  // Same as LibraryTarget.MaxSeason.
  const MAX_SEASON = 999;

  // A folder name can't start with a dot, so this never clashes with a series in the library.
  const NEW_SERIES = '.new';

  const MEDIA_TYPES = [
    { value: 'movie', title: 'Movie', hint: 'The videos go into a folder of their own in your Movies library.' },
    { value: 'series', title: 'Series', hint: 'The videos go into a season of a series in your Shows library.' },
    { value: 'other', title: 'Other', hint: 'Only downloaded to the server. Depot leaves it where qBittorrent saves it.' },
  ];

  function validateSource(text, file) {
    if (file) {
      if (!file.name.toLowerCase().endsWith('.torrent')) return 'Choose a file that ends in .torrent.';
      if (file.size > MAX_TORRENT_FILE_BYTES) return 'This file is larger than 10 MB, which is too large for a .torrent file.';
      return null;
    }
    if (!text) return 'Enter a magnet link or choose a .torrent file.';
    if (!text.toLowerCase().startsWith('magnet:')) return 'Enter a link starting with magnet:?';
    return MAGNET_PATTERN.test(text) ? null : 'This magnet link is missing a valid info hash (xt=urn:btih:…).';
  }

  // Mirrors LibraryFolderName on the backend.
  function validateFolderName(text, description) {
    if (!text) return `Enter ${description}.`;
    if (text.length > 200) return 'Use at most 200 characters.';
    if (/[/\\]/.test(text)) return 'A folder name can\'t contain / or \\.';
    if (text.startsWith('.')) return 'A folder name can\'t start with a dot.';
    return null;
  }

  function validateSeason(text) {
    const season = Number(text);
    return text !== '' && Number.isInteger(season) && season >= 0 && season <= MAX_SEASON
      ? null
      : `Enter the season number, from 0 (specials) to ${MAX_SEASON}.`;
  }

  function describeSeasons(seasons) {
    if (!seasons.length) return 'This series has no season folders yet. 0 is for specials.';
    const list = seasons.map((season) => (season === 0 ? 'specials' : String(season))).join(', ');
    return `Already in your library: season${seasons.length === 1 ? '' : 's'} ${list}. 0 is for specials.`;
  }

  function mediaTypeChoice({ value, title, hint }, onChange) {
    return h('label', { class: 'choice' },
      h('input', { type: 'radio', name: 'media-type', value, onChange }),
      h('span', { class: 'choice-title', textContent: title }),
      h('span', { class: 'choice-hint', textContent: hint }),
    );
  }

  // First what the download is (movie, series or other), then where it goes in the library,
  // then the magnet link or .torrent file. Only the videos are filed into the library, under
  // their own file names.
  function openAddDialog() {
    let file = null;
    let submitting = false;
    let mediaType = null;
    // null while loading; the series folders in the library once loaded.
    let librarySeries = null;
    let seriesLoadFailed = false;

    const typeError = h('p', { class: 'field-error', hidden: true });
    const typeList = h('fieldset', { class: 'choice-list' },
      h('legend', { class: 'sr-only', textContent: 'What are you downloading?' }),
      MEDIA_TYPES.map((type) => mediaTypeChoice(type, () => chooseType(type.value))),
    );
    const typeField = h('div', { class: 'field' },
      h('p', { class: 'field-label', textContent: 'What are you downloading?' }),
      typeList, typeError,
    );

    // Movie
    const movieTitle = textField({
      label: 'Movie name', placeholder: 'Dune (2021)',
      hint: 'The folder the movie gets in your library. Adding the year helps Jellyfin find the right one.',
    });
    const movieSection = h('div', { class: 'add-section', hidden: true }, movieTitle.el);

    // Series
    const seriesSelect = selectControl({ label: 'Series', options: [{ value: '', label: 'Loading your series…' }], onChange: onSeriesChange });
    const seriesInput = seriesSelect.querySelector('select');
    seriesInput.id = 'add-series';
    const seriesHint = h('p', { class: 'field-hint' });
    const seriesError = h('p', { class: 'field-error', hidden: true });
    const seriesField = h('div', { class: 'field' },
      h('label', { class: 'field-label', for: 'add-series', textContent: 'Series' }), seriesSelect, seriesHint, seriesError);
    const newSeries = textField({
      label: 'New series name', placeholder: 'The Last of Us',
      hint: 'Creates this folder in your Shows library.',
    });
    newSeries.el.hidden = true;
    const season = textField({ label: 'Season', type: 'number', value: '1', hint: '0 is for specials.' });
    Object.assign(season.input, { min: '0', max: String(MAX_SEASON), step: '1', inputMode: 'numeric' });
    season.input.classList.add('input-season');
    const seriesSection = h('div', { class: 'add-section', hidden: true }, seriesField, newSeries.el, season.el);

    // Source
    const source = textField({
      label: 'Magnet link',
      placeholder: 'magnet:?xt=urn:btih:…',
      name: 'source',
    });
    const fileInput = h('input', { type: 'file', accept: '.torrent,application/x-bittorrent', hidden: true, 'aria-label': 'Torrent file' });
    const fileName = h('span', { class: 'file-pick-name truncate', textContent: 'No file chosen' });
    const filePick = h('div', { class: 'file-pick' },
      h('button', { class: 'btn', type: 'button', onClick: () => fileInput.click() }, icon('file'), 'Choose .torrent file'),
      fileName,
      fileInput,
    );

    // Source is either text or a file. Using one clears the other.
    source.input.addEventListener('input', () => {
      source.setError(null);
      if (!file) return;
      file = null;
      fileInput.value = '';
      setText(fileName, 'No file chosen');
    });
    fileInput.addEventListener('change', () => {
      file = fileInput.files[0] ?? null;
      setText(fileName, file ? file.name : 'No file chosen');
      if (file) source.input.value = '';
      source.setError(null);
    });

    const formError = h('p', { class: 'dialog-error', role: 'alert', hidden: true });
    const submit = h('button', { class: 'btn btn-primary', type: 'submit', textContent: 'Add download' });

    const handle = openDialog({
      title: 'Add download',
      body: [typeField, movieSection, seriesSection, source.el, filePick, formError],
      footer: [cancelButton(() => handle.close()), submit],
      initialFocus: typeList.querySelector('input'),
      onSubmit,
    });

    loadSeries();

    function chooseType(value) {
      mediaType = value;
      typeError.hidden = true;
      movieSection.hidden = value !== 'movie';
      seriesSection.hidden = value !== 'series';
    }

    async function loadSeries() {
      try {
        librarySeries = await getLibrarySeries();
      } catch (error) {
        librarySeries = [];
        seriesLoadFailed = true;
        setText(seriesHint, `Couldn't read the series in your library: ${error.message} You can still add a new one.`);
      }
      const options = [
        ...(librarySeries.length ? [{ value: '', label: 'Choose a series' }] : []),
        ...librarySeries.map((series) => ({ value: series.name, label: series.name })),
        { value: NEW_SERIES, label: 'New series…' },
      ];
      seriesInput.replaceChildren(...options.map((option) => h('option', { value: option.value, textContent: option.label })));
      seriesInput.value = librarySeries.length ? '' : NEW_SERIES;
      onSeriesChange();
    }

    function chosenSeries() {
      return librarySeries?.find((series) => series.name === seriesInput.value) ?? null;
    }

    function onSeriesChange() {
      seriesError.hidden = true;
      seriesInput.setAttribute('aria-invalid', 'false');
      newSeries.el.hidden = seriesInput.value !== NEW_SERIES;
      const existing = chosenSeries();
      if (existing) {
        // Most downloads add episodes to the latest season.
        if (existing.seasons.length) season.input.value = String(existing.seasons.at(-1));
        season.setHint(describeSeasons(existing.seasons));
      } else {
        season.setHint('0 is for specials.');
      }
      if (librarySeries && !seriesLoadFailed) {
        setText(seriesHint, librarySeries.length
          ? 'The series already in your Shows library, or a new one.'
          : 'Your Shows library has no series yet. Name the first one below.');
      }
    }

    function showSeriesError(message) {
      seriesError.textContent = message;
      seriesError.hidden = false;
      seriesInput.setAttribute('aria-invalid', 'true');
    }

    // The library part of the request, or null with the first problem marked on its field.
    function readLibrary() {
      movieTitle.setError(null);
      newSeries.setError(null);
      season.setError(null);
      seriesError.hidden = true;

      if (!mediaType) {
        typeError.textContent = 'Choose what you are downloading.';
        typeError.hidden = false;
        typeList.querySelector('input').focus();
        return null;
      }
      if (mediaType === 'other') return { mediaType };

      if (mediaType === 'movie') {
        const title = movieTitle.input.value.trim();
        const problem = validateFolderName(title, 'the movie\'s name');
        movieTitle.setError(problem);
        if (problem) {
          movieTitle.input.focus();
          return null;
        }
        return { mediaType, title };
      }

      const isNew = seriesInput.value === NEW_SERIES;
      const title = isNew ? newSeries.input.value.trim() : seriesInput.value;
      if (!isNew && !title) {
        showSeriesError(librarySeries === null ? 'Your series are still loading.' : 'Choose a series, or New series….');
        seriesInput.focus();
        return null;
      }
      const titleProblem = isNew ? validateFolderName(title, 'the series\' name') : null;
      newSeries.setError(titleProblem);
      if (titleProblem) {
        newSeries.input.focus();
        return null;
      }
      const seasonText = season.input.value.trim();
      const seasonProblem = validateSeason(seasonText);
      season.setError(seasonProblem);
      if (seasonProblem) {
        season.input.focus();
        return null;
      }
      return { mediaType, title, season: Number(seasonText) };
    }

    // Shows a backend validation error next to its field. Returns whether one was shown.
    function showFieldError(error) {
      if (error.field === 'source') {
        source.setError(error.message);
        source.input.focus();
      } else if (error.field === 'title' && mediaType === 'movie') {
        movieTitle.setError(error.message);
        movieTitle.input.focus();
      } else if (error.field === 'title' && seriesInput.value === NEW_SERIES) {
        newSeries.setError(error.message);
        newSeries.input.focus();
      } else if (error.field === 'title') {
        showSeriesError(error.message);
        seriesInput.focus();
      } else if (error.field === 'season') {
        season.setError(error.message);
        season.input.focus();
      } else {
        return false;
      }
      return true;
    }

    async function onSubmit() {
      if (submitting) return;
      formError.hidden = true;
      const library = readLibrary();
      if (!library) return;

      const text = source.input.value.trim();
      const problem = validateSource(text, file);
      source.setError(problem);
      if (problem) {
        source.input.focus();
        return;
      }

      submitting = true;
      submit.setAttribute('aria-busy', 'true');
      setText(submit, 'Adding…');
      try {
        const added = await addDownload({ magnet: text, file, ...library });
        handle.close();
        toast(`Sent to qBittorrent: ${added.name}`);
      } catch (error) {
        if (!showFieldError(error)) {
          formError.textContent = error.message;
          formError.hidden = false;
        }
      } finally {
        submitting = false;
        submit.removeAttribute('aria-busy');
        setText(submit, 'Add download');
      }
    }
  }

  Object.assign(MediaFlow, { validateSource, openAddDialog });
})();
