using Depot.Domain.Downloads;

namespace Depot.Application.QBittorrent;

// Talks to the qBittorrent configured in Settings. Logging in and keeping the session
// alive are the implementation's job; callers never see either.
// Every method throws QBittorrentException on failure.
public interface IQBittorrentClient
{
    Task<string> GetVersionAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Torrent>> GetTorrentsAsync(CancellationToken cancellationToken);

    // Null when qBittorrent has no torrent with this hash (or the hash isn't a valid one).
    Task<Torrent?> GetTorrentAsync(string hash, CancellationToken cancellationToken);

    // Every file of the torrent with its progress. Throws TorrentNotFound for an unknown hash.
    Task<IReadOnlyList<TorrentContentFile>> GetTorrentFilesAsync(string hash, CancellationToken cancellationToken);

    Task AddMagnetAsync(string magnetUri, AddTorrentOptions options, CancellationToken cancellationToken);

    Task AddTorrentFileAsync(string fileName, byte[] content, AddTorrentOptions options, CancellationToken cancellationToken);

    Task PauseAsync(string hash, CancellationToken cancellationToken);

    Task ResumeAsync(string hash, CancellationToken cancellationToken);

    // Removes the torrent from qBittorrent. Its downloaded files are deleted only when deleteFiles is true.
    Task RemoveAsync(string hash, bool deleteFiles, CancellationToken cancellationToken);
}

// Null values leave the choice to qBittorrent's own defaults.
public sealed record AddTorrentOptions(string? Category = null, string? SavePath = null);
