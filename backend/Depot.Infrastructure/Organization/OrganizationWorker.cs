using Depot.Application.Organization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Depot.Infrastructure.Organization;

// Runs the organization passes in the background: recovery once at startup, then one pass per
// polling interval, or sooner when someone approves a job or saves settings.
public sealed class OrganizationWorker(
    IServiceScopeFactory scopeFactory,
    OrganizationSignal signal,
    OrganizationRuntimeStatus status,
    ILogger<OrganizationWorker> logger) : BackgroundService
{
    // Used when a pass fails before it could read the configured interval.
    private static readonly TimeSpan FallbackInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunGuardedAsync("Recovering interrupted imports", async (services, token) =>
        {
            await services.GetRequiredService<OrganizationRecoveryService>().RecoverAsync(token);
            return TimeSpan.Zero;
        }, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var next = await RunGuardedAsync(
                "Organization pass",
                (services, token) => services.GetRequiredService<OrganizationCycle>().RunAsync(token),
                stoppingToken);

            try
            {
                await signal.WaitAsync(next, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // A failing pass is logged and reported; it never stops the worker or the app.
    private async Task<TimeSpan> RunGuardedAsync(
        string description,
        Func<IServiceProvider, CancellationToken, Task<TimeSpan>> work,
        CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            return await work(scope.ServiceProvider, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return FallbackInterval;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "{Description} failed.", description);
            status.Report(OrganizationRunState.Error, $"{description} failed: {exception.Message}");
            return FallbackInterval;
        }
    }
}
