using Depot.Application.Imports;
using Depot.Application.Jellyfin;
using Depot.Application.Library;
using Depot.Application.QBittorrent;
using Depot.Application.Settings;
using Depot.Infrastructure.Imports;
using Depot.Infrastructure.Jellyfin;
using Depot.Infrastructure.Library;
using Depot.Infrastructure.Persistence;
using Depot.Infrastructure.QBittorrent;
using Depot.Infrastructure.Settings;
using Depot.Infrastructure.Storage;
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
        services.TryAddSingleton(TimeProvider.System);

        services.AddDataProtection()
            .SetApplicationName("Depot")
            .PersistKeysToFileSystem(new DirectoryInfo(storage.KeysDirectory));

        services.AddDbContext<DepotDbContext>(options =>
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = storage.DatabasePath }.ToString()));
        services.AddScoped<SettingsStore>();

        services.AddScoped<IQBittorrentSettingsRepository, QBittorrentSettingsRepository>();
        services.AddHttpClient<QBittorrentHttp>(client =>
            {
                client.Timeout = QBittorrentHttp.Timeout;
                client.MaxResponseContentBufferSize = QBittorrentHttp.MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
        services.AddScoped<IQBittorrentConnectionTester, QBittorrentConnectionTester>();
        services.AddSingleton<QBittorrentSessionStore>();
        services.AddSingleton<IQBittorrentSettingsListener>(provider => provider.GetRequiredService<QBittorrentSessionStore>());
        services.AddScoped<IQBittorrentClient, QBittorrentClient>();

        services.Configure<LibraryOptions>(configuration.GetSection(LibraryOptions.SectionName));
        services.AddSingleton<IPathEnvironment, SystemPathEnvironment>();
        services.AddSingleton<ILibraryFileSystem, LibraryFileSystem>();
        services.AddScoped<ILibrarySettingsRepository, LibrarySettingsRepository>();
        services.AddScoped<ILibraryChoiceRepository, LibraryChoiceRepository>();
        services.AddScoped<IImportJobRepository, ImportJobRepository>();
        services.AddSingleton<IFileTransfer, FileTransfer>();
        services.AddSingleton<IMediaImporter, MediaImporter>();
        services.AddHostedService<ImportWorker>();

        services.AddScoped<IJellyfinSettingsRepository, JellyfinSettingsRepository>();
        services.AddHttpClient<JellyfinHttp>(client =>
            {
                client.Timeout = JellyfinHttp.Timeout;
                client.MaxResponseContentBufferSize = JellyfinHttp.MaxResponseBytes;
            })
            // Redirects are refused, since one could take the API key to another server.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
        services.AddScoped<IJellyfinClient, JellyfinClient>();
        services.AddScoped<IJellyfinConnectionTester, JellyfinConnectionTester>();

        return services;
    }
}
