using Depot.Domain.Media;

namespace Depot.Application.Jellyfin;

public interface ILibrarySyncRunRepository
{
    // Saves a new run and returns it with its id. Only the latest KeptRuns runs are kept.
    Task<LibrarySyncRun> AddAsync(LibrarySyncRun run, CancellationToken cancellationToken);

    Task UpdateAsync(LibrarySyncRun run, CancellationToken cancellationToken);

    Task<LibrarySyncRun?> GetAsync(int id, CancellationToken cancellationToken);

    // Newest first.
    Task<IReadOnlyList<LibrarySyncRun>> ListAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<LibrarySyncRun>> ListRunningAsync(CancellationToken cancellationToken);
}

public sealed class SyncAlreadyRunningException(int? runId)
    : Exception("A library sync is already running. Wait for it to finish.")
{
    public int? RunId { get; } = runId;
}

public sealed class SyncRunNotFoundException(int id) : Exception($"There's no library sync {id}.");
