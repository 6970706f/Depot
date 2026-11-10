namespace Depot.Domain.Media;

public enum SyncTrigger
{
    // Someone pressed "Sync now".
    Manual,
    // Started on a schedule. Nothing schedules syncs yet; the value keeps their history apart.
    Scheduled,
}

public enum SyncRunStatus
{
    Running,
    // Every library was read.
    Succeeded,
    // Some libraries were read and others failed; what was read is saved.
    CompletedWithErrors,
    // Nothing could be read, such as when the server can't be reached.
    Failed,
    // Depot stopped before the run finished.
    Interrupted,
}

// A library that couldn't be read completely, and why. Its items that weren't seen are kept.
public sealed record LibrarySyncError(string Library, string Message);

// One pass that brings the local copy of the library up to date with the source: which
// libraries it read, what it added, changed and removed, and what failed.
public sealed class LibrarySyncRun
{
    // A run with every library failing keeps the first errors only.
    public const int MaxErrors = 50;

    private readonly List<LibrarySyncError> errors;

    private LibrarySyncRun(int id, SyncTrigger trigger, SyncRunStatus status, DateTimeOffset startedAt, List<LibrarySyncError> errors)
    {
        Id = id;
        Trigger = trigger;
        Status = status;
        StartedAt = startedAt;
        this.errors = errors;
    }

    // 0 until the run is saved.
    public int Id { get; }

    public SyncTrigger Trigger { get; }

    public SyncRunStatus Status { get; private set; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; private set; }

    // What the run is doing, or how it ended, in plain words.
    public string? Message { get; private set; }

    // The library being read while the run goes on.
    public string? CurrentLibrary { get; private set; }

    public int LibrariesTotal { get; private set; }

    public int LibrariesCompleted { get; private set; }

    public int LibrariesFailed { get; private set; }

    // Items read from the source, each counted once.
    public int ItemsSeen { get; private set; }

    public int ItemsAdded { get; private set; }

    public int ItemsUpdated { get; private set; }

    public int ItemsRemoved { get; private set; }

    // Entries the source sent that couldn't be read, such as an item without an id.
    public int ItemsSkipped { get; private set; }

    public IReadOnlyList<LibrarySyncError> Errors => errors;

    public bool IsFinished => Status != SyncRunStatus.Running;

    public static LibrarySyncRun Start(SyncTrigger trigger, DateTimeOffset now) =>
        new(0, trigger, SyncRunStatus.Running, now, []) { Message = "Waiting to start." };

    public static LibrarySyncRun Restore(
        int id,
        SyncTrigger trigger,
        SyncRunStatus status,
        DateTimeOffset startedAt,
        DateTimeOffset? finishedAt,
        string? message,
        string? currentLibrary,
        int librariesTotal,
        int librariesCompleted,
        int librariesFailed,
        int itemsSeen,
        int itemsAdded,
        int itemsUpdated,
        int itemsRemoved,
        int itemsSkipped,
        IEnumerable<LibrarySyncError> errors) =>
        new(id, trigger, status, startedAt, errors.ToList())
        {
            FinishedAt = finishedAt,
            Message = message,
            CurrentLibrary = currentLibrary,
            LibrariesTotal = librariesTotal,
            LibrariesCompleted = librariesCompleted,
            LibrariesFailed = librariesFailed,
            ItemsSeen = itemsSeen,
            ItemsAdded = itemsAdded,
            ItemsUpdated = itemsUpdated,
            ItemsRemoved = itemsRemoved,
            ItemsSkipped = itemsSkipped,
        };

    public void BeginLibraries(int count)
    {
        EnsureRunning();
        LibrariesTotal = count;
        Message = count == 0 ? "The server has no movie or show libraries." : $"Reading {count} librar{(count == 1 ? "y" : "ies")}.";
    }

    public void BeginLibrary(string name)
    {
        EnsureRunning();
        CurrentLibrary = name;
        Message = $"Reading {name}.";
    }

    public void RecordItems(int seen, int added, int updated, int skipped)
    {
        EnsureRunning();
        ItemsSeen += seen;
        ItemsAdded += added;
        ItemsUpdated += updated;
        ItemsSkipped += skipped;
    }

    public void RecordRemoved(int count)
    {
        EnsureRunning();
        ItemsRemoved += count;
    }

    public void CompleteLibrary()
    {
        EnsureRunning();
        LibrariesCompleted++;
        CurrentLibrary = null;
    }

    public void FailLibrary(string library, string message)
    {
        EnsureRunning();
        LibrariesFailed++;
        CurrentLibrary = null;
        AddError(library, message);
    }

    // Succeeded, or CompletedWithErrors when some libraries failed, or Failed when all did.
    public void Finish(DateTimeOffset now)
    {
        EnsureRunning();
        if (LibrariesFailed == 0)
        {
            Status = SyncRunStatus.Succeeded;
            Message = null;
        }
        else
        {
            Status = LibrariesCompleted == 0 ? SyncRunStatus.Failed : SyncRunStatus.CompletedWithErrors;
            Message = $"{LibrariesFailed} of {LibrariesTotal} librar{(LibrariesTotal == 1 ? "y" : "ies")} couldn't be read completely. "
                + "Their items that weren't seen are kept.";
        }

        End(now);
    }

    // Nothing could be synced, such as when the libraries can't be listed.
    public void Fail(string message, DateTimeOffset now)
    {
        EnsureRunning();
        Status = SyncRunStatus.Failed;
        Message = message;
        End(now);
    }

    // Also used for a run found unfinished after a restart.
    public void Interrupt(string message, DateTimeOffset now)
    {
        EnsureRunning();
        Status = SyncRunStatus.Interrupted;
        Message = message;
        End(now);
    }

    private void End(DateTimeOffset now)
    {
        CurrentLibrary = null;
        FinishedAt = now;
    }

    private void AddError(string library, string message)
    {
        if (errors.Count < MaxErrors)
        {
            errors.Add(new LibrarySyncError(library, message));
        }
    }

    private void EnsureRunning()
    {
        if (IsFinished)
        {
            throw new InvalidOperationException($"Sync run {Id} already finished ({Status}).");
        }
    }
}
