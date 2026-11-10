using Depot.Application.Jellyfin;
using Depot.Application.QBittorrent;
using Microsoft.Extensions.DependencyInjection;

namespace Depot.Application.Status;

public sealed record QBittorrentStatusView(bool Configured, bool Connected, string? Version, string? Error);

public sealed record JellyfinStatusView(bool Configured, bool Connected, string? ServerName, string? Version, string? Error);

public sealed class SystemStatusService(
    IQBittorrentClient qBittorrentClient,
    IServiceScopeFactory scopeFactory,
    JellyfinStatusCache jellyfinStatusCache)
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

    // Reused for a few seconds (JellyfinStatusCache), so polling from several tabs doesn't
    // turn into a request to Jellyfin every second. It runs in a scope of its own, so it can run
    // next to the qBittorrent probe: each reads its settings from the database, and one
    // database context can't serve two queries at once.
    public async Task<JellyfinStatusView> GetJellyfinStatusAsync(CancellationToken cancellationToken)
    {
        if (jellyfinStatusCache.Get() is { } cached)
        {
            return cached;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IJellyfinClient>();
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(ProbeTimeout);

        JellyfinStatusView status;
        try
        {
            var server = await client.GetServerInfoAsync(probe.Token);
            status = new JellyfinStatusView(Configured: true, Connected: true, server.ServerName, server.Version, Error: null);
        }
        catch (JellyfinException exception)
        {
            var configured = exception.Failure != JellyfinFailure.NotConfigured;
            status = new JellyfinStatusView(configured, Connected: false, ServerName: null, Version: null, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            status = new JellyfinStatusView(
                Configured: true, Connected: false, ServerName: null, Version: null,
                $"Jellyfin didn't answer within {ProbeTimeout.TotalSeconds:0} seconds.");
        }

        jellyfinStatusCache.Set(status);
        return status;
    }
}
