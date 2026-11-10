using Depot.Domain.Media;
using Depot.Domain.Settings;

namespace Depot.Application.Jellyfin;

// Reads the Jellyfin server configured in Settings. Every method throws JellyfinException on
// failure, and JellyfinFailure.NotConfigured while nothing is saved.
public interface IJellyfinClient
{
    Task<JellyfinServerInfo> GetServerInfoAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken cancellationToken);

    // One page of a library's items of the given kinds, with their metadata, images and files.
    Task<JellyfinItemPage> GetItemsAsync(
        string libraryId,
        IReadOnlyCollection<MediaItemKind> kinds,
        int startIndex,
        int limit,
        CancellationToken cancellationToken);

    // The items among these ids that the server still has; the others are left out.
    Task<IReadOnlyList<MediaItem>> GetItemsByIdAsync(IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken);

    // Null when the server has no such image. maxWidth null asks for the original size.
    Task<MediaImageContent?> GetImageAsync(
        string itemId,
        ImageKind kind,
        int index,
        string tag,
        int? maxWidth,
        CancellationToken cancellationToken);
}

public interface IJellyfinConnectionTester
{
    // Checks that the URL leads to a Jellyfin server that accepts the API key, with settings
    // that may not be saved yet. Throws JellyfinException when it can't be used.
    Task<JellyfinServerInfo> TestAsync(JellyfinSettings settings, CancellationToken cancellationToken);
}

public sealed record JellyfinServerInfo(string ServerName, string Version);

// Received counts every entry of the page, Skipped the ones that couldn't be read, so the next
// page starts at StartIndex + Received. TotalCount is the library's size as the server
// reported it with this page.
public sealed record JellyfinItemPage(IReadOnlyList<MediaItem> Items, int Received, int Skipped, int TotalCount);

// An image as it is served to the browser: one of the formats Depot accepts.
public sealed record MediaImageContent(string ContentType, byte[] Content);
