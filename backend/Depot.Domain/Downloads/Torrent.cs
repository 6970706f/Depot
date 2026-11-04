namespace Depot.Domain.Downloads;

// A snapshot of one torrent as the torrent client reports it. Hash is the client's id
// for the torrent; InfoHashes holds every info hash the client knows for it.
public sealed record Torrent(
    string Hash,
    TorrentHashes InfoHashes,
    string Name,
    TorrentStatus Status,
    long SizeBytes,
    long DownloadedBytes,
    long AmountLeftBytes,
    double Progress,
    long DownloadSpeedBytesPerSecond,
    long UploadSpeedBytesPerSecond,
    TimeSpan? EstimatedTimeLeft,
    string? Category,
    string SavePath,
    DateTimeOffset AddedAt)
{
    // Every wanted byte is on disk and the client itself reports the torrent as finished.
    // Paused or stopped is not enough: a torrent stopped halfway is not complete.
    public bool IsFullyDownloaded => Status == TorrentStatus.Completed && Progress >= 1 && AmountLeftBytes == 0;
}
