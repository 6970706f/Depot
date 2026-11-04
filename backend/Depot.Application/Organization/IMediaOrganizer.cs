using Depot.Domain.Organization;

namespace Depot.Application.Organization;

public enum FolderAccess
{
    Read,
    Write,
}

// Error holds the system's message when a check failed for an unexpected reason.
public sealed record FolderReport(
    string Path,
    bool Exists,
    bool CanRead,
    bool CanWrite,
    string? MountPoint,
    long? AvailableBytes,
    string? Error);

public sealed record ImportFile(string SourcePath, string DestinationPath, long SizeBytes);

// Everything the file system work needs, already resolved and validated by the caller; the
// organizer validates the paths again before touching anything. SourceRoot is the torrent's
// folder as Depot sees it: no source may lie outside it. DownloadsRoot is the mapped folder that
// holds it; no folder between the two may be a symbolic link.
public sealed record ImportRequest(
    int JobId,
    MediaType MediaType,
    ImportMode Mode,
    string LibraryRoot,
    string DownloadsRoot,
    string SourceRoot,
    ImportFile Video);

public enum ImportResult
{
    Completed,
    // The destination is taken; nothing was overwritten.
    Conflict,
    Failed,
}

// UsedMode is Hardlink or Copy for a completed import.
public sealed record ImportOutcome(ImportResult Result, string? Message, ImportMode? UsedMode)
{
    public static ImportOutcome Completed(ImportMode usedMode, string? message = null) => new(ImportResult.Completed, message, usedMode);

    public static ImportOutcome Conflict(string message) => new(ImportResult.Conflict, message, null);

    public static ImportOutcome Failed(string message) => new(ImportResult.Failed, message, null);
}

// An import that was interrupted. DestinationPath is the video's final path.
public sealed record RecoveryRequest(int JobId, string DestinationPath, long SizeBytes);

public enum RecoveryResult
{
    // The final file is in place.
    Completed,
    // Nothing final was written; Depot's temporary files were removed.
    NotStarted,
    Failed,
}

public sealed record RecoveryOutcome(RecoveryResult Result, string? Message);

// The file system side of organizing: checking and listing folders, importing and recovering.
// It never changes, renames or deletes source files, and never overwrites anything in the library.
public interface IMediaOrganizer
{
    // Checks a folder without changing it. For write access, a probe file is created and
    // removed again.
    Task<FolderReport> InspectFolderAsync(string path, FolderAccess access, CancellationToken cancellationToken);

    // The names of the visible folders directly inside path, sorted. Empty when path doesn't
    // exist or can't be read.
    Task<IReadOnlyList<string>> ListFoldersAsync(string path, CancellationToken cancellationToken);

    Task<ImportOutcome> ImportAsync(ImportRequest request, CancellationToken cancellationToken);

    // Settles an import interrupted by a crash or shutdown: removes Depot's temporary files
    // for the job and reports whether the final file is in place.
    Task<RecoveryOutcome> RecoverAsync(RecoveryRequest request, CancellationToken cancellationToken);
}
