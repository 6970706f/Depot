using Depot.Application;
using Depot.Application.Jellyfin;
using Depot.Application.Settings;
using Depot.Domain.Media;
using Depot.Domain.Settings;
using Depot.Infrastructure;
using Depot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Depot.Tests.TestSupport;

// The real application and infrastructure services (SQLite database, repositories, image
// cache) wired as in the app, with Jellyfin replaced by a fake and the data folder inside a
// temporary directory. Background workers don't run: tests start syncs themselves.
public sealed class JellyfinHarness : IAsyncDisposable
{
    private readonly ServiceProvider services;

    private JellyfinHarness(TempDirectory root, ServiceProvider services, FakeJellyfinClient jellyfin)
    {
        Root = root;
        this.services = services;
        Jellyfin = jellyfin;
    }

    public TempDirectory Root { get; }

    public FakeJellyfinClient Jellyfin { get; }

    public static async Task<JellyfinHarness> CreateAsync(int imageCacheMegabytes = 512)
    {
        var root = new TempDirectory();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataDirectory"] = root.Combine("data"),
                ["Jellyfin:ImageCacheMegabytes"] = imageCacheMegabytes.ToString(),
            })
            .Build();

        var jellyfin = new FakeJellyfinClient();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddApplication();
        collection.AddInfrastructure(configuration, new TestHostEnvironment(root.Path));
        collection.AddSingleton<IJellyfinClient>(jellyfin);
        var services = collection.BuildServiceProvider();

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DepotDbContext>().Database.MigrateAsync();
        }

        return new JellyfinHarness(root, services, jellyfin);
    }

    // Saves Jellyfin settings, as Settings does, so syncs can start.
    public Task ConfigureAsync() =>
        RunAsync<IJellyfinSettingsRepository>(repository =>
            repository.SaveAsync(JellyfinSettings.Create("http://jellyfin:8096", "0123456789abcdef0123456789abcdef"), CancellationToken.None));

    // Queues a manual sync and runs it to the end, as the worker would.
    public async Task<SyncRunView> SyncAsync()
    {
        var run = await RunAsync<JellyfinSyncService, SyncRunView>(service => service.StartAsync(SyncTrigger.Manual, CancellationToken.None));
        try
        {
            await RunAsync<JellyfinLibrarySync>(sync => sync.RunAsync(run.Id, CancellationToken.None));
        }
        finally
        {
            Get<JellyfinSyncQueue>().Release();
        }

        return await RunAsync<JellyfinSyncService, SyncRunView>(service => service.GetAsync(run.Id, CancellationToken.None));
    }

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

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        Root.Dispose();
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Depot.Tests";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
