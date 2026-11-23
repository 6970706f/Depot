using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Depot.Infrastructure.Storage;

// Where Depot keeps its own files: the database, the Data Protection keys and the access token.
// The folder is readable only by the user running Depot, because the keys that decrypt the
// stored passwords live in it unencrypted.
public sealed class StoragePaths
{
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

    public string AccessTokenPath => Path.Combine(DataDirectory, "access-token");

    public string DatabasePath => Path.Combine(DataDirectory, "depot.db");

    // Storage:DataDirectory, relative to the content root.
    public static StoragePaths From(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Storage:DataDirectory"];
        var directory = string.IsNullOrWhiteSpace(configured) ? "data" : configured;
        return new StoragePaths(Path.GetFullPath(directory, environment.ContentRootPath));
    }

    // Creates the folder, or narrows the permissions of one that already exists.
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
                + $"({Environment.UserName}). In Docker, create it before the first start and give it to that user.",
                exception);
        }
    }

    public static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
    }
}
