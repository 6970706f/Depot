using Depot.Domain.Library;

namespace Depot.Application.Imports;

public interface IImportJobRepository
{
    // Records the jobs of a finished download and marks its library choice as examined, together,
    // so a download is never examined twice nor lost between the two.
    Task AddScanAsync(string torrentHash, IReadOnlyList<ImportJob> jobs, DateTimeOffset now, CancellationToken cancellationToken);

    // Newest first.
    Task<IReadOnlyList<ImportJob>> ListAsync(ImportJobStatus? status, CancellationToken cancellationToken);

    Task<ImportJob?> GetAsync(int id, CancellationToken cancellationToken);

    // Saves the changes made to a job read from this repository.
    Task SaveAsync(ImportJob job, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<ImportJobStatus, int>> CountAsync(CancellationToken cancellationToken);
}

// One file to place: an absolute source inside the download and an absolute destination inside
// the library.
public sealed record ImportFile(string SourcePath, string DestinationPath, long SizeBytes);

// DownloadsRoot is the mapped folder the source must stay inside; LibraryRoot the library the
// destination must stay inside.
public sealed record ImportRequest(
    int JobId,
    string DownloadsRoot,
    string LibraryRoot,
    ImportFile Video,
    IReadOnlyList<ImportFile> Subtitles,
    ImportMode Mode);

public sealed record ImportOutcome(bool Succeeded, ImportMode? UsedMode, string? Message)
{
    public static ImportOutcome Completed(ImportMode usedMode, string? note) => new(true, usedMode, note);

    public static ImportOutcome Failed(string message) => new(false, null, message);
}

// Puts files in the library without touching the downloaded ones: sources are only read or
// hardlinked, and nothing in the library is ever overwritten.
public interface IMediaImporter
{
    Task<ImportOutcome> ImportAsync(ImportRequest request, CancellationToken cancellationToken);

    // Cleans up after an import that was interrupted (Depot stopped). True when the video is at
    // the destination, which a person must then check.
    bool CleanUpInterrupted(int jobId, string destinationPath);
}

// Told after videos reach the library, such as Jellyfin, which then scans for them.
public interface IImportListener
{
    Task ImportedAsync(CancellationToken cancellationToken);
}
