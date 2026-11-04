namespace Depot.Domain.Organization;

// Bringing one video of a finished torrent into the library folder a person chose when adding
// the download. A torrent with several videos has one job per video.
public sealed class OrganizationJob
{
    private OrganizationJob(
        int id,
        string torrentHash,
        string torrentName,
        string relativePath,
        long sizeBytes,
        LibraryTarget target,
        DateTimeOffset createdAt,
        int version)
    {
        Id = id;
        TorrentHash = torrentHash;
        TorrentName = torrentName;
        RelativePath = relativePath;
        SizeBytes = sizeBytes;
        Target = target;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Version = version;
    }

    // 0 until the job is saved.
    public int Id { get; }

    public string TorrentHash { get; }

    public string TorrentName { get; }

    // The video's path inside the torrent, as the torrent client lists it.
    public string RelativePath { get; }

    public long SizeBytes { get; }

    // Where the video goes, as chosen when the download was added.
    public LibraryTarget Target { get; }

    public OrganizationJobStatus Status { get; private set; }

    // Why the job failed or waits, or what happened on recovery. Null when all is well.
    public string? Message { get; private set; }

    // The video's final path, set once an import starts.
    public string? DestinationPath { get; private set; }

    // Hardlink or Copy, once imported.
    public ImportMode? ImportedWith { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    // Kept by the repository to detect concurrent changes.
    public int Version { get; }

    public bool CanRetry => Status is OrganizationJobStatus.Failed or OrganizationJobStatus.Skipped;

    public bool CanSkip => Status is OrganizationJobStatus.Discovered or OrganizationJobStatus.Failed;

    public bool IsReadyToImport => Status == OrganizationJobStatus.Discovered;

    public static OrganizationJob Discover(
        string torrentHash,
        string torrentName,
        string relativePath,
        long sizeBytes,
        LibraryTarget target,
        DateTimeOffset now) =>
        new(0, torrentHash, torrentName, relativePath, sizeBytes, target, now, 0) { Status = OrganizationJobStatus.Discovered };

    // Rebuilds a stored job without re-running any rule.
    public static OrganizationJob Restore(
        int id,
        string torrentHash,
        string torrentName,
        string relativePath,
        long sizeBytes,
        LibraryTarget target,
        OrganizationJobStatus status,
        string? message,
        string? destinationPath,
        ImportMode? importedWith,
        int attempts,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? completedAt,
        int version) =>
        new(id, torrentHash, torrentName, relativePath, sizeBytes, target, createdAt, version)
        {
            Status = status,
            Message = message,
            DestinationPath = destinationPath,
            ImportedWith = importedWith,
            Attempts = attempts,
            UpdatedAt = updatedAt,
            CompletedAt = completedAt,
        };

    public void StartProcessing(string destinationPath, DateTimeOffset now)
    {
        Require(Status == OrganizationJobStatus.Discovered, "imported");
        Status = OrganizationJobStatus.Processing;
        DestinationPath = destinationPath;
        Message = null;
        Attempts++;
        UpdatedAt = now;
    }

    // note explains anything unusual, such as an import confirmed after a restart.
    public void Complete(ImportMode? importedWith, string? note, DateTimeOffset now)
    {
        Require(Status == OrganizationJobStatus.Processing, "completed");
        Status = OrganizationJobStatus.Completed;
        ImportedWith = importedWith;
        Message = note;
        CompletedAt = now;
        UpdatedAt = now;
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        Require(Status is OrganizationJobStatus.Processing or OrganizationJobStatus.Discovered, "marked as failed");
        Status = OrganizationJobStatus.Failed;
        Message = reason;
        UpdatedAt = now;
    }

    // Keeps the job waiting for something outside it, such as a missing path mapping. It's
    // tried again on the next pass without counting as a failure.
    public void Defer(string reason, DateTimeOffset now)
    {
        Require(Status == OrganizationJobStatus.Discovered, "postponed");
        Message = reason;
        UpdatedAt = now;
    }

    // Puts an interrupted import back in the queue; nothing of it is assumed to be done.
    public void ReturnToQueue(string? reason, DateTimeOffset now)
    {
        Require(Status == OrganizationJobStatus.Processing, "returned to the queue");
        Status = OrganizationJobStatus.Discovered;
        DestinationPath = null;
        Message = reason;
        UpdatedAt = now;
    }

    // Tries a failed or skipped job again on the next pass.
    public void Retry(DateTimeOffset now)
    {
        Require(CanRetry, "retried");
        Status = OrganizationJobStatus.Discovered;
        Message = null;
        DestinationPath = null;
        UpdatedAt = now;
    }

    public void Skip(DateTimeOffset now)
    {
        Require(CanSkip, "skipped");
        Status = OrganizationJobStatus.Skipped;
        Message = null;
        UpdatedAt = now;
    }

    private void Require(bool allowed, string action)
    {
        if (!allowed)
        {
            throw new InvalidJobStateException($"This job is {Describe(Status)}, so it can't be {action}.");
        }
    }

    private static string Describe(OrganizationJobStatus status) => status switch
    {
        OrganizationJobStatus.Discovered => "waiting to be imported",
        OrganizationJobStatus.Processing => "being imported",
        OrganizationJobStatus.Completed => "already imported",
        OrganizationJobStatus.Failed => "failed",
        _ => "skipped",
    };
}
