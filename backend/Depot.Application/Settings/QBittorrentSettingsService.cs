using Depot.Application.QBittorrent;
using Depot.Domain.Settings;

namespace Depot.Application.Settings;

public sealed class QBittorrentSettingsService(
    IQBittorrentSettingsRepository repository,
    IQBittorrentConnectionTester connectionTester)
{
    public async Task<QBittorrentSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(cancellationToken);
        return ToView(settings);
    }

    public async Task<QBittorrentSettingsView> SaveAsync(QBittorrentSettingsInput input, CancellationToken cancellationToken)
    {
        var settings = await ResolveAsync(input, cancellationToken);
        await repository.SaveAsync(settings, cancellationToken);
        return ToView(settings);
    }

    // Tests the values being edited, before they're saved.
    public async Task<QBittorrentConnectionResult> TestConnectionAsync(QBittorrentSettingsInput input, CancellationToken cancellationToken)
    {
        var settings = await ResolveAsync(input, cancellationToken);
        var version = await connectionTester.TestAsync(settings, cancellationToken);
        return new QBittorrentConnectionResult(version);
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
