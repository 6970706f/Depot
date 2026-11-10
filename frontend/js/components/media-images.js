(() => {
  'use strict';

  const { loadJellyfinImage, loadJellyfinPersonImage } = MediaFlow;

  // Images being fetched at once; the browser would queue the rest anyway, behind the API calls.
  const MAX_PARALLEL = 6;

  // Loads a page's images through Depot: only once they come near the screen, a few at a time,
  // and each address once. Images come as blob: URLs (an <img> can't send the token), and every
  // URL made for the page is revoked when the page goes away (signal).
  function createImageLoader(signal) {
    const urls = new Map();
    const waiting = new Map();
    const queue = [];
    let active = 0;

    const observer = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        observer.unobserve(entry.target);
        const start = waiting.get(entry.target);
        waiting.delete(entry.target);
        start?.();
      }
    }, { rootMargin: '600px 0px' });

    signal.addEventListener('abort', () => {
      observer.disconnect();
      waiting.clear();
      queue.length = 0;
      for (const url of urls.values()) url.then((made) => made && URL.revokeObjectURL(made));
      urls.clear();
    });

    function pump() {
      while (active < MAX_PARALLEL && queue.length) {
        const run = queue.shift();
        active++;
        run().finally(() => {
          active--;
          pump();
        });
      }
    }

    // Depot's rate limit can refuse a burst of images (a long grid scrolled fast): those are tried
    // again after a pause instead of being shown as missing.
    async function attempt(fetch) {
      for (let tries = 1; ; tries++) {
        try {
          return await fetch();
        } catch (error) {
          if (error.code !== 'rate_limited' || tries === 4 || signal.aborted) throw error;
          await new Promise((resolve) => setTimeout(resolve, 1000 * tries));
        }
      }
    }

    // A failed image is shown as missing rather than as an error: the page around it still works.
    // It isn't remembered, so showing it again later tries again.
    function load(key, fetch) {
      if (!urls.has(key)) {
        urls.set(key, new Promise((resolve) => {
          queue.push(() => attempt(fetch).catch(() => {
            urls.delete(key);
            return null;
          }).then(resolve));
          pump();
        }));
      }
      return urls.get(key);
    }

    // Puts the image in img once it's near the screen; onMissing runs when there's none.
    function show(img, key, fetch, onMissing) {
      img.dataset.state = 'loading';
      waiting.set(img, async () => {
        const url = await load(key, fetch);
        if (signal.aborted) return;
        if (!url) {
          img.dataset.state = 'missing';
          onMissing?.();
          return;
        }
        img.addEventListener('load', () => { img.dataset.state = 'loaded'; }, { once: true });
        img.src = url;
      });
      observer.observe(img);
    }

    return {
      // kind is 'primary', 'backdrop', 'thumb' or 'logo', as in loadJellyfinImage.
      item(img, item, kind, { width, index = 0, onMissing } = {}) {
        const version = kind === 'backdrop' ? item.images.backdrops[index] : item.images[kind];
        if (!version) {
          img.dataset.state = 'missing';
          onMissing?.();
          return;
        }
        show(img, `${item.id}/${kind}/${index}/${version}/${width}`, () => loadJellyfinImage(item, kind, { width, index }), onMissing);
      },

      person(img, itemId, person, { width, onMissing } = {}) {
        if (!person.imageVersion) {
          img.dataset.state = 'missing';
          onMissing?.();
          return;
        }
        show(img, `${itemId}/person/${person.index}/${person.imageVersion}/${width}`, () => loadJellyfinPersonImage(itemId, person, { width }), onMissing);
      },
    };
  }

  Object.assign(MediaFlow, { createImageLoader });
})();
