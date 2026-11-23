using Depot.Domain.Settings;

namespace Depot.Application.Jellyfin;

public sealed record JellyfinServerInfo(string ServerName, string Version);

public interface IJellyfinSettingsRepository
{
    Task<JellyfinSettings?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(JellyfinSettings settings, CancellationToken cancellationToken);
}

// The Jellyfin REST API with the saved settings, read live. Ids are Jellyfin's, as 32 lowercase
// hex characters. Every method throws JellyfinException on failure.
public interface IJellyfinClient
{
    Task<JellyfinServerInfo> GetServerInfoAsync(CancellationToken cancellationToken);

    // Asks Jellyfin to scan its libraries for new files.
    Task RefreshLibraryAsync(CancellationToken cancellationToken);

    Task<MediaPage> GetItemsAsync(MediaQuery query, CancellationToken cancellationToken);

    Task<MediaCounts> GetCountsAsync(CancellationToken cancellationToken);

    // A movie or series with its details; null when Jellyfin has no such item. A series comes
    // with its seasons and their episodes.
    Task<MediaDetail?> GetItemAsync(string id, CancellationToken cancellationToken);

    // Null when the item has no such image.
    Task<MediaImage?> GetImageAsync(string id, MediaImageType type, int width, string? tag, CancellationToken cancellationToken);
}

public interface IJellyfinConnectionTester
{
    // Checks settings that may not be saved yet.
    Task<JellyfinServerInfo> TestAsync(JellyfinSettings settings, CancellationToken cancellationToken);
}
