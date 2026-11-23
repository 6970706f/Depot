using Depot.Application.QBittorrent;
using Depot.Domain.Settings;

namespace Depot.Application.Settings;

public sealed record QBittorrentSettingsInput(string? Url, string? Username, string? Password);

// The password itself never leaves the server.
public sealed record QBittorrentSettingsView(string? Url, string? Username, bool HasPassword);

public sealed record QBittorrentConnectionResult(string Version);

public sealed class QBittorrentSettingsService(
    IQBittorrentSettingsRepository repository,
    IQBittorrentConnectionTester connectionTester,
    IEnumerable<IQBittorrentSettingsListener> listeners)
{
    public async Task<QBittorrentSettingsView> GetAsync(CancellationToken cancellationToken) =>
        ToView(await repository.GetAsync(cancellationToken));

    public async Task<QBittorrentSettingsView> SaveAsync(QBittorrentSettingsInput input, CancellationToken cancellationToken)
    {
        var settings = await ResolveAsync(input, cancellationToken);
        await repository.SaveAsync(settings, cancellationToken);
        foreach (var listener in listeners)
        {
            await listener.SettingsSavedAsync();
        }

        return ToView(settings);
    }

    // Tests the values being edited, before they're saved.
    public async Task<QBittorrentConnectionResult> TestAsync(QBittorrentSettingsInput input, CancellationToken cancellationToken)
    {
        var settings = await ResolveAsync(input, cancellationToken);
        return new QBittorrentConnectionResult(await connectionTester.TestAsync(settings, cancellationToken));
    }

    private async Task<QBittorrentSettings> ResolveAsync(QBittorrentSettingsInput input, CancellationToken cancellationToken)
    {
        var current = await repository.GetAsync(cancellationToken);
        return current is null
            ? QBittorrentSettings.Create(input.Url, input.Username, input.Password)
            : current.With(input.Url, input.Username, input.Password);
    }

    private static QBittorrentSettingsView ToView(QBittorrentSettings? settings) =>
        settings is null
            ? new QBittorrentSettingsView(null, null, HasPassword: false)
            : new QBittorrentSettingsView(settings.Url.ToString(), settings.Username, HasPassword: true);
}

// Told when the settings change, such as the client's shared session, which must log in again.
public interface IQBittorrentSettingsListener
{
    Task SettingsSavedAsync();
}
