using Depot.Application.Jellyfin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Depot.Infrastructure.Jellyfin;

// Runs the library syncs that JellyfinSyncService queues, one at a time, each in its own scope.
// A periodic sync would only need a timer here that calls
// JellyfinSyncService.StartAsync(SyncTrigger.Scheduled).
public sealed class JellyfinSyncWorker(
    IServiceScopeFactory scopeFactory,
    JellyfinSyncQueue queue,
    ILogger<JellyfinSyncWorker> logger) : BackgroundService
{
    // Closes the runs a previous Depot left unfinished. Done here, before the API takes
    // requests, so a sync started right after startup is never mistaken for one of them.
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<JellyfinSyncService>().InterruptUnfinishedAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Unfinished library syncs couldn't be closed.");
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var runId in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<JellyfinLibrarySync>().RunAsync(runId, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // The run itself records its failures; this is for one that couldn't even be read.
                    logger.LogError(exception, "Library sync {RunId} couldn't run.", runId);
                }
                finally
                {
                    queue.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
