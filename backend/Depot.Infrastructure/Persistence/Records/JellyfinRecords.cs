using Depot.Domain.Media;

namespace Depot.Infrastructure.Persistence.Records;

// How the Jellyfin settings are stored: a single row, with the API key encrypted.
public sealed class JellyfinSettingsRecord
{
    public const int SingletonId = 1;

    public int Id { get; set; }

    public required string Url { get; set; }

    public required string ProtectedApiKey { get; set; }
}

// A library of the media server. Lists (Locations here, and the ones on items and files) are
// JSON text: they're always read and written whole, never queried.
public sealed class MediaLibraryRecord
{
    public int Id { get; set; }

    public required string ExternalId { get; set; }

    public required string Name { get; set; }

    public MediaLibraryKind Kind { get; set; }

    public required string Locations { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }

    public List<MediaItemRecord> Items { get; set; } = [];
}

// A movie, series, season or episode. Images aren't stored, only their versions (tags); the
// files live in MediaFiles, as many as the item has.
public sealed class MediaItemRecord
{
    public int Id { get; set; }

    public int LibraryId { get; set; }

    public required string ExternalId { get; set; }

    public MediaItemKind Kind { get; set; }

    public required string Name { get; set; }

    public string? OriginalTitle { get; set; }

    // The source's sort name, or the name: what lists are sorted by.
    public required string SortName { get; set; }

    public string? Overview { get; set; }

    public int? ProductionYear { get; set; }

    public DateOnly? PremiereDate { get; set; }

    public DateOnly? EndDate { get; set; }

    public string? OfficialRating { get; set; }

    public double? CommunityRating { get; set; }

    public long? RuntimeTicks { get; set; }

    public required string Genres { get; set; }

    public required string Studios { get; set; }

    public required string ProviderIds { get; set; }

    public string? SeriesStatus { get; set; }

    public string? SeriesExternalId { get; set; }

    public string? SeasonExternalId { get; set; }

    public int? IndexNumber { get; set; }

    public int? IndexNumberEnd { get; set; }

    public int? ParentIndexNumber { get; set; }

    public string? Path { get; set; }

    public DateTimeOffset? AddedAt { get; set; }

    public string? PrimaryImageTag { get; set; }

    public string? ThumbImageTag { get; set; }

    public string? LogoImageTag { get; set; }

    public required string BackdropImageTags { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // When the metadata last changed, not when it was last seen.
    public DateTimeOffset UpdatedAt { get; set; }

    public MediaLibraryRecord? Library { get; set; }

    public List<MediaFileRecord> Files { get; set; } = [];
}

public sealed class MediaFileRecord
{
    public int Id { get; set; }

    public int MediaItemId { get; set; }

    public required string ExternalId { get; set; }

    // The order the source lists the files in; the first is the default version.
    public int Position { get; set; }

    public string? Name { get; set; }

    public string? Path { get; set; }

    public string? Container { get; set; }

    public long? SizeBytes { get; set; }

    public long? Bitrate { get; set; }

    public long? RuntimeTicks { get; set; }

    public required string Streams { get; set; }
}

public sealed class LibrarySyncRunRecord
{
    public int Id { get; set; }

    public SyncTrigger Trigger { get; set; }

    public SyncRunStatus Status { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string? Message { get; set; }

    public string? CurrentLibrary { get; set; }

    public int LibrariesTotal { get; set; }

    public int LibrariesCompleted { get; set; }

    public int LibrariesFailed { get; set; }

    public int ItemsSeen { get; set; }

    public int ItemsAdded { get; set; }

    public int ItemsUpdated { get; set; }

    public int ItemsRemoved { get; set; }

    public int ItemsSkipped { get; set; }

    public required string Errors { get; set; }
}
