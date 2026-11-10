using Depot.Application.Jellyfin;
using Depot.Application.Status;
using Depot.Domain.Settings;

namespace Depot.Application.Settings;

public sealed class JellyfinSettingsService(
    IJellyfinSettingsRepository repository,
    IJellyfinConnectionTester connectionTester,
    JellyfinStatusCache statusCache)
{
    public async Task<JellyfinSettingsView> GetAsync(CancellationToken cancellationToken) =>
        ToView(await repository.GetAsync(cancellationToken));

    public async Task<JellyfinSettingsView> SaveAsync(JellyfinSettingsInput input, CancellationToken cancellationToken)
    {
        var settings = await ResolveAsync(input, cancellationToken);
        await repository.SaveAsync(settings, cancellationToken);
        // The status shown everywhere must describe the new server, not the old one.
        statusCache.Clear();
        return ToView(settings);
    }

    // Tests the values being edited, before they're saved.
    public async Task<JellyfinConnectionResult> TestConnectionAsync(JellyfinSettingsInput input, CancellationToken cancellationToken)
    {
        var settings = await ResolveAsync(input, cancellationToken);
        var server = await connectionTester.TestAsync(settings, cancellationToken);
        return new JellyfinConnectionResult(server.ServerName, server.Version);
    }

    private async Task<JellyfinSettings> ResolveAsync(JellyfinSettingsInput input, CancellationToken cancellationToken)
    {
        var current = await repository.GetAsync(cancellationToken);
        return current is null
            ? JellyfinSettings.Create(input.Url, input.ApiKey)
            : current.With(input.Url, input.ApiKey);
    }

    private static JellyfinSettingsView ToView(JellyfinSettings? settings) =>
        settings is null ? new JellyfinSettingsView(null, HasApiKey: false) : new JellyfinSettingsView(settings.Url.ToString(), HasApiKey: true);
}
