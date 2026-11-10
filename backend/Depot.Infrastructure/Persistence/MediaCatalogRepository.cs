using System.Text.Json;
using System.Text.Json.Serialization;
using Depot.Application.Media;
using Depot.Domain.Media;
using Depot.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;

namespace Depot.Infrastructure.Persistence;

public sealed class MediaCatalogRepository(DepotDbContext dbContext) : IMediaCatalogRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

    public async Task<SavedLibraries> SaveLibrariesAsync(IReadOnlyList<MediaLibrary> libraries, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var records = await dbContext.MediaLibraries.ToListAsync(cancellationToken);
        var current = libraries.Select(library => library.ExternalId).ToHashSet(StringComparer.Ordinal);

        // Gone from the server, or no longer a kind Depot keeps: their items go too (the files
        // with them, by cascade).
        var emptied = records
            .Where(record => !current.Contains(record.ExternalId)
                || libraries.Single(library => library.ExternalId == record.ExternalId).IsSynced is false)
            .Select(record => record.Id)
            .ToList();
        var removedItems = emptied.Count == 0
            ? 0
            : await dbContext.MediaItems.Where(item => emptied.Contains(item.LibraryId)).ExecuteDeleteAsync(cancellationToken);
        dbContext.MediaLibraries.RemoveRange(records.Where(record => !current.Contains(record.ExternalId)));

        foreach (var library in libraries)
        {
            var record = records.SingleOrDefault(existing => existing.ExternalId == library.ExternalId);
            if (record is null)
            {
                record = new MediaLibraryRecord { ExternalId = library.ExternalId, Name = library.Name, Locations = "[]" };
                dbContext.MediaLibraries.Add(record);
                records.Add(record);
            }

            record.Name = library.Name;
            record.Kind = library.Kind;
            record.Locations = Serialize(library.Locations);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SavedLibraries(
                records.Where(record => current.Contains(record.ExternalId)).ToDictionary(record => record.ExternalId, record => record.Id),
                removedItems);
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }
    }

    public async Task<SavedItems> SaveItemsAsync(int libraryId, IReadOnlyList<MediaItem> items, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var distinct = items.DistinctBy(item => item.ExternalId).ToList();
        if (distinct.Count == 0)
        {
            return new SavedItems(0, 0);
        }

        var ids = distinct.Select(item => item.ExternalId).ToList();
        var existing = await dbContext.MediaItems
            .Include(item => item.Files)
            .Where(item => ids.Contains(item.ExternalId))
            .ToDictionaryAsync(item => item.ExternalId, cancellationToken);

        int added = 0, updated = 0;
        foreach (var item in distinct)
        {
            if (!existing.TryGetValue(item.ExternalId, out var record))
            {
                record = new MediaItemRecord
                {
                    ExternalId = item.ExternalId,
                    Name = item.Name,
                    SortName = item.Name,
                    Genres = "[]",
                    Studios = "[]",
                    ProviderIds = "{}",
                    BackdropImageTags = "[]",
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                Apply(record, item, libraryId);
                ApplyFiles(record, item.Files);
                dbContext.MediaItems.Add(record);
                added++;
                continue;
            }

            Apply(record, item, libraryId);
            var filesChanged = ApplyFiles(record, item.Files);
            // Entry() looks for changes in this item, so only real changes count as updates.
            if (filesChanged || dbContext.Entry(record).State == EntityState.Modified)
            {
                record.UpdatedAt = now;
                updated++;
            }
        }

        // One SaveChanges is one transaction: the page is saved whole or not at all.
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }

        return new SavedItems(added, updated);
    }

    public async Task<IReadOnlyList<string>> ListExternalIdsAsync(int libraryId, CancellationToken cancellationToken) =>
        await dbContext.MediaItems
            .AsNoTracking()
            .Where(item => item.LibraryId == libraryId)
            .OrderBy(item => item.Id)
            .Select(item => item.ExternalId)
            .ToListAsync(cancellationToken);

    public async Task<int> RemoveItemsAsync(IReadOnlyCollection<string> externalIds, CancellationToken cancellationToken)
    {
        if (externalIds.Count == 0)
        {
            return 0;
        }

        var ids = externalIds.ToList();
        return await dbContext.MediaItems.Where(item => ids.Contains(item.ExternalId)).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task MarkLibrarySyncedAsync(int libraryId, DateTimeOffset syncedAt, CancellationToken cancellationToken) =>
        await dbContext.MediaLibraries
            .Where(library => library.Id == libraryId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(library => library.LastSyncedAt, syncedAt), cancellationToken);

    public async Task<IReadOnlyList<CatalogLibrary>> ListLibrariesAsync(CancellationToken cancellationToken)
    {
        var libraries = await dbContext.MediaLibraries.AsNoTracking().OrderBy(library => library.Name).ToListAsync(cancellationToken);
        var counts = await dbContext.MediaItems
            .AsNoTracking()
            .GroupBy(item => new { item.LibraryId, item.Kind })
            .Select(group => new { group.Key.LibraryId, group.Key.Kind, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return libraries
            .Select(library => new CatalogLibrary(
                library.Id,
                ToDomain(library),
                counts.Where(count => count.LibraryId == library.Id).ToDictionary(count => count.Kind, count => count.Count),
                library.LastSyncedAt))
            .ToList();
    }

    public async Task<CatalogPage> ListItemsAsync(CatalogQuery query, CancellationToken cancellationToken)
    {
        var kinds = query.Kinds.ToList();
        var items = dbContext.MediaItems.AsNoTracking().Where(item => kinds.Contains(item.Kind));
        if (query.LibraryId is { } libraryId)
        {
            items = items.Where(item => item.LibraryId == libraryId);
        }

        if (query.SeriesExternalId is { } seriesId)
        {
            items = items.Where(item => item.SeriesExternalId == seriesId);
        }

        if (query.SeasonExternalId is { } seasonId)
        {
            items = items.Where(item => item.SeasonExternalId == seasonId);
        }

        if (!string.IsNullOrEmpty(query.Search))
        {
            // LIKE with the wildcards in the search taken literally.
            var pattern = $"%{query.Search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            items = items.Where(item => EF.Functions.Like(item.Name, pattern, "\\")
                || (item.OriginalTitle != null && EF.Functions.Like(item.OriginalTitle, pattern, "\\")));
        }

        var total = await items.CountAsync(cancellationToken);
        var sorted = query.Sort switch
        {
            CatalogSort.Added => items.OrderByDescending(item => item.AddedAt).ThenBy(item => item.SortName),
            CatalogSort.Year => items.OrderByDescending(item => item.ProductionYear).ThenBy(item => item.SortName),
            CatalogSort.Number => items.OrderBy(item => item.ParentIndexNumber).ThenBy(item => item.IndexNumber).ThenBy(item => item.SortName),
            _ => items.OrderBy(item => item.SortName),
        };

        var page = await sorted
            .ThenBy(item => item.Id)
            .Skip(query.Skip)
            .Take(query.Take)
            .Select(item => new { Item = item, FileCount = item.Files.Count })
            .ToListAsync(cancellationToken);
        return new CatalogPage(page.Select(entry => ToCatalogItem(entry.Item, entry.FileCount)).ToList(), total);
    }

    public async Task<CatalogItem?> GetItemAsync(int id, bool includeFiles, CancellationToken cancellationToken)
    {
        var items = dbContext.MediaItems.AsNoTracking().Where(item => item.Id == id);
        if (includeFiles)
        {
            var record = await items.Include(item => item.Files.OrderBy(file => file.Position)).SingleOrDefaultAsync(cancellationToken);
            return record is null ? null : ToCatalogItem(record, record.Files.Count);
        }

        var entry = await items.Select(item => new { Item = item, FileCount = item.Files.Count }).SingleOrDefaultAsync(cancellationToken);
        return entry is null ? null : ToCatalogItem(entry.Item, entry.FileCount);
    }

    public async Task<CatalogItem?> FindByExternalIdAsync(string externalId, CancellationToken cancellationToken)
    {
        var entry = await dbContext.MediaItems
            .AsNoTracking()
            .Where(item => item.ExternalId == externalId)
            .Select(item => new { Item = item, FileCount = item.Files.Count })
            .SingleOrDefaultAsync(cancellationToken);
        return entry is null ? null : ToCatalogItem(entry.Item, entry.FileCount);
    }

    private static void Apply(MediaItemRecord record, MediaItem item, int libraryId)
    {
        record.LibraryId = libraryId;
        record.Kind = item.Kind;
        record.Name = item.Name;
        record.OriginalTitle = item.OriginalTitle;
        record.SortName = item.SortName ?? item.Name;
        record.Overview = item.Overview;
        record.ProductionYear = item.ProductionYear;
        record.PremiereDate = item.PremiereDate;
        record.EndDate = item.EndDate;
        record.OfficialRating = item.OfficialRating;
        record.CommunityRating = item.CommunityRating;
        record.RuntimeTicks = item.Runtime?.Ticks;
        record.Genres = Serialize(item.Genres);
        record.Studios = Serialize(item.Studios);
        // Sorted, so the same ids in another order aren't a change.
        record.ProviderIds = Serialize(new SortedDictionary<string, string>(item.ProviderIds.ToDictionary(), StringComparer.Ordinal));
        record.SeriesStatus = item.SeriesStatus;
        record.SeriesExternalId = item.SeriesExternalId;
        record.SeasonExternalId = item.SeasonExternalId;
        record.IndexNumber = item.IndexNumber;
        record.IndexNumberEnd = item.IndexNumberEnd;
        record.ParentIndexNumber = item.ParentIndexNumber;
        record.Path = item.Path;
        record.AddedAt = item.AddedAt?.ToUniversalTime();
        record.PrimaryImageTag = item.Images.Primary;
        record.ThumbImageTag = item.Images.Thumb;
        record.LogoImageTag = item.Images.Logo;
        record.BackdropImageTags = Serialize(item.Images.Backdrops);
    }

    // Matches files by their external id: new ones are added, gone ones deleted, the rest
    // updated in place. Returns whether anything changed.
    private bool ApplyFiles(MediaItemRecord record, IReadOnlyList<MediaFile> files)
    {
        var changed = false;
        var wanted = files.DistinctBy(file => file.ExternalId).ToList();
        foreach (var stale in record.Files.Where(existing => wanted.All(file => file.ExternalId != existing.ExternalId)).ToList())
        {
            record.Files.Remove(stale);
            if (record.Id != 0)
            {
                dbContext.MediaFiles.Remove(stale);
            }

            changed = true;
        }

        for (var position = 0; position < wanted.Count; position++)
        {
            var file = wanted[position];
            var fileRecord = record.Files.SingleOrDefault(existing => existing.ExternalId == file.ExternalId);
            if (fileRecord is null)
            {
                fileRecord = new MediaFileRecord { ExternalId = file.ExternalId, Streams = "[]" };
                record.Files.Add(fileRecord);
                changed = true;
            }

            fileRecord.Position = position;
            fileRecord.Name = file.Name;
            fileRecord.Path = file.Path;
            fileRecord.Container = file.Container;
            fileRecord.SizeBytes = file.SizeBytes;
            fileRecord.Bitrate = file.Bitrate;
            fileRecord.RuntimeTicks = file.Runtime?.Ticks;
            fileRecord.Streams = Serialize(file.Streams);
            if (fileRecord.Id != 0 && dbContext.Entry(fileRecord).State == EntityState.Modified)
            {
                changed = true;
            }
        }

        return changed;
    }

    private static CatalogItem ToCatalogItem(MediaItemRecord record, int fileCount) => new(
        record.Id,
        record.LibraryId,
        new MediaItem
        {
            ExternalId = record.ExternalId,
            Kind = record.Kind,
            Name = record.Name,
            OriginalTitle = record.OriginalTitle,
            SortName = record.SortName,
            Overview = record.Overview,
            ProductionYear = record.ProductionYear,
            PremiereDate = record.PremiereDate,
            EndDate = record.EndDate,
            OfficialRating = record.OfficialRating,
            CommunityRating = record.CommunityRating,
            Runtime = record.RuntimeTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null,
            Genres = Deserialize<List<string>>(record.Genres) ?? [],
            Studios = Deserialize<List<string>>(record.Studios) ?? [],
            ProviderIds = Deserialize<Dictionary<string, string>>(record.ProviderIds) ?? [],
            SeriesStatus = record.SeriesStatus,
            SeriesExternalId = record.SeriesExternalId,
            SeasonExternalId = record.SeasonExternalId,
            IndexNumber = record.IndexNumber,
            IndexNumberEnd = record.IndexNumberEnd,
            ParentIndexNumber = record.ParentIndexNumber,
            Path = record.Path,
            AddedAt = record.AddedAt,
            Images = new MediaImageTags(
                record.PrimaryImageTag, record.ThumbImageTag, record.LogoImageTag, Deserialize<List<string>>(record.BackdropImageTags) ?? []),
            Files = record.Files
                .OrderBy(file => file.Position)
                .Select(file => new MediaFile
                {
                    ExternalId = file.ExternalId,
                    Name = file.Name,
                    Path = file.Path,
                    Container = file.Container,
                    SizeBytes = file.SizeBytes,
                    Bitrate = file.Bitrate,
                    Runtime = file.RuntimeTicks is { } fileTicks ? TimeSpan.FromTicks(fileTicks) : null,
                    Streams = Deserialize<List<MediaStream>>(file.Streams) ?? [],
                })
                .ToList(),
        },
        fileCount,
        record.CreatedAt,
        record.UpdatedAt);

    private static MediaLibrary ToDomain(MediaLibraryRecord record) =>
        new(record.ExternalId, record.Name, record.Kind, Deserialize<List<string>>(record.Locations) ?? []);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);
}
