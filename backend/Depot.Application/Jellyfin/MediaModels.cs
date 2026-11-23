namespace Depot.Application.Jellyfin;

public enum MediaKind
{
    Movie,
    Series,
}

public enum MediaSort
{
    Name,
    Added,
    Year,
    Rating,
}

public enum MediaImageType
{
    Primary,
    Backdrop,
    Thumb,
}

public sealed record MediaQuery(MediaKind Kind, string? Search, MediaSort Sort, int Page, int PageSize);

// What a poster card shows. Image tags are Jellyfin's versions of each image: a new image gets a
// new tag, so an image address with its tag never changes content.
public sealed record MediaSummary(
    string Id,
    MediaKind Kind,
    string Name,
    int? Year,
    double? Rating,
    string? OfficialRating,
    string? PrimaryImageTag);

public sealed record MediaPage(IReadOnlyList<MediaSummary> Items, int Total, int Page, int PageSize);

public sealed record MediaCounts(int Movies, int Series, int Episodes);

public sealed record MediaPerson(string Name, string? Role, string Type);

// One file of an item (a movie can have several versions) with its streams described.
public sealed record MediaFileInfo(
    string? Path,
    long? SizeBytes,
    string? Container,
    string? Video,
    IReadOnlyList<string> Audio,
    IReadOnlyList<string> Subtitles);

public sealed record MediaDetail(
    string Id,
    MediaKind Kind,
    string Name,
    string? OriginalTitle,
    int? Year,
    double? Rating,
    string? OfficialRating,
    string? Overview,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Studios,
    int? RuntimeMinutes,
    string? PrimaryImageTag,
    string? BackdropImageTag,
    IReadOnlyList<MediaPerson> People,
    IReadOnlyList<MediaFileInfo> Files,
    IReadOnlyDictionary<string, string> ProviderIds,
    IReadOnlyList<MediaSeason> Seasons);

public sealed record MediaSeason(string Id, string Name, int? Number, string? PrimaryImageTag, IReadOnlyList<MediaEpisode> Episodes);

public sealed record MediaEpisode(
    string Id,
    string Name,
    int? Season,
    int? Number,
    string? Overview,
    int? RuntimeMinutes,
    string? PrimaryImageTag,
    MediaFileInfo? File);

public sealed record MediaImage(string ContentType, byte[] Content);
