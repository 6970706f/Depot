using Depot.Domain.Settings;

namespace Depot.Application.QBittorrent;

public interface IQBittorrentConnectionTester
{
    // Logs in with these settings (not necessarily the saved ones) and returns qBittorrent's version.
    Task<string> TestAsync(QBittorrentSettings settings, CancellationToken cancellationToken);
}
