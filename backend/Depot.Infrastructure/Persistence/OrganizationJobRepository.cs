using Depot.Application.Organization;
using Depot.Domain.Organization;
using Depot.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;

namespace Depot.Infrastructure.Persistence;

public sealed class OrganizationJobRepository(DepotDbContext dbContext) : IOrganizationJobRepository
{
    public async Task AddScanAsync(
        string torrentHash,
        IReadOnlyList<OrganizationJob> jobs,
        DateTimeOffset scannedAt,
        CancellationToken cancellationToken)
    {
        var target = await dbContext.LibraryTargets.SingleOrDefaultAsync(existing => existing.Hash == torrentHash, cancellationToken);
        if (target is null)
        {
            // The download was removed while it was being examined; there's nothing to import.
            return;
        }

        target.ScannedAt = scannedAt;
        target.VideoCount = jobs.Count;

        // One SaveChanges is one transaction: the scan and its jobs are recorded together or not at all.
        dbContext.OrganizationJobs.AddRange(jobs.Select(ToRecord));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }
    }

    public async Task<OrganizationJob?> GetAsync(int id, CancellationToken cancellationToken)
    {
        var record = await dbContext.OrganizationJobs.AsNoTracking().SingleOrDefaultAsync(job => job.Id == id, cancellationToken);
        return record is null ? null : ToDomain(record);
    }

    public async Task<IReadOnlyList<OrganizationJob>> ListAsync(OrganizationJobStatus? status, int limit, CancellationToken cancellationToken)
    {
        var query = dbContext.OrganizationJobs.AsNoTracking();
        if (status is { } wanted)
        {
            query = query.Where(job => job.Status == wanted);
        }

        var records = await query.OrderByDescending(job => job.Id).Take(limit).ToListAsync(cancellationToken);
        return records.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<OrganizationJob>> ListByStatusAsync(OrganizationJobStatus status, CancellationToken cancellationToken)
    {
        var records = await dbContext.OrganizationJobs
            .AsNoTracking()
            .Where(job => job.Status == status)
            .OrderBy(job => job.Id)
            .ToListAsync(cancellationToken);
        return records.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyDictionary<OrganizationJobStatus, int>> CountByStatusAsync(CancellationToken cancellationToken)
    {
        var counts = await dbContext.OrganizationJobs
            .AsNoTracking()
            .GroupBy(job => job.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return counts.ToDictionary(entry => entry.Status, entry => entry.Count);
    }

    public async Task<OrganizationJob> UpdateAsync(OrganizationJob job, CancellationToken cancellationToken)
    {
        var record = ToRecord(job);
        record.Id = job.Id;
        record.Version = job.Version + 1;

        var entry = dbContext.OrganizationJobs.Update(record);
        // The row is written only if it still has the version this job was read with.
        entry.Property(stored => stored.Version).OriginalValue = job.Version;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new JobConcurrencyException();
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }

        return ToDomain(record);
    }

    private static OrganizationJobRecord ToRecord(OrganizationJob job) => new()
    {
        TorrentHash = job.TorrentHash,
        TorrentName = job.TorrentName,
        RelativePath = job.RelativePath,
        SizeBytes = job.SizeBytes,
        MediaType = job.Target.Type,
        Title = job.Target.Title,
        Season = job.Target.Season,
        Status = job.Status,
        Message = job.Message,
        DestinationPath = job.DestinationPath,
        ImportedWith = job.ImportedWith,
        Attempts = job.Attempts,
        CreatedAt = job.CreatedAt,
        UpdatedAt = job.UpdatedAt,
        CompletedAt = job.CompletedAt,
        Version = job.Version,
    };

    private static OrganizationJob ToDomain(OrganizationJobRecord record) => OrganizationJob.Restore(
        record.Id,
        record.TorrentHash,
        record.TorrentName,
        record.RelativePath,
        record.SizeBytes,
        new LibraryTarget(record.MediaType, record.Title, record.Season),
        record.Status,
        record.Message,
        record.DestinationPath,
        record.ImportedWith,
        record.Attempts,
        record.CreatedAt,
        record.UpdatedAt,
        record.CompletedAt,
        record.Version);
}
