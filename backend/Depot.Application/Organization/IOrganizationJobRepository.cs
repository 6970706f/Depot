using Depot.Domain.Organization;

namespace Depot.Application.Organization;

public interface IOrganizationJobRepository
{
    // Records the jobs found in a finished torrent and marks its library choice as examined,
    // all or nothing, so a torrent is never examined twice and never half-recorded.
    Task AddScanAsync(string torrentHash, IReadOnlyList<OrganizationJob> jobs, DateTimeOffset scannedAt, CancellationToken cancellationToken);

    Task<OrganizationJob?> GetAsync(int id, CancellationToken cancellationToken);

    // Newest first, at most limit jobs. A null status lists every job.
    Task<IReadOnlyList<OrganizationJob>> ListAsync(OrganizationJobStatus? status, int limit, CancellationToken cancellationToken);

    // Every job in the status, oldest first.
    Task<IReadOnlyList<OrganizationJob>> ListByStatusAsync(OrganizationJobStatus status, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<OrganizationJobStatus, int>> CountByStatusAsync(CancellationToken cancellationToken);

    // Saves the job and returns it with its new version. Throws JobConcurrencyException when
    // the job changed since it was read, so two writers never overwrite each other.
    Task<OrganizationJob> UpdateAsync(OrganizationJob job, CancellationToken cancellationToken);
}

public sealed class JobConcurrencyException()
    : Exception("This job changed while you were working on it. Reload the list and try again.");

public sealed class JobNotFoundException(int id) : Exception($"There's no organization job {id}.");
