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

    Task<IReadOnlyList<CatalogItem>> FindByExternalIdsAsync(IReadOnlyCollection<string> externalIds, CancellationToken cancellationToken);

    // The collections an item is in.
    Task<IReadOnlyList<CatalogItem>> ListCollectionsContainingAsync(string externalId, CancellationToken cancellationToken);

    // The genres and years of the items a filter would choose from, with how many items have each.
    Task<CatalogFacets> GetFacetsAsync(IReadOnlyCollection<MediaItemKind> kinds, bool? anime, CancellationToken cancellationToken);

    Task<CatalogStats> GetStatsAsync(CancellationToken cancellationToken);
}

public sealed record SavedLibraries(IReadOnlyDictionary<string, int> IdsByExternalId, int RemovedItems);

public sealed record SavedItems(int Added, int Updated);

// LastSyncedAt is the end of the last sync that read the whole library.
public sealed record CatalogLibrary(int Id, MediaLibrary Library, IReadOnlyDictionary<MediaItemKind, int> Counts, DateTimeOffset? LastSyncedAt);

// An item as kept locally. FileCount is set even when the files themselves weren't read;
// EpisodeCount is the episodes kept for a series or season. UpdatedAt is when its metadata last changed.
public sealed record CatalogItem(int Id, int LibraryId, MediaItem Item, int FileCount, int EpisodeCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CatalogCount<T>(T Value, int Count);

// Most common first.
public sealed record CatalogFacets(IReadOnlyList<CatalogCount<string>> Genres, IReadOnlyList<CatalogCount<int>> Years);

// AnimeMovies and AnimeSeries are also counted in Counts. LastSyncedAt is the latest library read completely.
public sealed record CatalogStats(
    IReadOnlyDictionary<MediaItemKind, int> Counts,
    int AnimeMovies,
    int AnimeSeries,
    int Files,
    long SizeBytes,
    DateTimeOffset? LastSyncedAt);

public enum CatalogSort
{
    // By sort name, A to Z.
    Name,
    // Newest in the library first.
    Added,
    // Newest release year first.
    Year,
    // Newest premiere date first.
    Release,
    // Season, then episode number.
    Number,
}

// Every filter is optional. Search matches the name or original title; Genre one genre exactly;
// ExternalIds limits the items to these, such as a collection's members.
public sealed record CatalogQuery(
    IReadOnlyCollection<MediaItemKind> Kinds,
    int? LibraryId = null,
    string? SeriesExternalId = null,
    string? SeasonExternalId = null,
    string? Search = null,
    string? Genre = null,
    int? Year = null,
    bool? Anime = null,
    IReadOnlyCollection<string>? ExternalIds = null,
    CatalogSort Sort = CatalogSort.Name,
    int Skip = 0,
    int Take = 50);

public sealed record CatalogPage(IReadOnlyList<CatalogItem> Items, int Total);
