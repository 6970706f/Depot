using Depot.Domain.Media;

namespace Depot.Application.Jellyfin;

// ItemsUnchanged is what was seen and neither added nor updated.
public sealed record SyncRunView(
    int Id,
    SyncTrigger Trigger,
    SyncRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Message,
    string? CurrentLibrary,
    int LibrariesTotal,
    int LibrariesCompleted,
    int LibrariesFailed,
    int ItemsSeen,
    int ItemsAdded,
    int ItemsUpdated,
    int ItemsUnchanged,
    int ItemsRemoved,
    int ItemsSkipped,
    IReadOnlyList<LibrarySyncError> Errors)
{
    public static SyncRunView From(LibrarySyncRun run) => new(
        run.Id, run.Trigger, run.Status, run.StartedAt, run.FinishedAt, run.Message, run.CurrentLibrary,
        run.LibrariesTotal, run.LibrariesCompleted, run.LibrariesFailed,
        run.ItemsSeen, run.ItemsAdded, run.ItemsUpdated, Math.Max(0, run.ItemsSeen - run.ItemsAdded - run.ItemsUpdated),
        run.ItemsRemoved, run.ItemsSkipped, run.Errors);
}

// LatestRun is the one running, or else the last one; null before the first sync.
public sealed record SyncStatusView(bool IsRunning, SyncRunView? LatestRun);
