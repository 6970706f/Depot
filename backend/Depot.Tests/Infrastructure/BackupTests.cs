using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Depot.Application.Organization;
using Depot.Application.Settings;
using Depot.Domain.Organization;
using Depot.Domain.Settings;
using Depot.Infrastructure;
using Depot.Infrastructure.Backup;
using Depot.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Depot.Tests.Infrastructure;

public sealed class BackupTests
{
    private const string Hash = "1111111111111111111111111111111111111111";
    private const string Token = "token-0123456789abcdefghijklmnopqrstuvwxyz";
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnInstallationIsRestoredOnAnotherServerWithItsSettingsAndPassword()
    {
        await using var original = await InstallationAsync();
        var archive = await DepotBackup.CreateAsync(original.Get<StoragePaths>(), original.Root.Combine("backups"), Now, CancellationToken.None);

        // Another server: another folder, nothing in it but the backup.
        using var otherServer = new TempDirectory();
        var archiveThere = Path.Join(otherServer.CreateFolder("incoming"), Path.GetFileName(archive));
        File.Copy(archive, archiveThere);
        var storage = new StoragePaths(otherServer.Combine("data"));
        var manifest = await DepotBackup.RestoreAsync(storage, archiveThere, BackupCommand.KnownMigrations(), CancellationToken.None);
        // One file, back in WAL mode like a database Depot creates.
        Assert.False(File.Exists($"{storage.DatabasePath}-wal"));
        Assert.Equal("wal", await QueryAsync(storage.DatabasePath, "PRAGMA journal_mode;"));
        await using var restored = await OrganizationHarness.StartOnAsync(otherServer);

        var settings = await restored.RunAsync<IQBittorrentSettingsRepository, QBittorrentSettings?>(
            repository => repository.GetAsync(CancellationToken.None));
        Assert.NotNull(settings);
        Assert.Equal("secret password", settings.Password);
        Assert.Equal("admin", settings.Username);
        var job = Assert.Single(await restored.JobsAsync());
        Assert.Equal(OrganizationJobStatus.Completed, job.Status);
        Assert.Equal(Token, await File.ReadAllTextAsync(storage.AccessTokenPath));
        Assert.Equal(Now, manifest.CreatedAt);
        Assert.Contains("depot.db", manifest.Files);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(StoragePaths.PrivateFileMode, File.GetUnixFileMode(storage.DatabasePath));
            Assert.Equal(StoragePaths.PrivateFileMode, File.GetUnixFileMode(storage.AccessTokenPath));
            Assert.All(Directory.GetFiles(storage.KeysDirectory), key => Assert.Equal(StoragePaths.PrivateFileMode, File.GetUnixFileMode(key)));
        }

        // Nothing is left from unpacking it.
        Assert.Empty(Directory.GetDirectories(storage.DataDirectory, ".restore-*"));
    }

    [Fact]
    public async Task ABackupHoldsOnlyWhatAnInstallationNeedsAndStaysPrivate()
    {
        await using var original = await InstallationAsync();
        var storage = original.Get<StoragePaths>();
        File.WriteAllText(Path.Join(storage.DataDirectory, "notes.txt"), "not Depot's");
        File.WriteAllText(Path.Join(storage.KeysDirectory, "readme.txt"), "not a key");
        var backups = Path.Join(storage.DataDirectory, "backups");

        var first = await DepotBackup.CreateAsync(storage, backups, Now, CancellationToken.None);
        var second = await DepotBackup.CreateAsync(storage, backups, Now.AddMinutes(1), CancellationToken.None);

        var entries = Entries(second);
        Assert.Equal("depot-backup-20261104-183000.tar.gz", Path.GetFileName(first));
        Assert.Contains("depot.db", entries);
        Assert.Contains("access-token", entries);
        Assert.Contains(DepotBackup.ManifestName, entries);
        Assert.Contains(entries, entry => entry.StartsWith("keys/", StringComparison.Ordinal) && entry.EndsWith(".xml", StringComparison.Ordinal));
        Assert.All(entries, entry => Assert.Matches(@"^(depot\.db|access-token|depot-backup\.json|keys/?|keys/[^/]+\.xml)$", entry));
        // The earlier backup in the data folder isn't backed up again, and no temporary file is left.
        Assert.Equal(2, Directory.GetFileSystemEntries(backups).Length);
        Assert.Throws<BackupException>(() => DepotBackup.CreateAsync(storage, backups, Now, CancellationToken.None).GetAwaiter().GetResult());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(StoragePaths.PrivateFileMode, File.GetUnixFileMode(second));
            Assert.Equal(StoragePaths.PrivateFolderMode, File.GetUnixFileMode(backups));
        }
    }

    [Fact]
    public async Task ABackupTakenWhileDepotWritesIsConsistent()
    {
        await using var original = await InstallationAsync();
        var storage = original.Get<StoragePaths>();
        await ExecuteAsync(storage.DatabasePath, "CREATE TABLE Writes (Batch INTEGER NOT NULL, Row INTEGER NOT NULL);");

        // An uncommitted change is never part of a backup.
        await using var pending = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = storage.DatabasePath, Pooling = false }.ToString());
        await pending.OpenAsync();
        await using (var transaction = await pending.BeginTransactionAsync())
        {
            await using var insert = pending.CreateCommand();
            insert.CommandText = "INSERT INTO Writes VALUES (-1, 0);";
            await insert.ExecuteNonQueryAsync();
            var during = await DepotBackup.CreateAsync(storage, original.Root.Combine("backups"), Now, CancellationToken.None);
            await transaction.CommitAsync();
            Assert.Equal(0L, await CountAsync(during, "SELECT COUNT(*) FROM Writes;"));
        }

        // Batches of ten rows committed one after another while backups are taken: each backup
        // has whole batches only.
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            var batch = 0;
            while (!stop.IsCancellationRequested)
            {
                var values = string.Join(", ", Enumerable.Range(0, 10).Select(row => $"({batch}, {row})"));
                await ExecuteAsync(storage.DatabasePath, $"BEGIN IMMEDIATE; INSERT INTO Writes VALUES {values}; COMMIT;");
                batch++;
            }

            return batch;
        });

        var archives = new List<string>();
        for (var index = 1; index <= 5; index++)
        {
            await Task.Delay(30);
            archives.Add(await DepotBackup.CreateAsync(storage, original.Root.Combine("backups"), Now.AddSeconds(index), CancellationToken.None));
        }

        await stop.CancelAsync();
        Assert.True(await writer > 0);
        foreach (var archive in archives)
        {
            var rows = await CountAsync(archive, "SELECT COUNT(*) FROM Writes WHERE Batch >= 0;");
            Assert.Equal(0, rows % 10);
        }
    }

    [Fact]
    public async Task AnExistingInstallationIsNeverReplaced()
    {
        await using var original = await InstallationAsync();
        var archive = await DepotBackup.CreateAsync(original.Get<StoragePaths>(), original.Root.Combine("backups"), Now, CancellationToken.None);
        await using var other = await OrganizationHarness.CreateAsync();
        var storage = other.Get<StoragePaths>();
        var before = await File.ReadAllBytesAsync(storage.DatabasePath);

        var error = await Assert.ThrowsAsync<BackupException>(
            () => DepotBackup.RestoreAsync(storage, archive, BackupCommand.KnownMigrations(), CancellationToken.None));

        Assert.Contains("already has an installation", error.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(storage.DatabasePath));
        Assert.Empty(Directory.GetDirectories(storage.DataDirectory, ".restore-*"));
    }

    [Fact]
    public async Task ABackupFromANewerDepotIsRefusedAndNothingIsPutInPlace()
    {
        await using var original = await InstallationAsync();
        var archive = await DepotBackup.CreateAsync(original.Get<StoragePaths>(), original.Root.Combine("backups"), Now, CancellationToken.None);
        using var target = new TempDirectory();
        var storage = new StoragePaths(target.Combine("data"));
        var olderBuild = BackupCommand.KnownMigrations().Order(StringComparer.Ordinal).SkipLast(1).ToList();

        var error = await Assert.ThrowsAsync<BackupException>(() => DepotBackup.RestoreAsync(storage, archive, olderBuild, CancellationToken.None));

        Assert.Contains("newer Depot", error.Message);
        AssertEmpty(storage);
    }

    [Fact]
    public async Task ADamagedOrForeignArchiveIsRefused()
    {
        using var target = new TempDirectory();
        var storage = new StoragePaths(target.Combine("data"));
        var garbage = target.WriteFile("garbage.tar.gz", Encoding.UTF8.GetBytes("not an archive"));
        var escaping = target.Combine("escaping.tar.gz");
        await WriteArchiveAsync(escaping, new()
        {
            [DepotBackup.ManifestName] = """{ "format": 1, "createdAt": "2026-11-04T18:30:00Z", "files": ["depot.db", "keys/../../evil.xml"] }""",
            ["depot.db"] = "x",
        });
        var noDatabase = target.Combine("no-database.tar.gz");
        await WriteArchiveAsync(noDatabase, new()
        {
            [DepotBackup.ManifestName] = """{ "format": 1, "createdAt": "2026-11-04T18:30:00Z", "files": ["depot.db"] }""",
        });
        var damaged = target.Combine("damaged.tar.gz");
        await WriteArchiveAsync(damaged, new()
        {
            [DepotBackup.ManifestName] = """{ "format": 1, "createdAt": "2026-11-04T18:30:00Z", "files": ["depot.db"] }""",
            ["depot.db"] = "SQLite format 3\0 but nothing else",
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => DepotBackup.RestoreAsync(storage, garbage, [], CancellationToken.None));
        Assert.Contains("never holds", (await Assert.ThrowsAsync<BackupException>(
            () => DepotBackup.RestoreAsync(storage, escaping, [], CancellationToken.None))).Message);
        Assert.Contains("no database", (await Assert.ThrowsAsync<BackupException>(
            () => DepotBackup.RestoreAsync(storage, noDatabase, [], CancellationToken.None))).Message);
        await Assert.ThrowsAsync<BackupException>(() => DepotBackup.RestoreAsync(storage, damaged, [], CancellationToken.None));
        AssertEmpty(storage);
        Assert.False(File.Exists(target.Combine("evil.xml")));
    }

    // Without the keys, only the stored qBittorrent password is lost: it must be entered again.
    [Fact]
    public async Task LosingTheKeysLosesOnlyTheQbittorrentPassword()
    {
        await using var original = await InstallationAsync();
        var archive = await DepotBackup.CreateAsync(original.Get<StoragePaths>(), original.Root.Combine("backups"), Now, CancellationToken.None);
        using var otherServer = new TempDirectory();
        var storage = new StoragePaths(otherServer.Combine("data"));
        await DepotBackup.RestoreAsync(storage, archive, BackupCommand.KnownMigrations(), CancellationToken.None);
        foreach (var key in Directory.GetFiles(storage.KeysDirectory))
        {
            File.Delete(key);
        }

        await using var restored = await OrganizationHarness.StartOnAsync(otherServer);

        Assert.Null(await restored.RunAsync<IQBittorrentSettingsRepository, QBittorrentSettings?>(repository => repository.GetAsync(CancellationToken.None)));
        Assert.Equal(OrganizationJobStatus.Completed, Assert.Single(await restored.JobsAsync()).Status);
        var organization = await restored.RunAsync<OrganizationSettingsService, OrganizationSettingsView>(service => service.GetAsync(CancellationToken.None));
        Assert.True(organization.IsSaved);
    }

    [Fact]
    public async Task TheCommandsUseTheAppsDataFolderAndReportFailures()
    {
        await using var original = await InstallationAsync();
        using var otherServer = new TempDirectory();
        var output = new StringWriter();

        var backedUp = await BackupCommand.RunAsync(
            ["--backup", original.Root.Combine("backups")], Configuration(original.Root.Combine("data")), Environment(original.Root.Path), output);
        var archive = Directory.GetFiles(original.Root.Combine("backups")).Single();
        var restoredExit = await BackupCommand.RunAsync(
            ["--restore", archive], Configuration("data"), Environment(otherServer.Path), output);
        var again = await BackupCommand.RunAsync(
            ["--restore", archive], Configuration("data"), Environment(otherServer.Path), output);

        Assert.Equal(0, backedUp);
        Assert.Equal(0, restoredExit);
        Assert.Equal(1, again);
        Assert.True(File.Exists(otherServer.Combine("data", "depot.db")));
        Assert.Contains("keep it as private as the data folder", output.ToString());
        Assert.Contains("Restore failed:", output.ToString());
        Assert.True(BackupCommand.Handles(["--backup", "/backups"]));
        Assert.False(BackupCommand.Handles(["--backup"]));
    }

    // A Depot with a little of everything: qBittorrent credentials (encrypted with the keys),
    // organization settings, a library choice, an imported video and an access token.
    private static async Task<OrganizationHarness> InstallationAsync()
    {
        var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        await harness.RunAsync<IQBittorrentSettingsRepository>(repository => repository.SaveAsync(
            QBittorrentSettings.Create("http://qbittorrent:8080", "admin", "secret password"), CancellationToken.None));
        await harness.ChooseAsync(Hash, new LibraryTarget(MediaType.Movie, "Dune (2021)", null));
        harness.AddDownloadedTorrent(Hash, "Dune", "movies", ("Dune/Dune.mkv", 1024));
        await harness.RunPassAsync();
        await File.WriteAllTextAsync(harness.Get<StoragePaths>().AccessTokenPath, Token);
        return harness;
    }

    private static List<string> Entries(string archive)
    {
        using var input = File.OpenRead(archive);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var entries = new List<string>();
        while (reader.GetNextEntry() is { } entry)
        {
            entries.Add(entry.Name.TrimStart('.', '/'));
        }

        return entries.Where(entry => entry.Length > 0).ToList();
    }

    // Runs a query against the database inside an archive.
    private static async Task<long> CountAsync(string archive, string query)
    {
        using var folder = new TempDirectory();
        await using (var input = File.OpenRead(archive))
        await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        {
            await TarFile.ExtractToDirectoryAsync(gzip, folder.Path, overwriteFiles: false);
        }

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = folder.Combine("depot.db"), Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string?> QueryAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false, DefaultTimeout = 30 }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WriteArchiveAsync(string path, Dictionary<string, string> files)
    {
        await using var output = File.Create(path);
        await using var gzip = new GZipStream(output, CompressionLevel.Fastest);
        await using var writer = new TarWriter(gzip);
        foreach (var (name, content) in files)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };
            await writer.WriteEntryAsync(entry);
        }
    }

    private static void AssertEmpty(StoragePaths storage)
    {
        Assert.False(File.Exists(storage.DatabasePath));
        Assert.False(File.Exists(storage.AccessTokenPath));
        Assert.Empty(Directory.GetFileSystemEntries(storage.KeysDirectory));
        Assert.Empty(Directory.GetDirectories(storage.DataDirectory, ".restore-*"));
    }

    private static IConfiguration Configuration(string dataDirectory) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DataDirectory"] = dataDirectory }).Build();

    private static IHostEnvironment Environment(string contentRoot) => new CommandEnvironment(contentRoot);

    private sealed class CommandEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Depot";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
