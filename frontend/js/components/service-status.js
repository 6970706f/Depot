import { h, clear } from '../dom.js';

// The dots in the top bar. Hovering shows why a service isn't connected.
export function renderServices(container, status) {
  clear(container);
  for (const [name, service] of Object.entries(status ?? {})) {
    if (!service) continue;
    const label = name === 'qBittorrent' ? 'qBittorrent' : 'Jellyfin';
    const state = !service.configured ? 'not set up' : service.connected ? (service.version ?? 'connected') : 'not connected';
    const tone = !service.configured ? '' : service.connected ? 'on' : 'off';
    container.append(h('a', { class: 'service', href: '#/settings', title: service.error ?? `${label} ${state}` },
      h('span', { class: `dot ${tone}` }), `${label}`));
  }
}