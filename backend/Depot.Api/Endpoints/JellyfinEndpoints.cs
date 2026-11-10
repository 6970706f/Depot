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

        // ?kind=movie|series|season|episode|collection&libraryId=&search=&genre=&year=&anime=true|false
        // &collectionId=&sort=name|added|year|release&page=&pageSize=
        group.MapGet("/items", (
            string? kind, int? libraryId, string? search, string? sort, int? page, int? pageSize,
            string? genre, int? year, bool? anime, int? collectionId,
            MediaCatalogService service, CancellationToken cancellationToken) =>
            service.ListItemsAsync(
                new MediaItemQueryInput(kind, libraryId, search, sort, page, pageSize, genre, year, anime, collectionId), cancellationToken));

        // The genres and years the filters can offer for ?kind= and ?anime=, with their counts.
        group.MapGet("/filters", (string? kind, bool? anime, MediaCatalogService service, CancellationToken cancellationToken) =>
            service.GetFiltersAsync(kind, anime, cancellationToken));

        // Totals for the library overview.
        group.MapGet("/stats", (MediaCatalogService service, CancellationToken cancellationToken) =>
            service.GetStatsAsync(cancellationToken));

        group.MapGet("/items/{id:int}", (int id, MediaCatalogService service, CancellationToken cancellationToken) =>
            service.GetItemAsync(id, cancellationToken));

        // kind is primary, backdrop, thumb or logo; ?index= picks a backdrop, ?width= a size and
        // ?v= is the image's version from the item, which makes the address safe to cache for good.
        group.MapGet("/items/{id:int}/images/{kind}", async (
            int id, string kind, int? index, int? width, string? v,
            HttpContext context, MediaImageService service, CancellationToken cancellationToken) =>
        {
            var result = await service.GetAsync(id, kind, index, width, cancellationToken);
            return ImageResult(context, result, v, $"{index ?? 0}-{MediaImageService.RoundWidth(width)?.ToString() ?? "full"}");
        });

        // The photo of someone in the item's cast or crew, by their index in its people.
        group.MapGet("/items/{id:int}/people/{index:int}/image", async (
            int id, int index, int? width, string? v,
            HttpContext context, MediaImageService service, CancellationToken cancellationToken) =>
        {
            var result = await service.GetPersonImageAsync(id, index, width, cancellationToken);
            return ImageResult(context, result, v, MediaImageService.RoundWidth(width)?.ToString() ?? "full");
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

    // Private: an image is only for whoever has the token, never for a shared cache. With its
    // version in the address (v), the address never changes content and can be kept for good.
    private static IResult ImageResult(HttpContext context, MediaImageResult result, string? version, string variant)
    {
        context.Response.Headers.CacheControl = version == result.Tag ? "private, max-age=31536000, immutable" : "private, no-cache";
        return Results.File(result.Image.Content, result.Image.ContentType, entityTag: new EntityTagHeaderValue($"\"{result.Tag}-{variant}\""));
    }
}
