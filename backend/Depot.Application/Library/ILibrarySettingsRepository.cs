using Depot.Domain.Library;

namespace Depot.Application.Library;

// IsSaved is false while the defaults from configuration are in use.
public sealed record StoredLibrarySettings(LibrarySettings Settings, bool IsSaved);

public interface ILibrarySettingsRepository
{
    Task<StoredLibrarySettings> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(LibrarySettings settings, CancellationToken cancellationToken);
}
