import { h } from '../dom.js';
import { qbittorrentSettingsPanel } from '../components/qbittorrent-settings.js';
import { librarySettingsPanel } from '../components/library-settings.js';
import { jellyfinSettingsPanel } from '../components/jellyfin-settings.js';

// No refresh: the forms would lose what is being typed.
export default {
  title: 'Settings',
  mount(view) {
    view.append(h('div', { class: 'page' }, qbittorrentSettingsPanel(), jellyfinSettingsPanel(), librarySettingsPanel()));
    return {};
  },
};
