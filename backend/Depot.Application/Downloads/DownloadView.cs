using Depot.Application.Organization;
using Depot.Domain.Downloads;

namespace Depot.Application.Downloads;

// One download as the API returns it. Speeds are bytes per second; EtaSeconds is null
// when qBittorrent can't estimate it or the torrent isn't downloading. Library is where the
// videos go once it finishes, or null for a download that is only saved on the server.
public sealed record DownloadView(
    string Hash,
    string Name,
    TorrentStatus Status,
    double Progress,
    long SizeBytes,
    long DownloadedBytes,
    long DownloadSpeed,
    long UploadSpeed,
    long? EtaSeconds,
    string? Category,
    string SavePath,
    DateTimeOffset AddedAt,
    LibraryTargetView? Library)
{
    public static DownloadView From(Torrent torrent, LibraryTargetView? library = null) => new(
        torrent.Hash,
        torrent.Name,
        torrent.Status,
        torrent.Progress,
        torrent.SizeBytes,
        torrent.DownloadedBytes,
        torrent.DownloadSpeedBytesPerSecond,
        torrent.UploadSpeedBytesPerSecond,
        torrent.EstimatedTimeLeft is { } eta ? (long)eta.TotalSeconds : null,
        torrent.Category,
        torrent.SavePath,
        torrent.AddedAt,
        library);
}
