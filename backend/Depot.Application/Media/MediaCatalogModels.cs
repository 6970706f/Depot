using Depot.Domain.Media;

namespace Depot.Application.Media;

// The version of each image an item has; null (or no backdrops) when it has none. The browser
// asks for an image with its version, so a new version is a new address and never a stale copy.
public sealed record MediaImagesView(string? Primary, string? Thumb, string? Logo, IReadOnlyList<string> Backdrops)
{
    public static MediaImagesView From(MediaImageTags tags) => new(tags.Primary, tags.Thumb, tags.Logo, tags.Backdrops);
}

public sealed record MediaLibraryView(
    int Id,
    string ExternalId,
    string Name,
    MediaLibraryKind Kind,
    IReadOnlyList<string> Locations,
    bool IsSynced,
    int Movies,
    int Series,
    int Seasons,
    int Episodes,
    DateTimeOffset? LastSyncedAt);

// An item for a poster to stand in for a collection without a poster of its own.
public sealed record MediaPreviewView(int Id, MediaImagesView Images);

// SeasonNumber is a season's own number or an episode's season; EpisodeNumber is an episode's.
// EpisodeCount counts a series' or season's episodes, MemberCount a collection's movies and
// series. Overview is only filled in for the seasons and episodes listed with an item, and
// Preview only for collections.
public sealed record MediaItemSummaryView(
    int Id,
    int LibraryId,
    string ExternalId,
    MediaItemKind Kind,
    string Name,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? EpisodeNumberEnd,
    DateTimeOffset? AddedAt,
    int FileCount,
    MediaImagesView Images,
    IReadOnlyList<string> Genres,
    double? CommunityRating,
    string? OfficialRating,
    double? RuntimeSeconds,
    bool IsAnime,
    int EpisodeCount,
    int MemberCount,
    string? Overview,
    IReadOnlyList<MediaPreviewView>? Preview);

public sealed record MediaItemPageView(IReadOnlyList<MediaItemSummaryView> Items, int Total, int Page, int PageSize);

public sealed record MediaStreamView(
    MediaStreamKind Kind,
    int Index,
    string? Codec,
    string? Language,
    string? Title,
    bool IsDefault,
    bool IsForced,
    bool IsExternal,
    int? Width,
    int? Height,
    string? VideoRange,
    int? Channels,
    long? Bitrate);

public sealed record MediaFileView(
    string ExternalId,
    string? Name,
    string? Path,
    string? Container,
    long? SizeBytes,
    long? Bitrate,
    double? RuntimeSeconds,
    IReadOnlyList<MediaStreamView> Streams);

// Index is the person's place in the item's list, which addresses their photo
// (/items/{id}/people/{index}/image); ImageVersion is null when there's no photo.
public sealed record MediaPersonView(int Index, string Name, PersonKind Kind, string? Role, string? ImageVersion);

public sealed record MediaItemReferenceView(int Id, string Name);

// Series and Season point up (for a season or an episode); Children point down: the seasons of
// a series, the episodes of a season or the movies and series of a collection. Collections
// are the collections the item is in.
public sealed record MediaItemDetailView(
    int Id,
    int LibraryId,
    string ExternalId,
    MediaItemKind Kind,
    string Name,
    string? OriginalTitle,
    string? Overview,
    int? Year,
    DateOnly? PremiereDate,
    DateOnly? EndDate,
    string? OfficialRating,
    double? CommunityRating,
    double? RuntimeSeconds,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Studios,
    IReadOnlyList<string> ProductionLocations,
    IReadOnlyDictionary<string, string> ProviderIds,
    string? SeriesStatus,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? EpisodeNumberEnd,
    bool IsAnime,
    int EpisodeCount,
    string? Path,
    DateTimeOffset? AddedAt,
    DateTimeOffset UpdatedAt,
    MediaImagesView Images,
    IReadOnlyList<MediaPersonView> People,
    IReadOnlyList<MediaFileView> Files,
    MediaItemReferenceView? Series,
    MediaItemReferenceView? Season,
    IReadOnlyList<MediaItemReferenceView> Collections,
    IReadOnlyList<MediaItemSummaryView> Children);

// Kind is "movie", "series", "season", "episode" or "collection"; without it, movies and
// series. Sort is "name" (default), "added", "year" or "release". Anime true keeps only anime,
// false leaves it out. CollectionId lists a collection's members. Page starts at 1.
public sealed record MediaItemQueryInput(
    string? Kind,
    int? LibraryId,
    string? Search,
    string? Sort,
    int? Page,
    int? PageSize,
    string? Genre = null,
    int? Year = null,
    bool? Anime = null,
    int? CollectionId = null);

public sealed record MediaCountView<T>(T Value, int Count);

// What the genre and year filters can choose from, most common genre first and newest year first.
public sealed record MediaFiltersView(IReadOnlyList<MediaCountView<string>> Genres, IReadOnlyList<MediaCountView<int>> Years);

// Movies and Series include their anime; TopGenres counts movies and series.
public sealed record MediaStatsView(
    int Movies,
    int Series,
    int Seasons,
    int Episodes,
    int Collections,
    int AnimeMovies,
    int AnimeSeries,
    int Files,
    long SizeBytes,
    IReadOnlyList<MediaCountView<string>> TopGenres,
    DateTimeOffset? LastSyncedAt);

public sealed class MediaItemNotFoundException(int id) : Exception($"There's no media item {id} in Depot's copy of the library.");

public sealed class ImageNotFoundException() : Exception("This item has no such image.");
