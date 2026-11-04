using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Depot.Infrastructure.Backup;

// What a backup holds, written next to the files as depot-backup.json.
public sealed record BackupManifest(int Format, DateTimeOffset CreatedAt, string? DepotVersion, string? LastMigration, IReadOnlyList<string> Files);

// Backs up and restores everything a Depot installation needs to start again on another
// server: the database, the Data Protection keys that decrypt the stored qBittorrent password,
// and the access token. The media folders aren't part of it, and neither are SQLite's journal
// or anything else in the data folder.
//
// The database is copied with SQLite's online backup, which reads one consistent state even
// while Depot writes to it, so a backup never needs Depot to stop. A restore does: it only
// fills a data folder that has no installation in it, and never replaces one.
public static class DepotBackup
{
    public const int Format = 1;
    public const string ManifestName = "depot-backup.json";
    public const string DatabaseName = "depot.db";
    public const string KeysFolder = "keys";
    public const string AccessTokenName = "access-token";

    // SQLITE_BUSY and SQLITE_LOCKED, tried again for up to about 30 seconds.
    private const int BusyError = 5;
    private const int LockedError = 6;
    private const int BusyAttempts = 300;
    private static readonly TimeSpan BusyDelay = TimeSpan.FromMilliseconds(100);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // Writes depot-backup-<UTC time>.tar.gz into folder, readable only by Depot's user (it holds
    // the keys and the encrypted password together), and returns its path.
    public static async Task<string> CreateAsync(StoragePaths storage, string folder, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!File.Exists(storage.DatabasePath))
        {
            throw new BackupException($"There's no database at {storage.DatabasePath}. Check Storage:DataDirectory.");
        }

        StoragePaths.CreatePrivateFolder(folder);
        var name = $"depot-backup-{now.UtcDateTime:yyyyMMdd-HHmmss}";
        var archive = Path.Join(folder, $"{name}.tar.gz");
        if (File.Exists(archive))
        {
            throw new BackupException($"{archive} already exists.");
        }

        var staging = Path.Join(folder, $".{name}.partial");
        StoragePaths.CreatePrivateFolder(staging);
        try
        {
            var database = Path.Join(staging, DatabaseName);
            await CopyDatabaseAsync(storage.DatabasePath, database, cancellationToken);
            var lastMigration = await CheckDatabaseAsync(database, cancellationToken);

            var files = new List<string> { DatabaseName };
            var keys = Path.Join(staging, KeysFolder);
            StoragePaths.CreatePrivateFolder(keys);
            foreach (var key in Directory.EnumerateFiles(storage.KeysDirectory, "*.xml").Order(StringComparer.Ordinal))
            {
                File.Copy(key, Path.Join(keys, Path.GetFileName(key)));
                files.Add($"{KeysFolder}/{Path.GetFileName(key)}");
            }

            if (File.Exists(storage.AccessTokenPath))
            {
                File.Copy(storage.AccessTokenPath, Path.Join(staging, AccessTokenName));
                files.Add(AccessTokenName);
            }

            var manifest = new BackupManifest(Format, now, DepotVersion(), lastMigration, files);
            await File.WriteAllTextAsync(Path.Join(staging, ManifestName), JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                StoragePaths.RestrictFile(file);
            }

            // Written under a temporary name and moved in once complete, so a half-written
            // archive is never mistaken for a backup.
            var partial = $"{archive}.partial";
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write))
            {
                StoragePaths.RestrictFile(partial);
                await using var gzip = new GZipStream(output, CompressionLevel.Optimal);
                await TarFile.CreateFromDirectoryAsync(staging, gzip, includeBaseDirectory: false, cancellationToken);
            }

            File.Move(partial, archive, overwrite: false);
            return archive;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
            DeleteIfThere($"{archive}.partial");
        }
    }

    // Unpacks a backup into a data folder that has no database, keys or token yet. The archive
    // is checked first (the manifest, the database's integrity, a version this Depot knows), and
    // nothing is put in place unless all of it passes. knownMigrations are this build's migrations:
    // a backup from a newer Depot is refused, an older one is brought up to date on the next start.
    public static async Task<BackupManifest> RestoreAsync(
        StoragePaths storage,
        string archive,
        IReadOnlyCollection<string> knownMigrations,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(archive))
        {
            throw new BackupException($"There's no backup at {archive}.");
        }

        var existing = new[] { storage.DatabasePath, $"{storage.DatabasePath}-journal", $"{storage.DatabasePath}-wal", storage.AccessTokenPath }
            .Where(File.Exists)
            .Concat(Directory.EnumerateFileSystemEntries(storage.KeysDirectory))
            .ToList();
        if (existing.Count > 0)
        {
            throw new BackupException(
                $"{storage.DataDirectory} already has an installation ({string.Join(", ", existing.Select(Path.GetFileName))}). "
                + "Nothing was changed. Stop Depot and restore into an empty data folder, or move those files away first.");
        }

        var staging = Path.Join(storage.DataDirectory, $".restore-{Guid.NewGuid():N}");
        StoragePaths.CreatePrivateFolder(staging);
        try
        {
            await using (var input = File.OpenRead(archive))
            await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            {
                await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: false, cancellationToken);
            }

            var manifest = await ReadManifestAsync(staging, cancellationToken);
            var database = RegularFile(Path.Join(staging, DatabaseName))
                ?? throw new BackupException("The backup has no database.");
            var lastMigration = await CheckDatabaseAsync(database, cancellationToken);
            if (lastMigration is not null && !knownMigrations.Contains(lastMigration))
            {
                throw new BackupException(
                    $"The backup was made by a newer Depot (its database is at {lastMigration}). Install that version or a later one, then restore.");
            }

            var keys = manifest.Files.Where(file => file.StartsWith($"{KeysFolder}/", StringComparison.Ordinal)).ToList();
            foreach (var file in manifest.Files.Where(file => file != DatabaseName))
            {
                if (RegularFile(Path.Join(staging, file)) is null)
                {
                    throw new BackupException($"The backup lists {file}, but it isn't in it.");
                }
            }

            // Back to WAL, as Depot creates its database. Closing the only connection leaves a
            // single file again.
            await using (var connection = new SqliteConnection(ConnectionString(database, SqliteOpenMode.ReadWrite)))
            {
                await connection.OpenAsync(cancellationToken);
                await SetJournalModeAsync(connection, "WAL", cancellationToken);
            }

            // The database goes in last: until it's there, Depot doesn't see an installation.
            foreach (var key in keys)
            {
                var target = Path.Join(storage.KeysDirectory, Path.GetFileName(key));
                File.Move(Path.Join(staging, key), target);
                StoragePaths.RestrictFile(target);
            }

            if (manifest.Files.Contains(AccessTokenName))
            {
                File.Move(Path.Join(staging, AccessTokenName), storage.AccessTokenPath);
                StoragePaths.RestrictFile(storage.AccessTokenPath);
            }

            File.Move(database, storage.DatabasePath);
            StoragePaths.RestrictFile(storage.DatabasePath);
            return manifest;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static async Task CopyDatabaseAsync(string source, string target, CancellationToken cancellationToken)
    {
        // Read-write so SQLite can roll back a transaction a crash left behind; pooling off so
        // both files are closed as soon as the copy is done.
        await using var from = new SqliteConnection(ConnectionString(source, SqliteOpenMode.ReadWrite));
        await using var to = new SqliteConnection(ConnectionString(target, SqliteOpenMode.ReadWriteCreate));
        await from.OpenAsync(cancellationToken);
        await to.OpenAsync(cancellationToken);

        // The copy is one step under a shared lock; while Depot commits, the file is briefly
        // locked, so it's tried again for a while rather than failing.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                from.BackupDatabase(to);
                break;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is BusyError or LockedError && attempt < BusyAttempts)
            {
                await Task.Delay(BusyDelay, cancellationToken);
            }
        }

        // Depot's database is in WAL mode, and so is the copy. The backup is made a single,
        // self-contained file; a restore turns WAL back on.
        await SetJournalModeAsync(to, "DELETE", cancellationToken);
    }

    private static async Task SetJournalModeAsync(SqliteConnection connection, string mode, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA journal_mode = {mode};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Returns the last applied migration, or throws when SQLite finds the file damaged.
    private static async Task<string?> CheckDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadOnly));
            await connection.OpenAsync(cancellationToken);
            await using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = (string?)await integrity.ExecuteScalarAsync(cancellationToken);
            if (result != "ok")
            {
                throw new BackupException($"The database failed SQLite's integrity check: {result}");
            }

            await using var migration = connection.CreateCommand();
            migration.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;";
            return (string?)await migration.ExecuteScalarAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw new BackupException($"The database can't be read: {exception.Message}");
        }
    }

    private static async Task<BackupManifest> ReadManifestAsync(string folder, CancellationToken cancellationToken)
    {
        var path = RegularFile(Path.Join(folder, ManifestName))
            ?? throw new BackupException($"This isn't a Depot backup: it has no {ManifestName}.");
        BackupManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
        }
        catch (JsonException)
        {
            manifest = null;
        }

        if (manifest is not { Format: Format, Files: not null })
        {
            throw new BackupException($"{ManifestName} isn't one this Depot can read.");
        }

        // Only the files a backup can hold, by these exact names: nothing else is ever put in place.
        foreach (var file in manifest.Files)
        {
            var allowed = file is DatabaseName or AccessTokenName
                || (file.StartsWith($"{KeysFolder}/", StringComparison.Ordinal)
                    && file.EndsWith(".xml", StringComparison.Ordinal)
                    && file.Count(character => character == '/') == 1
                    && !file.Contains("..", StringComparison.Ordinal));
            if (!allowed)
            {
                throw new BackupException($"{ManifestName} lists {file}, which a Depot backup never holds.");
            }
        }

        return manifest;
    }

    // The path when it's a regular file and not a link; null otherwise.
    private static string? RegularFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.LinkTarget is null ? path : null;
    }

    private static string ConnectionString(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();

    private static string? DepotVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static void DeleteIfThere(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

public sealed class BackupException(string message) : Exception(message);
