namespace Depot.Application.Library;

// What Depot can tell about one folder. MountPoint is null when it can't be determined;
// hardlinks only work between folders on the same mount point.
public sealed record FolderReport(
    string Path,
    bool Exists,
    bool CanRead,
    bool CanWrite,
    string? MountPoint,
    long? AvailableBytes,
    string? Error);

// The file system as the library sees it.
public interface ILibraryFileSystem
{
    // checkWrite creates and removes a hidden probe file, the only reliable write check.
    FolderReport InspectFolder(string path, bool checkWrite);

    // The visible folders directly inside path, sorted; empty when path doesn't exist.
    IReadOnlyList<string> ListFolders(string path);
}

public interface IPathEnvironment
{
    // $HOME, used to expand "~/" in configured paths. Empty when unset.
    string HomeDirectory { get; }
}
