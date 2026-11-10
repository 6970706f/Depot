using Depot.Api.Security;
using Depot.Application.Jellyfin;
using Depot.Application.Media;
using Depot.Application.Status;
using Depot.Domain.Media;
using Microsoft.Net.Http.Headers;

namespace Depot.Api.Endpoints;

public static class JellyfinEndpoints
{
    public static IEndpointRouteBuilder MapJellyfinEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/jellyfin");

        // Always 200, like /api/status: a disconnected server is a status, not a failed request.
        group.MapGet("/status", (SystemStatusService service, CancellationToken cancellationToken) =>
            service.GetJellyfinStatusAsync(cancellationToken));

        // The rest reads Depot's copy of the library, as of the last sync.
        group.MapGet("/libraries", (MediaCatalogService service, CancellationToken cancellationToken) =>
            service.ListLibrariesAsync(cancellationToken));

        // ?kind=movie|series|season|episode&libraryId=&search=&sort=name|added|year&page=&pageSize=
        group.MapGet("/items", (
            string? kind, int? libraryId, string? search, string? sort, int? page, int? pageSize,
            MediaCatalogService service, CancellationToken cancellationToken) =>
            service.ListItemsAsync(new MediaItemQueryInput(kind, libraryId, search, sort, page, pageSize), cancellationToken));

        group.MapGet("/items/{id:int}", (int id, MediaCatalogService service, CancellationToken cancellationToken) =>
            service.GetItemAsync(id, cancellationToken));

        // kind is primary, backdrop, thumb or logo; ?index= picks a backdrop, ?width= a size and
        // ?v= is the image's version from the item, which makes the address safe to cache for good.
        group.MapGet("/items/{id:int}/images/{kind}", async (
            int id, string kind, int? index, int? width, string? v,
            HttpContext context, MediaImageService service, CancellationToken cancellationToken) =>
        {
            var result = await service.GetAsync(id, kind, index, width, cancellationToken);
            // Private: the image is only for whoever has the token, never for a shared cache.
            context.Response.Headers.CacheControl = v == result.Tag ? "private, max-age=31536000, immutable" : "private, no-cache";
            var entityTag = new EntityTagHeaderValue($"\"{result.Tag}-{index ?? 0}-{MediaImageService.RoundWidth(width)?.ToString() ?? "full"}\"");
            return Results.File(result.Image.Content, result.Image.ContentType, entityTag: entityTag);
        });

        // 202 with the queued run; 409 sync_running while another one is queued or running.
        group.MapPost("/sync", async (JellyfinSyncService service, CancellationToken cancellationToken) =>
        {
            var run = await service.StartAsync(SyncTrigger.Manual, cancellationToken);
            return Results.Accepted($"/api/jellyfin/sync/runs/{run.Id}", run);
        })
            .RequireRateLimiting(RateLimits.Expensive);

        // { isRunning, latestRun }: the run going on, or the last one.
        group.MapGet("/sync", (JellyfinSyncService service, CancellationToken cancellationToken) =>
            service.GetStatusAsync(cancellationToken));

        group.MapGet("/sync/runs", (JellyfinSyncService service, CancellationToken cancellationToken) =>
            service.ListAsync(cancellationToken));

        group.MapGet("/sync/runs/{id:int}", (int id, JellyfinSyncService service, CancellationToken cancellationToken) =>
            service.GetAsync(id, cancellationToken));

        return routes;
    }
}
