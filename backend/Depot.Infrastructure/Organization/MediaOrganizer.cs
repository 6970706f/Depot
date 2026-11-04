using Depot.Application.Organization;
using Depot.Domain.Organization;
using Microsoft.Extensions.Logging;

namespace Depot.Infrastructure.Organization;

// Puts downloaded videos into the library without disturbing the torrent: sources are only
// ever read or hardlinked, never moved, renamed, changed or deleted, and nothing in the
// library is overwritten. Copies are written under temporary names that are removed on failure.
public sealed class MediaOrganizer(IFileTransfer transfer, IDiskSpace diskSpace, ILogger<MediaOrganizer> logger) : IMediaOrganizer
{
    // Everything temporary Depot creates starts with this and the job id, so leftovers are
    // recognized and nothing else is ever removed.
    public const string TemporaryPrefix = ".depot-job-";

    // Space left free on the destination when copying.
    public const long FreeSpaceMarginBytes = 64L * 1024 * 1024;

    public Task<FolderReport> InspectFolderAsync(string path, FolderAccess access, CancellationToken cancellationToken)
    {
        var mountPoint = diskSpace.MountPointOf(path);
        if (!Directory.Exists(path))
        {
            return Task.FromResult(new FolderReport(path, false, false, false, mountPoint, null, null));
        }

        string? error = null;
        var canRead = false;
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
            canRead = true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            error = exception.Message;
        }

        var canWrite = false;
        if (access == FolderAccess.Write)
        {
            // A probe file is the only reliable check: permissions, read-only mounts and ACLs
            // all show up here. It's hidden and removed at once.
            var probe = Path.Join(path, $".depot-write-test-{Guid.NewGuid():N}");
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                }

                canWrite = true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                error ??= exception.Message;
            }
        }

        return Task.FromResult(new FolderReport(path, true, canRead, canWrite, mountPoint, diskSpace.AvailableBytes(path), error));
    }

    public Task<IReadOnlyList<string>> ListFoldersAsync(string path, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> folders = [];
        try
        {
            if (Directory.Exists(path))
            {
                folders = Directory.EnumerateDirectories(path)
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .Where(name => !name.StartsWith('.'))
                    .Order(StringComparer.Ordinal)
                    .ToList();
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            logger.LogWarning("Couldn't list the folders in {Path}: {Reason}", path, exception.Message);
        }

        return Task.FromResult(folders);
    }

    // The video's folder is shared with earlier imports (other episodes, or other videos of the
    // same movie), so the file is placed on its own without ever replacing an existing one.
    public async Task<ImportOutcome> ImportAsync(ImportRequest request, ExpectFileAsync expectFile, CancellationToken cancellationToken)
    {
        var problem = Validate(request);
        if (problem is not null)
        {
            logger.LogWarning("Import of job {JobId} refused: {Reason}", request.JobId, problem);
            return ImportOutcome.Failed(problem);
        }

        var destination = request.Video.DestinationPath;
        var folder = Path.GetDirectoryName(destination)!;
        if (Exists(destination))
        {
            return Conflict(request, destination);
        }

        var createdFolders = new List<string>();
        var placedSubtitles = new List<string>();
        try
        {
            CreateFolders(request.LibraryRoot, folder, createdFolders);
            // Subtitles first: once the video is in place, so is everything that comes with it,
            // which is what recovery relies on when it finds the video after a crash.
            var subtitles = await PlaceSubtitlesAsync(request, folder, placedSubtitles, cancellationToken);
            var usedMode = await PlaceAsync(
                request.Video.SourcePath, destination, request.Video.SizeBytes, request.Mode,
                TemporaryPath(folder, request.JobId, "v"), expectFile, cancellationToken);

            var final = new FileInfo(destination);
            if (!final.Exists || final.Length != request.Video.SizeBytes)
            {
                Undo([destination, .. placedSubtitles], createdFolders);
                return ImportOutcome.Failed($"{destination} isn't complete in the library after the import.");
            }

            return ImportOutcome.Completed(usedMode, subtitles);
        }
        catch (DestinationTaken taken)
        {
            Undo(placedSubtitles, createdFolders);
            return Conflict(request, taken.Path);
        }
        catch (ImportFailure failure)
        {
            Undo(placedSubtitles, createdFolders);
            return ImportOutcome.Failed(failure.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Undo(placedSubtitles, createdFolders);
            return ImportOutcome.Failed(Describe(exception, folder));
        }
        catch
        {
            // Cancelled, or the job couldn't be saved: the video wasn't published.
            Undo(placedSubtitles, createdFolders);
            throw;
        }
    }

    // Places each subtitle the way the video is placed. A subtitle never holds the video back
    // and never replaces anything: one already there with the same content counts as imported
    // (a repeated import, or a copy shipped twice in the torrent), and one that can't be placed
    // is left out and named in the returned note, which also lists those imported.
    private async Task<string?> PlaceSubtitlesAsync(ImportRequest request, string folder, List<string> placed, CancellationToken cancellationToken)
    {
        var imported = new List<string>();
        var skipped = new List<string>();
        for (var index = 0; index < request.Subtitles.Count; index++)
        {
            var subtitle = request.Subtitles[index];
            var name = Path.GetFileName(subtitle.DestinationPath);
            if (ValidateSubtitle(request, subtitle) is { } problem)
            {
                skipped.Add($"{name} ({problem.TrimEnd('.')})");
                continue;
            }

            try
            {
                if (!Exists(subtitle.DestinationPath))
                {
                    await PlaceAsync(
                        subtitle.SourcePath, subtitle.DestinationPath, subtitle.SizeBytes, request.Mode,
                        TemporaryPath(folder, request.JobId, $"s{index}"), null, cancellationToken);
                    placed.Add(subtitle.DestinationPath);
                }
                else if (!await SameContentAsync(subtitle.SourcePath, subtitle.DestinationPath, cancellationToken))
                {
                    throw new DestinationTaken(subtitle.DestinationPath);
                }

                if (!imported.Contains(name, StringComparer.Ordinal))
                {
                    imported.Add(name);
                }
            }
            catch (DestinationTaken)
            {
                skipped.Add($"{name} (a different file with that name is already there; nothing was overwritten)");
            }
            catch (ImportFailure failure)
            {
                skipped.Add($"{name} ({failure.Message.TrimEnd('.')})");
            }
        }

        var notes = new List<string>();
        if (imported.Count > 0)
        {
            notes.Add($"Subtitles imported: {string.Join(", ", imported)}.");
        }

        if (skipped.Count > 0)
        {
            notes.Add($"Subtitles left out: {string.Join("; ", skipped)}.");
        }

        return notes.Count == 0 ? null : string.Join(' ', notes);
    }

    // Subtitles are small, so comparing them whole is cheap. Only regular files are compared.
    private static async Task<bool> SameContentAsync(string first, string second, CancellationToken cancellationToken)
    {
        var secondInfo = new FileInfo(second);
        if (!secondInfo.Exists || secondInfo.LinkTarget is not null || secondInfo.Length != new FileInfo(first).Length)
        {
            return false;
        }

        const int chunk = 64 * 1024;
        await using var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read, chunk, FileOptions.Asynchronous);
        await using var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read, chunk, FileOptions.Asynchronous);
        var left = new byte[chunk];
        var right = new byte[chunk];
        while (true)
        {
            var read = await a.ReadAtLeastAsync(left, chunk, throwOnEndOfStream: false, cancellationToken);
            var other = await b.ReadAtLeastAsync(right, chunk, throwOnEndOfStream: false, cancellationToken);
            if (read != other || !left.AsSpan(0, read).SequenceEqual(right.AsSpan(0, other)))
            {
                return false;
            }

            if (read < chunk)
            {
                return true;
            }
        }
    }

    public Task<RecoveryOutcome> RecoverAsync(RecoveryRequest request, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(request.DestinationPath)!;
        if (Directory.Exists(folder))
        {
            foreach (var leftover in Directory.EnumerateFiles(folder, $"{TemporaryPrefix}{request.JobId}-*.tmp"))
            {
                RemoveTemporary(leftover);
            }
        }

        var destination = request.DestinationPath;
        var video = new FileInfo(destination);
        if (!video.Exists)
        {
            return Task.FromResult(new RecoveryOutcome(RecoveryResult.NotStarted, null));
        }

        // A name and a size could belong to any file: an older one, a duplicate, or another
        // job's. Only the identity recorded before publishing shows this import put it there. The
        // video is published under its final name only once it's complete, so if it's ours, it's whole.
        var fileId = transfer.FileId(destination);
        if (request.ExpectedFileId is null || fileId is null)
        {
            return Task.FromResult(new RecoveryOutcome(
                RecoveryResult.Failed,
                $"{destination} is in the library, but Depot can't confirm this import wrote it, so it was left alone. If it's this video, skip the job; otherwise move it away, then retry."));
        }

        if (fileId != request.ExpectedFileId)
        {
            return Task.FromResult(new RecoveryOutcome(
                RecoveryResult.Failed,
                $"{destination} is in the library, but it isn't the file this import wrote, so it was left alone. If it's this video, skip the job; otherwise move it away, then retry."));
        }

        return Task.FromResult(video.Length == request.SizeBytes
            ? new RecoveryOutcome(RecoveryResult.Completed, null)
            : new RecoveryOutcome(
                RecoveryResult.Failed,
                $"{destination} exists but has {video.Length} bytes instead of {request.SizeBytes}. Check it, then retry."));
    }

    // Hardlinks when the mode allows and the system agrees, otherwise copies. A hardlink goes
    // straight to its final name (the system refuses existing names); a copy is written under
    // temporaryPath first, checked, and moved in without overwriting. Either way, the identity
    // of the video to publish is saved with the job before it can appear at the destination;
    // subtitles pass no expectFile.
    private async Task<ImportMode> PlaceAsync(
        string source,
        string destination,
        long size,
        ImportMode mode,
        string temporaryPath,
        ExpectFileAsync? expectFile,
        CancellationToken cancellationToken)
    {
        if (mode != ImportMode.Copy)
        {
            // A hardlink is the downloaded file itself under a second name.
            await ExpectAsync(source, expectFile, cancellationToken);
            var link = transfer.TryCreateHardLink(source, destination);
            switch (link)
            {
                case HardLinkResult.Created:
                    logger.LogInformation("Hardlinked {Source} to {Destination}.", source, destination);
                    return ImportMode.Hardlink;
                case HardLinkResult.AlreadyExists:
                    throw new DestinationTaken(destination);
                case var _ when mode == ImportMode.Hardlink:
                    throw new ImportFailure(HardLinkProblem(link, source, destination));
                default:
                    logger.LogInformation("No hardlink for {Destination} ({Reason}); copying instead.", destination, link);
                    break;
            }
        }

        RemoveTemporary(temporaryPath);
        try
        {
            // The move below keeps the temporary file's identity.
            await CopyAsync(source, temporaryPath, size, () => ExpectAsync(temporaryPath, expectFile, cancellationToken), cancellationToken);
            try
            {
                File.Move(temporaryPath, destination, overwrite: false);
            }
            catch (IOException) when (Exists(destination))
            {
                throw new DestinationTaken(destination);
            }
        }
        finally
        {
            RemoveTemporary(temporaryPath);
        }

        logger.LogInformation("Copied {Source} to {Destination}.", source, destination);
        return ImportMode.Copy;
    }

    // Where the system can't tell a file's identity the import still goes on, but a crash right
    // after it is left for a person to check.
    private async Task ExpectAsync(string path, ExpectFileAsync? expectFile, CancellationToken cancellationToken)
    {
        if (expectFile is null)
        {
            return;
        }

        if (transfer.FileId(path) is { } fileId)
        {
            await expectFile(fileId, cancellationToken);
        }
        else
        {
            logger.LogWarning("Couldn't read the identity of {Path}; if Depot stops during this import, a person must check it.", path);
        }
    }

    private async Task CopyAsync(string source, string destination, long size, Func<Task> created, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(destination)!;
        var available = diskSpace.AvailableBytes(folder);
        if (available is not null && available < size + FreeSpaceMarginBytes)
        {
            throw new ImportFailure(
                $"Not enough free space in {folder}: the copy needs {FormatBytes(size)} and only {FormatBytes(available.Value)} is free.");
        }

        await transfer.CopyAsync(source, destination, created, cancellationToken);
        var copied = new FileInfo(destination).Length;
        if (copied != size)
        {
            throw new ImportFailure($"The copy of {source} has {copied} bytes instead of {size}. Nothing was published.");
        }
    }

    // Validates every path again, independently of the caller: the source must be a regular
    // file inside the torrent's folder, and the destination must stay inside the library, in
    // the layout Jellyfin expects (Movie/video or Series/Season/video).
    private static string? Validate(ImportRequest request)
    {
        if (!Path.IsPathFullyQualified(request.LibraryRoot) || !Directory.Exists(request.LibraryRoot))
        {
            return $"The library folder {request.LibraryRoot} doesn't exist.";
        }

        var file = request.Video;
        var destination = Path.GetFullPath(file.DestinationPath);
        var videoFolder = Path.GetDirectoryName(destination);
        var relativeFolder = videoFolder is null ? ".." : Path.GetRelativePath(request.LibraryRoot, videoFolder);
        var depth = request.MediaType == MediaType.Movie ? 1 : 2;
        var segments = relativeFolder.Split(Path.DirectorySeparatorChar);
        if (segments.Length != depth || segments.Any(segment => segment is "." or ".." || segment.StartsWith('.')))
        {
            return $"{file.DestinationPath} isn't laid out the way the library expects, so it was refused.";
        }

        if (Path.GetFileName(destination).StartsWith('.'))
        {
            return $"{file.DestinationPath} isn't a valid destination, so it was refused.";
        }

        if (FindLinkedFolder(request.LibraryRoot, videoFolder!) is { } linkedDestination)
        {
            return $"{linkedDestination} in the library is a symbolic link, so the import was refused.";
        }

        return SourceProblem(request, file);
    }

    // A subtitle goes next to the video (whose folder is validated above), under a visible name,
    // and comes from the torrent's folder like the video.
    private static string? ValidateSubtitle(ImportRequest request, ImportFile subtitle)
    {
        var destination = Path.GetFullPath(subtitle.DestinationPath);
        if (Path.GetDirectoryName(destination) != Path.GetDirectoryName(Path.GetFullPath(request.Video.DestinationPath))
            || Path.GetFileName(destination).StartsWith('.'))
        {
            return "its destination isn't next to the video";
        }

        return SourceProblem(request, subtitle);
    }

    // A source must be a regular file inside the torrent's folder, reached through real folders.
    private static string? SourceProblem(ImportRequest request, ImportFile file)
    {
        var source = Path.GetFullPath(file.SourcePath);
        if (!LibraryPaths.IsSameOrInside(source, request.SourceRoot) || source == Path.TrimEndingDirectorySeparator(request.SourceRoot))
        {
            return $"{file.SourcePath} is outside the torrent's folder ({request.SourceRoot}), so it was refused.";
        }

        var info = new FileInfo(source);
        if (!info.Exists)
        {
            return $"The downloaded file {source} isn't there. Check the path mapping, or whether the file was moved.";
        }

        if (info.LinkTarget is not null)
        {
            return $"{source} is a symbolic link. Depot only imports regular files.";
        }

        // The checks above compare paths as text, so a linked folder on the way would lead
        // somewhere else entirely. The roots themselves are configured by a person and may be links.
        if (!LibraryPaths.IsSameOrInside(source, request.DownloadsRoot))
        {
            return $"{file.SourcePath} is outside the mapped downloads folder ({request.DownloadsRoot}), so it was refused.";
        }

        if (FindLinkedFolder(request.DownloadsRoot, Path.GetDirectoryName(source)!) is { } linkedSource)
        {
            return $"{linkedSource} is a symbolic link. Depot only reads downloads through real folders.";
        }

        if (info.Length != file.SizeBytes)
        {
            return $"{source} has {info.Length} bytes, but qBittorrent reports {file.SizeBytes}. The torrent may be rechecking; retry later.";
        }

        return null;
    }

    // The first folder from folder up to root (root excluded) that is a symbolic link, or null.
    // Folders that don't exist yet are fine: Depot creates them as real folders.
    private static string? FindLinkedFolder(string root, string folder)
    {
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        for (var current = folder; current is not null && current != trimmedRoot && LibraryPaths.IsSameOrInside(current, trimmedRoot);
             current = Path.GetDirectoryName(current))
        {
            if (new DirectoryInfo(current).LinkTarget is not null)
            {
                return current;
            }
        }

        return null;
    }

    private static void CreateFolders(string root, string folder, List<string> created)
    {
        var missing = new Stack<string>();
        for (var current = folder; !string.Equals(current, root, StringComparison.Ordinal) && !Directory.Exists(current);
             current = Path.GetDirectoryName(current)!)
        {
            missing.Push(current);
        }

        while (missing.Count > 0)
        {
            var next = missing.Pop();
            Directory.CreateDirectory(next);
            created.Add(next);
        }
    }

    // Removes what this import added: the files it placed, then the folders it created if they're
    // still empty. A hardlink removed this way leaves the downloaded original untouched.
    private void Undo(IEnumerable<string> placedFiles, List<string> createdFolders)
    {
        foreach (var placedFile in placedFiles)
        {
            TryDelete(() => File.Delete(placedFile));
        }

        foreach (var folder in Enumerable.Reverse(createdFolders))
        {
            TryDelete(() =>
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                }
            });
        }
    }

    private void RemoveTemporary(string path)
    {
        if (!Path.GetFileName(path).StartsWith(TemporaryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to remove {path}: it isn't one of Depot's temporary files.");
        }

        TryDelete(() =>
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        });
    }

    private void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't clean up after an import: {Reason}", exception.Message);
        }
    }

    private string HardLinkProblem(HardLinkResult result, string source, string destination) => result switch
    {
        HardLinkResult.CrossDevice =>
            $"Hardlink mode is on, but the download ({diskSpace.MountPointOf(source) ?? source}) and the library ({diskSpace.MountPointOf(destination) ?? destination}) are on different filesystems. Use Auto or Copy, or keep both on the same filesystem.",
        HardLinkResult.NotPermitted =>
            "Hardlink mode is on, but the system refused the hardlink. With protected_hardlinks enabled, Depot's user must own the downloaded file or be able to write to it. Use Auto or Copy, or run Depot as the user that owns the downloads.",
        HardLinkResult.AccessDenied => $"Permission denied creating a hardlink in {Path.GetDirectoryName(destination)}.",
        HardLinkResult.NotSupported => "Hardlink mode is on, but this system or filesystem doesn't support hardlinks. Use Auto or Copy.",
        _ => "Hardlink mode is on, but the hardlink couldn't be created. Use Auto or Copy.",
    };

    private static ImportOutcome Conflict(ImportRequest request, string path) => ImportOutcome.Conflict(
        $"{Path.GetRelativePath(request.LibraryRoot, path)} already exists in the library. Nothing was overwritten. Move that file away, then retry.");

    private static string Describe(Exception exception, string folder) => exception switch
    {
        UnauthorizedAccessException => $"Permission denied writing to {folder}. Check that Depot's user can write there.",
        // ENOSPC
        IOException { HResult: 28 } => $"The disk holding {folder} is full.",
        _ => exception.Message,
    };

    // "v" for the video, "s0", "s1"... for its subtitles; recovery removes them all by the job id.
    private static string TemporaryPath(string folder, int jobId, string part) => Path.Join(folder, $"{TemporaryPrefix}{jobId}-{part}.tmp");

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    private sealed class ImportFailure(string message) : Exception(message);

    private sealed class DestinationTaken(string path) : Exception(path)
    {
        public string Path { get; } = path;
    }
}
