using Depot.Domain.Library;

namespace Depot.Application.Library;

// The library folder chosen for each download when it was added, by torrent hash.
public interface ILibraryChoiceRepository
{
    // Records or replaces the choice for a torrent. Replacing it also drops the jobs of an earlier
    // download that weren't imported, so the new choice is examined from scratch.
    Task SaveAsync(string torrentHash, LibraryTarget target, DateTimeOffset now, CancellationToken cancellationToken);

    Task RemoveAsync(string torrentHash, CancellationToken cancellationToken);

    Task<LibraryTarget?> GetAsync(string torrentHash, CancellationToken cancellationToken);

    // Drops the choice and the jobs of an earlier download that weren't imported, as when the
    // torrent is added again as Other.
    Task ForgetAsync(string torrentHash, CancellationToken cancellationToken);

    // The choices whose finished download wasn't turned into jobs yet.
    Task<IReadOnlyDictionary<string, LibraryTarget>> GetPendingAsync(CancellationToken cancellationToken);

    // Every choice, keyed by torrent hash (case-insensitive).
    Task<IReadOnlyDictionary<string, LibraryTarget>> GetAllAsync(CancellationToken cancellationToken);
}
