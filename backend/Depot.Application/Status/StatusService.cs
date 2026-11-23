using Depot.Application.Jellyfin;
using Depot.Application.QBittorrent;
using Microsoft.Extensions.DependencyInjection;

namespace Depot.Application.Status;

public sealed record ServiceStatusView(bool Configured, bool Connected, string? Version, string? Error, string? ServerName = null);

public sealed record StatusView(ServiceStatusView QBittorrent, ServiceStatusView Jellyfin);

public sealed class StatusService(IQBittorrentClient qBittorrent, IServiceScopeFactory scopeFactory, JellyfinStatusCache jellyfinCache)
{
    // The interface polls the status, so an unresponsive server must not hold the answer for
    // the client's full request timeout.
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    // Both probes run at once; Jellyfin's in a scope of its own, since one database context can't
    // serve two queries at the same time.
    public async Task<StatusView> GetAsync(CancellationToken cancellationToken)
    {
        var jellyfin = GetJellyfinAsync(cancellationToken);
        var qBittorrentStatus = await GetQBittorrentAsync(cancellationToken);
        return new StatusView(qBittorrentStatus, await jellyfin);
    }

    private async Task<ServiceStatusView> GetQBittorrentAsync(CancellationToken cancellationToken)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(ProbeTimeout);
        try
        {
            var version = await qBittorrent.GetVersionAsync(probe.Token);
            return new ServiceStatusView(Configured: true, Connected: true, version, Error: null);
        }
        catch (QBittorrentException exception)
        {
            return new ServiceStatusView(exception.Failure != QBittorrentFailure.NotConfigured, Connected: false, null, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ServiceStatusView(true, false, null, $"qBittorrent didn't answer within {ProbeTimeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<ServiceStatusView> GetJellyfinAsync(CancellationToken cancellationToken)
    {
        if (jellyfinCache.Get() is { } cached)
        {
            return cached;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IJellyfinClient>();
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(ProbeTimeout);
        ServiceStatusView status;
        try
        {
            var server = await client.GetServerInfoAsync(probe.Token);
            status = new ServiceStatusView(true, true, server.Version, null, server.ServerName);
        }
        catch (JellyfinException exception)
        {
            status = new ServiceStatusView(exception.Failure != JellyfinFailure.NotConfigured, false, null, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            status = new ServiceStatusView(true, false, null, $"Jellyfin didn't answer within {ProbeTimeout.TotalSeconds:0} seconds.");
        }

        jellyfinCache.Set(status);
        return status;
    }
}
