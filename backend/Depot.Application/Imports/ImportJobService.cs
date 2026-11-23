using Depot.Application.Library;
using Depot.Domain.Library;
using Microsoft.Extensions.Logging;

namespace Depot.Application.Imports;

public sealed record ImportJobView(
    int Id,
    string TorrentHash,
    string TorrentName,
    string FileName,
    string SourcePath,
    MediaType MediaType,
    string Title,
    int? Season,
    string Destination,
    long SizeBytes,
    int SubtitleCount,
    ImportJobStatus Status,
    string? Message,
    ImportMode? UsedMode,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ImportJobView From(ImportJob job) => new(
        job.Id, job.TorrentHash, job.TorrentName, job.FileName, job.SourcePath, job.MediaType, job.Title, job.Season,
        job.Destination, job.SizeBytes, job.Subtitles.Count, job.Status, job.Message, job.UsedMode, job.Attempts,
        job.CreatedAt, job.UpdatedAt);
}

public sealed record ImportStatusView(ImportRuntimeView Worker, IReadOnlyDictionary<ImportJobStatus, int> Counts);

public sealed class JobNotFoundException(int id) : Exception($"Import job {id} doesn't exist.");

public sealed class ImportJobService(
    IImportJobRepository jobs,
    ImportRuntime runtime,
    ImportSignal signal,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<ImportJobView>> ListAsync(ImportJobStatus? status, CancellationToken cancellationToken) =>
        (await jobs.ListAsync(status, cancellationToken)).Select(ImportJobView.From).ToList();

    public async Task<ImportStatusView> StatusAsync(CancellationToken cancellationToken) =>
        new(runtime.Current, await jobs.CountAsync(cancellationToken));

    public async Task<ImportJobView> RetryAsync(int id, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(id, cancellationToken) ?? throw new JobNotFoundException(id);
        job.Retry(clock.GetUtcNow());
        await jobs.SaveAsync(job, cancellationToken);
        signal.RequestPass();
        return ImportJobView.From(job);
    }

    // Only marks the job: no file is changed or deleted.
    public async Task<ImportJobView> SkipAsync(int id, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(id, cancellationToken) ?? throw new JobNotFoundException(id);
        job.Skip(clock.GetUtcNow());
        await jobs.SaveAsync(job, cancellationToken);
        return ImportJobView.From(job);
    }
}

// Settles imports that were running when Depot stopped, before any new import starts.
public sealed class ImportRecovery(
    IImportJobRepository jobs,
    ILibrarySettingsRepository settingsRepository,
    IMediaImporter importer,
    IPathEnvironment environment,
    TimeProvider clock,
    ILogger<ImportRecovery> logger)
{
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var interrupted = await jobs.ListAsync(ImportJobStatus.Importing, cancellationToken);
        if (interrupted.Count == 0)
        {
            return;
        }

        var paths = (await settingsRepository.GetAsync(cancellationToken)).Settings.Resolve(environment.HomeDirectory);
        foreach (var job in interrupted)
        {
            var destination = LibraryPaths.CombineInside(paths.LibraryRootFor(job.MediaType), job.Destination);
            if (destination is not null && importer.CleanUpInterrupted(job.Id, destination))
            {
                job.Fail(
                    $"Depot stopped during this import and {job.Destination} is in the library. Check it: if it's this video, skip the job; otherwise move it away, then retry.",
                    clock.GetUtcNow());
            }
            else
            {
                job.Requeue(clock.GetUtcNow());
            }

            await jobs.SaveAsync(job, cancellationToken);
            logger.LogWarning("Import of {Destination} was interrupted; it is now {Status}.", job.Destination, job.Status);
        }
    }
}
