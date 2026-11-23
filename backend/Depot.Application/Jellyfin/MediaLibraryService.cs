using System.Text.RegularExpressions;
using Depot.Domain.Common;

namespace Depot.Application.Jellyfin;

// The Jellyfin library, read live. Checks what the browser sends before anything reaches
// Jellyfin: ids, paging, search length and image sizes.
public sealed partial class MediaLibraryService(IJellyfinClient jellyfin)
{
    public const int MaxPageSize = 100;
    public const int MaxSearchLength = 100;

    // Widths are rounded up to one of these, so the browser can't ask Jellyfin for every size.
    private static readonly int[] ImageWidths = [160, 320, 480, 720, 1080, 1920];

    public Task<MediaPage> ListAsync(MediaKind kind, string? search, MediaSort sort, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1)
        {
            throw new DomainValidationException("page", "The page starts at 1.");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            throw new DomainValidationException("pageSize", $"Ask for 1 to {MaxPageSize} items per page.");
        }

        var text = search?.Trim();
        if (text?.Length > MaxSearchLength)
        {
            throw new DomainValidationException("search", $"Search with at most {MaxSearchLength} characters.");
        }

        return jellyfin.GetItemsAsync(new MediaQuery(kind, string.IsNullOrEmpty(text) ? null : text, sort, page, pageSize), cancellationToken);
    }

    public Task<MediaCounts> CountAsync(CancellationToken cancellationToken) => jellyfin.GetCountsAsync(cancellationToken);

    public async Task<MediaDetail> GetAsync(string id, CancellationToken cancellationToken) =>
        await jellyfin.GetItemAsync(NormalizeId(id), cancellationToken) ?? throw ItemNotFound();

    public async Task<MediaImage> GetImageAsync(string id, MediaImageType type, int? width, string? tag, CancellationToken cancellationToken)
    {
        if (tag is not null && !TagPattern().IsMatch(tag))
        {
            throw new DomainValidationException("tag", "This isn't an image version.");
        }

        var size = ImageWidths.FirstOrDefault(candidate => candidate >= (width ?? 480), ImageWidths[^1]);
        return await jellyfin.GetImageAsync(NormalizeId(id), type, size, tag, cancellationToken)
            ?? throw new JellyfinException(JellyfinFailure.ItemNotFound, "This image doesn't exist in Jellyfin.");
    }

    // Jellyfin ids are GUIDs, written with or without dashes.
    private static string NormalizeId(string id) =>
        Guid.TryParse(id, out var guid) ? guid.ToString("N") : throw ItemNotFound();

    private static JellyfinException ItemNotFound() =>
        new(JellyfinFailure.ItemNotFound, "This item isn't in the Jellyfin library anymore.");

    [GeneratedRegex("^[0-9a-fA-F]{8,64}$")]
    private static partial Regex TagPattern();
}
