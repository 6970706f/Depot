namespace Depot.Infrastructure;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    // Relative paths are resolved against the API's content root.
    public string DataDirectory { get; init; } = "data";
}

// Where Depot keeps its own files, resolved once at startup.
public sealed class StoragePaths
{
    // Read and write for Depot's user only. The folder holds the database and the keys that
    // decrypt the qBittorrent password, which no other user may read.
    public const UnixFileMode PrivateFolderMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public StoragePaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        CreatePrivateFolder(dataDirectory);
        CreatePrivateFolder(KeysDirectory);
    }

    public string DataDirectory { get; }

    public string KeysDirectory => Path.Combine(DataDirectory, "keys");

    public string DatabasePath => Path.Combine(DataDirectory, "depot.db");

    // Creates the folder, or narrows the permissions of one that already exists. Elsewhere than
    // on Unix the system's own access rules apply.
    public static void CreatePrivateFolder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        try
        {
            Directory.CreateDirectory(path, PrivateFolderMode);
            File.SetUnixFileMode(path, PrivateFolderMode);
        }
        catch (UnauthorizedAccessException exception)
        {
            // Typical in Docker: a mounted folder that Docker created for root.
            throw new InvalidOperationException(
                $"Depot can't create {path} or set its permissions. The folder must belong to the user Depot runs as "
                + $"({Environment.UserName}). In Docker, create the folder before starting the container and give it "
                + "to the user set in \"user:\" (chown).",
                exception);
        }
    }

    // Narrows the permissions of a file Depot created, such as the database. Does nothing for a
    // file that isn't there yet.
    public static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
    }
}
