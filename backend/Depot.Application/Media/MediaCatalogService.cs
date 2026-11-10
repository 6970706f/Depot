using Depot.Domain.Common;
using Depot.Domain.Media;

namespace Depot.Application.Media;

// Reads Depot's copy of the media library. Nothing here calls Jellyfin: the copy answers even
// while the server is down, as of the last sync.
public sealed class MediaCatalogService(IMediaCatalogRepository catalog)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    // Seasons of a series, episodes of a season or members of a collection, listed with an item.
    public const int MaxChildren = 1000;

    public const int MaxSearchLength = 200;

    public const int TopGenreCount = 8;

    // Posters shown for a collection without its own.
    private const int PreviewCount = 4;

    private static readonly MediaItemKind[] Titles = [MediaItemKind.Movie, MediaItemKind.Series];

    public async Task<IReadOnlyList<MediaLibraryView>> ListLibrariesAsync(CancellationToken cancellationToken)
    {
        var libraries = await catalog.ListLibrariesAsync(cancellationToken);
        return libraries
            .Select(entry => new MediaLibraryView(
                entry.Id,
                entry.Library.ExternalId,
                entry.Library.Name,
                entry.Library.Kind,
                entry.Library.Locations,
                entry.Library.IsSynced,
                entry.Counts.GetValueOrDefault(MediaItemKind.Movie),
                entry.Counts.GetValueOrDefault(MediaItemKind.Series),
                entry.Counts.GetValueOrDefault(MediaItemKind.Season),
                entry.Counts.GetValueOrDefault(MediaItemKind.Episode),
                entry.LastSyncedAt))
            .ToList();
    }

    public async Task<MediaItemPageView> ListItemsAsync(MediaItemQueryInput input, CancellationToken cancellationToken)
    {
        var page = input.Page ?? 1;
        if (page < 1)
        {
            throw new DomainValidationException("page", "Pages start at 1.");
        }

        var pageSize = input.PageSize ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize)
        {
            throw new DomainValidationException("pageSize", $"Ask for 1 to {MaxPageSize} items per page.");
        }

        var search = Trimmed(input.Search, "search");
        var genre = Trimmed(input.Genre, "genre");
        if (input.Year is < 1800 or > 3000)
        {
            throw new DomainValidationException("year", "Use a year from 1800 to 3000.");
        }

        IReadOnlyCollection<string>? members = null;
        if (input.CollectionId is { } collectionId)
        {
            var collection = await catalog.GetItemAsync(collectionId, includeFiles: false, cancellationToken);
            if (collection?.Item.Kind != MediaItemKind.Collection)
            {
                throw new MediaItemNotFoundException(collectionId);
            }

            members = collection.Item.MemberExternalIds;
        }

        var query = new CatalogQuery(
            ParseKinds(input.Kind),
            input.LibraryId,
            Search: search,
            Genre: genre,
            Year: input.Year,
            Anime: input.Anime,
            ExternalIds: members,
            Sort: ParseSort(input.Sort),
            Skip: (int)Math.Min(int.MaxValue, (long)(page - 1) * pageSize),
            Take: pageSize);
        var result = await catalog.ListItemsAsync(query, cancellationToken);
        return new MediaItemPageView(await SummariesAsync(result.Items, withOverview: false, cancellationToken), result.Total, page, pageSize);
    }

    public async Task<MediaFiltersView> GetFiltersAsync(string? kind, bool? anime, CancellationToken cancellationToken)
    {
        var facets = await catalog.GetFacetsAsync(ParseKinds(kind), anime, cancellationToken);
        return new MediaFiltersView(
            facets.Genres.Select(genre => new MediaCountView<string>(genre.Value, genre.Count)).ToList(),
            facets.Years.Select(year => new MediaCountView<int>(year.Value, year.Count)).ToList());
    }

    public async Task<MediaStatsView> GetStatsAsync(CancellationToken cancellationToken)
    {
        var stats = await catalog.GetStatsAsync(cancellationToken);
        var genres = await catalog.GetFacetsAsync(Titles, anime: null, cancellationToken);
        return new MediaStatsView(
            stats.Counts.GetValueOrDefault(MediaItemKind.Movie),
            stats.Counts.GetValueOrDefault(MediaItemKind.Series),
            stats.Counts.GetValueOrDefault(MediaItemKind.Season),
            stats.Counts.GetValueOrDefault(MediaItemKind.Episode),
            stats.Counts.GetValueOrDefault(MediaItemKind.Collection),
            stats.AnimeMovies,
            stats.AnimeSeries,
            stats.Files,
            stats.SizeBytes,
            genres.Genres.Take(TopGenreCount).Select(genre => new MediaCountView<string>(genre.Value, genre.Count)).ToList(),
            stats.LastSyncedAt);
    }

    public async Task<MediaItemDetailView> GetItemAsync(int id, CancellationToken cancellationToken)
    {
        var entry = await catalog.GetItemAsync(id, includeFiles: true, cancellationToken) ?? throw new MediaItemNotFoundException(id);
        var item = entry.Item;

        var series = item.SeriesExternalId is { } seriesId ? await ReferenceAsync(seriesId, cancellationToken) : null;
        var season = item.SeasonExternalId is { } seasonId ? await ReferenceAsync(seasonId, cancellationToken) : null;
        var collections = item.Kind is MediaItemKind.Movie or MediaItemKind.Series
            ? (await catalog.ListCollectionsContainingAsync(item.ExternalId, cancellationToken)).Select(found => new MediaItemReferenceView(found.Id, found.Item.Name)).ToList()
            : [];
        var children = item.Kind switch
        {
            MediaItemKind.Series => await ChildrenAsync(new CatalogQuery([MediaItemKind.Season], SeriesExternalId: item.ExternalId, Sort: CatalogSort.Number), cancellationToken),
            MediaItemKind.Season => await ChildrenAsync(new CatalogQuery([MediaItemKind.Episode], SeasonExternalId: item.ExternalId, Sort: CatalogSort.Number), cancellationToken),
            MediaItemKind.Collection => await ChildrenAsync(new CatalogQuery(Titles, ExternalIds: item.MemberExternalIds, Sort: CatalogSort.Release), cancellationToken),
            _ => [],
        };

        return new MediaItemDetailView(
            entry.Id,
            entry.LibraryId,
            item.ExternalId,
            item.Kind,
            item.Name,
            item.OriginalTitle,
            item.Overview,
            item.ProductionYear,
            item.PremiereDate,
            item.EndDate,
            item.OfficialRating,
            item.CommunityRating,
            item.Runtime?.TotalSeconds,
            item.Genres,
            item.Studios,
            item.ProductionLocations,
            item.ProviderIds,
            item.SeriesStatus,
            SeasonNumber(item),
            EpisodeNumber(item),
            item.Kind == MediaItemKind.Episode ? item.IndexNumberEnd : null,
            item.IsAnime,
            entry.EpisodeCount,
            item.Path,
            item.AddedAt,
            entry.UpdatedAt,
            MediaImagesView.From(item.Images),
            item.People.Select((person, index) => new MediaPersonView(index, person.Name, person.Kind, person.Role, person.ImageTag)).ToList(),
            item.Files.Select(ToView).ToList(),
            series,
            season,
            collections,
            children);
    }

    private async Task<IReadOnlyList<MediaItemSummaryView>> ChildrenAsync(CatalogQuery query, CancellationToken cancellationToken)
    {
        var page = await catalog.ListItemsAsync(query with { Take = MaxChildren }, cancellationToken);
        return await SummariesAsync(page.Items, withOverview: true, cancellationToken);
    }

    // Collections get the posters of their first members, fetched for the whole page at once.
    private async Task<IReadOnlyList<MediaItemSummaryView>> SummariesAsync(
        IReadOnlyList<CatalogItem> entries,
        bool withOverview,
        CancellationToken cancellationToken)
    {
        var previewIds = entries
            .Where(entry => entry.Item.Kind == MediaItemKind.Collection)
            .SelectMany(entry => entry.Item.MemberExternalIds.Take(PreviewCount * 2))
            .Distinct()
            .ToList();
        var previews = previewIds.Count == 0
            ? new Dictionary<string, CatalogItem>()
            : (await catalog.FindByExternalIdsAsync(previewIds, cancellationToken)).ToDictionary(found => found.Item.ExternalId);

        return entries.Select(entry =>
        {
            var item = entry.Item;
            var preview = item.Kind == MediaItemKind.Collection
                ? item.MemberExternalIds
                    .Select(id => previews.GetValueOrDefault(id))
                    .OfType<CatalogItem>()
                    .Where(member => member.Item.Images.Primary is not null)
                    .Take(PreviewCount)
                    .Select(member => new MediaPreviewView(member.Id, MediaImagesView.From(member.Item.Images)))
                    .ToList()
                : null;
            return new MediaItemSummaryView(
                entry.Id,
                entry.LibraryId,
                item.ExternalId,
                item.Kind,
                item.Name,
                item.ProductionYear,
                SeasonNumber(item),
                EpisodeNumber(item),
                item.Kind == MediaItemKind.Episode ? item.IndexNumberEnd : null,
                item.AddedAt,
                entry.FileCount,
                MediaImagesView.From(item.Images),
                item.Genres,
                item.CommunityRating,
                item.OfficialRating,
                item.Runtime?.TotalSeconds,
                item.IsAnime,
                entry.EpisodeCount,
                item.MemberExternalIds.Count,
                withOverview ? item.Overview : null,
                preview);
        }).ToList();
    }

    private async Task<MediaItemReferenceView?> ReferenceAsync(string externalId, CancellationToken cancellationToken) =>
        await catalog.FindByExternalIdAsync(externalId, cancellationToken) is { } found
            ? new MediaItemReferenceView(found.Id, found.Item.Name)
            : null;

    private static MediaFileView ToView(MediaFile file) => new(
        file.ExternalId,
        file.Name,
        file.Path,
        file.Container,
        file.SizeBytes,
        file.Bitrate,
        file.Runtime?.TotalSeconds,
        file.Streams
            .Select(stream => new MediaStreamView(
                stream.Kind, stream.Index, stream.Codec, stream.Language, stream.Title, stream.IsDefault, stream.IsForced,
                stream.IsExternal, stream.Width, stream.Height, stream.VideoRange, stream.Channels, stream.Bitrate))
            .ToList());

    private static int? SeasonNumber(MediaItem item) => item.Kind switch
    {
        MediaItemKind.Season => item.IndexNumber,
        MediaItemKind.Episode => item.ParentIndexNumber,
        _ => null,
    };

    private static int? EpisodeNumber(MediaItem item) => item.Kind == MediaItemKind.Episode ? item.IndexNumber : null;

    private static string? Trimmed(string? text, string field)
    {
        var trimmed = text?.Trim();
        if (trimmed?.Length > MaxSearchLength)
        {
            throw new DomainValidationException(field, $"Use at most {MaxSearchLength} characters.");
        }

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static IReadOnlyCollection<MediaItemKind> ParseKinds(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        null or "" => Titles,
        "movie" => [MediaItemKind.Movie],
        "series" => [MediaItemKind.Series],
        "season" => [MediaItemKind.Season],
        "episode" => [MediaItemKind.Episode],
        "collection" => [MediaItemKind.Collection],
        _ => throw new DomainValidationException("kind", "Use one of: movie, series, season, episode, collection."),
    };

    private static CatalogSort ParseSort(string? sort) => sort?.Trim().ToLowerInvariant() switch
    {
        null or "" or "name" => CatalogSort.Name,
        "added" => CatalogSort.Added,
        "year" => CatalogSort.Year,
        "release" => CatalogSort.Release,
        _ => throw new DomainValidationException("sort", "Use one of: name, added, year, release."),
    };
}
