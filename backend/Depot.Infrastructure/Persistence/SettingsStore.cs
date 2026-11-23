using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Depot.Infrastructure.Persistence;

// Reads and writes settings documents. Each repository owns its document's shape.
public sealed class SettingsStore(DepotDbContext dbContext, TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
        where T : class
    {
        var entry = await dbContext.Settings.AsNoTracking().SingleOrDefaultAsync(setting => setting.Key == key, cancellationToken);
        return entry is null ? null : JsonSerializer.Deserialize<T>(entry.Value, JsonOptions);
    }

    public async Task SaveAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var entry = await dbContext.Settings.SingleOrDefaultAsync(setting => setting.Key == key, cancellationToken);
        if (entry is null)
        {
            dbContext.Settings.Add(new SettingsEntry { Key = key, Value = json, UpdatedAt = clock.GetUtcNow() });
        }
        else
        {
            entry.Value = json;
            entry.UpdatedAt = clock.GetUtcNow();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
