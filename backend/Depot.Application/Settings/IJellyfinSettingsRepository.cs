using Depot.Domain.Settings;

namespace Depot.Application.Settings;

public interface IJellyfinSettingsRepository
{
    // Null until the settings are saved for the first time, or when the stored key can't be
    // decrypted anymore.
    Task<JellyfinSettings?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(JellyfinSettings settings, CancellationToken cancellationToken);
}
