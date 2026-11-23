using Depot.Application.Jellyfin;

namespace Depot.Api.Endpoints;

public static class MediaEndpoints
{
    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        // The Jellyfin library, read live.
        var group = app.MapGroup("/api/media");

        // ?kind=movie|series&search=&sort=name|added|year|rating&page=1&pageSize=48
        group.MapGet("", (string? kind, string? search, string? sort, int? page, int? pageSize, MediaLibraryService service, CancellationToken cancellationToken) =>
            service.ListAsync(
                QueryValues.Enum<MediaKind>(kind, "kind") ?? MediaKind.Movie,
                search,
                QueryValues.Enum<MediaSort>(sort, "sort") ?? MediaSort.Name,
                page ?? 1,
                pageSize ?? 48,
                cancellationToken));

        group.MapGet("/counts", (MediaLibraryService service, CancellationToken cancellationToken) => service.CountAsync(cancellationToken));

        group.MapGet("/{id}", (string id, MediaLibraryService service, CancellationToken cancellationToken) => service.GetAsync(id, cancellationToken));

        // Images come through Depot so the API key never reaches the browser. An address with the
        // image's version (?tag=) never changes content, so the browser may keep it for good.
        group.MapGet("/{id}/images/{type}", async (
            string id, string type, int? width, string? tag, HttpContext context, MediaLibraryService service, CancellationToken cancellationToken) =>
        {
            var imageType = QueryValues.Enum<MediaImageType>(type, "type") ?? MediaImageType.Primary;
            var image = await service.GetImageAsync(id, imageType, width, tag, cancellationToken);
            context.Response.Headers.CacheControl = tag is null ? "private, no-cache" : "private, max-age=31536000, immutable";
            return Results.File(image.Content, image.ContentType);
        });
    }
}
