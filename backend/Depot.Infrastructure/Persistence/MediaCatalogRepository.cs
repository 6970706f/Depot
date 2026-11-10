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
                    People = "[]",
                    Tags = "[]",
                    ProductionLocations = "[]",
                    MemberIds = "[]",
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
            var pattern = $"%{EscapeLike(query.Search)}%";
            items = items.Where(item => EF.Functions.Like(item.Name, pattern, "\\")
                || (item.OriginalTitle != null && EF.Functions.Like(item.OriginalTitle, pattern, "\\")));
        }

        if (!string.IsNullOrEmpty(query.Genre))
        {
            // The genres are a JSON list: the genre as a JSON string, quotes included, is an exact match.
            var pattern = $"%{EscapeLike(Serialize(query.Genre))}%";
            items = items.Where(item => EF.Functions.Like(item.Genres, pattern, "\\"));
        }

        if (query.Year is { } year)
        {
            items = items.Where(item => item.ProductionYear == year);
        }

        if (query.Anime is { } anime)
        {
            items = items.Where(item => item.IsAnime == anime);
        }

        if (query.ExternalIds is { } externalIds)
        {
            var ids = externalIds.ToList();
            items = items.Where(item => ids.Contains(item.ExternalId));
        }

        var total = await items.CountAsync(cancellationToken);
        var sorted = query.Sort switch
        {
            CatalogSort.Added => items.OrderByDescending(item => item.AddedAt).ThenBy(item => item.SortName),
            CatalogSort.Year => items.OrderByDescending(item => item.ProductionYear).ThenBy(item => item.SortName),
            CatalogSort.Release => items.OrderByDescending(item => item.PremiereDate).ThenByDescending(item => item.ProductionYear).ThenBy(item => item.SortName),
            CatalogSort.Number => items.OrderBy(item => item.ParentIndexNumber).ThenBy(item => item.IndexNumber).ThenBy(item => item.SortName),
            _ => items.OrderBy(item => item.SortName),
        };

        var page = await WithCounts(sorted.ThenBy(item => item.Id).Skip(query.Skip).Take(query.Take)).ToListAsync(cancellationToken);
        return new CatalogPage(page.Select(ToCatalogItem).ToList(), total);
    }

    public async Task<CatalogItem?> GetItemAsync(int id, bool includeFiles, CancellationToken cancellationToken)
    {
        var items = dbContext.MediaItems.AsNoTracking().Where(item => item.Id == id);
        if (includeFiles)
        {
            items = items.Include(item => item.Files.OrderBy(file => file.Position));
        }

        var entry = await WithCounts(items).SingleOrDefaultAsync(cancellationToken);
        return entry is null ? null : ToCatalogItem(entry);
    }

    public async Task<CatalogItem?> FindByExternalIdAsync(string externalId, CancellationToken cancellationToken) =>
        (await FindByExternalIdsAsync([externalId], cancellationToken)).SingleOrDefault();

    public async Task<IReadOnlyList<CatalogItem>> FindByExternalIdsAsync(IReadOnlyCollection<string> externalIds, CancellationToken cancellationToken)
    {
        var ids = externalIds.ToList();
        var entries = await WithCounts(dbContext.MediaItems.AsNoTracking().Where(item => ids.Contains(item.ExternalId))).ToListAsync(cancellationToken);
        return entries.Select(ToCatalogItem).ToList();
    }

    public async Task<IReadOnlyList<CatalogItem>> ListCollectionsContainingAsync(string externalId, CancellationToken cancellationToken)
    {
        var pattern = $"%{EscapeLike(Serialize(externalId))}%";
        var entries = await WithCounts(dbContext.MediaItems
                .AsNoTracking()
                .Where(item => item.Kind == MediaItemKind.Collection && EF.Functions.Like(item.MemberIds, pattern, "\\"))
                .OrderBy(item => item.SortName))
            .ToListAsync(cancellationToken);
        return entries.Select(ToCatalogItem).ToList();
    }

    // Counted in memory from the genre lists and years alone: a few kilobytes even for a large
    // library, and SQLite has no way to group by the elements of a JSON list through EF.
    public async Task<CatalogFacets> GetFacetsAsync(IReadOnlyCollection<MediaItemKind> kinds, bool? anime, CancellationToken cancellationToken)
    {
        var kindList = kinds.ToList();
        var items = dbContext.MediaItems.AsNoTracking().Where(item => kindList.Contains(item.Kind));
        if (anime is { } wanted)
        {
            items = items.Where(item => item.IsAnime == wanted);
        }

        var rows = await items.Select(item => new { item.Genres, item.ProductionYear }).ToListAsync(cancellationToken);
        var genres = rows
            .SelectMany(row => Deserialize<List<string>>(row.Genres) ?? [])
            .GroupBy(genre => genre, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CatalogCount<string>(group.First(), group.Count()))
            .OrderByDescending(count => count.Count)
            .ThenBy(count => count.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var years = rows
            .Where(row => row.ProductionYear is not null)
            .GroupBy(row => row.ProductionYear!.Value)
            .Select(group => new CatalogCount<int>(group.Key, group.Count()))
            .OrderByDescending(count => count.Value)
            .ToList();
        return new CatalogFacets(genres, years);
    }

    public async Task<CatalogStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        var counts = await dbContext.MediaItems
            .AsNoTracking()
            .GroupBy(item => new { item.Kind, item.IsAnime })
            .Select(group => new { group.Key.Kind, group.Key.IsAnime, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var files = await dbContext.MediaFiles
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Size = group.Sum(file => file.SizeBytes ?? 0) })
            .SingleOrDefaultAsync(cancellationToken);
        var synced = await dbContext.MediaLibraries.AsNoTracking().Select(library => library.LastSyncedAt).ToListAsync(cancellationToken);

        return new CatalogStats(
            counts.GroupBy(count => count.Kind).ToDictionary(group => group.Key, group => group.Sum(count => count.Count)),
            counts.Where(count => count.Kind == MediaItemKind.Movie && count.IsAnime).Sum(count => count.Count),
            counts.Where(count => count.Kind == MediaItemKind.Series && count.IsAnime).Sum(count => count.Count),
            files?.Count ?? 0,
            files?.Size ?? 0,
            synced.Max());
    }

    // The item with its number of files and, for a series or season, of episodes.
    private IQueryable<Counted> WithCounts(IQueryable<MediaItemRecord> items) =>
        items.Select(item => new Counted
        {
            Item = item,
            FileCount = item.Files.Count,
            EpisodeCount = item.Kind == MediaItemKind.Series
                ? dbContext.MediaItems.Count(episode => episode.Kind == MediaItemKind.Episode && episode.SeriesExternalId == item.ExternalId)
                : item.Kind == MediaItemKind.Season
                    ? dbContext.MediaItems.Count(episode => episode.Kind == MediaItemKind.Episode && episode.SeasonExternalId == item.ExternalId)
                    : 0,
        });

    // LIKE with the wildcards in the text taken literally.
    private static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

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
        record.People = Serialize(item.People);
        record.Tags = Serialize(item.Tags);
        record.ProductionLocations = Serialize(item.ProductionLocations);
        record.MemberIds = Serialize(item.MemberExternalIds);
        record.IsAnime = item.IsAnime;
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

    private static CatalogItem ToCatalogItem(Counted counted) => ToCatalogItem(counted.Item, counted.FileCount, counted.EpisodeCount);

    private static CatalogItem ToCatalogItem(MediaItemRecord record, int fileCount, int episodeCount) => new(
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
            People = Deserialize<List<MediaPerson>>(record.People) ?? [],
            Tags = Deserialize<List<string>>(record.Tags) ?? [],
            ProductionLocations = Deserialize<List<string>>(record.ProductionLocations) ?? [],
            MemberExternalIds = Deserialize<List<string>>(record.MemberIds) ?? [],
            IsAnime = record.IsAnime,
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
        episodeCount,
        record.CreatedAt,
        record.UpdatedAt);

    private static MediaLibrary ToDomain(MediaLibraryRecord record) =>
        new(record.ExternalId, record.Name, record.Kind, Deserialize<List<string>>(record.Locations) ?? []);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);

    private sealed class Counted
    {
        public required MediaItemRecord Item { get; init; }

        public int FileCount { get; init; }

        public int EpisodeCount { get; init; }
    }
}
