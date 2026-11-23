import { api } from '../api.js';
import { h } from '../dom.js';

// Images come from Depot's API with the token, so an <img> can't load them directly: they're
// fetched and shown as blob: URLs. Each address is fetched once per page load, and only when the
// image scrolls into view.
const cache = new Map();

const observer = 'IntersectionObserver' in window
  ? new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      observer.unobserve(entry.target);
      load(entry.target);
    }
  }, { rootMargin: '400px' })
  : null;

function load(img) {
  const { id, type, width, tag } = img.dataset;
  const key = `${id}/${type}/${width}/${tag ?? ''}`;
  if (!cache.has(key)) {
    cache.set(key, api.mediaImage(id, type, width, tag).catch(() => {
      cache.delete(key);
      return null;
    }));
  }
  cache.get(key).then((url) => {
    if (url) {
      img.src = url;
      img.classList.add('loaded');
    }
  });
}

// A placeholder with the title until the picture arrives; nothing at all when there's no tag.
export function mediaImage({ id, type = 'primary', width = 320, tag, alt = '', className = 'poster' }) {
  const frame = h('div', { class: `${className} media-frame` }, h('span', { class: 'media-fallback', text: alt }));
  if (!tag) return frame;
  const img = h('img', { alt, dataset: { id, type, width, tag }, decoding: 'async' });
  frame.append(img);
  if (observer) observer.observe(img);
  else load(img);
  return frame;
}
