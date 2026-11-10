using System.Security.Cryptography;
using Depot.Application.Settings;
using Depot.Domain.Settings;
using Depot.Infrastructure.Persistence.Records;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Depot.Infrastructure.Persistence;

// The API key is encrypted with ASP.NET Core Data Protection, as the qBittorrent password is,
// under a purpose of its own, so a copy of the database alone doesn't reveal it.
public sealed class JellyfinSettingsRepository(DepotDbContext dbContext, IDataProtectionProvider dataProtectionProvider)
    : IJellyfinSettingsRepository
{
    private readonly IDataProtector apiKeyProtector = dataProtectionProvider.CreateProtector("Depot.Jellyfin.ApiKey");

    public async Task<JellyfinSettings?> GetAsync(CancellationToken cancellationToken)
    {
        var record = await dbContext.JellyfinSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.Id == JellyfinSettingsRecord.SingletonId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        string apiKey;
        try
        {
            apiKey = apiKeyProtector.Unprotect(record.ProtectedApiKey);
        }
        catch (CryptographicException)
        {
            // The keys in data/keys were lost or replaced: the key must be entered again.
            return null;
        }

        return JellyfinSettings.Create(record.Url, apiKey);
    }

    public async Task SaveAsync(JellyfinSettings settings, CancellationToken cancellationToken)
    {
        var record = await dbContext.JellyfinSettings
            .SingleOrDefaultAsync(existing => existing.Id == JellyfinSettingsRecord.SingletonId, cancellationToken);
        var protectedApiKey = apiKeyProtector.Protect(settings.ApiKey);

        if (record is null)
        {
            dbContext.JellyfinSettings.Add(new JellyfinSettingsRecord
            {
                Id = JellyfinSettingsRecord.SingletonId,
                Url = settings.Url.ToString(),
                ProtectedApiKey = protectedApiKey,
            });
        }
        else
        {
            record.Url = settings.Url.ToString();
            record.ProtectedApiKey = protectedApiKey;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }
}
