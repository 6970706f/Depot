using Depot.Application.Jellyfin;
using Depot.Domain.Settings;
using Depot.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;

namespace Depot.Infrastructure.Jellyfin;

// The API key is encrypted with Data Protection, with its own purpose.
public sealed class JellyfinSettingsRepository(SettingsStore store, IDataProtectionProvider dataProtection) : IJellyfinSettingsRepository
{
    private const string Key = "jellyfin";

    private readonly IDataProtector protector = dataProtection.CreateProtector("Depot.Jellyfin.ApiKey");

    public async Task<JellyfinSettings?> GetAsync(CancellationToken cancellationToken)
    {
        var document = await store.GetAsync<Document>(Key, cancellationToken);
        return document is null ? null : JellyfinSettings.Create(document.Url, protector.Unprotect(document.ProtectedApiKey));
    }

    public Task SaveAsync(JellyfinSettings settings, CancellationToken cancellationToken) =>
        store.SaveAsync(Key, new Document(settings.Url.ToString(), protector.Protect(settings.ApiKey)), cancellationToken);

    private sealed record Document(string Url, string ProtectedApiKey);
}
