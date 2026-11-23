using Depot.Application.Settings;
using Depot.Domain.Settings;

namespace Depot.Tests.TestSupport;

public sealed class FixedQBittorrentSettings(QBittorrentSettings? settings) : IQBittorrentSettingsRepository
{
    public QBittorrentSettings? Settings { get; set; } = settings;

    public Task<QBittorrentSettings?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Settings);

    public Task SaveAsync(QBittorrentSettings settings, CancellationToken cancellationToken)
    {
        Settings = settings;
        return Task.CompletedTask;
    }
}
