using Depot.Application.Imports;
using Depot.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace Depot.Application.Jellyfin;

public sealed record JellyfinSettingsInput(string? Url, string? ApiKey);

// The key itself never leaves the server.
public sealed record JellyfinSettingsView(string? Url, bool HasApiKey);

public sealed class JellyfinSettingsService(
    IJellyfinSettingsRepository repository,
    IJellyfinConnectionTester tester,
    JellyfinStatusCache statusCache)
{
    public async Task<JellyfinSettingsView> GetAsync(CancellationToken cancellationToken) =>
        ToView(await repository.GetAsync(cancellationToken));

    public async Task<JellyfinSettingsView> SaveAsync(JellyfinSettingsInput input, CancellationToken cancellationToken)
    {
        var settings = await ResolveAsync(input, cancellationToken);
        await repository.SaveAsync(settings, cancellationToken);
        statusCache.Clear();
        return ToView(settings);
    }

    public async Task<JellyfinServerInfo> TestAsync(JellyfinSettingsInput input, CancellationToken cancellationToken) =>
        await tester.TestAsync(await ResolveAsync(input, cancellationToken), cancellationToken);

    private async Task<JellyfinSettings> ResolveAsync(JellyfinSettingsInput input, CancellationToken cancellationToken)
    {
        var current = await repository.GetAsync(cancellationToken);
        return current is null ? JellyfinSettings.Create(input.Url, input.ApiKey) : current.With(input.Url, input.ApiKey);
    }

    private static JellyfinSettingsView ToView(JellyfinSettings? settings) =>
        new(settings?.Url.ToString(), settings is not null);
}

// Jellyfin's status, reused for a few seconds so polling from several tabs doesn't reach Jellyfin
// every second.
public sealed class JellyfinStatusCache(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    private (Status.ServiceStatusView Status, DateTimeOffset At)? cached;

    public Status.ServiceStatusView? Get() =>
        cached is { } entry && clock.GetUtcNow() - entry.At < Lifetime ? entry.Status : null;

    public void Set(Status.ServiceStatusView status) => cached = (status, clock.GetUtcNow());

    public void Clear() => cached = null;
}

// After videos reach the library, asks Jellyfin to scan, so they show up without waiting for its
// own schedule. Failures only get logged: the import itself succeeded.
public sealed class JellyfinRefreshOnImport(IJellyfinClient jellyfin, ILogger<JellyfinRefreshOnImport> logger) : IImportListener
{
    public async Task ImportedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await jellyfin.RefreshLibraryAsync(cancellationToken);
            logger.LogInformation("Asked Jellyfin to scan its libraries for the new imports.");
        }
        catch (JellyfinException exception) when (exception.Failure == JellyfinFailure.NotConfigured)
        {
        }
        catch (JellyfinException exception)
        {
            logger.LogWarning("Couldn't ask Jellyfin to scan for the new imports: {Reason}", exception.Message);
        }
    }
}
