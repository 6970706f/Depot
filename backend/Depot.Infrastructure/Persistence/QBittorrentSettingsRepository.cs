using System.Security.Cryptography;
using Depot.Application.Settings;
using Depot.Domain.Settings;
using Depot.Infrastructure.Persistence.Records;
using Depot.Infrastructure.QBittorrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Depot.Infrastructure.Persistence;

// The password is encrypted with ASP.NET Core Data Protection, so a copy of the
// database alone doesn't reveal it.
public sealed class QBittorrentSettingsRepository(
    DepotDbContext dbContext,
    IDataProtectionProvider dataProtectionProvider,
    QBittorrentSessionStore sessions) : IQBittorrentSettingsRepository
{
    private readonly IDataProtector passwordProtector =
        dataProtectionProvider.CreateProtector("Depot.QBittorrent.Password");

    public async Task<QBittorrentSettings?> GetAsync(CancellationToken cancellationToken)
    {
        var record = await dbContext.QBittorrentSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.Id == QBittorrentSettingsRecord.SingletonId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        string password;
        try
        {
            password = passwordProtector.Unprotect(record.ProtectedPassword);
        }
        catch (CryptographicException)
        {
            // The encryption keys in data/keys were lost or replaced. The stored password
            // can no longer be read, so the settings must be entered again.
            return null;
        }

        return QBittorrentSettings.Create(record.Url, record.Username, password);
    }

    public async Task SaveAsync(QBittorrentSettings settings, CancellationToken cancellationToken)
    {
        var record = await dbContext.QBittorrentSettings
            .SingleOrDefaultAsync(existing => existing.Id == QBittorrentSettingsRecord.SingletonId, cancellationToken);
        var protectedPassword = passwordProtector.Protect(settings.Password);

        if (record is null)
        {
            dbContext.QBittorrentSettings.Add(new QBittorrentSettingsRecord
            {
                Id = QBittorrentSettingsRecord.SingletonId,
                Url = settings.Url.ToString(),
                Username = settings.Username,
                ProtectedPassword = protectedPassword,
            });
        }
        else
        {
            record.Url = settings.Url.ToString();
            record.Username = settings.Username;
            record.ProtectedPassword = protectedPassword;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // Saved settings are a fresh start: a new session, and no wait left from a refused login.
        await sessions.ResetAsync();
    }
}
