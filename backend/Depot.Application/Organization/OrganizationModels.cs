using Depot.Domain.Organization;

namespace Depot.Application.Organization;

public sealed record PathMappingInput(string? QbittorrentPath, string? DepotPath);

public sealed record OrganizationSettingsInput(
    string? MoviesPath,
    string? SeriesPath,
    ImportMode ImportMode,
    int PollingIntervalSeconds,
    IReadOnlyList<PathMappingInput>? PathMappings);

public sealed record PathMappingView(string QbittorrentPath, string DepotPath);

// Role is "movies", "series" or "downloads" (a path mapping's folder). Problem says, in plain
// words, why the folder can't be used; null when it's fine.
public sealed record FolderCheckView(
    string Role,
    string Label,
    string ConfiguredPath,
    string ResolvedPath,
    bool Exists,
    bool CanRead,
    bool CanWrite,
    string? MountPoint,
    long? AvailableBytes,
    string? Problem);

public sealed record OrganizationDiagnosticsView(
    bool CanOrganize,
    IReadOnlyList<FolderCheckView> Folders,
    IReadOnlyList<string> Notes);

public sealed record OrganizationSettingsView(
    string MoviesPath,
    string SeriesPath,
    ImportMode ImportMode,
    int PollingIntervalSeconds,
    IReadOnlyList<PathMappingView> PathMappings,
    bool IsSaved,
    OrganizationDiagnosticsView Diagnostics);

// The library choice of a download. Season is null for a movie.
public sealed record LibraryTargetView(MediaType MediaType, string Title, int? Season)
{
    public static LibraryTargetView From(LibraryTarget target) => new(target.Type, target.Title, target.Season);
}

// Where a video goes inside its movie or series folder. Kind is "main" (the movie or an
// episode; season 0 for a special) or "extra" (with ExtrasFolder).
public sealed record VideoPlacementView(VideoKind Kind, int? Season, string? ExtrasFolder)
{
    public static VideoPlacementView From(VideoPlacement placement) => new(placement.Kind, placement.Season, placement.ExtrasFolder);
}

// A person's correction of where a series video goes; see VideoPlacement.ForSeries.
public sealed record VideoPlacementInput(string? Kind, int? Season, string? ExtrasFolder);

// DestinationPath is the final path once an import started (DestinationIsFinal), otherwise
// where the video would go with the current settings and folders.
public sealed record OrganizationJobView(
    int Id,
    string TorrentHash,
    string TorrentName,
    string RelativePath,
    long SizeBytes,
    LibraryTargetView Target,
    VideoPlacementView Placement,
    OrganizationJobStatus Status,
    string? Message,
    string? DestinationPath,
    bool DestinationIsFinal,
    ImportMode? ImportedWith,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    bool CanRetry,
    bool CanSkip,
    bool CanClassify);

// A series folder already in the library, with the seasons that have a folder in it.
public sealed record LibrarySeriesView(string Name, IReadOnlyList<int> Seasons);

// State is "starting", "idle", "running", "paused" or "error". Counts has one entry per job status.
public sealed record OrganizationStatusView(
    string State,
    string? Message,
    DateTimeOffset? LastRunAt,
    IReadOnlyDictionary<string, int> Counts);
