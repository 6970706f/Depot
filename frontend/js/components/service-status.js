(() => {
  'use strict';

  // Turns one service from GET /api/status into what the interface shows. Shared by the
  // status bar, the sidebar and the overview so they always agree.
  // state: 'ok' (connected), 'down' (needs attention) or 'off' (an optional service that
  // isn't set up, which isn't a problem).
  // service is null when the Depot API itself couldn't be reached.
  function describeService(name, service, { optional = false } = {}) {
    if (!service) {
      return { name, state: 'down', label: 'Unknown', detail: `Can't check ${name} while the Depot API is unavailable.` };
    }
    if (service.connected) {
      return { name, state: 'ok', label: 'Connected', detail: `Connected to ${name} ${service.version}` };
    }
    if (!service.configured) {
      return { name, state: optional ? 'off' : 'down', label: 'Not set up', detail: service.error };
    }
    return { name, state: 'down', label: 'Disconnected', detail: service.error };
  }

  // Every service Depot works with, in display order.
  function describeServices(status) {
    return [
      describeService('qBittorrent', status?.qbittorrent ?? null),
      describeService('Jellyfin', status?.jellyfin ?? null, { optional: true }),
    ];
  }

  Object.assign(MediaFlow, { describeService, describeServices });
})();
