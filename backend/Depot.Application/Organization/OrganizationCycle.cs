using Depot.Application.QBittorrent;
using Depot.Domain.Organization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Depot.Application.Organization;

// One pass of the organization worker.
public sealed class OrganizationCycle(
    IOrganizationSettingsRepository settingsRepository,
    IOrganizationJobRepository jobs,
    OrganizationDiagnosticsService diagnostics,
    MediaDiscoveryService discovery,
    IPathEnvironment environment,
    IServiceScopeFactory scopeFactory,
    OrganizationRuntimeStatus status,
    TimeProvider clock,
    ILogger<OrganizationCycle> logger)
{
    // Copies compete for the same disks, so only a couple run at once.
    public const int MaxParallelImports = 2;

    // Looks for finished downloads that have a library choice, then imports the jobs that are
    // ready. Returns the time until the next pass.
    public async Task<TimeSpan> RunAsync(CancellationToken cancellationToken)
    {
        var settings = (await settingsRepository.GetAsync(cancellationToken)).Settings;
        var interval = TimeSpan.FromSeconds(settings.PollingIntervalSeconds);

        try
        {
            status.Report(OrganizationRunState.Running, "Looking for finished downloads.");
            await discovery.DiscoverAsync(cancellationToken);

            var ready = (await jobs.ListByStatusAsync(OrganizationJobStatus.Discovered, cancellationToken))
                .Where(job => job.IsReadyToImport)
                .Select(job => job.Id)
                .ToList();

            if (ready.Count > 0 && await CanImportAsync(settings, cancellationToken))
            {
                status.Report(OrganizationRunState.Running, $"Importing {ready.Count} video(s).");
                await ImportAsync(ready, settings, cancellationToken);
            }
            else if (ready.Count > 0)
            {
                return interval;
            }

            status.Report(OrganizationRunState.Idle, null, clock.GetUtcNow());
        }
        catch (QBittorrentException exception)
        {
            status.Report(OrganizationRunState.Error, $"qBittorrent: {exception.Message}", clock.GetUtcNow());
            logger.LogWarning("Organization pass stopped: {Reason}", exception.Message);
        }

        return interval;
    }

    // Imports never start while a folder check fails: the library must be writable and the
    // mapped downloads readable.
    private async Task<bool> CanImportAsync(OrganizationSettings settings, CancellationToken cancellationToken)
    {
        var check = await diagnostics.CheckAsync(settings, cancellationToken);
        if (check.CanOrganize)
        {
            return true;
        }

        var problem = check.Folders.First(folder => folder.Problem is not null);
        var message = $"Imports are paused. {problem.Label} ({problem.ResolvedPath}): {problem.Problem}";
        status.Report(OrganizationRunState.Paused, message, clock.GetUtcNow());
        logger.LogWarning("{Message}", message);
        return false;
    }

    private async Task ImportAsync(List<int> jobIds, OrganizationSettings settings, CancellationToken cancellationToken)
    {
        var paths = settings.Resolve(environment.HomeDirectory);
        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelImports, CancellationToken = cancellationToken };

        // Each import gets its own scope, and so its own database context.
        await Parallel.ForEachAsync(jobIds, options, async (jobId, token) =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<MediaImportService>().ImportAsync(jobId, settings, paths, token);
            }
            catch (JobConcurrencyException)
            {
                logger.LogInformation("Job {JobId} changed during the pass; it's left as it is now.", jobId);
            }
        });
    }
}
