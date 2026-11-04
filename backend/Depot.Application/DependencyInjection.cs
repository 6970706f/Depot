using Depot.Application.Downloads;
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
