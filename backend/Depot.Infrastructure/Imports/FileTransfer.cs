using System.Runtime.InteropServices;

namespace Depot.Infrastructure.Imports;

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
// simulate other filesystems and failing copies.
public interface IFileTransfer
{
    // Never replaces an existing destination.
    HardLinkResult TryCreateHardLink(string sourcePath, string destinationPath);

    // Copies into a new file; fails when destinationPath already exists.
    Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);
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

    public async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
        await input.CopyToAsync(output, BufferSize, cancellationToken);
        await output.FlushAsync(cancellationToken);
        // The file must really be on disk before it's published under its final name.
        output.Flush(flushToDisk: true);
    }

    // libc.so.6 is already loaded by the runtime on any glibc system.
    [LibraryImport("libc.so.6", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string existingPath, string newPath);
}
