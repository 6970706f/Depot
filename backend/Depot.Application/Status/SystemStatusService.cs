using Depot.Application.QBittorrent;

namespace Depot.Application.Status;

public sealed record QBittorrentStatusView(bool Configured, bool Connected, string? Version, string? Error);

public sealed class SystemStatusService(IQBittorrentClient qBittorrentClient)
{
    // The interface polls the status every few seconds, so an unresponsive server must
    // not hold the answer for the client's full request timeout.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public async Task<QBittorrentStatusView> GetQBittorrentStatusAsync(CancellationToken cancellationToken)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(ProbeTimeout);
        try
        {
            var version = await qBittorrentClient.GetVersionAsync(probe.Token);
            return new QBittorrentStatusView(Configured: true, Connected: true, version, Error: null);
        }
        catch (QBittorrentException exception)
        {
            var configured = exception.Failure != QBittorrentFailure.NotConfigured;
            return new QBittorrentStatusView(configured, Connected: false, Version: null, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new QBittorrentStatusView(
                Configured: true, Connected: false, Version: null,
                $"qBittorrent didn't answer within {ProbeTimeout.TotalSeconds:0} seconds.");
        }
    }
}
