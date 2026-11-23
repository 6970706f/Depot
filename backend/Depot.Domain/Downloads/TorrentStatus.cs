namespace Depot.Domain.Downloads;

public enum TorrentStatus
{
    Downloading,
    Queued,
    Checking,
    Paused,
    Completed,
    Error,
}
