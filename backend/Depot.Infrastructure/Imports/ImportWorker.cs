using Depot.Application.Imports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Depot.Infrastructure.Imports;

// Runs import passes in the background: right away, then every polling interval, or sooner when
// something asks for a pass (new settings, a retry).
public sealed class ImportWorker(IServiceScopeFactory scopeFactory, ImportSignal signal, ImportRuntime runtime, ILogger<ImportWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan RetryAfterCrash = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunScopedAsync<ImportRecovery>((recovery, token) => recovery.RecoverAsync(token), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = RetryAfterCrash;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                wait = await scope.ServiceProvider.GetRequiredService<ImportCycle>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                runtime.Report(ImportRunState.Error, exception.Message);
                logger.LogError(exception, "The import pass failed.");
            }

            try
            {
                await signal.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunScopedAsync<T>(Func<T, CancellationToken, Task> work, CancellationToken cancellationToken)
        where T : notnull
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await work(scope.ServiceProvider.GetRequiredService<T>(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Recovering interrupted imports failed.");
        }
    }
}
