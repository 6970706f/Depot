using Depot.Domain.Settings;

namespace Depot.Application.Settings;

public interface IQBittorrentSettingsRepository
{
    Task<QBittorrentSettings?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(QBittorrentSettings settings, CancellationToken cancellationToken);
}
