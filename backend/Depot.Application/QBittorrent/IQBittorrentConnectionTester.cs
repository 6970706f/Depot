using Depot.Domain.Settings;

namespace Depot.Application.QBittorrent;

public interface IQBittorrentConnectionTester
{
    // Logs in with the given settings, which may not be saved yet, and returns the
    // qBittorrent version. Throws QBittorrentException when the server can't be used.
    Task<string> TestAsync(QBittorrentSettings settings, CancellationToken cancellationToken);
}
