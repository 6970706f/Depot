using Depot.Domain.Organization;

namespace Depot.Application.Organization;

// The library folder chosen for each download when it was added, by torrent hash.
public interface ILibraryTargetRepository
{
    // Records or replaces the choice for a torrent. Replacing it also forgets the jobs of an
    // earlier download of the same torrent, so the new choice is imported from scratch.
    Task SaveAsync(string torrentHash, LibraryTarget target, DateTimeOffset now, CancellationToken cancellationToken);

    // Drops the choice for a torrent. Its jobs stay, as the history of what was imported.
    Task RemoveAsync(string torrentHash, CancellationToken cancellationToken);

    // Drops the choice and the jobs of an earlier download of the torrent, as when it's added
    // again without a choice (Other): nothing from the earlier download may be imported anymore.
    Task ForgetAsync(string torrentHash, CancellationToken cancellationToken);

    // Null when the torrent has no choice.
    Task<LibraryTarget?> GetAsync(string torrentHash, CancellationToken cancellationToken);

    // Every choice, keyed by torrent hash (case-insensitive).
    Task<IReadOnlyDictionary<string, LibraryTarget>> GetAllAsync(CancellationToken cancellationToken);

    // The choices whose torrent wasn't examined yet, keyed by torrent hash (case-insensitive).
    Task<IReadOnlyDictionary<string, LibraryTarget>> GetPendingAsync(CancellationToken cancellationToken);
}
