using Depot.Domain.Organization;
using Microsoft.Extensions.Logging;

namespace Depot.Application.Organization;

// What people do with organization jobs: list them, retry and skip. Retried jobs are
// imported by the worker on its next pass.
public sealed class OrganizationJobService(
    IOrganizationJobRepository jobs,
    IOrganizationSettingsRepository settingsRepository,
    IMediaOrganizer organizer,
    IPathEnvironment environment,
    OrganizationSignal signal,
    OrganizationRuntimeStatus runtime,
    TimeProvider clock,
    ILogger<OrganizationJobService> logger)
{
    public const int ListLimit = 500;

    public async Task<IReadOnlyList<OrganizationJobView>> ListAsync(OrganizationJobStatus? status, CancellationToken cancellationToken)
    {
        var folders = await FoldersAsync(cancellationToken);
        var views = new List<OrganizationJobView>();
        foreach (var job in await jobs.ListAsync(status, ListLimit, cancellationToken))
        {
            views.Add(await ToViewAsync(job, folders, cancellationToken));
        }

        return views;
    }

    public async Task<OrganizationJobView> GetAsync(int id, CancellationToken cancellationToken) =>
        await ToViewAsync(await FindAsync(id, cancellationToken), await FoldersAsync(cancellationToken), cancellationToken);

    public async Task<OrganizationJobView> RetryAsync(int id, CancellationToken cancellationToken)
    {
        var job = await FindAsync(id, cancellationToken);
        job.Retry(clock.GetUtcNow());
        job = await jobs.UpdateAsync(job, cancellationToken);
        logger.LogInformation("Job {JobId} ({RelativePath}) retried.", job.Id, job.RelativePath);
        signal.RequestPass();
        return await ToViewAsync(job, await FoldersAsync(cancellationToken), cancellationToken);
    }

    // Confirms or corrects where a series video goes, and queues it for import there.
    public async Task<OrganizationJobView> ClassifyAsync(int id, VideoPlacementInput input, CancellationToken cancellationToken)
    {
        var placement = VideoPlacement.ForSeries(input.Kind, input.Season, input.ExtrasFolder);
        var job = await FindAsync(id, cancellationToken);
        job.Classify(placement, clock.GetUtcNow());
        job = await jobs.UpdateAsync(job, cancellationToken);
        logger.LogInformation("Job {JobId} ({RelativePath}) classified as {Placement}.", job.Id, job.RelativePath, placement);
        signal.RequestPass();
        return await ToViewAsync(job, await FoldersAsync(cancellationToken), cancellationToken);
    }

    // Accepts the suggested placement of every video of a download that waits for review, as
    // for a pack of several seasons. Returns the jobs it confirmed.
    public async Task<IReadOnlyList<OrganizationJobView>> ConfirmDownloadAsync(string torrentHash, CancellationToken cancellationToken)
    {
        var confirmed = new List<OrganizationJob>();
        foreach (var job in await jobs.ListByStatusAsync(OrganizationJobStatus.NeedsReview, cancellationToken))
        {
            if (!job.TorrentHash.Equals(torrentHash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            job.Classify(job.Placement, clock.GetUtcNow());
            confirmed.Add(await jobs.UpdateAsync(job, cancellationToken));
        }

        logger.LogInformation("Confirmed {Count} video(s) of download {Hash} as suggested.", confirmed.Count, torrentHash);
        signal.RequestPass();
        var folders = await FoldersAsync(cancellationToken);
        var views = new List<OrganizationJobView>();
        foreach (var job in confirmed)
        {
            views.Add(await ToViewAsync(job, folders, cancellationToken));
        }

        return views;
    }

    // Skipping only changes the job: nothing is deleted, in the library or in the download.
    public async Task<OrganizationJobView> SkipAsync(int id, CancellationToken cancellationToken)
    {
        var job = await FindAsync(id, cancellationToken);
        job.Skip(clock.GetUtcNow());
        job = await jobs.UpdateAsync(job, cancellationToken);
        logger.LogInformation("Job {JobId} ({RelativePath}) skipped.", job.Id, job.RelativePath);
        return await ToViewAsync(job, await FoldersAsync(cancellationToken), cancellationToken);
    }

    public async Task<OrganizationStatusView> GetStatusAsync(CancellationToken cancellationToken)
    {
        var counts = await jobs.CountByStatusAsync(cancellationToken);
        var snapshot = runtime.Current;
        return new OrganizationStatusView(
            CamelCase(snapshot.State.ToString()),
            snapshot.Message,
            snapshot.LastRunAt,
            Enum.GetValues<OrganizationJobStatus>().ToDictionary(
                status => CamelCase(status.ToString()),
                status => counts.GetValueOrDefault(status)));
    }

    private async Task<OrganizationJob> FindAsync(int id, CancellationToken cancellationToken) =>
        await jobs.GetAsync(id, cancellationToken) ?? throw new JobNotFoundException(id);

    private async Task<LibraryFolders> FoldersAsync(CancellationToken cancellationToken) => new(
        organizer, (await settingsRepository.GetAsync(cancellationToken)).Settings.Resolve(environment.HomeDirectory));

    // Before an import starts, the destination shown is worked out the same way the import does.
    private static async Task<OrganizationJobView> ToViewAsync(OrganizationJob job, LibraryFolders folders, CancellationToken cancellationToken)
    {
        var isFinal = job.Status is OrganizationJobStatus.Processing or OrganizationJobStatus.Completed && job.DestinationPath is not null;
        var destination = isFinal
            ? job.DestinationPath
            : Path.Join(
                folders.RootFor(job.Target),
                await folders.RelativeFolderAsync(job.Target, job.Placement, cancellationToken),
                MediaFiles.FileName(job.RelativePath));

        return new OrganizationJobView(
            job.Id, job.TorrentHash, job.TorrentName, job.RelativePath, job.SizeBytes, LibraryTargetView.From(job.Target),
            VideoPlacementView.From(job.Placement), job.Status, job.Message, destination, isFinal, job.ImportedWith, job.Attempts,
            job.CreatedAt, job.UpdatedAt, job.CompletedAt, job.CanRetry, job.CanSkip, job.CanClassify);
    }

    private static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
