using Depot.Application;
using Depot.Application.Library;
using Depot.Application.QBittorrent;
using Depot.Infrastructure;
using Depot.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Depot.Tests.TestSupport;

// The application and infrastructure services as the app wires them, on a temporary SQLite
// database and media folder, with qBittorrent replaced by a fake.
public sealed class DepotHarness : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    private DepotHarness(TempDirectory root, ServiceProvider provider)
    {
        Root = root;
        this.provider = provider;
    }

    public TempDirectory Root { get; }

    public FakeQBittorrentClient QBittorrent { get; } = new();

    public IServiceProvider Services => provider;

    public string MoviesRoot => Root.Combine("media", "movies");

    public string SeriesRoot => Root.Combine("media", "series");

    public string DownloadsRoot => Root.Combine("media", "downloads");

    public static async Task<DepotHarness> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var root = new TempDirectory();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataDirectory"] = root.Combine("data"),
                ["Library:MoviesPath"] = root.Combine("media", "movies"),
                ["Library:SeriesPath"] = root.Combine("media", "series"),
                ["Library:PathMappings:0:QbittorrentPath"] = "/downloads",
                ["Library:PathMappings:0:DepotPath"] = root.Combine("media", "downloads"),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddApplication();
        services.AddInfrastructure(configuration, new TestEnvironment(root.Path));
        services.AddSingleton<IPathEnvironment>(new FixedHome(root.Combine("home")));

        DepotHarness? harness = null;
        services.AddSingleton<IQBittorrentClient>(_ => harness!.QBittorrent);
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider();
        harness = new DepotHarness(root, provider);

        foreach (var folder in new[] { harness.MoviesRoot, harness.SeriesRoot, harness.DownloadsRoot })
        {
            Directory.CreateDirectory(folder);
        }

        await provider.MigrateDatabaseAsync();
        return harness;
    }

    // Runs work in its own scope, as a request would.
    public async Task<T> RunAsync<T, TService>(Func<TService, Task<T>> work)
        where TService : notnull
    {
        await using var scope = provider.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async Task RunAsync<TService>(Func<TService, Task> work)
        where TService : notnull
    {
        await using var scope = provider.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async ValueTask DisposeAsync()
    {
        await provider.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Root.Dispose();
    }

    private sealed class FixedHome(string home) : IPathEnvironment
    {
        public string HomeDirectory => home;
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Depot.Tests";

        public string ContentRootPath { get; set; } = root;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
