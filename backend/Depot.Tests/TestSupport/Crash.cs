using Depot.Application.Organization;
using Depot.Domain.Organization;
using Depot.Infrastructure.Organization;

namespace Depot.Tests.TestSupport;

// Where an import is stopped, in the order an import goes through them.
public enum CrashStep
{
    // The job is Processing, but the file to publish isn't saved with it yet. A copy has
    // created its empty temporary file; a hardlink hasn't touched anything.
    BeforeTheFileIsRecorded,
    // The temporary file is recorded and half written.
    CopyHalfway,
    // The temporary file is complete and recorded, but not yet under its final name.
    CopyWritten,
    // The video is under its final name; the job isn't saved as completed yet.
    BeforeCompletionIsSaved,
}

// Stands in for Depot being killed in the middle of an import: the import stops at the step for
// good, so nothing after it runs (no clean-up, no database update), as when the process dies.
// Restarting the harness then plays the next start of Depot. With onlyFor, only imports whose
// video path contains it are stopped; the other copies wait until then, so that both are under
// way at the same time.
public sealed class Crash(CrashStep step, string? onlyFor = null)
{
    private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CrashStep Step { get; } = step;

    // Completes once an import is stopped.
    public Task Reached => reached.Task;

    public bool AppliesTo(string videoPath) => onlyFor is null || videoPath.Contains(onlyFor, StringComparison.Ordinal);

    // Never completes.
    public Task Hang()
    {
        reached.TrySetResult();
        return new TaskCompletionSource().Task;
    }
}

// The real file transfer, stopped in the middle of a copy.
public sealed class CrashingTransfer(Crash crash) : IFileTransfer
{
    private readonly FileTransfer real = new();

    public HardLinkResult TryCreateHardLink(string sourcePath, string destinationPath) =>
        real.TryCreateHardLink(sourcePath, destinationPath);

    public async Task CopyAsync(string sourcePath, string destinationPath, Func<Task> created, CancellationToken cancellationToken)
    {
        if (!crash.AppliesTo(sourcePath))
        {
            await crash.Reached.WaitAsync(cancellationToken);
        }

        if (crash.Step == CrashStep.CopyHalfway && crash.AppliesTo(sourcePath))
        {
            var content = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
            await using (var output = new FileStream(destinationPath, FileMode.CreateNew))
            {
                await created();
                await output.WriteAsync(content.AsMemory(0, content.Length / 2), cancellationToken);
            }

            await crash.Hang();
        }

        await real.CopyAsync(sourcePath, destinationPath, created, cancellationToken);
        if (crash.Step == CrashStep.CopyWritten && crash.AppliesTo(sourcePath))
        {
            await crash.Hang();
        }
    }

    public long? FileId(string path) => real.FileId(path);
}

// The real job repository, stopped before the file to publish, or the finished import, is saved.
public sealed class CrashingJobRepository(IOrganizationJobRepository real, Crash crash) : IOrganizationJobRepository
{
    public Task AddScanAsync(string torrentHash, IReadOnlyList<OrganizationJob> jobs, DateTimeOffset scannedAt, CancellationToken cancellationToken) =>
        real.AddScanAsync(torrentHash, jobs, scannedAt, cancellationToken);

    public Task<OrganizationJob?> GetAsync(int id, CancellationToken cancellationToken) => real.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<OrganizationJob>> ListAsync(OrganizationJobStatus? status, int limit, CancellationToken cancellationToken) =>
        real.ListAsync(status, limit, cancellationToken);

    public Task<IReadOnlyList<OrganizationJob>> ListByStatusAsync(OrganizationJobStatus status, CancellationToken cancellationToken) =>
        real.ListByStatusAsync(status, cancellationToken);

    public Task<IReadOnlyDictionary<OrganizationJobStatus, int>> CountByStatusAsync(CancellationToken cancellationToken) =>
        real.CountByStatusAsync(cancellationToken);

    public async Task<OrganizationJob> UpdateAsync(OrganizationJob job, CancellationToken cancellationToken)
    {
        var stops = crash.Step switch
        {
            CrashStep.BeforeTheFileIsRecorded => job is { Status: OrganizationJobStatus.Processing, ExpectedFileId: not null },
            CrashStep.BeforeCompletionIsSaved => job.Status == OrganizationJobStatus.Completed,
            _ => false,
        };
        if (stops && crash.AppliesTo(job.RelativePath))
        {
            await crash.Hang();
        }

        return await real.UpdateAsync(job, cancellationToken);
    }
}
