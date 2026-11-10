using Depot.Application.Settings;
using Depot.Domain.Media;
using Microsoft.Extensions.Logging;

namespace Depot.Application.Jellyfin;

// Starting syncs and reporting on them. The sync itself runs in the background
// (JellyfinLibrarySync, run by the worker), so starting one answers right away.
public sealed class JellyfinSyncService(
    IJellyfinSettingsRepository settingsRepository,
    ILibrarySyncRunRepository runs,
    JellyfinSyncQueue queue,
    TimeProvider clock,
    ILogger<JellyfinSyncService> logger)
{
    public const int HistoryLimit = 20;

    // Queues a sync. Throws SyncAlreadyRunningException while one is queued or running, and
    // JellyfinException (NotConfigured) before Jellyfin is set up. A scheduled sync would start
    // here too, with SyncTrigger.Scheduled.
    public async Task<SyncRunView> StartAsync(SyncTrigger trigger, CancellationToken cancellationToken)
    {
        if (await settingsRepository.GetAsync(cancellationToken) is null)
        {
            throw new JellyfinException(JellyfinFailure.NotConfigured, "Jellyfin isn't set up yet. Add its URL and an API key in Settings.");
        }

        if (!queue.TryReserve())
        {
            throw new SyncAlreadyRunningException(queue.ActiveRunId);
        }

        try
        {
            var run = await runs.AddAsync(LibrarySyncRun.Start(trigger, clock.GetUtcNow()), cancellationToken);
            queue.Enqueue(run.Id);
            logger.LogInformation("Library sync {RunId} queued ({Trigger}).", run.Id, trigger);
            return SyncRunView.From(run);
        }
        catch
        {
            queue.Release();
            throw;
        }
    }

    public async Task<SyncStatusView> GetStatusAsync(CancellationToken cancellationToken)
    {
        var latest = (await runs.ListAsync(1, cancellationToken)).FirstOrDefault();
        return new SyncStatusView(queue.IsBusy, latest is null ? null : SyncRunView.From(latest));
    }

    public async Task<IReadOnlyList<SyncRunView>> ListAsync(CancellationToken cancellationToken) =>
        (await runs.ListAsync(HistoryLimit, cancellationToken)).Select(SyncRunView.From).ToList();

    public async Task<SyncRunView> GetAsync(int id, CancellationToken cancellationToken) =>
        SyncRunView.From(await runs.GetAsync(id, cancellationToken) ?? throw new SyncRunNotFoundException(id));

    // Runs left "running" by a Depot that stopped (or crashed) never finish: they're marked
    // interrupted at startup, before any new run can start.
    public async Task InterruptUnfinishedAsync(CancellationToken cancellationToken)
    {
        foreach (var run in await runs.ListRunningAsync(cancellationToken))
        {
            run.Interrupt("Depot stopped before this sync finished. Start it again.", clock.GetUtcNow());
            await runs.UpdateAsync(run, cancellationToken);
            logger.LogWarning("Library sync {RunId} was interrupted by a restart.", run.Id);
        }
    }
}
