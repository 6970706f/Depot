using Depot.Application.Jellyfin;
using Depot.Domain.Common;
using Depot.Domain.Media;

namespace Depot.Application.Media;

// Keeps images on disk (outside the database) by item, kind, version and width. The version
// (tag) changes when the image changes, so a cached image is never stale.
public interface IImageCache
{
    Task<MediaImageContent?> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, MediaImageContent image, CancellationToken cancellationToken);
}

// Tag is the version served, for the browser's cache.
public sealed record MediaImageResult(MediaImageContent Image, string Tag);

// Serves the posters, backdrops, thumbs and logos of the items in the local library. The
// browser only ever asks Depot: the image comes from Depot's cache or, the first time, from
// Jellyfin through the backend, which alone holds the API key.
public sealed class MediaImageService(IMediaCatalogRepository catalog, IJellyfinClient client, IImageCache cache)
{
    // The widths that are fetched and cached. A requested width is rounded up to one of them,
    // so arbitrary widths can't fill the cache with copies of the same image.
    public static readonly IReadOnlyList<int> Widths = [160, 320, 480, 720, 1080, 1920];

    public const int MaxBackdropIndex = 99;

    public async Task<MediaImageResult> GetAsync(int itemId, string kind, int? index, int? width, CancellationToken cancellationToken)
    {
        var imageKind = ParseKind(kind);
        var imageIndex = index ?? 0;
        if (imageIndex is < 0 or > MaxBackdropIndex)
        {
            throw new DomainValidationException("index", $"Use an index from 0 to {MaxBackdropIndex}.");
        }

        var maxWidth = RoundWidth(width);
        var entry = await catalog.GetItemAsync(itemId, includeFiles: false, cancellationToken) ?? throw new MediaItemNotFoundException(itemId);
        var tag = entry.Item.Images.TagFor(imageKind, imageIndex) ?? throw new ImageNotFoundException();

        var key = $"{entry.Item.ExternalId}-{imageKind}-{imageIndex}-{tag}-{maxWidth?.ToString() ?? "full"}";
        if (await cache.GetAsync(key, cancellationToken) is { } cached)
        {
            return new MediaImageResult(cached, tag);
        }

        var image = await client.GetImageAsync(entry.Item.ExternalId, imageKind, imageIndex, tag, maxWidth, cancellationToken)
            ?? throw new ImageNotFoundException();
        await cache.SetAsync(key, image, cancellationToken);
        return new MediaImageResult(image, tag);
    }

    // Null keeps the original size.
    public static int? RoundWidth(int? width)
    {
        if (width is null)
        {
            return null;
        }

        if (width is < 1 or > 10_000)
        {
            throw new DomainValidationException("width", "Use a width from 1 to 10000 pixels.");
        }

        return Widths.FirstOrDefault(candidate => candidate >= width, Widths[^1]);
    }

    private static ImageKind ParseKind(string kind) =>
        Enum.TryParse<ImageKind>(kind, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(kind, out _)
            ? parsed
            : throw new DomainValidationException("kind", "Use one of: primary, backdrop, thumb, logo.");
}
