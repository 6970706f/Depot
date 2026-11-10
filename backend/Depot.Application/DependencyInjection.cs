using Depot.Application.Downloads;
using Depot.Application.Jellyfin;
using Depot.Application.Media;
using Depot.Application.Organization;
using Depot.Application.Settings;
using Depot.Application.Status;
using Microsoft.Extensions.DependencyInjection;

namespace Depot.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<QBittorrentSettingsService>();
        services.AddScoped<SystemStatusService>();
        services.AddScoped<DownloadService>();

        services.AddSingleton<JellyfinStatusCache>();
        services.AddScoped<JellyfinSettingsService>();
        services.AddSingleton<JellyfinSyncQueue>();
        services.AddScoped<JellyfinSyncService>();
        services.AddScoped<JellyfinLibrarySync>();
        services.AddScoped<MediaCatalogService>();
        services.AddScoped<MediaImageService>();

        services.AddSingleton<OrganizationSignal>();
        services.AddSingleton<OrganizationRuntimeStatus>();
        services.AddScoped<OrganizationDiagnosticsService>();
        services.AddScoped<OrganizationSettingsService>();
        services.AddScoped<OrganizationJobService>();
        services.AddScoped<LibraryService>();
        services.AddScoped<MediaDiscoveryService>();
        services.AddScoped<MediaImportService>();
        services.AddScoped<OrganizationRecoveryService>();
        services.AddScoped<OrganizationCycle>();
        return services;
    }
}
