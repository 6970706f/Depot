using Depot.Domain.Downloads;
using Depot.Domain.Library;

namespace Depot.Application.Downloads;

// One download as the API returns it. Speeds are bytes per second; EtaSeconds is null when
// qBittorrent can't estimate it or the torrent isn't downloading. Library is where the videos go
// once it finishes, or null for a download that is only kept on the server.
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
    public static DownloadView From(Torrent torrent, LibraryTarget? target) => new(
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
        target is null ? null : new LibraryTargetView(target.Type, target.Title, target.Season));
}

public sealed record LibraryTargetView(MediaType MediaType, string Title, int? Season);
