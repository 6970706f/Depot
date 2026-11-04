using Depot.Api.Security;
using Depot.Application.Settings;

namespace Depot.Api.Endpoints;

public static class QBittorrentSettingsEndpoints
{
    public static IEndpointRouteBuilder MapQBittorrentSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/settings/qbittorrent");

        group.MapGet("/", (QBittorrentSettingsService service, CancellationToken cancellationToken) =>
            service.GetAsync(cancellationToken));

        group.MapPut("/", (QBittorrentSettingsInput input, QBittorrentSettingsService service, CancellationToken cancellationToken) =>
            service.SaveAsync(input, cancellationToken))
            .RequireRateLimiting(RateLimits.Expensive);

        group.MapPost("/test", (QBittorrentSettingsInput input, QBittorrentSettingsService service, CancellationToken cancellationToken) =>
            service.TestConnectionAsync(input, cancellationToken))
            .RequireRateLimiting(RateLimits.Expensive);

        return routes;
    }
}
