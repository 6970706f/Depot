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

// SeasonNumber is a season's own number or an episode's season; EpisodeNumber is an episode's.
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
    MediaImagesView Images);

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

public sealed record MediaItemReferenceView(int Id, string Name);

// Series and Season point up (for a season or an episode); Children point down: the seasons of
// a series or the episodes of a season.
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
    IReadOnlyDictionary<string, string> ProviderIds,
    string? SeriesStatus,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? EpisodeNumberEnd,
    string? Path,
    DateTimeOffset? AddedAt,
    DateTimeOffset UpdatedAt,
    MediaImagesView Images,
    IReadOnlyList<MediaFileView> Files,
    MediaItemReferenceView? Series,
    MediaItemReferenceView? Season,
    IReadOnlyList<MediaItemSummaryView> Children);

// Kind is "movie", "series", "season" or "episode"; without it, movies and series. Sort is
// "name" (default), "added" or "year". Page starts at 1.
public sealed record MediaItemQueryInput(string? Kind, int? LibraryId, string? Search, string? Sort, int? Page, int? PageSize);

public sealed class MediaItemNotFoundException(int id) : Exception($"There's no media item {id} in Depot's copy of the library.");

public sealed class ImageNotFoundException() : Exception("This item has no such image.");
