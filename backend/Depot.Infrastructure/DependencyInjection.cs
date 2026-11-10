using Depot.Application.Jellyfin;
using Depot.Application.Media;
using Depot.Application.Organization;
using Depot.Application.QBittorrent;
using Depot.Application.Settings;
using Depot.Infrastructure.Jellyfin;
using Depot.Infrastructure.Organization;
using Depot.Infrastructure.Persistence;
using Depot.Infrastructure.QBittorrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Depot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var storage = StoragePaths.From(configuration, environment);
        services.AddSingleton(storage);

        services.AddDataProtection()
            .SetApplicationName("Depot")
            .PersistKeysToFileSystem(new DirectoryInfo(storage.KeysDirectory));

        services.AddDbContext<DepotDbContext>(options =>
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = storage.DatabasePath }.ToString()));
        services.AddHostedService<DatabaseMigrator>();
        services.AddScoped<IQBittorrentSettingsRepository, QBittorrentSettingsRepository>();

        services.AddHttpClient<QBittorrentHttp>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                client.MaxResponseContentBufferSize = QBittorrentHttp.MaxResponseBytes;
            })
            // Redirects are refused: one could send the session cookie or, on login, the
            // password to another address.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
        services.AddSingleton<QBittorrentSessionStore>();
        services.AddScoped<IQBittorrentClient, QBittorrentClient>();
        services.AddScoped<IQBittorrentConnectionTester, QBittorrentConnectionTester>();

        services.AddScoped<IJellyfinSettingsRepository, JellyfinSettingsRepository>();
        services.AddHttpClient<JellyfinHttp>(client =>
            {
                client.Timeout = JellyfinHttp.Timeout;
                client.MaxResponseContentBufferSize = JellyfinHttp.MaxResponseBytes;
            })
            // Redirects are refused, since one could take the API key to another server; no proxy
            // either, so that the address check in ConnectCallback is made on Jellyfin's own address.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectCallback = JellyfinNetwork.ConnectAsync,
            });
        services.AddScoped<IJellyfinClient, JellyfinClient>();
        services.AddScoped<IJellyfinConnectionTester, JellyfinConnectionTester>();
        services.AddScoped<IMediaCatalogRepository, MediaCatalogRepository>();
        services.AddScoped<ILibrarySyncRunRepository, LibrarySyncRunRepository>();
        services.Configure<JellyfinOptions>(configuration.GetSection(JellyfinOptions.SectionName));
        services.AddSingleton<IImageCache, DiskImageCache>();
        // Registered after DatabaseMigrator, so the schema is up to date when it starts.
        services.AddHostedService<JellyfinSyncWorker>();

        services.Configure<OrganizationOptions>(configuration.GetSection(OrganizationOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IPathEnvironment, SystemPathEnvironment>();
        services.AddSingleton<IFileTransfer, FileTransfer>();
        services.AddSingleton<IDiskSpace, DriveDiskSpace>();
        services.AddScoped<IMediaOrganizer, MediaOrganizer>();
        services.AddScoped<IOrganizationSettingsRepository, OrganizationSettingsRepository>();
        services.AddScoped<IOrganizationJobRepository, OrganizationJobRepository>();
        services.AddScoped<ILibraryTargetRepository, LibraryTargetRepository>();
        // Registered after DatabaseMigrator, so the schema is up to date before the first pass.
        services.AddHostedService<OrganizationWorker>();

        return services;
    }
}
