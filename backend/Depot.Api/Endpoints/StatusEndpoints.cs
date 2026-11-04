using Depot.Application.Status;

namespace Depot.Api.Endpoints;

public static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder routes)
    {
        // Always 200: a disconnected service is a status to report, not a failed request.
        routes.MapGet("/api/status", async (SystemStatusService service, CancellationToken cancellationToken) =>
            new { qbittorrent = await service.GetQBittorrentStatusAsync(cancellationToken) });

        return routes;
    }
}
