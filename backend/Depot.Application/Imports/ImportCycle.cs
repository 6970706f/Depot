using Depot.Application.Library;
using Depot.Application.QBittorrent;
using Depot.Domain.Library;
using Microsoft.Extensions.Logging;

namespace Depot.Application.Imports;

// One pass of the import worker: turns finished downloads that have a library choice into jobs,
// then imports the waiting jobs, one at a time.
public sealed class ImportCycle(
    ILibrarySettingsRepository settingsRepository,
    ILibraryChoiceRepository choices,
    IImportJobRepository jobs,
    IQBittorrentClient qBittorrent,
    ILibraryFileSystem fileSystem,
    IMediaImporter importer,
    LibraryCheckService checker,
    IPathEnvironment environment,
    IEnumerable<IImportListener> listeners,
    ImportRuntime runtime,
    TimeProvider clock,
    ILogger<ImportCycle> logger)
{
    // Returns the time until the next pass.
    public async Task<TimeSpan> RunAsync(CancellationToken cancellationToken)
    {
        var settings = (await settingsRepository.GetAsync(cancellationToken)).Settings;
        var interval = TimeSpan.FromSeconds(settings.PollingIntervalSeconds);
        var paths = settings.Resolve(environment.HomeDirectory);

        try
        {
            runtime.Report(ImportRunState.Running, "Looking for finished downloads.");
            await DiscoverAsync(paths, cancellationToken);
        }
        catch (QBittorrentException exception)
        {
            runtime.Report(ImportRunState.Error, $"qBittorrent: {exception.Message}", clock.GetUtcNow());
            logger.LogWarning("Import pass stopped: {Reason}", exception.Message);
            return interval;
        }

        var waiting = (await jobs.ListAsync(ImportJobStatus.Waiting, cancellationToken)).Select(job => job.Id).Reverse().ToList();
        if (waiting.Count > 0)
        {
            // Imports never start while a folder check fails: the libraries must be writable and
            // the mapped downloads readable.
            var check = checker.Check(settings);
            if (!check.Ready)
            {
                var problem = check.Folders.FirstOrDefault(folder => folder.Problem is not null);
                var message = problem is null
                    ? "Imports are paused: add a path mapping in Settings › Media library."
                    : $"Imports are paused. {problem.Label} ({problem.ResolvedPath}): {problem.Problem}";
                runtime.Report(ImportRunState.Paused, message, clock.GetUtcNow());
                return interval;
            }

            runtime.Report(ImportRunState.Running, $"Importing {waiting.Count} video(s).");
            var imported = 0;
            foreach (var id in waiting)
            {
                imported += await ImportAsync(id, settings, paths, cancellationToken) ? 1 : 0;
            }

            if (imported > 0)
            {
                foreach (var listener in listeners)
                {
                    await listener.ImportedAsync(cancellationToken);
                }
            }
        }

        runtime.Report(ImportRunState.Idle, null, clock.GetUtcNow());
        return interval;
    }

    // Every finished torrent with a library choice that wasn't examined yet, including those that
    // finished while Depot was off. Torrents without a choice are never touched.
    private async Task DiscoverAsync(ResolvedLibraryPaths paths, CancellationToken cancellationToken)
    {
        var pending = await choices.GetPendingAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var torrent in (await qBittorrent.GetTorrentsAsync(cancellationToken)).Where(torrent => torrent.IsFullyDownloaded))
        {
            if (!pending.TryGetValue(torrent.Hash, out var target))
            {
                continue;
            }

            IReadOnlyList<Depot.Domain.Downloads.TorrentContentFile> files;
            try
            {
                files = await qBittorrent.GetTorrentFilesAsync(torrent.Hash, cancellationToken);
            }
            catch (QBittorrentException exception) when (exception.Failure == QBittorrentFailure.TorrentNotFound)
            {
                continue;
            }

            var existingFolders = target.Type == MediaType.Series
                ? fileSystem.ListFolders(Path.Join(paths.SeriesRoot, target.Title))
                : [];
            var now = clock.GetUtcNow();
            var newJobs = ImportPlanner.Plan(target, files, existingFolders)
                .Select(plan => new ImportJob(torrent.Hash, torrent.Name, torrent.SavePath, target, plan, now))
                .ToList();
            await jobs.AddScanAsync(torrent.Hash, newJobs, now, cancellationToken);

            if (newJobs.Count == 0)
            {
                logger.LogWarning("{TorrentName} finished but has no videos; nothing to import.", torrent.Name);
            }
            else
            {
                logger.LogInformation(
                    "{TorrentName} finished: {Count} video(s) go to the {MediaType} library as \"{Title}\".",
                    torrent.Name, newJobs.Count, target.Type, target.Title);
            }
        }
    }

    private async Task<bool> ImportAsync(int id, LibrarySettings settings, ResolvedLibraryPaths paths, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(id, cancellationToken);
        if (job is null || job.Status != ImportJobStatus.Waiting)
        {
            return false;
        }

        job.Start(clock.GetUtcNow());
        await jobs.SaveAsync(job, cancellationToken);

        var outcome = Resolve(job, paths, settings.ImportMode) is { } request
            ? await importer.ImportAsync(request, cancellationToken)
            : ImportOutcome.Failed(UnmappedMessage(job, paths));

        if (outcome.Succeeded)
        {
            job.Complete(outcome.UsedMode!.Value, outcome.Message, clock.GetUtcNow());
            logger.LogInformation("Imported {Destination} ({Mode}).", job.Destination, outcome.UsedMode);
        }
        else
        {
            job.Fail(outcome.Message ?? "The import failed.", clock.GetUtcNow());
            logger.LogWarning("Import of {Destination} failed: {Reason}", job.Destination, outcome.Message);
        }

        await jobs.SaveAsync(job, CancellationToken.None);
        return outcome.Succeeded;
    }

    // The absolute paths of the job, or null when qBittorrent's folder isn't mapped or a relative
    // path would leave its root.
    public static ImportRequest? Resolve(ImportJob job, ResolvedLibraryPaths paths, ImportMode mode)
    {
        var downloadsRoot = paths.Sources.MappedRootOf(job.SavePath);
        var torrentFolder = paths.Sources.ToDepotPath(job.SavePath);
        if (downloadsRoot is null || torrentFolder is null)
        {
            return null;
        }

        var libraryRoot = paths.LibraryRootFor(job.MediaType);
        var video = File(torrentFolder, libraryRoot, job.Folder, job.SourcePath, job.SizeBytes);
        if (video is null)
        {
            return null;
        }

        var subtitles = job.Subtitles
            .Select(subtitle => File(torrentFolder, libraryRoot, job.Folder, subtitle.RelativePath, subtitle.SizeBytes))
            .OfType<ImportFile>()
            .ToList();
        return new ImportRequest(job.Id, downloadsRoot, libraryRoot, video, subtitles, mode);
    }

    private static ImportFile? File(string torrentFolder, string libraryRoot, string folder, string relativePath, long size)
    {
        var source = LibraryPaths.CombineInside(torrentFolder, relativePath);
        var destination = LibraryPaths.CombineInside(libraryRoot, $"{folder}/{MediaFiles.FileName(relativePath)}");
        return source is null || destination is null ? null : new ImportFile(source, destination, size);
    }

    private static string UnmappedMessage(ImportJob job, ResolvedLibraryPaths paths) =>
        paths.Sources.MappedRootOf(job.SavePath) is null
            ? $"qBittorrent saved this download in {job.SavePath}, which no path mapping covers. Add a mapping in Settings › Media library, then retry."
            : $"{job.SourcePath} isn't a path inside the download, so it was refused.";
}
