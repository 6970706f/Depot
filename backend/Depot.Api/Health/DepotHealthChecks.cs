using Depot.Application.Status;
using Depot.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Depot.Api.Health;

// Without its database Depot can't do anything: unhealthy.
public sealed class DatabaseHealthCheck(DepotDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The database can't be opened.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The database can't be opened.", exception);
        }
    }
}

// Depot still answers without qBittorrent, and says why in /api/status: degraded.
public sealed class QBittorrentHealthCheck(SystemStatusService status) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var qBittorrent = await status.GetQBittorrentStatusAsync(cancellationToken);
        return qBittorrent.Connected ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded(qBittorrent.Error);
    }
}
