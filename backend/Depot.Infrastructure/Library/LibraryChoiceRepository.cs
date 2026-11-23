using Depot.Application.Library;
using Depot.Domain.Library;
using Depot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Depot.Infrastructure.Library;

public sealed class LibraryChoiceRepository(DepotDbContext dbContext) : ILibraryChoiceRepository
{
    public async Task SaveAsync(string torrentHash, LibraryTarget target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hash = torrentHash.ToLowerInvariant();
        await DeleteUnfinishedJobsAsync(hash, cancellationToken);
        var existing = await dbContext.LibraryChoices.SingleOrDefaultAsync(choice => choice.TorrentHash == hash, cancellationToken);
        if (existing is null)
        {
            dbContext.LibraryChoices.Add(new LibraryChoice(hash, target, now));
        }
        else
        {
            existing.Replace(target, now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ForgetAsync(string torrentHash, CancellationToken cancellationToken)
    {
        var hash = torrentHash.ToLowerInvariant();
        await DeleteUnfinishedJobsAsync(hash, cancellationToken);
        await RemoveAsync(hash, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, LibraryTarget>> GetPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await dbContext.LibraryChoices.AsNoTracking().Where(choice => choice.ExaminedAt == null).ToListAsync(cancellationToken);
        return pending.ToDictionary(choice => choice.TorrentHash, choice => choice.Target, StringComparer.OrdinalIgnoreCase);
    }

    // Imported jobs stay as the history of what reached the library.
    private Task DeleteUnfinishedJobsAsync(string hash, CancellationToken cancellationToken) =>
        dbContext.ImportJobs
            .Where(job => job.TorrentHash == hash && job.Status != ImportJobStatus.Completed)
            .ExecuteDeleteAsync(cancellationToken);

    public Task RemoveAsync(string torrentHash, CancellationToken cancellationToken)
    {
        var hash = torrentHash.ToLowerInvariant();
        return dbContext.LibraryChoices.Where(choice => choice.TorrentHash == hash).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<LibraryTarget?> GetAsync(string torrentHash, CancellationToken cancellationToken)
    {
        var hash = torrentHash.ToLowerInvariant();
        var choice = await dbContext.LibraryChoices.AsNoTracking().SingleOrDefaultAsync(choice => choice.TorrentHash == hash, cancellationToken);
        return choice?.Target;
    }

    public async Task<IReadOnlyDictionary<string, LibraryTarget>> GetAllAsync(CancellationToken cancellationToken)
    {
        var all = await dbContext.LibraryChoices.AsNoTracking().ToListAsync(cancellationToken);
        return all.ToDictionary(choice => choice.TorrentHash, choice => choice.Target, StringComparer.OrdinalIgnoreCase);
    }
}
