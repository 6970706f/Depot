using Depot.Application.Jellyfin;
using Depot.Domain.Common;

namespace Depot.Tests.Application;

public sealed class MediaLibraryServiceTests
{
    private readonly RecordingJellyfin jellyfin = new();

    [Theory]
    [InlineData(0, 48, "page")]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, 101, "pageSize")]
    public async Task Paging_is_checked(int page, int pageSize, string field)
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Service().ListAsync(MediaKind.Movie, null, MediaSort.Name, page, pageSize, CancellationToken.None));

        Assert.Equal(field, error.Field);
    }

    [Fact]
    public async Task A_blank_search_is_no_search()
    {
        await Service().ListAsync(MediaKind.Series, "  ", MediaSort.Name, 1, 10, CancellationToken.None);

        Assert.Null(jellyfin.LastQuery!.Search);
    }

    [Theory]
    [InlineData("../System/Info")]
    [InlineData("not-a-guid")]
    public async Task Ids_that_are_not_jellyfin_ids_never_reach_jellyfin(string id)
    {
        var error = await Assert.ThrowsAsync<JellyfinException>(() => Service().GetAsync(id, CancellationToken.None));

        Assert.Equal(JellyfinFailure.ItemNotFound, error.Failure);
        Assert.Null(jellyfin.LastId);
    }

    [Fact]
    public async Task Ids_with_dashes_are_normalized()
    {
        await Assert.ThrowsAsync<JellyfinException>(() => Service().GetAsync("4F2B13D6-1E08-331C-E834-FF2DE2B1DDF7", CancellationToken.None));

        Assert.Equal("4f2b13d61e08331ce834ff2de2b1ddf7", jellyfin.LastId);
    }

    [Theory]
    [InlineData(null, 480)]
    [InlineData(100, 160)]
    [InlineData(500, 720)]
    [InlineData(5000, 1920)]
    public async Task Image_widths_are_rounded_to_a_few_sizes(int? asked, int expected)
    {
        await Assert.ThrowsAsync<JellyfinException>(() =>
            Service().GetImageAsync("4f2b13d61e08331ce834ff2de2b1ddf7", MediaImageType.Primary, asked, null, CancellationToken.None));

        Assert.Equal(expected, jellyfin.LastWidth);
    }

    [Fact]
    public async Task Image_versions_must_look_like_tags()
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Service().GetImageAsync("4f2b13d61e08331ce834ff2de2b1ddf7", MediaImageType.Primary, 320, "x&y=1", CancellationToken.None));

        Assert.Equal("tag", error.Field);
    }

    private MediaLibraryService Service() => new(jellyfin);

    private sealed class RecordingJellyfin : IJellyfinClient
    {
        public MediaQuery? LastQuery { get; private set; }

        public string? LastId { get; private set; }

        public int? LastWidth { get; private set; }

        public Task<MediaPage> GetItemsAsync(MediaQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(new MediaPage([], 0, query.Page, query.PageSize));
        }

        public Task<MediaDetail?> GetItemAsync(string id, CancellationToken cancellationToken)
        {
            LastId = id;
            return Task.FromResult<MediaDetail?>(null);
        }

        public Task<MediaImage?> GetImageAsync(string id, MediaImageType type, int width, string? tag, CancellationToken cancellationToken)
        {
            LastWidth = width;
            return Task.FromResult<MediaImage?>(null);
        }

        public Task<MediaCounts> GetCountsAsync(CancellationToken cancellationToken) => Task.FromResult(new MediaCounts(0, 0, 0));

        public Task<JellyfinServerInfo> GetServerInfoAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RefreshLibraryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
