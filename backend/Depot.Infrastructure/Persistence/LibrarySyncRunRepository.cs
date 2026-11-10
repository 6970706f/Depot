using System.Text.Json;
using Depot.Application.Jellyfin;
using Depot.Domain.Media;
using Depot.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;

namespace Depot.Infrastructure.Persistence;

public sealed class LibrarySyncRunRepository(DepotDbContext dbContext) : ILibrarySyncRunRepository
{
    // The history kept: enough to see what recent syncs did, without growing forever.
    public const int KeptRuns = 50;

    public async Task<LibrarySyncRun> AddAsync(LibrarySyncRun run, CancellationToken cancellationToken)
    {
        var record = new LibrarySyncRunRecord { Errors = "[]" };
        Apply(record, run);
        dbContext.LibrarySyncRuns.Add(record);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }

        await dbContext.LibrarySyncRuns
            .Where(old => old.Id <= record.Id - KeptRuns && old.Status != SyncRunStatus.Running)
            .ExecuteDeleteAsync(cancellationToken);
        return ToDomain(record);
    }

    public async Task UpdateAsync(LibrarySyncRun run, CancellationToken cancellationToken)
    {
        var record = await dbContext.LibrarySyncRuns.SingleOrDefaultAsync(existing => existing.Id == run.Id, cancellationToken)
            ?? throw new SyncRunNotFoundException(run.Id);
        Apply(record, run);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }
    }

    public async Task<LibrarySyncRun?> GetAsync(int id, CancellationToken cancellationToken)
    {
        var record = await dbContext.LibrarySyncRuns.AsNoTracking().SingleOrDefaultAsync(run => run.Id == id, cancellationToken);
        return record is null ? null : ToDomain(record);
    }

    public async Task<IReadOnlyList<LibrarySyncRun>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        var records = await dbContext.LibrarySyncRuns.AsNoTracking().OrderByDescending(run => run.Id).Take(limit).ToListAsync(cancellationToken);
        return records.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<LibrarySyncRun>> ListRunningAsync(CancellationToken cancellationToken)
    {
        var records = await dbContext.LibrarySyncRuns
            .AsNoTracking()
            .Where(run => run.Status == SyncRunStatus.Running)
            .OrderBy(run => run.Id)
            .ToListAsync(cancellationToken);
        return records.Select(ToDomain).ToList();
    }

    private static void Apply(LibrarySyncRunRecord record, LibrarySyncRun run)
    {
        record.Trigger = run.Trigger;
        record.Status = run.Status;
        record.StartedAt = run.StartedAt;
        record.FinishedAt = run.FinishedAt;
        record.Message = run.Message;
        record.CurrentLibrary = run.CurrentLibrary;
        record.LibrariesTotal = run.LibrariesTotal;
        record.LibrariesCompleted = run.LibrariesCompleted;
        record.LibrariesFailed = run.LibrariesFailed;
        record.ItemsSeen = run.ItemsSeen;
        record.ItemsAdded = run.ItemsAdded;
        record.ItemsUpdated = run.ItemsUpdated;
        record.ItemsRemoved = run.ItemsRemoved;
        record.ItemsSkipped = run.ItemsSkipped;
        record.Errors = JsonSerializer.Serialize(run.Errors);
    }

    private static LibrarySyncRun ToDomain(LibrarySyncRunRecord record) => LibrarySyncRun.Restore(
        record.Id,
        record.Trigger,
        record.Status,
        record.StartedAt,
        record.FinishedAt,
        record.Message,
        record.CurrentLibrary,
        record.LibrariesTotal,
        record.LibrariesCompleted,
        record.LibrariesFailed,
        record.ItemsSeen,
        record.ItemsAdded,
        record.ItemsUpdated,
        record.ItemsRemoved,
        record.ItemsSkipped,
        JsonSerializer.Deserialize<List<LibrarySyncError>>(record.Errors) ?? []);
}
