using Depot.Application.Downloads;
using Depot.Application.Imports;
using Depot.Application.Jellyfin;
using Depot.Application.Library;
using Depot.Application.Settings;
using Depot.Application.Status;
using Microsoft.Extensions.DependencyInjection;

namespace Depot.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<QBittorrentSettingsService>();
        services.AddScoped<DownloadService>();
        services.AddScoped<StatusService>();
        services.AddScoped<LibraryCheckService>();
        services.AddScoped<LibrarySettingsService>();
        services.AddScoped<LibraryService>();
        services.AddSingleton<ImportRuntime>();
        services.AddSingleton<ImportSignal>();
        services.AddSingleton<ILibrarySettingsListener>(provider => provider.GetRequiredService<ImportSignal>());
        services.AddScoped<ImportCycle>();
        services.AddScoped<ImportRecovery>();
        services.AddScoped<ImportJobService>();
        services.AddSingleton<JellyfinStatusCache>();
        services.AddScoped<JellyfinSettingsService>();
        services.AddScoped<MediaLibraryService>();
        services.AddScoped<IImportListener, JellyfinRefreshOnImport>();
        return services;
    }
}
