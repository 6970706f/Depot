namespace Depot.Infrastructure.Persistence.Records;

// How the qBittorrent settings are stored. Kept apart from the domain entity so the
// table can hold the encrypted password while the domain only knows the plain one.
public sealed class QBittorrentSettingsRecord
{
    // The settings are a single row.
    public const int SingletonId = 1;

    public int Id { get; set; }

    public required string Url { get; set; }

    public required string Username { get; set; }

    public required string ProtectedPassword { get; set; }
}
