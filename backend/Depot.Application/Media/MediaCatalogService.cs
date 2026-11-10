using Depot.Domain.Common;
using Depot.Domain.Media;

namespace Depot.Application.Media;

// Reads Depot's copy of the media library. Nothing here calls Jellyfin: the copy answers even
// while the server is down, as of the last sync.
public sealed class MediaCatalogService(IMediaCatalogRepository catalog)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    // Seasons of a series, or episodes of a season, listed with an item. More is unusual.
    public const int MaxChildren = 1000;

    public const int MaxSearchLength = 200;

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

        var search = input.Search?.Trim();
        if (search?.Length > MaxSearchLength)
        {
            throw new DomainValidationException("search", $"Search for at most {MaxSearchLength} characters.");
        }

        var query = new CatalogQuery(
            ParseKinds(input.Kind),
            input.LibraryId,
            Search: string.IsNullOrEmpty(search) ? null : search,
            Sort: ParseSort(input.Sort),
            Skip: (int)Math.Min(int.MaxValue, (long)(page - 1) * pageSize),
            Take: pageSize);
        var result = await catalog.ListItemsAsync(query, cancellationToken);
        return new MediaItemPageView(result.Items.Select(ToSummary).ToList(), result.Total, page, pageSize);
    }

    public async Task<MediaItemDetailView> GetItemAsync(int id, CancellationToken cancellationToken)
    {
        var entry = await catalog.GetItemAsync(id, includeFiles: true, cancellationToken) ?? throw new MediaItemNotFoundException(id);
        var item = entry.Item;

        var series = item.SeriesExternalId is { } seriesId ? await ReferenceAsync(seriesId, cancellationToken) : null;
        var season = item.SeasonExternalId is { } seasonId ? await ReferenceAsync(seasonId, cancellationToken) : null;
        var children = item.Kind switch
        {
            MediaItemKind.Series => await ChildrenAsync(new CatalogQuery([MediaItemKind.Season], SeriesExternalId: item.ExternalId), cancellationToken),
            MediaItemKind.Season => await ChildrenAsync(new CatalogQuery([MediaItemKind.Episode], SeasonExternalId: item.ExternalId), cancellationToken),
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
            item.ProviderIds,
            item.SeriesStatus,
            SeasonNumber(item),
            EpisodeNumber(item),
            item.Kind == MediaItemKind.Episode ? item.IndexNumberEnd : null,
            item.Path,
            item.AddedAt,
            entry.UpdatedAt,
            MediaImagesView.From(item.Images),
            item.Files.Select(ToView).ToList(),
            series,
            season,
            children);
    }

    private async Task<IReadOnlyList<MediaItemSummaryView>> ChildrenAsync(CatalogQuery query, CancellationToken cancellationToken)
    {
        var page = await catalog.ListItemsAsync(query with { Sort = CatalogSort.Number, Take = MaxChildren }, cancellationToken);
        return page.Items.Select(ToSummary).ToList();
    }

    private async Task<MediaItemReferenceView?> ReferenceAsync(string externalId, CancellationToken cancellationToken) =>
        await catalog.FindByExternalIdAsync(externalId, cancellationToken) is { } found
            ? new MediaItemReferenceView(found.Id, found.Item.Name)
            : null;

    private static MediaItemSummaryView ToSummary(CatalogItem entry) => new(
        entry.Id,
        entry.LibraryId,
        entry.Item.ExternalId,
        entry.Item.Kind,
        entry.Item.Name,
        entry.Item.ProductionYear,
        SeasonNumber(entry.Item),
        EpisodeNumber(entry.Item),
        entry.Item.Kind == MediaItemKind.Episode ? entry.Item.IndexNumberEnd : null,
        entry.Item.AddedAt,
        entry.FileCount,
        MediaImagesView.From(entry.Item.Images));

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

    private static IReadOnlyCollection<MediaItemKind> ParseKinds(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        null or "" => [MediaItemKind.Movie, MediaItemKind.Series],
        "movie" => [MediaItemKind.Movie],
        "series" => [MediaItemKind.Series],
        "season" => [MediaItemKind.Season],
        "episode" => [MediaItemKind.Episode],
        _ => throw new DomainValidationException("kind", "Use one of: movie, series, season, episode."),
    };

    private static CatalogSort ParseSort(string? sort) => sort?.Trim().ToLowerInvariant() switch
    {
        null or "" or "name" => CatalogSort.Name,
        "added" => CatalogSort.Added,
        "year" => CatalogSort.Year,
        _ => throw new DomainValidationException("sort", "Use one of: name, added, year."),
    };
}
