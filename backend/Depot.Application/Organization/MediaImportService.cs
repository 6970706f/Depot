using Depot.Application.QBittorrent;
using Depot.Domain.Downloads;
using Depot.Domain.Organization;
using Microsoft.Extensions.Logging;

namespace Depot.Application.Organization;

// Imports one job: the video goes, under its own file name, into the folder chosen when the
// download was added.
public sealed class MediaImportService(
    IQBittorrentClient qBittorrentClient,
    IOrganizationJobRepository jobs,
    IMediaOrganizer organizer,
    TimeProvider clock,
    ILogger<MediaImportService> logger)
{
    public async Task ImportAsync(int jobId, OrganizationSettings settings, ResolvedOrganizationPaths paths, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(jobId, cancellationToken);
        if (job is null || job.Status != OrganizationJobStatus.Discovered)
        {
            // Changed since the pass started, for example skipped by a person.
            return;
        }

        var plan = await PlanAsync(job, settings.ImportMode, paths, cancellationToken);
        if (plan.Failure is not null || plan.Wait is not null)
        {
            if (plan.Wait is not null)
            {
                job.Defer(plan.Wait, clock.GetUtcNow());
                logger.LogInformation("Import of {RelativePath} postponed: {Reason}", job.RelativePath, plan.Wait);
            }
            else
            {
                job.Fail(plan.Failure!, clock.GetUtcNow());
                logger.LogWarning("Import of {RelativePath} failed before it started: {Reason}", job.RelativePath, plan.Failure);
            }

            await jobs.UpdateAsync(job, cancellationToken);
            return;
        }

        var request = plan.Request!;
        job.StartProcessing(request.Video.DestinationPath, clock.GetUtcNow());
        job = await jobs.UpdateAsync(job, cancellationToken);
        logger.LogInformation(
            "Importing {Source} to {Destination}, mode {Mode}.", request.Video.SourcePath, request.Video.DestinationPath, request.Mode);

        ImportOutcome outcome;
        try
        {
            outcome = await organizer.ImportAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.ReturnToQueue("Depot stopped during this import. It was cleaned up and will run again.", clock.GetUtcNow());
            await jobs.UpdateAsync(job, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected error importing {RelativePath}.", job.RelativePath);
            outcome = ImportOutcome.Failed($"Unexpected error: {exception.Message}");
        }

        var now = clock.GetUtcNow();
        if (outcome.Result == ImportResult.Completed)
        {
            job.Complete(outcome.UsedMode, outcome.Message, now);
            logger.LogInformation(
                "Imported {Source} to {Destination} using {Mode}.", request.Video.SourcePath, request.Video.DestinationPath, outcome.UsedMode);
        }
        else
        {
            // A taken destination fails too: nothing is overwritten, and a retry tries again
            // once the file in the way is moved.
            job.Fail(outcome.Message!, now);
            logger.LogWarning("Import of {RelativePath} failed: {Reason}", job.RelativePath, outcome.Message);
        }

        await jobs.UpdateAsync(job, CancellationToken.None);
    }

    // Works out the source and destination from what qBittorrent reports right now. Wait means
    // try again later (nothing is wrong with the job itself); Failure means the job can't be
    // imported as it is.
    private async Task<ImportPlan> PlanAsync(
        OrganizationJob job,
        ImportMode mode,
        ResolvedOrganizationPaths paths,
        CancellationToken cancellationToken)
    {
        Torrent? torrent;
        IReadOnlyList<TorrentContentFile> files;
        try
        {
            torrent = await qBittorrentClient.GetTorrentAsync(job.TorrentHash, cancellationToken);
            if (torrent is null)
            {
                return ImportPlan.Failed("The torrent is no longer in qBittorrent, so Depot can't locate its files safely.");
            }

            if (!torrent.IsFullyDownloaded)
            {
                return ImportPlan.Failed($"The torrent isn't complete anymore (status: {torrent.Status}). Retry once qBittorrent finishes it.");
            }

            files = await qBittorrentClient.GetTorrentFilesAsync(job.TorrentHash, cancellationToken);
        }
        catch (QBittorrentException exception) when (exception.Failure == QBittorrentFailure.TorrentNotFound)
        {
            return ImportPlan.Failed("The torrent is no longer in qBittorrent, so Depot can't locate its files safely.");
        }
        catch (QBittorrentException exception)
        {
            return ImportPlan.Waiting($"Waiting for qBittorrent: {exception.Message}");
        }

        var video = files.FirstOrDefault(file => file.RelativePath == job.RelativePath);
        if (video is not { IsDownloaded: true })
        {
            return ImportPlan.Failed("qBittorrent no longer lists this file as downloaded.");
        }

        var sourceRoot = paths.Sources.ToDepotPath(torrent.SavePath);
        if (sourceRoot is null)
        {
            return ImportPlan.Waiting(paths.Sources.HasMappings
                ? $"qBittorrent saves this torrent in {torrent.SavePath}, which no path mapping covers. Add one in Settings."
                : $"qBittorrent saves this torrent in {torrent.SavePath}. Add a path mapping for that folder in Settings (the same path on both sides when qBittorrent runs on this machine): Depot only reads downloads from mapped folders.");
        }

        var folders = new LibraryFolders(organizer, paths);
        var libraryRoot = folders.RootFor(job.Target);
        var folder = await folders.RelativeFolderAsync(job.Target, cancellationToken);
        var videoSource = LibraryPaths.CombineInside(sourceRoot, job.RelativePath);
        var videoDestination = LibraryPaths.CombineInside(libraryRoot, $"{folder}/{MediaFiles.FileName(job.RelativePath)}");
        if (videoSource is null || videoDestination is null)
        {
            return ImportPlan.Failed("The source or destination path points outside its folder, so it was refused.");
        }

        return ImportPlan.Ready(new ImportRequest(
            job.Id, job.Target.Type, mode, libraryRoot, paths.Sources.MappedRootOf(torrent.SavePath)!, sourceRoot,
            new ImportFile(videoSource, videoDestination, video.SizeBytes)));
    }

    private sealed record ImportPlan(ImportRequest? Request, string? Wait, string? Failure)
    {
        public static ImportPlan Ready(ImportRequest request) => new(request, null, null);

        public static ImportPlan Waiting(string reason) => new(null, reason, null);

        public static ImportPlan Failed(string reason) => new(null, null, reason);
    }
}
