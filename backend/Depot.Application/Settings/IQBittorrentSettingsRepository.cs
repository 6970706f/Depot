using Depot.Domain.Settings;

namespace Depot.Application.Settings;

public interface IQBittorrentSettingsRepository
{
    // Null until the settings are saved for the first time.
    Task<QBittorrentSettings?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(QBittorrentSettings settings, CancellationToken cancellationToken);
}
