using Depot.Domain.Media;

namespace Depot.Application.Media;

// The local copy of the media server's library: its libraries, items and their files. Images
// aren't kept here, only their versions (MediaImageTags).
public interface IMediaCatalogRepository
{
    // Saves the libraries the server reports and deletes the ones it no longer has, with their
    // items. Returns each library's local id by its external id, and how many items went with
    // the deleted libraries.
    Task<SavedLibraries> SaveLibrariesAsync(IReadOnlyList<MediaLibrary> libraries, CancellationToken cancellationToken);

    // Adds the new items and updates the ones that changed, files included, all or nothing. An
    // item kept under another library moves to this one. Items are matched by external id, so
    // the same item is never kept twice.
    Task<SavedItems> SaveItemsAsync(int libraryId, IReadOnlyList<MediaItem> items, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListExternalIdsAsync(int libraryId, CancellationToken cancellationToken);

    // Returns how many were deleted.
    Task<int> RemoveItemsAsync(IReadOnlyCollection<string> externalIds, CancellationToken cancellationToken);

    Task MarkLibrarySyncedAsync(int libraryId, DateTimeOffset syncedAt, CancellationToken cancellationToken);

    Task<IReadOnlyList<CatalogLibrary>> ListLibrariesAsync(CancellationToken cancellationToken);

    Task<CatalogPage> ListItemsAsync(CatalogQuery query, CancellationToken cancellationToken);

    // The item's files are read only when includeFiles.
    Task<CatalogItem?> GetItemAsync(int id, bool includeFiles, CancellationToken cancellationToken);

    Task<CatalogItem?> FindByExternalIdAsync(string externalId, CancellationToken cancellationToken);
}

public sealed record SavedLibraries(IReadOnlyDictionary<string, int> IdsByExternalId, int RemovedItems);

public sealed record SavedItems(int Added, int Updated);

// LastSyncedAt is the end of the last sync that read the whole library.
public sealed record CatalogLibrary(int Id, MediaLibrary Library, IReadOnlyDictionary<MediaItemKind, int> Counts, DateTimeOffset? LastSyncedAt);

// An item as kept locally. FileCount is set even when the files themselves weren't read.
// UpdatedAt is when its metadata last changed.
public sealed record CatalogItem(int Id, int LibraryId, MediaItem Item, int FileCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public enum CatalogSort
{
    // By sort name, A to Z.
    Name,
    // Newest in the library first.
    Added,
    // Newest release first.
    Year,
    // Season, then episode number.
    Number,
}

// Every filter is optional. Search matches the name or original title.
public sealed record CatalogQuery(
    IReadOnlyCollection<MediaItemKind> Kinds,
    int? LibraryId = null,
    string? SeriesExternalId = null,
    string? SeasonExternalId = null,
    string? Search = null,
    CatalogSort Sort = CatalogSort.Name,
    int Skip = 0,
    int Take = 50);

public sealed record CatalogPage(IReadOnlyList<CatalogItem> Items, int Total);
