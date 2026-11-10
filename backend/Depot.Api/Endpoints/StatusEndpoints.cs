using Depot.Application.Status;

namespace Depot.Api.Endpoints;

public static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder routes)
    {
        // Always 200: a disconnected service is a status to report, not a failed request.
        // Both services are asked at once, so two slow ones cost one timeout, not two.
        routes.MapGet("/api/status", async (SystemStatusService service, CancellationToken cancellationToken) =>
        {
            var jellyfin = service.GetJellyfinStatusAsync(cancellationToken);
            var qbittorrent = await service.GetQBittorrentStatusAsync(cancellationToken);
            return new { qbittorrent, jellyfin = await jellyfin };
        });

        return routes;
    }
}
