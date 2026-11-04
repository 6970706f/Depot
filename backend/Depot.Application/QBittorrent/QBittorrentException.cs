namespace Depot.Application.QBittorrent;

public enum QBittorrentFailure
{
    NotConfigured,
    Unreachable,
    Timeout,
    InvalidCredentials,
    Banned,
    UnexpectedResponse,
    TorrentNotFound,
    TorrentRejected,
}

// Every failure talking to qBittorrent ends up as this exception. The message is written
// for the person using Depot and never contains credentials.
public sealed class QBittorrentException(QBittorrentFailure failure, string message) : Exception(message)
{
    public QBittorrentFailure Failure { get; } = failure;
}
