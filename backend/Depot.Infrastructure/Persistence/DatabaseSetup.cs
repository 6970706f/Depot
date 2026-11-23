using Depot.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Depot.Infrastructure.Persistence;

public static class DatabaseSetup
{
    // Applies pending migrations before the app serves requests or starts its workers.
    public static async Task MigrateDatabaseAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DepotDbContext>().Database.MigrateAsync();

        // SQLite creates its files with the default permissions; keep them private too.
        var storage = services.GetRequiredService<StoragePaths>();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            StoragePaths.RestrictFile(storage.DatabasePath + suffix);
        }
    }
}
