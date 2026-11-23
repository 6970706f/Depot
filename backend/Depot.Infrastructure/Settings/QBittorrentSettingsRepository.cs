using Depot.Application.Settings;
using Depot.Domain.Settings;
using Depot.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;

namespace Depot.Infrastructure.Settings;

// The password is encrypted with Data Protection; the keys live in data/keys.
public sealed class QBittorrentSettingsRepository(SettingsStore store, IDataProtectionProvider dataProtection)
    : IQBittorrentSettingsRepository
{
    private const string Key = "qbittorrent";

    private readonly IDataProtector protector = dataProtection.CreateProtector("Depot.QBittorrent.Password");

    public async Task<QBittorrentSettings?> GetAsync(CancellationToken cancellationToken)
    {
        var document = await store.GetAsync<Document>(Key, cancellationToken);
        return document is null
            ? null
            : QBittorrentSettings.Create(document.Url, document.Username, protector.Unprotect(document.ProtectedPassword));
    }

    public Task SaveAsync(QBittorrentSettings settings, CancellationToken cancellationToken) =>
        store.SaveAsync(Key, new Document(settings.Url.ToString(), settings.Username, protector.Protect(settings.Password)), cancellationToken);

    private sealed record Document(string Url, string Username, string ProtectedPassword);
}
