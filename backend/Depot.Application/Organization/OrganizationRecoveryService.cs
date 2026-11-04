using Depot.Domain.Organization;
using Microsoft.Extensions.Logging;

namespace Depot.Application.Organization;

public sealed class OrganizationRecoveryService(
    IOrganizationJobRepository jobs,
    IMediaOrganizer organizer,
    TimeProvider clock,
    ILogger<OrganizationRecoveryService> logger)
{
    // Runs once at startup, before any import. Each interrupted import is inspected on disk
    // before anything is repeated: nothing is assumed to have finished. The destination is
    // recorded before any file is written, so every temporary file belongs to one of these.
    public async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        var interrupted = await jobs.ListByStatusAsync(OrganizationJobStatus.Processing, cancellationToken);
        foreach (var job in interrupted)
        {
            await RecoverAsync(job, cancellationToken);
        }

        return interrupted.Count;
    }

    private async Task RecoverAsync(OrganizationJob job, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (job.DestinationPath is null)
        {
            job.ReturnToQueue("Depot stopped during this import. It will run again.", now);
            await jobs.UpdateAsync(job, cancellationToken);
            return;
        }

        var outcome = await organizer.RecoverAsync(new RecoveryRequest(job.Id, job.DestinationPath, job.SizeBytes), cancellationToken);
        switch (outcome.Result)
        {
            case RecoveryResult.Completed:
                job.Complete(null, "The import finished just before Depot stopped; the file was checked on startup.", now);
                break;
            case RecoveryResult.NotStarted:
                job.ReturnToQueue("Depot stopped during this import. It was cleaned up and will run again.", now);
                break;
            default:
                job.Fail(outcome.Message ?? "The interrupted import left files Depot can't account for.", now);
                break;
        }

        await jobs.UpdateAsync(job, cancellationToken);
        logger.LogWarning(
            "Recovered the interrupted import of {RelativePath} to {Destination}: {Result}. {Message}",
            job.RelativePath, job.DestinationPath, outcome.Result, outcome.Message);
    }
}
