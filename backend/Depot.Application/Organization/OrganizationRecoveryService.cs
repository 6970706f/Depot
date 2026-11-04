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
    // recorded before any file is written, so every temporary file belongs to one of these, and
    // the identity of the file to publish is recorded before it appears, so a file at the
    // destination is recognized as this import's or left alone. Running it again after a
    // crash in the middle gives the same result.
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

        var outcome = await organizer.RecoverAsync(
            new RecoveryRequest(job.Id, job.DestinationPath, job.SizeBytes, job.ExpectedFileId), cancellationToken);
        switch (outcome.Result)
        {
            case RecoveryResult.Completed:
                job.Complete(null, "The import finished just before Depot stopped; on startup, the file in the library was confirmed to be the one it wrote.", now);
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
