using System.Runtime.InteropServices;

namespace Depot.Infrastructure.Organization;

public enum HardLinkResult
{
    Created,
    AlreadyExists,
    // Source and destination are on different filesystems (or mount points).
    CrossDevice,
    // Refused by the system, typically protected_hardlinks for a file Depot doesn't own.
    NotPermitted,
    // No write access to the destination folder.
    AccessDenied,
    NotSupported,
    Failed,
}

// The two ways a file reaches the library. Kept apart from the import logic so tests can
// simulate other filesystems, full disks and failing copies.
public interface IFileTransfer
{
    // Never replaces an existing destination.
    HardLinkResult TryCreateHardLink(string sourcePath, string destinationPath);

    // Copies into a new file; fails when destinationPath already exists. created is awaited once
    // the new file exists, before any data is written to it.
    Task CopyAsync(string sourcePath, string destinationPath, Func<Task> created, CancellationToken cancellationToken);

    // The file's inode, without following symbolic links: every name of one file shares it, and
    // a rename keeps it. Null when the system can't tell.
    long? FileId(string path);
}

public sealed partial class FileTransfer : IFileTransfer
{
    private const int BufferSize = 1024 * 1024;

    // errno values on Linux.
    private const int NotPermittedError = 1;
    private const int AccessDeniedError = 13;
    private const int ExistsError = 17;
    private const int CrossDeviceError = 18;
    private const int TooManyLinksError = 31;
    private const int NotSupportedError = 95;

    // statx(2) arguments: AT_FDCWD, AT_SYMLINK_NOFOLLOW and STATX_INO.
    private const int CurrentDirectory = -100;
    private const int NoFollow = 0x100;
    private const uint InodeField = 0x100;

    public HardLinkResult TryCreateHardLink(string sourcePath, string destinationPath)
    {
        // .NET has no hardlink API; elsewhere Auto falls back to copying.
        if (!OperatingSystem.IsLinux())
        {
            return HardLinkResult.NotSupported;
        }

        if (Link(sourcePath, destinationPath) == 0)
        {
            return HardLinkResult.Created;
        }

        return Marshal.GetLastPInvokeError() switch
        {
            ExistsError => HardLinkResult.AlreadyExists,
            CrossDeviceError => HardLinkResult.CrossDevice,
            NotPermittedError => HardLinkResult.NotPermitted,
            AccessDeniedError => HardLinkResult.AccessDenied,
            NotSupportedError or TooManyLinksError => HardLinkResult.NotSupported,
            _ => HardLinkResult.Failed,
        };
    }

    public async Task CopyAsync(string sourcePath, string destinationPath, Func<Task> created, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
        await created();
        await input.CopyToAsync(output, BufferSize, cancellationToken);
        await output.FlushAsync(cancellationToken);
        // The file must really be on disk before it's published under its final name.
        output.Flush(flushToDisk: true);
    }

    public long? FileId(string path)
    {
        // .NET doesn't expose inodes.
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            if (Statx(CurrentDirectory, path, NoFollow, InodeField, out var status) != 0 || (status.Mask & InodeField) == 0)
            {
                return null;
            }

            return unchecked((long)status.Inode);
        }
        catch (EntryPointNotFoundException)
        {
            // glibc older than 2.28.
            return null;
        }
    }

    // libc.so.6 is already loaded by the runtime on any glibc system.
    [LibraryImport("libc.so.6", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string existingPath, string newPath);

    [LibraryImport("libc.so.6", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int directory, string path, int flags, uint mask, out StatxBuffer buffer);

    // struct statx from <linux/stat.h>, which has the same layout on every architecture. Only
    // the fields Depot reads are declared.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)]
        public uint Mask;

        [FieldOffset(32)]
        public ulong Inode;
    }
}

public interface IDiskSpace
{
    // Null when it can't be determined.
    long? AvailableBytes(string path);

    // The mount point holding path. Hardlinks only work within one mount point.
    string? MountPointOf(string path);
}

public sealed class DriveDiskSpace : IDiskSpace
{
    public long? AvailableBytes(string path)
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

    public string? MountPointOf(string path) => FindDrive(path)?.RootDirectory.FullName;

    // The mount point with the longest path that contains the folder.
    private static DriveInfo? FindDrive(string path)
    {
        var fullPath = Path.GetFullPath(path);
        DriveInfo? best = null;
        foreach (var drive in DriveInfo.GetDrives())
        {
            var root = drive.RootDirectory.FullName;
            var contains = root == "/"
                || fullPath == Path.TrimEndingDirectorySeparator(root)
                || fullPath.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            if (contains && (best is null || root.Length > best.RootDirectory.FullName.Length))
            {
                best = drive;
            }
        }

        return best;
    }
}
