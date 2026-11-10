using Depot.Application.Media;
using Depot.Domain.Media;
using Depot.Infrastructure.Persistence;
using Depot.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using static Depot.Tests.TestSupport.FakeJellyfinClient;

namespace Depot.Tests.Infrastructure;

// The local copy of the library in a real SQLite database.
public sealed class MediaCatalogRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private JellyfinHarness harness = null!;
    private int libraryId;

    public async Task InitializeAsync()
    {
        harness = await JellyfinHarness.CreateAsync();
        var saved = await Catalog(repository => repository.SaveLibrariesAsync(
            [new MediaLibrary(Id(1), "Movies", MediaLibraryKind.Movies, ["/media/movies"])], CancellationToken.None));
        libraryId = saved.IdsByExternalId[Id(1)];
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task SavingTheSameItemsAgainChangesNothing()
    {
        // Jellyfin's dates have seven decimal digits, which must read back exactly.
        var added = DateTimeOffset.Parse("2026-11-10T15:07:41.2136713Z");
        var items = new[] { Movie(2, "Dune", MovieFile(2, "1080p"), MovieFile(2, "720p")) with { AddedAt = added, CommunityRating = 7.786 }, Movie(3, "Arrival") };

        var first = await Save(items);
        var second = await Save(items);

        Assert.Equal(new SavedItems(2, 0), first);
        Assert.Equal(new SavedItems(0, 0), second);
    }

    [Fact]
    public async Task AChangedFieldOrFileIsAnUpdate()
    {
        var dune = Movie(2, "Dune", MovieFile(2, "1080p"));
        await Save([dune, Movie(3, "Arrival")]);

        var renamed = await Save([dune with { Overview = "Spice." }]);
        var newVersion = await Save([dune with { Overview = "Spice.", Files = [MovieFile(2, "1080p"), MovieFile(2, "2160p")] }]);
        var newPoster = await Save([dune with { Overview = "Spice.", Files = [MovieFile(2, "2160p")], Images = dune.Images with { Primary = "new" } }]);

        Assert.Equal(new SavedItems(0, 1), renamed);
        Assert.Equal(new SavedItems(0, 1), newVersion);
        Assert.Equal(new SavedItems(0, 1), newPoster);
        var stored = await Find(dune.ExternalId, includeFiles: true);
        Assert.Equal("new", stored.Item.Images.Primary);
        Assert.Equal(["2160p"], stored.Item.Files.Select(file => file.Name));
    }

    [Fact]
    public async Task AnItemKeepsEveryFileWithItsStreams()
    {
        await Save([Movie(2, "Dune", MovieFile(2, "1080p"), MovieFile(2, "720p"))]);

        var dune = await Find(Id(2), includeFiles: true);

        Assert.Equal(2, dune.FileCount);
        Assert.Equal(["1080p", "720p"], dune.Item.Files.Select(file => file.Name));
        var video = dune.Item.Files[0].Streams[0];
        Assert.Equal((MediaStreamKind.Video, "h264", 1920, 1080, "SDR"), (video.Kind, video.Codec, video.Width, video.Height, video.VideoRange));
        Assert.Equal(6, dune.Item.Files[0].Streams[1].Channels);
        Assert.Equal("tt2", dune.Item.ProviderIds["Imdb"]);
    }

    [Fact]
    public async Task RemovingAnItemRemovesItsFiles()
    {
        await Save([Movie(2, "Dune", MovieFile(2, "1080p"), MovieFile(2, "720p")), Movie(3, "Arrival")]);

        var removed = await Catalog(repository => repository.RemoveItemsAsync([Id(2)], CancellationToken.None));

        Assert.Equal(1, removed);
        Assert.Equal([Id(3)], await Catalog(repository => repository.ListExternalIdsAsync(libraryId, CancellationToken.None)));
        Assert.Equal(1, await harness.RunAsync<DepotDbContext, int>(db => db.MediaFiles.CountAsync()));
    }

    [Fact]
    public async Task ALibraryGoneFromTheServerTakesItsItems()
    {
        await Save([Movie(2, "Dune"), Movie(3, "Arrival")]);

        var saved = await Catalog(repository => repository.SaveLibrariesAsync(
            [new MediaLibrary(Id(9), "Shows", MediaLibraryKind.Shows, [])], CancellationToken.None));

        Assert.Equal(2, saved.RemovedItems);
        Assert.Equal([Id(9)], saved.IdsByExternalId.Keys);
        Assert.Equal(0, await harness.RunAsync<DepotDbContext, int>(db => db.MediaItems.CountAsync()));
        Assert.Equal(0, await harness.RunAsync<DepotDbContext, int>(db => db.MediaFiles.CountAsync()));
        var library = Assert.Single(await Catalog(repository => repository.ListLibrariesAsync(CancellationToken.None)));
        Assert.Equal("Shows", library.Library.Name);
    }

    [Fact]
    public async Task AnItemSeenInAnotherLibraryMovesThereInsteadOfBeingCopied()
    {
        await Save([Movie(2, "Dune")]);
        var saved = await Catalog(repository => repository.SaveLibrariesAsync(
            [new MediaLibrary(Id(1), "Movies", MediaLibraryKind.Movies, []), new MediaLibrary(Id(8), "4K", MediaLibraryKind.Movies, [])],
            CancellationToken.None));

        var moved = await Catalog(repository => repository.SaveItemsAsync(saved.IdsByExternalId[Id(8)], [Movie(2, "Dune")], Now, CancellationToken.None));

        Assert.Equal(new SavedItems(0, 1), moved);
        Assert.Equal(1, await harness.RunAsync<DepotDbContext, int>(db => db.MediaItems.CountAsync()));
        Assert.Equal(saved.IdsByExternalId[Id(8)], (await Find(Id(2), includeFiles: false)).LibraryId);
    }

    [Fact]
    public async Task ListsFilterSearchSortAndPage()
    {
        await Save(
        [
            Movie(2, "Dune") with { SortName = "dune", ProductionYear = 2021 },
            Movie(3, "The Arrival") with { SortName = "arrival", ProductionYear = 2016, OriginalTitle = "Story of Your Life" },
            Movie(4, "100% Wolf") with { SortName = "100% wolf", ProductionYear = 2020 },
            new MediaItem { ExternalId = Id(5), Kind = MediaItemKind.Series, Name = "Severance" },
        ]);

        var byName = await List(new CatalogQuery([MediaItemKind.Movie]));
        var byYear = await List(new CatalogQuery([MediaItemKind.Movie], Sort: CatalogSort.Year));
        var page = await List(new CatalogQuery([MediaItemKind.Movie], Skip: 1, Take: 1));
        var searched = await List(new CatalogQuery([MediaItemKind.Movie, MediaItemKind.Series], Search: "story"));
        var literal = await List(new CatalogQuery([MediaItemKind.Movie], Search: "0%"));
        var series = await List(new CatalogQuery([MediaItemKind.Series], LibraryId: libraryId));

        Assert.Equal(["100% Wolf", "The Arrival", "Dune"], byName.Items.Select(item => item.Item.Name));
        Assert.Equal(["Dune", "100% Wolf", "The Arrival"], byYear.Items.Select(item => item.Item.Name));
        Assert.Equal((3, "The Arrival"), (page.Total, Assert.Single(page.Items).Item.Name));
        Assert.Equal("The Arrival", Assert.Single(searched.Items).Item.Name);
        Assert.Equal("100% Wolf", Assert.Single(literal.Items).Item.Name);
        Assert.Equal("Severance", Assert.Single(series.Items).Item.Name);
    }

    [Fact]
    public async Task LibrariesCountTheirItemsByKind()
    {
        await Save([Movie(2, "Dune"), Movie(3, "Arrival")]);
        await harness.RunAsync<IMediaCatalogRepository>(repository => repository.MarkLibrarySyncedAsync(libraryId, Now, CancellationToken.None));

        var library = Assert.Single(await Catalog(repository => repository.ListLibrariesAsync(CancellationToken.None)));

        Assert.Equal(2, library.Counts[MediaItemKind.Movie]);
        Assert.Equal(Now, library.LastSyncedAt);
        Assert.Equal(["/media/movies"], library.Library.Locations);
    }

    private Task<SavedItems> Save(IReadOnlyList<MediaItem> items) =>
        Catalog(repository => repository.SaveItemsAsync(libraryId, items, Now, CancellationToken.None));

    private Task<CatalogPage> List(CatalogQuery query) => Catalog(repository => repository.ListItemsAsync(query, CancellationToken.None));

    private async Task<CatalogItem> Find(string externalId, bool includeFiles)
    {
        var found = await Catalog(repository => repository.FindByExternalIdAsync(externalId, CancellationToken.None));
        Assert.NotNull(found);
        return (await Catalog(repository => repository.GetItemAsync(found.Id, includeFiles, CancellationToken.None)))!;
    }

    private Task<T> Catalog<T>(Func<IMediaCatalogRepository, Task<T>> action) => harness.RunAsync(action);
}
