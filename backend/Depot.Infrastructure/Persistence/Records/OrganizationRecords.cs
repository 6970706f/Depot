using Depot.Domain.Organization;

namespace Depot.Infrastructure.Persistence.Records;

// The organization settings are a single row; the path mappings are kept in order beside it.
public sealed class OrganizationSettingsRecord
{
    public const int SingletonId = 1;

    public int Id { get; set; }

    public required string MoviesPath { get; set; }

    public required string SeriesPath { get; set; }

    public ImportMode ImportMode { get; set; }

    public int PollingIntervalSeconds { get; set; }
}

public sealed class OrganizationPathMappingRecord
{
    public int Id { get; set; }

    public int Position { get; set; }

    public required string QbittorrentPath { get; set; }

    public required string DepotPath { get; set; }
}

// The library folder chosen for a download when it was added. ScannedAt is set once the
// finished torrent was examined, so it isn't examined again on every pass.
public sealed class LibraryTargetRecord
{
    public required string Hash { get; set; }

    public MediaType MediaType { get; set; }

    public required string Title { get; set; }

    public int? Season { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ScannedAt { get; set; }

    public int VideoCount { get; set; }
}

public sealed class OrganizationJobRecord
{
    public int Id { get; set; }

    public required string TorrentHash { get; set; }

    public required string TorrentName { get; set; }

    public required string RelativePath { get; set; }

    public long SizeBytes { get; set; }

    public MediaType MediaType { get; set; }

    public required string Title { get; set; }

    public int? Season { get; set; }

    // Where the video goes inside the target: Kind, and PlacementSeason for an episode or
    // ExtrasFolder for an extra.
    public VideoKind Kind { get; set; }

    public int? PlacementSeason { get; set; }

    public string? ExtrasFolder { get; set; }

    public OrganizationJobStatus Status { get; set; }

    public string? Message { get; set; }

    public string? DestinationPath { get; set; }

    public long? ExpectedFileId { get; set; }

    public ImportMode? ImportedWith { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    // Optimistic concurrency: an update succeeds only if nobody saved the job in between.
    public int Version { get; set; }
}
