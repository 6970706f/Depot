using Depot.Application.Imports;
using Depot.Domain.Library;
using Depot.Infrastructure.Library;
using Microsoft.Extensions.Logging;

namespace Depot.Infrastructure.Imports;

// Puts downloaded videos into the library without disturbing the torrent: sources are only ever
// read or hardlinked, never moved, renamed, changed or deleted, and nothing in the library is
// overwritten. Copies are written under a temporary name and renamed into place once complete.
public sealed class MediaImporter(IFileTransfer transfer, ILogger<MediaImporter> logger) : IMediaImporter
{
    // Everything temporary Depot creates starts with this and the job id, so leftovers are
    // recognized and nothing else is ever removed.
    public const string TemporaryPrefix = ".depot-job-";

    // Space left free on the destination when copying.
    public const long FreeSpaceMarginBytes = 64L * 1024 * 1024;

    public async Task<ImportOutcome> ImportAsync(ImportRequest request, CancellationToken cancellationToken)
    {
        if (Validate(request) is { } problem)
        {
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
            // Subtitles first: once the video is in place, so is everything that comes with it.
            var note = await PlaceSubtitlesAsync(request, folder, placedSubtitles, cancellationToken);
            var usedMode = await PlaceAsync(request.Video, request.Mode, TemporaryPath(folder, request.JobId, "v"), cancellationToken);
            return ImportOutcome.Completed(usedMode, note);
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
            // Cancelled: the video wasn't published.
            Undo(placedSubtitles, createdFolders);
            throw;
        }
    }

    public bool CleanUpInterrupted(int jobId, string destinationPath)
    {
        var folder = Path.GetDirectoryName(destinationPath)!;
        if (Directory.Exists(folder))
        {
            foreach (var leftover in Directory.EnumerateFiles(folder, $"{TemporaryPrefix}{jobId}-*.tmp"))
            {
                TryDelete(() => File.Delete(leftover));
            }
        }

        // The video is published under its final name only once complete, but Depot can't tell
        // whether this import or something else put a file there, so a person decides.
        return File.Exists(destinationPath);
    }

    // Places each subtitle the way the video is placed. A subtitle never holds the video back and
    // never replaces anything: one already there with the same content counts as imported, and one
    // that can't be placed is left out and named in the returned note.
    private async Task<string?> PlaceSubtitlesAsync(ImportRequest request, string folder, List<string> placed, CancellationToken cancellationToken)
    {
        var imported = new List<string>();
        var skipped = new List<string>();
        for (var index = 0; index < request.Subtitles.Count; index++)
        {
            var subtitle = request.Subtitles[index];
            var name = Path.GetFileName(subtitle.DestinationPath);
            if (Path.GetDirectoryName(subtitle.DestinationPath) != folder || SourceProblem(request, subtitle) is not null)
            {
                skipped.Add($"{name} (not a file of this download)");
                continue;
            }

            try
            {
                if (!Exists(subtitle.DestinationPath))
                {
                    await PlaceAsync(subtitle, request.Mode, TemporaryPath(folder, request.JobId, $"s{index}"), cancellationToken);
                    placed.Add(subtitle.DestinationPath);
                }
                else if (!await SameContentAsync(subtitle.SourcePath, subtitle.DestinationPath, cancellationToken))
                {
                    throw new DestinationTaken(subtitle.DestinationPath);
                }

                imported.Add(name);
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

    // Hardlinks when the mode allows and the system agrees, otherwise copies. A hardlink goes
    // straight to its final name (the system refuses existing names); a copy is written under
    // temporaryPath first, checked, and moved in without overwriting.
    private async Task<ImportMode> PlaceAsync(ImportFile file, ImportMode mode, string temporaryPath, CancellationToken cancellationToken)
    {
        if (mode != ImportMode.Copy)
        {
            var link = transfer.TryCreateHardLink(file.SourcePath, file.DestinationPath);
            switch (link)
            {
                case HardLinkResult.Created:
                    logger.LogInformation("Hardlinked {Source} to {Destination}.", file.SourcePath, file.DestinationPath);
                    return ImportMode.Hardlink;
                case HardLinkResult.AlreadyExists:
                    throw new DestinationTaken(file.DestinationPath);
                case var _ when mode == ImportMode.Hardlink:
                    throw new ImportFailure(HardLinkProblem(link, file));
                default:
                    logger.LogInformation("No hardlink for {Destination} ({Reason}); copying instead.", file.DestinationPath, link);
                    break;
            }
        }

        var folder = Path.GetDirectoryName(file.DestinationPath)!;
        var available = LibraryFileSystem.AvailableBytes(folder);
        if (available is not null && available < file.SizeBytes + FreeSpaceMarginBytes)
        {
            throw new ImportFailure(
                $"Not enough free space in {folder}: the copy needs {FormatBytes(file.SizeBytes)} and only {FormatBytes(available.Value)} is free.");
        }

        RemoveTemporary(temporaryPath);
        try
        {
            await transfer.CopyAsync(file.SourcePath, temporaryPath, cancellationToken);
            var copied = new FileInfo(temporaryPath).Length;
            if (copied != file.SizeBytes)
            {
                throw new ImportFailure($"The copy of {file.SourcePath} has {copied} bytes instead of {file.SizeBytes}. Nothing was published.");
            }

            try
            {
                File.Move(temporaryPath, file.DestinationPath, overwrite: false);
            }
            catch (IOException) when (Exists(file.DestinationPath))
            {
                throw new DestinationTaken(file.DestinationPath);
            }
        }
        finally
        {
            RemoveTemporary(temporaryPath);
        }

        logger.LogInformation("Copied {Source} to {Destination}.", file.SourcePath, file.DestinationPath);
        return ImportMode.Copy;
    }

    // Validates every path again, independently of the caller: the source must be a regular file
    // inside the mapped downloads folder, and the destination must stay inside the library in the
    // layout Jellyfin expects.
    private static string? Validate(ImportRequest request)
    {
        if (!Directory.Exists(request.LibraryRoot))
        {
            return $"The library folder {request.LibraryRoot} doesn't exist.";
        }

        var destination = Path.GetFullPath(request.Video.DestinationPath);
        var folder = Path.GetDirectoryName(destination);
        var relative = folder is null ? ".." : Path.GetRelativePath(request.LibraryRoot, folder);
        var segments = relative.Split(Path.DirectorySeparatorChar);
        if (segments.Length is < 1 or > 2 || segments.Any(segment => segment is "." or ".." || segment.StartsWith('.'))
            || Path.GetFileName(destination).StartsWith('.'))
        {
            return $"{request.Video.DestinationPath} isn't laid out the way the library expects, so it was refused.";
        }

        if (FindLinkedFolder(request.LibraryRoot, folder!) is { } linked)
        {
            return $"{linked} in the library is a symbolic link, so the import was refused.";
        }

        return SourceProblem(request, request.Video);
    }

    // A source must be a regular file inside the mapped downloads folder, reached through real
    // folders, with the size qBittorrent reported.
    private static string? SourceProblem(ImportRequest request, ImportFile file)
    {
        var source = Path.GetFullPath(file.SourcePath);
        if (!LibraryPaths.IsSameOrInside(source, request.DownloadsRoot) || source == Path.TrimEndingDirectorySeparator(request.DownloadsRoot))
        {
            return $"{file.SourcePath} is outside the mapped downloads folder ({request.DownloadsRoot}), so it was refused.";
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

        if (FindLinkedFolder(request.DownloadsRoot, Path.GetDirectoryName(source)!) is { } linked)
        {
            return $"{linked} is a symbolic link. Depot only reads downloads through real folders.";
        }

        return info.Length != file.SizeBytes
            ? $"{source} has {info.Length} bytes, but qBittorrent reports {file.SizeBytes}. The torrent may be rechecking; retry later."
            : null;
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

    // Subtitles are small, so comparing them whole is cheap.
    private static async Task<bool> SameContentAsync(string first, string second, CancellationToken cancellationToken)
    {
        var info = new FileInfo(second);
        if (!info.Exists || info.LinkTarget is not null || info.Length != new FileInfo(first).Length)
        {
            return false;
        }

        var left = await File.ReadAllBytesAsync(first, cancellationToken);
        var right = await File.ReadAllBytesAsync(second, cancellationToken);
        return left.AsSpan().SequenceEqual(right);
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
        foreach (var placed in placedFiles)
        {
            TryDelete(() => File.Delete(placed));
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

    private static string HardLinkProblem(HardLinkResult result, ImportFile file) => result switch
    {
        HardLinkResult.CrossDevice =>
            $"Hardlink mode is on, but the download ({LibraryFileSystem.MountPointOf(file.SourcePath) ?? file.SourcePath}) and the library ({LibraryFileSystem.MountPointOf(file.DestinationPath) ?? file.DestinationPath}) are on different filesystems. Use Auto or Copy, or keep both on the same filesystem.",
        HardLinkResult.NotPermitted =>
            "Hardlink mode is on, but the system refused the hardlink. With protected_hardlinks enabled, Depot's user must own the downloaded file or be able to write to it. Use Auto or Copy, or run Depot as the user that owns the downloads.",
        HardLinkResult.AccessDenied => $"Permission denied creating a hardlink in {Path.GetDirectoryName(file.DestinationPath)}.",
        HardLinkResult.NotSupported => "Hardlink mode is on, but this system or filesystem doesn't support hardlinks. Use Auto or Copy.",
        _ => "Hardlink mode is on, but the hardlink couldn't be created. Use Auto or Copy.",
    };

    private static ImportOutcome Conflict(ImportRequest request, string path) => ImportOutcome.Failed(
        $"{Path.GetRelativePath(request.LibraryRoot, path)} already exists in the library. Nothing was overwritten. Move that file away, then retry.");

    private static string Describe(Exception exception, string folder) => exception switch
    {
        UnauthorizedAccessException => $"Permission denied writing to {folder}. Check that Depot's user can write there.",
        IOException { HResult: 28 } => $"The disk holding {folder} is full.",
        _ => exception.Message,
    };

    // "v" for the video, "s0", "s1"... for its subtitles; cleanup removes them all by the job id.
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
