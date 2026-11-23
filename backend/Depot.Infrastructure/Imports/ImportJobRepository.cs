using Depot.Application.Imports;
using Depot.Domain.Library;
using Depot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Depot.Infrastructure.Imports;

public sealed class ImportJobRepository(DepotDbContext dbContext) : IImportJobRepository
{
    // The page lists the latest jobs; older ones stay in the database as history.
    private const int ListLimit = 500;

    public async Task AddScanAsync(string torrentHash, IReadOnlyList<ImportJob> jobs, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hash = torrentHash.ToLowerInvariant();
        var choice = await dbContext.LibraryChoices.SingleOrDefaultAsync(choice => choice.TorrentHash == hash, cancellationToken);
        choice?.MarkExamined(now);
        dbContext.ImportJobs.AddRange(jobs);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ImportJob>> ListAsync(ImportJobStatus? status, CancellationToken cancellationToken)
    {
        var query = dbContext.ImportJobs.AsQueryable();
        if (status is { } wanted)
        {
            query = query.Where(job => job.Status == wanted);
        }

        return await query.OrderByDescending(job => job.Id).Take(ListLimit).AsNoTracking().ToListAsync(cancellationToken);
    }

    public Task<ImportJob?> GetAsync(int id, CancellationToken cancellationToken) =>
        dbContext.ImportJobs.SingleOrDefaultAsync(job => job.Id == id, cancellationToken);

    public async Task SaveAsync(ImportJob job, CancellationToken cancellationToken)
    {
        if (dbContext.Entry(job).State == EntityState.Detached)
        {
            dbContext.ImportJobs.Update(job);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<ImportJobStatus, int>> CountAsync(CancellationToken cancellationToken)
    {
        var counts = await dbContext.ImportJobs
            .GroupBy(job => job.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return Enum.GetValues<ImportJobStatus>().ToDictionary(status => status, status => counts.FirstOrDefault(entry => entry.Status == status)?.Count ?? 0);
    }
}
