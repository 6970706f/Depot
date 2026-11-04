using Depot.Domain.Organization;

namespace Depot.Application.Organization;

// IsSaved is false while the settings are still the configured defaults.
public sealed record StoredOrganizationSettings(OrganizationSettings Settings, bool IsSaved);

public interface IOrganizationSettingsRepository
{
    Task<StoredOrganizationSettings> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(OrganizationSettings settings, CancellationToken cancellationToken);
}
