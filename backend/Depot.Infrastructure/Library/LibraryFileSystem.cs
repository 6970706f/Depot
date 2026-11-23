using Depot.Application.Library;

namespace Depot.Infrastructure.Library;

public sealed class LibraryFileSystem : ILibraryFileSystem
{
    public FolderReport InspectFolder(string path, bool checkWrite)
    {
        var mountPoint = MountPointOf(path);
        if (!Directory.Exists(path))
        {
            return new FolderReport(path, false, false, false, mountPoint, null, null);
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
        if (checkWrite)
        {
            // Permissions, read-only mounts and ACLs all show up here. Hidden and removed at once.
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

        return new FolderReport(path, true, canRead, canWrite, mountPoint, AvailableBytes(path), error);
    }

    public IReadOnlyList<string> ListFolders(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateDirectories(path)
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .Where(name => !name.StartsWith('.'))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    public static long? AvailableBytes(string path)
    {
        try
        {
            return FindDrive(path)?.AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The mount point holding path. Hardlinks only work within one mount point.
    public static string? MountPointOf(string path) => FindDrive(path)?.RootDirectory.FullName;

    // The mount point with the longest path that contains the folder.
    private static DriveInfo? FindDrive(string path)
    {
        var fullPath = Path.GetFullPath(path);
        DriveInfo? best = null;
        foreach (var drive in DriveInfo.GetDrives())
        {
            var root = Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName);
            var contains = drive.RootDirectory.FullName == "/"
                || fullPath == root
                || fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            if (contains && (best is null || root.Length > Path.TrimEndingDirectorySeparator(best.RootDirectory.FullName).Length))
            {
                best = drive;
            }
        }

        return best;
    }
}
