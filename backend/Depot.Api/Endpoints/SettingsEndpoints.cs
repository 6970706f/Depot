using Depot.Application.Jellyfin;
using Depot.Application.Library;
using Depot.Application.Settings;

namespace Depot.Api.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var qBittorrent = app.MapGroup("/api/settings/qbittorrent");
        qBittorrent.MapGet("", (QBittorrentSettingsService service, CancellationToken cancellationToken) =>
            service.GetAsync(cancellationToken));
        qBittorrent.MapPut("", (QBittorrentSettingsInput input, QBittorrentSettingsService service, CancellationToken cancellationToken) =>
            service.SaveAsync(input, cancellationToken));
        qBittorrent.MapPost("/test", (QBittorrentSettingsInput input, QBittorrentSettingsService service, CancellationToken cancellationToken) =>
            service.TestAsync(input, cancellationToken));

        var jellyfin = app.MapGroup("/api/settings/jellyfin");
        jellyfin.MapGet("", (JellyfinSettingsService service, CancellationToken cancellationToken) =>
            service.GetAsync(cancellationToken));
        jellyfin.MapPut("", (JellyfinSettingsInput input, JellyfinSettingsService service, CancellationToken cancellationToken) =>
            service.SaveAsync(input, cancellationToken));
        jellyfin.MapPost("/test", (JellyfinSettingsInput input, JellyfinSettingsService service, CancellationToken cancellationToken) =>
            service.TestAsync(input, cancellationToken));

        // The folders, import mode and path mappings, with a check of each folder.
        var library = app.MapGroup("/api/settings/library");
        library.MapGet("", (LibrarySettingsService service, CancellationToken cancellationToken) =>
            service.GetAsync(cancellationToken));
        library.MapPut("", (LibrarySettingsInput input, LibrarySettingsService service, CancellationToken cancellationToken) =>
            service.SaveAsync(input, cancellationToken));
        library.MapPost("/check", (LibrarySettingsInput input, LibrarySettingsService service) => service.Check(input));
    }
}
