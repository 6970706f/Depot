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
    public async Task<ImportOutcome> ImportAsync(ImportRequest request, CancellationToken cancellationToken)
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
        try
        {
            CreateFolders(request.LibraryRoot, folder, createdFolders);
            var usedMode = await PlaceAsync(
                request.Video.SourcePath, destination, request.Video.SizeBytes, request.Mode,
                TemporaryPath(folder, request.JobId), cancellationToken);

            var final = new FileInfo(destination);
            if (!final.Exists || final.Length != request.Video.SizeBytes)
            {
                Undo(destination, createdFolders);
                return ImportOutcome.Failed($"{destination} isn't complete in the library after the import.");
            }

            return ImportOutcome.Completed(usedMode);
        }
        catch (DestinationTaken taken)
        {
            Undo(null, createdFolders);
            return Conflict(request, taken.Path);
        }
        catch (ImportFailure failure)
        {
            Undo(null, createdFolders);
            return ImportOutcome.Failed(failure.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Undo(null, createdFolders);
            return ImportOutcome.Failed(Describe(exception, folder));
        }
        catch (OperationCanceledException)
        {
            Undo(null, createdFolders);
            throw;
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

        // The video is published under its final name only once it's complete, so its presence
        // means the import finished.
        var video = new FileInfo(request.DestinationPath);
        if (!video.Exists)
        {
            return Task.FromResult(new RecoveryOutcome(RecoveryResult.NotStarted, null));
        }

        return Task.FromResult(video.Length == request.SizeBytes
            ? new RecoveryOutcome(RecoveryResult.Completed, null)
            : new RecoveryOutcome(
                RecoveryResult.Failed,
                $"{request.DestinationPath} exists but has {video.Length} bytes instead of {request.SizeBytes}. Check it, then retry."));
    }

    // Hardlinks when the mode allows and the system agrees, otherwise copies. A hardlink goes
    // straight to its final name (the system refuses existing names); a copy is written under
    // temporaryPath first, checked, and moved in without overwriting.
    private async Task<ImportMode> PlaceAsync(
        string source,
        string destination,
        long size,
        ImportMode mode,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        if (mode != ImportMode.Copy)
        {
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
            await CopyAsync(source, temporaryPath, size, cancellationToken);
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

    private async Task CopyAsync(string source, string destination, long size, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(destination)!;
        var available = diskSpace.AvailableBytes(folder);
        if (available is not null && available < size + FreeSpaceMarginBytes)
        {
            throw new ImportFailure(
                $"Not enough free space in {folder}: the copy needs {FormatBytes(size)} and only {FormatBytes(available.Value)} is free.");
        }

        await transfer.CopyAsync(source, destination, cancellationToken);
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

        if (FindLinkedFolder(request.LibraryRoot, videoFolder!) is { } linkedDestination)
        {
            return $"{linkedDestination} in the library is a symbolic link, so the import was refused.";
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

    // Removes what this import added: its own file, then the folders it created if they're
    // still empty. A hardlink removed this way leaves the downloaded original untouched.
    private void Undo(string? placedFile, List<string> createdFolders)
    {
        if (placedFile is not null)
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

    private static string TemporaryPath(string folder, int jobId) => Path.Join(folder, $"{TemporaryPrefix}{jobId}-v.tmp");

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
