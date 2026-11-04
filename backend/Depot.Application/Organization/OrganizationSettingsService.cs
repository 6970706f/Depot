using Depot.Domain.Organization;
using Microsoft.Extensions.Logging;

namespace Depot.Application.Organization;

public sealed class OrganizationSettingsService(
    IOrganizationSettingsRepository repository,
    OrganizationDiagnosticsService diagnostics,
    IPathEnvironment environment,
    OrganizationSignal signal,
    ILogger<OrganizationSettingsService> logger)
{
    public async Task<OrganizationSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await repository.GetAsync(cancellationToken);
        return ToView(stored.Settings, stored.IsSaved, await diagnostics.CheckAsync(stored.Settings, cancellationToken));
    }

    // Saves even when a folder check fails, so settings can be prepared before the folders
    // exist; no import runs until every check passes.
    public async Task<OrganizationSettingsView> SaveAsync(OrganizationSettingsInput input, CancellationToken cancellationToken)
    {
        var settings = Create(input);
        await repository.SaveAsync(settings, cancellationToken);
        var check = await diagnostics.CheckAsync(settings, cancellationToken);

        logger.LogInformation(
            "Organization settings saved: movies {MoviesPath}, series {SeriesPath}, mode {ImportMode}, every {Interval}s, {MappingCount} path mapping(s). Folders ready: {Ready}.",
            settings.MoviesPath, settings.SeriesPath, settings.ImportMode,
            settings.PollingIntervalSeconds, settings.PathMappings.Count, check.CanOrganize);
        signal.RequestPass();
        return ToView(settings, true, check);
    }

    // Checks the folders of settings that aren't saved yet.
    public async Task<OrganizationDiagnosticsView> TestAsync(OrganizationSettingsInput input, CancellationToken cancellationToken) =>
        await diagnostics.CheckAsync(Create(input), cancellationToken);

    private OrganizationSettings Create(OrganizationSettingsInput input) => OrganizationSettings.Create(
        input.MoviesPath,
        input.SeriesPath,
        input.ImportMode,
        input.PollingIntervalSeconds,
        (input.PathMappings ?? []).Select(mapping => (mapping.QbittorrentPath, mapping.DepotPath)).ToList(),
        environment.HomeDirectory);

    private static OrganizationSettingsView ToView(OrganizationSettings settings, bool isSaved, OrganizationDiagnosticsView check) => new(
        settings.MoviesPath,
        settings.SeriesPath,
        settings.ImportMode,
        settings.PollingIntervalSeconds,
        settings.PathMappings.Select(mapping => new PathMappingView(mapping.QbittorrentPath, mapping.DepotPath)).ToList(),
        isSaved,
        check);
}
