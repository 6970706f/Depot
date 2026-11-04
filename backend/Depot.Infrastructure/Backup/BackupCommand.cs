using Depot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Depot.Infrastructure.Backup;

// "dotnet Depot.Api.dll --backup <folder>" and "--restore <archive>", with the same configuration
// (and so the same data folder) as the app, but without starting it. Returns the exit code.
public static class BackupCommand
{
    public static bool Handles(string[] args) => args is ["--backup" or "--restore", _];

    public static async Task<int> RunAsync(
        string[] args,
        IConfiguration configuration,
        IHostEnvironment environment,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        var storage = StoragePaths.From(configuration, environment);
        var path = Path.GetFullPath(args[1]);
        try
        {
            if (args[0] == "--backup")
            {
                var archive = await DepotBackup.CreateAsync(storage, path, DateTimeOffset.UtcNow, cancellationToken);
                await output.WriteLineAsync($"Backup of {storage.DataDirectory} written to {archive}.");
                await output.WriteLineAsync("It holds the keys that decrypt the qBittorrent password: keep it as private as the data folder.");
                return 0;
            }

            var manifest = await DepotBackup.RestoreAsync(storage, path, KnownMigrations(), cancellationToken);
            await output.WriteLineAsync(
                $"Restored the backup from {manifest.CreatedAt:u} ({manifest.Files.Count} files) into {storage.DataDirectory}. Start Depot to use it.");
            return 0;
        }
        catch (Exception exception) when (exception is BackupException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await output.WriteLineAsync($"{(args[0] == "--backup" ? "Backup" : "Restore")} failed: {exception.Message}");
            return 1;
        }
    }

    // The migrations this build knows, read from the assembly; no database is opened.
    public static IReadOnlyCollection<string> KnownMigrations()
    {
        var options = new DbContextOptionsBuilder<DepotDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var context = new DepotDbContext(options);
        return context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
    }
}
