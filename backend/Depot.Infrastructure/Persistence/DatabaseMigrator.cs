using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Depot.Infrastructure.Persistence;

// Applies pending EF Core migrations before the API starts serving requests.
public sealed class DatabaseMigrator(IServiceScopeFactory scopeFactory, StoragePaths storage) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DepotDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);

        // SQLite creates the file with the default permissions; the folder already keeps others
        // out, and this keeps the file private too if it's ever copied or the folder opened up.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            StoragePaths.RestrictFile(storage.DatabasePath + suffix);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
