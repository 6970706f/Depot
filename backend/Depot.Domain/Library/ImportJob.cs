namespace Depot.Domain.Library;

public enum ImportJobStatus
{
    // Found in a finished download and waiting to be imported.
    Waiting,
    Importing,
    Completed,
    Failed,
    // A person decided this video stays out of the library. Nothing on disk is changed.
    Skipped,
}

// The job's state doesn't allow the action, such as skipping a finished import.
public sealed class InvalidJobStateException(string message) : Exception(message);

// One video of a finished download on its way to the library. Paths are kept as qBittorrent
// lists them (relative to the torrent's save path) and as the library sees them (relative to the
// library root), so the job stays valid if the configured roots move.
public sealed class ImportJob
{
    public ImportJob(
        string torrentHash,
        string torrentName,
        string savePath,
        LibraryTarget target,
        PlannedImport plan,
        DateTimeOffset now)
    {
        TorrentHash = torrentHash.ToLowerInvariant();
        TorrentName = torrentName;
        SavePath = savePath;
        MediaType = target.Type;
        Title = target.Title;
        Season = target.Season;
        SourcePath = plan.Video.RelativePath;
        SizeBytes = plan.Video.SizeBytes;
        Folder = plan.Folder;
        Subtitles = plan.Subtitles.ToList();
        Status = ImportJobStatus.Waiting;
        CreatedAt = UpdatedAt = now;
    }

    // For EF Core.
    private ImportJob()
    {
        TorrentHash = TorrentName = SavePath = Title = SourcePath = Folder = string.Empty;
        Subtitles = [];
    }

    public int Id { get; private set; }

    public string TorrentHash { get; private set; }

    public string TorrentName { get; private set; }

    public MediaType MediaType { get; private set; }

    public string Title { get; private set; }

    public int? Season { get; private set; }

    // The video, relative to the torrent's save path.
    public string SourcePath { get; private set; }

    // The torrent's save path as qBittorrent reported it when the job was created.
    public string SavePath { get; private set; }

    public long SizeBytes { get; private set; }

    // Where it goes, relative to the library root: "<Title>" or "<Title>/<season folder>".
    public string Folder { get; private set; }

    public List<PlannedFile> Subtitles { get; private set; }

    public ImportJobStatus Status { get; private set; }

    // Why it failed, or what happened (subtitles imported or left out).
    public string? Message { get; private set; }

    // Hardlink or Copy, once imported.
    public ImportMode? UsedMode { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string FileName => MediaFiles.FileName(SourcePath);

    // The destination relative to the library root, "/"-separated.
    public string Destination => $"{Folder}/{FileName}";

    public void Start(DateTimeOffset now)
    {
        if (Status != ImportJobStatus.Waiting)
        {
            throw new InvalidJobStateException($"Only a waiting job can start; this one is {Status}.");
        }

        Status = ImportJobStatus.Importing;
        Attempts++;
        Message = null;
        UpdatedAt = now;
    }

    public void Complete(ImportMode usedMode, string? message, DateTimeOffset now)
    {
        RequireImporting();
        Status = ImportJobStatus.Completed;
        UsedMode = usedMode;
        Message = message;
        UpdatedAt = now;
        CompletedAt = now;
    }

    public void Fail(string message, DateTimeOffset now)
    {
        RequireImporting();
        Status = ImportJobStatus.Failed;
        Message = message;
        UpdatedAt = now;
    }

    // Puts an interrupted import back in the queue (nothing reached the library).
    public void Requeue(DateTimeOffset now)
    {
        RequireImporting();
        Status = ImportJobStatus.Waiting;
        UpdatedAt = now;
    }

    public void Retry(DateTimeOffset now)
    {
        if (Status is not (ImportJobStatus.Failed or ImportJobStatus.Skipped))
        {
            throw new InvalidJobStateException("Only a failed or skipped import can be retried.");
        }

        Status = ImportJobStatus.Waiting;
        Message = null;
        UpdatedAt = now;
    }

    public void Skip(DateTimeOffset now)
    {
        if (Status is not (ImportJobStatus.Waiting or ImportJobStatus.Failed))
        {
            throw new InvalidJobStateException("Only a waiting or failed import can be skipped.");
        }

        Status = ImportJobStatus.Skipped;
        UpdatedAt = now;
    }

    private void RequireImporting()
    {
        if (Status != ImportJobStatus.Importing)
        {
            throw new InvalidJobStateException($"The job isn't importing; it's {Status}.");
        }
    }
}
