using Depot.Application;
using Depot.Application.Organization;
using Depot.Application.QBittorrent;
using Depot.Domain.Organization;
using Depot.Infrastructure;
using Depot.Infrastructure.Organization;
using Depot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Depot.Tests.TestSupport;

// The real application and infrastructure services (SQLite database, file system work,
// repositories) wired as in the app, with qBittorrent replaced by a fake and every folder
// inside a temporary directory.
public sealed class OrganizationHarness : IAsyncDisposable
{
    private readonly ServiceProvider services;

    private OrganizationHarness(TempDirectory root, ServiceProvider services, FakeQBittorrentClient qBittorrent)
    {
        Root = root;
        this.services = services;
        QBittorrent = qBittorrent;
    }

    public TempDirectory Root { get; }

    public FakeQBittorrentClient QBittorrent { get; }

    public string Downloads => Root.Combine("downloads");

    public string Movies => Root.Combine("library", "movies");

    public string Series => Root.Combine("library", "series");

    // With a crash, imports stop for good at its step; see RestartAsync.
    public static Task<OrganizationHarness> CreateAsync(Crash? crash = null)
    {
        var root = new TempDirectory();
        root.CreateFolder("downloads");
        root.CreateFolder("library", "movies");
        root.CreateFolder("library", "series");
        return StartAsync(root, new FakeQBittorrentClient(), crash);
    }

    // Plays the next start of Depot after it was killed: new services on the same folders and
    // database, while qBittorrent keeps running. This harness's services are left as they are,
    // and an import stopped by a crash never resumes.
    public Task<OrganizationHarness> RestartAsync() => StartAsync(Root, QBittorrent, null);

    // Starts Depot on a folder prepared elsewhere, such as another server's restored data/.
    public static Task<OrganizationHarness> StartOnAsync(TempDirectory root) => StartAsync(root, new FakeQBittorrentClient(), null);

    private static async Task<OrganizationHarness> StartAsync(TempDirectory root, FakeQBittorrentClient qBittorrent, Crash? crash)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DataDirectory"] = root.Combine("data") })
            .Build();

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddApplication();
        collection.AddInfrastructure(configuration, new TestHostEnvironment(root.Path));
        collection.AddSingleton<IQBittorrentClient>(qBittorrent);
        collection.AddSingleton<IPathEnvironment>(new TestPathEnvironment(root.Path));
        if (crash is not null)
        {
            collection.AddSingleton<IFileTransfer>(new CrashingTransfer(crash));
            collection.AddScoped<IOrganizationJobRepository>(provider =>
                new CrashingJobRepository(ActivatorUtilities.CreateInstance<OrganizationJobRepository>(provider), crash));
        }

        var services = collection.BuildServiceProvider();

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DepotDbContext>().Database.MigrateAsync();
        }

        return new OrganizationHarness(root, services, qBittorrent);
    }

    // Saves settings pointing at the temporary library; "~" is the temporary root. Without
    // mappings given, the downloads folder is mapped to itself, as when qBittorrent runs on the
    // same machine.
    public Task SaveSettingsAsync(ImportMode mode = ImportMode.Auto, IReadOnlyList<PathMappingInput>? mappings = null) =>
        RunAsync<OrganizationSettingsService>(service => service.SaveAsync(
            new OrganizationSettingsInput("~/library/movies", "~/library/series", mode, 60, mappings ?? [new PathMappingInput(Downloads, Downloads)]),
            CancellationToken.None));

    // Records the library choice made when a download is added, as the add dialog does.
    public Task ChooseAsync(string hash, LibraryTarget target) =>
        RunAsync<ILibraryTargetRepository>(repository => repository.SaveAsync(hash, target, DateTimeOffset.UtcNow, CancellationToken.None));

    public Task RunPassAsync() => RunAsync<OrganizationCycle>(cycle => cycle.RunAsync(CancellationToken.None));

    public Task RecoverAsync() => RunAsync<OrganizationRecoveryService>(service => service.RecoverAsync(CancellationToken.None));

    public Task<IReadOnlyList<OrganizationJobView>> JobsAsync() =>
        RunAsync<OrganizationJobService, IReadOnlyList<OrganizationJobView>>(service => service.ListAsync(null, CancellationToken.None));

    public async Task RunAsync<TService>(Func<TService, Task> action)
        where TService : notnull
    {
        await using var scope = services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        await using var scope = services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public T Get<T>()
        where T : notnull => services.GetRequiredService<T>();

    // Creates real files under the downloads folder and registers the finished torrent in the
    // fake qBittorrent, in the given category ("movies", "series" or null).
    public void AddDownloadedTorrent(string hash, string name, string? category, params (string RelativePath, int Size)[] files) =>
        AddTorrent(hash, name, Downloads, files, category: category);

    public void AddTorrent(
        string hash,
        string name,
        string savePath,
        (string RelativePath, int Size)[] files,
        Depot.Domain.Downloads.TorrentStatus status = Depot.Domain.Downloads.TorrentStatus.Completed,
        double progress = 1,
        string? category = null)
    {
        foreach (var (relativePath, size) in files)
        {
            var bytes = new byte[size];
            Random.Shared.NextBytes(bytes);
            Root.WriteFile(Path.Join("downloads", relativePath), bytes);
        }

        QBittorrent.Add(hash, name, savePath, files.Select(file => (file.RelativePath, (long)file.Size)), status, progress, category);
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        Root.Dispose();
    }

    private sealed class TestPathEnvironment(string home) : IPathEnvironment
    {
        public string HomeDirectory => home;
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Depot.Tests";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
