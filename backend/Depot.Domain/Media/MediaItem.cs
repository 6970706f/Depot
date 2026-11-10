namespace Depot.Domain.Media;

public enum MediaItemKind
{
    Movie,
    Series,
    Season,
    Episode,
    // A collection (box set) grouping movies or series, such as a film saga.
    Collection,
}

public enum ImageKind
{
    // The poster.
    Primary,
    Backdrop,
    Thumb,
    Logo,
}

// The version of each image the source has for an item. A new version (tag) means the image
// changed, so anything cached under the old one is stale.
public sealed record MediaImageTags(string? Primary, string? Thumb, string? Logo, IReadOnlyList<string> Backdrops)
{
    public static readonly MediaImageTags None = new(null, null, null, []);

    // Null when the item has no such image. Only backdrops come in more than one.
    public string? TagFor(ImageKind kind, int index) => kind switch
    {
        ImageKind.Backdrop => index >= 0 && index < Backdrops.Count ? Backdrops[index] : null,
        _ when index != 0 => null,
        ImageKind.Primary => Primary,
        ImageKind.Thumb => Thumb,
        ImageKind.Logo => Logo,
        _ => null,
    };
}

// A movie, series, season or episode with the metadata the source (Jellyfin) has for it.
// ExternalId is the source's id; seasons and episodes point to their series and season by it.
public sealed record MediaItem
{
    public required string ExternalId { get; init; }

    public required MediaItemKind Kind { get; init; }

    public required string Name { get; init; }

    public string? OriginalTitle { get; init; }

    // The name to sort by, such as "dune" for "Dune" or "matrix" for "The Matrix".
    public string? SortName { get; init; }

    public string? Overview { get; init; }

    public int? ProductionYear { get; init; }

    public DateOnly? PremiereDate { get; init; }

    // When a series ended.
    public DateOnly? EndDate { get; init; }

    public string? OfficialRating { get; init; }

    // From 0 to 10.
    public double? CommunityRating { get; init; }

    public TimeSpan? Runtime { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    public IReadOnlyList<string> Studios { get; init; } = [];

    // Ids in metadata providers, such as { "Imdb": "tt1160419", "Tmdb": "438631" }.
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();

    // For a series, such as "Continuing" or "Ended".
    public string? SeriesStatus { get; init; }

    public string? SeriesExternalId { get; init; }

    public string? SeasonExternalId { get; init; }

    // The season number of a season, or the episode number of an episode.
    public int? IndexNumber { get; init; }

    // The last episode of a file holding several (S01E01-E02).
    public int? IndexNumberEnd { get; init; }

    // The season number of an episode.
    public int? ParentIndexNumber { get; init; }

    // Where the source keeps it, as the source's machine sees it: a folder for a series or season.
    public string? Path { get; init; }

    // When the source added it to its library.
    public DateTimeOffset? AddedAt { get; init; }

    public MediaImageTags Images { get; init; } = MediaImageTags.None;

    // Movies and episodes can have several files (versions such as 1080p and 720p); series and
    // seasons have none.
    public IReadOnlyList<MediaFile> Files { get; init; } = [];

    // Directors, writers and cast, in the source's order.
    public IReadOnlyList<MediaPerson> People { get; init; } = [];

    // Keywords, such as "space opera" or "anime".
    public IReadOnlyList<string> Tags { get; init; } = [];

    // Countries of production, such as "Japan".
    public IReadOnlyList<string> ProductionLocations { get; init; } = [];

    // For a collection, the external ids of the movies and series in it, in the source's order.
    public IReadOnlyList<string> MemberExternalIds { get; init; } = [];

    // Set by the sync from MediaCategories.IsAnime: the source has no such type.
    public bool IsAnime { get; init; }
}
