using Depot.Application.Organization;
using Depot.Domain.Organization;
using Depot.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;

namespace Depot.Infrastructure.Persistence;

public sealed class LibraryTargetRepository(DepotDbContext dbContext) : ILibraryTargetRepository
{
    public async Task SaveAsync(string torrentHash, LibraryTarget target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var record = await dbContext.LibraryTargets.SingleOrDefaultAsync(existing => existing.Hash == torrentHash, cancellationToken);
        if (record is null)
        {
            dbContext.LibraryTargets.Add(new LibraryTargetRecord
            {
                Hash = torrentHash,
                MediaType = target.Type,
                Title = target.Title,
                Season = target.Season,
                CreatedAt = now,
            });
        }
        else
        {
            // The same torrent added again: the new choice replaces the old one, and the jobs
            // of the earlier download go, so the new choice is examined from scratch. Library
            // files stay where they are.
            record.MediaType = target.Type;
            record.Title = target.Title;
            record.Season = target.Season;
            record.CreatedAt = now;
            record.ScannedAt = null;
            record.VideoCount = 0;
            dbContext.OrganizationJobs.RemoveRange(
                await dbContext.OrganizationJobs.Where(job => job.TorrentHash == record.Hash).ToListAsync(cancellationToken));
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }
    }

    public async Task RemoveAsync(string torrentHash, CancellationToken cancellationToken)
    {
        await dbContext.LibraryTargets.Where(target => target.Hash == torrentHash).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task ForgetAsync(string torrentHash, CancellationToken cancellationToken)
    {
        // One transaction, so a choice is never left without its jobs or the other way round.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.OrganizationJobs.Where(job => job.TorrentHash == torrentHash).ExecuteDeleteAsync(cancellationToken);
        await dbContext.LibraryTargets.Where(target => target.Hash == torrentHash).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<LibraryTarget?> GetAsync(string torrentHash, CancellationToken cancellationToken)
    {
        var record = await dbContext.LibraryTargets.AsNoTracking().SingleOrDefaultAsync(target => target.Hash == torrentHash, cancellationToken);
        return record is null ? null : new LibraryTarget(record.MediaType, record.Title, record.Season);
    }

    public async Task<IReadOnlyDictionary<string, LibraryTarget>> GetAllAsync(CancellationToken cancellationToken) =>
        ToDictionary(await dbContext.LibraryTargets.AsNoTracking().ToListAsync(cancellationToken));

    public async Task<IReadOnlyDictionary<string, LibraryTarget>> GetPendingAsync(CancellationToken cancellationToken) =>
        ToDictionary(await dbContext.LibraryTargets.AsNoTracking().Where(target => target.ScannedAt == null).ToListAsync(cancellationToken));

    private static Dictionary<string, LibraryTarget> ToDictionary(List<LibraryTargetRecord> records) =>
        records.ToDictionary(
            record => record.Hash,
            record => new LibraryTarget(record.MediaType, record.Title, record.Season),
            StringComparer.OrdinalIgnoreCase);
}
