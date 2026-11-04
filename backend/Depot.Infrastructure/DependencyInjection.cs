using Depot.Application.Organization;
using Depot.Application.QBittorrent;
using Depot.Application.Settings;
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
        var storageOptions = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        // Creates data/ and data/keys/ readable only by the user running Depot: the keys that
        // encrypt stored passwords are kept unencrypted on Linux.
        var storage = new StoragePaths(Path.GetFullPath(storageOptions.DataDirectory, environment.ContentRootPath));
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
