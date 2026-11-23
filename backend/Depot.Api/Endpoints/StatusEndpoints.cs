using Depot.Application.Status;

namespace Depot.Api.Endpoints;

public static class StatusEndpoints
{
    public static void MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        // Always 200: whether each service is configured and reachable, and why not.
        app.MapGet("/api/status", (StatusService service, CancellationToken cancellationToken) => service.GetAsync(cancellationToken));
    }
}
