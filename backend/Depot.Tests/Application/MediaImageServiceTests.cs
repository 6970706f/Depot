using Depot.Application.Jellyfin;
using Depot.Application.Media;
using Depot.Domain.Common;
using Depot.Domain.Media;
using Depot.Infrastructure;
using Depot.Infrastructure.Jellyfin;
using Depot.Tests.TestSupport;
using static Depot.Tests.TestSupport.FakeJellyfinClient;

namespace Depot.Tests.Application;

// Images through the real service and disk cache, from a fake Jellyfin.
public sealed class MediaImageServiceTests : IAsyncLifetime
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    private JellyfinHarness harness = null!;
    private int duneId;

    public async Task InitializeAsync()
    {
        harness = await JellyfinHarness.CreateAsync(imageCacheMegabytes: 1);
        await harness.ConfigureAsync();
        harness.Jellyfin.AddLibrary(Id(1), "Movies", MediaLibraryKind.Movies);
        harness.Jellyfin.AddItem(Id(1), Movie(3, "Dune"));
        harness.Jellyfin.Images[$"{Id(3)}/Primary/0/poster3"] = new MediaImageContent("image/jpeg", Jpeg);
        await harness.SyncAsync();
        duneId = (await harness.RunAsync<IMediaCatalogRepository, CatalogItem?>(catalog => catalog.FindByExternalIdAsync(Id(3), CancellationToken.None)))!.Id;
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task AnImageIsFetchedOnceThenServedFromTheCache()
    {
        var first = await Get(duneId, "primary", width: 300);
        var second = await Get(duneId, "PRIMARY", width: 310);

        Assert.Equal(("image/jpeg", "poster3"), (first.Image.ContentType, first.Tag));
        Assert.Equal(Jpeg, second.Image.Content);
        Assert.Equal([$"image {Id(3)} Primary 0 poster3 320"], harness.Jellyfin.Requests.Where(request => request.StartsWith("image", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CachedImagesStillAnswerWhileJellyfinIsDown()
    {
        await Get(duneId, "primary", width: null);
        harness.Jellyfin.Images.Clear();

        var cached = await Get(duneId, "primary", width: null);

        Assert.Equal(Jpeg, cached.Image.Content);
        Assert.Equal(1, harness.Jellyfin.ImageRequests);
    }

    [Fact]
    public async Task ANewVersionOfTheImageIsFetchedAgain()
    {
        await Get(duneId, "primary", width: 320);
        harness.Jellyfin.Replace(Movie(3, "Dune") with { Images = new MediaImageTags("poster3-new", null, null, []) });
        harness.Jellyfin.Images[$"{Id(3)}/Primary/0/poster3-new"] = new MediaImageContent("image/jpeg", [.. Jpeg, 9]);
        await harness.SyncAsync();

        var updated = await Get(duneId, "primary", width: 320);

        Assert.Equal("poster3-new", updated.Tag);
        Assert.Equal(9, updated.Image.Content[^1]);
        Assert.Equal(2, harness.Jellyfin.ImageRequests);
    }

    [Fact]
    public async Task MissingImagesAreNotFoundWithoutAskingJellyfinWhenNothingIsKnown()
    {
        var noThumb = await Assert.ThrowsAsync<ImageNotFoundException>(() => Get(duneId, "thumb", null));
        var noSecondBackdrop = await Assert.ThrowsAsync<ImageNotFoundException>(() => Get(duneId, "backdrop", null, index: 1));
        var goneFromJellyfin = await Assert.ThrowsAsync<ImageNotFoundException>(() => Get(duneId, "backdrop", null));
        await Assert.ThrowsAsync<MediaItemNotFoundException>(() => Get(9999, "primary", null));

        Assert.NotNull(noThumb);
        Assert.NotNull(noSecondBackdrop);
        Assert.NotNull(goneFromJellyfin);
        Assert.Equal(1, harness.Jellyfin.ImageRequests);
    }

    [Theory]
    [InlineData("svg", null, null)]
    [InlineData("0", null, null)]
    [InlineData("primary", -1, null)]
    [InlineData("backdrop", 100, null)]
    [InlineData("primary", null, 0)]
    [InlineData("primary", null, 20000)]
    public async Task OddRequestsAreRefusedByField(string kind, int? index, int? width)
    {
        var exception = await Assert.ThrowsAsync<DomainValidationException>(() => Get(duneId, kind, width, index));

        Assert.Contains(exception.Field, new[] { "kind", "index", "width" });
    }

    [Theory]
    [InlineData(1, 160)]
    [InlineData(320, 320)]
    [InlineData(321, 480)]
    [InlineData(4000, 1920)]
    public void WidthsAreRoundedUpToAFewSizes(int requested, int fetched)
    {
        Assert.Equal(fetched, MediaImageService.RoundWidth(requested));
    }

    [Fact]
    public async Task TheCacheIsPrivateAndStaysUnderItsLimit()
    {
        var cache = (DiskImageCache)harness.Get<IImageCache>();
        var folder = harness.Get<StoragePaths>().ImageCacheDirectory;
        for (var image = 0; image < 8; image++)
        {
            var content = new byte[300 * 1024];
            Jpeg.CopyTo(content, 0);
            await cache.SetAsync($"key-{image}", new MediaImageContent("image/jpeg", content), CancellationToken.None);
            // One minute apart, the first one used longest ago.
            File.SetLastWriteTimeUtc(Path.Join(folder, Hash($"key-{image}") + ".jpg"), new DateTime(2000, 1, 1, 0, image, 0, DateTimeKind.Utc));
        }

        await cache.PruneAsync();

        var left = Directory.GetFiles(folder);
        Assert.True(left.Sum(path => new FileInfo(path).Length) <= 1024 * 1024 * 8 / 10);
        // The newest stay.
        Assert.NotNull(await cache.GetAsync("key-7", CancellationToken.None));
        Assert.Null(await cache.GetAsync("key-0", CancellationToken.None));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(left[0]));
            Assert.Equal(StoragePaths.PrivateFolderMode, File.GetUnixFileMode(folder));
            Assert.Equal(StoragePaths.PrivateFolderMode, File.GetUnixFileMode(Path.GetDirectoryName(folder)!));
        }
    }

    private static string Hash(string key) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));

    private Task<MediaImageResult> Get(int id, string kind, int? width, int? index = null) =>
        harness.RunAsync<MediaImageService, MediaImageResult>(service => service.GetAsync(id, kind, index, width, CancellationToken.None));
}
