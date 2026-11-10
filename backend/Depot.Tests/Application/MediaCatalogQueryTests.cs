using Depot.Application.Jellyfin;
using Depot.Application.Media;
using Depot.Domain.Common;
using Depot.Domain.Media;
using Depot.Tests.TestSupport;
using static Depot.Tests.TestSupport.FakeJellyfinClient;

namespace Depot.Tests.Application;

// What the library pages ask for, after a sync from a fake Jellyfin into SQLite.
public sealed class MediaCatalogQueryTests : IAsyncLifetime
{
    private static readonly string Movies = Id(1);
    private static readonly string Shows = Id(2);
    private static readonly string Anime = Id(3);
    private static readonly string Collections = Id(4);

    private JellyfinHarness harness = null!;

    public async Task InitializeAsync()
    {
        harness = await JellyfinHarness.CreateAsync();
        await harness.ConfigureAsync();
        var jellyfin = harness.Jellyfin;
        jellyfin.AddLibrary(Movies, "Movies", MediaLibraryKind.Movies);
        jellyfin.AddLibrary(Shows, "Shows", MediaLibraryKind.Shows);
        jellyfin.AddLibrary(Anime, "Anime", MediaLibraryKind.Shows);
        jellyfin.AddLibrary(Collections, "Collections", MediaLibraryKind.Collections);

        jellyfin.AddItem(Movies, Movie(10, "Dune") with
        {
            Genres = ["Science Fiction", "Adventure"], ProductionYear = 2021, PremiereDate = new DateOnly(2021, 9, 15),
            People = [new MediaPerson(Id(900), "Denis Villeneuve", PersonKind.Director, null, "face900"), new MediaPerson(Id(901), "Zendaya", PersonKind.Actor, "Chani", null)],
        });
        jellyfin.AddItem(Movies, Movie(11, "Arrival") with { Genres = ["Science Fiction", "Drama"], ProductionYear = 2016, PremiereDate = new DateOnly(2016, 11, 10) });
        jellyfin.AddItem(Movies, Movie(12, "Spirited Away") with { Genres = ["Animation", "Fantasy"], ProductionYear = 2001, ProductionLocations = ["Japan"] });
        jellyfin.AddItem(Movies, Movie(13, "Toy Story") with { Genres = ["Animation", "Family"], ProductionYear = 1995, ProductionLocations = ["United States of America"] });
        jellyfin.AddItem(Shows, new MediaItem { ExternalId = Id(20), Kind = MediaItemKind.Series, Name = "Severance", Genres = ["Drama"], ProductionYear = 2022 });
        jellyfin.AddItem(Shows, new MediaItem { ExternalId = Id(21), Kind = MediaItemKind.Season, Name = "Season 1", IndexNumber = 1, SeriesExternalId = Id(20) });
        foreach (var episode in new[] { 1, 2 })
        {
            jellyfin.AddItem(Shows, new MediaItem
            {
                ExternalId = Id(210 + episode), Kind = MediaItemKind.Episode, Name = $"Episode {episode}", IndexNumber = episode, ParentIndexNumber = 1,
                SeriesExternalId = Id(20), SeasonExternalId = Id(21), Overview = $"What happens in episode {episode}.",
            });
        }

        jellyfin.AddItem(Anime, new MediaItem { ExternalId = Id(30), Kind = MediaItemKind.Series, Name = "Frieren", Genres = ["Fantasy"], ProductionYear = 2023 });
        jellyfin.AddItem(Collections, new MediaItem { ExternalId = Id(40), Kind = MediaItemKind.Collection, Name = "Villeneuve Collection" });
        jellyfin.CollectionMembers[Id(40)] = [Id(11), Id(10)];
        jellyfin.Images[$"{Id(900)}/Primary/0/face900"] = new MediaImageContent("image/jpeg", [0xFF, 0xD8, 0xFF, 1]);

        Assert.Equal(SyncRunStatus.Succeeded, (await harness.SyncAsync()).Status);
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task AnimeIsWorkedOutAtSyncAndCanBeListedApart()
    {
        var anime = await List(new MediaItemQueryInput(null, null, null, null, null, null, Anime: true));
        var notAnime = await List(new MediaItemQueryInput("movie", null, null, null, null, null, Anime: false));

        Assert.Equal(["Frieren", "Spirited Away"], anime.Items.Select(item => item.Name));
        Assert.All(anime.Items, item => Assert.True(item.IsAnime));
        Assert.Equal(["Arrival", "Dune", "Toy Story"], notAnime.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task GenreYearAndReleaseOrderFilterTheList()
    {
        var sciFi = await List(new MediaItemQueryInput(null, null, null, "release", null, null, Genre: "science fiction"));
        var drama = await List(new MediaItemQueryInput(null, null, null, null, null, null, Genre: "Drama"));
        var year = await List(new MediaItemQueryInput(null, null, null, null, null, null, Year: 2016));
        var none = await List(new MediaItemQueryInput(null, null, "Dune", null, null, null, Genre: "Drama"));

        Assert.Equal(["Dune", "Arrival"], sciFi.Items.Select(item => item.Name));
        Assert.Equal(["Arrival", "Severance"], drama.Items.Select(item => item.Name));
        Assert.Equal("Arrival", Assert.Single(year.Items).Name);
        Assert.Equal(0, none.Total);
    }

    [Fact]
    public async Task FiltersOfferTheGenresAndYearsThereAre()
    {
        var movies = await harness.RunAsync<MediaCatalogService, MediaFiltersView>(service => service.GetFiltersAsync("movie", null, CancellationToken.None));
        var anime = await harness.RunAsync<MediaCatalogService, MediaFiltersView>(service => service.GetFiltersAsync(null, true, CancellationToken.None));

        // Ties go alphabetically.
        Assert.Equal([new MediaCountView<string>("Animation", 2), new MediaCountView<string>("Science Fiction", 2)], movies.Genres.Take(2));
        Assert.Contains(new MediaCountView<string>("Family", 1), movies.Genres);
        Assert.DoesNotContain(movies.Genres, genre => genre.Value == "Drama" && genre.Count > 1);
        Assert.Equal([2021, 2016, 2001, 1995], movies.Years.Select(year => year.Value));
        Assert.Equal(["Fantasy", "Animation"], anime.Genres.Select(genre => genre.Value));
    }

    [Fact]
    public async Task StatsCountTheWholeLibrary()
    {
        var stats = await harness.RunAsync<MediaCatalogService, MediaStatsView>(service => service.GetStatsAsync(CancellationToken.None));

        Assert.Equal((4, 2, 1, 2, 1), (stats.Movies, stats.Series, stats.Seasons, stats.Episodes, stats.Collections));
        Assert.Equal((1, 1), (stats.AnimeMovies, stats.AnimeSeries));
        Assert.Equal(4, stats.Files);
        // Movies and series: four genres with two titles each, alphabetically.
        Assert.Equal(["Animation", "Drama", "Fantasy", "Science Fiction"], stats.TopGenres.Take(4).Select(genre => genre.Value));
        Assert.NotNull(stats.LastSyncedAt);
    }

    [Fact]
    public async Task ACollectionListsItsMembersAndTheyPointBack()
    {
        var collections = await List(new MediaItemQueryInput("collection", null, null, null, null, null));
        var collection = Assert.Single(collections.Items);
        var detail = await Detail(collection.Id);
        var members = await List(new MediaItemQueryInput(null, null, null, null, null, null, CollectionId: collection.Id));
        var dune = await Detail(detail.Children.Single(child => child.Name == "Dune").Id);

        Assert.Equal(2, collection.MemberCount);
        // The preview keeps Jellyfin's order of the members; the list is by release.
        Assert.Equal(["Arrival", "Dune"], collection.Preview!.Select(preview => members.Items.Single(member => member.Id == preview.Id).Name));
        Assert.Equal(["Dune", "Arrival"], detail.Children.Select(child => child.Name));
        Assert.Equal(2, members.Total);
        Assert.Equal("Villeneuve Collection", Assert.Single(dune.Collections).Name);
        Assert.Contains($"members {Id(40)}", harness.Jellyfin.Requests);
        await Assert.ThrowsAsync<MediaItemNotFoundException>(() =>
            List(new MediaItemQueryInput(null, null, null, null, null, null, CollectionId: dune.Id)));
    }

    [Fact]
    public async Task ASeriesCountsItsEpisodesAndASeasonListsThemWithTheirSynopses()
    {
        var series = Assert.Single((await List(new MediaItemQueryInput("series", null, "Severance", null, null, null))).Items);
        var detail = await Detail(series.Id);
        var season = await Detail(detail.Children[0].Id);

        Assert.Equal(2, series.EpisodeCount);
        Assert.Null(series.Overview);
        Assert.Equal(2, detail.Children[0].EpisodeCount);
        Assert.Equal(["What happens in episode 1.", "What happens in episode 2."], season.Children.Select(episode => episode.Overview));
    }

    [Fact]
    public async Task TheCastIsListedAndOnlyTheirOwnPhotosCanBeFetched()
    {
        var dune = Assert.Single((await List(new MediaItemQueryInput("movie", null, "Dune", null, null, null))).Items);
        var detail = await Detail(dune.Id);

        var photo = await harness.RunAsync<MediaImageService, MediaImageResult>(service => service.GetPersonImageAsync(dune.Id, 0, 120, CancellationToken.None));

        Assert.Equal([("Denis Villeneuve", PersonKind.Director, "face900"), ("Zendaya", PersonKind.Actor, null)],
            detail.People.Select(person => (person.Name, person.Kind, person.ImageVersion)));
        Assert.Equal("face900", photo.Tag);
        Assert.Contains($"image {Id(900)} Primary 0 face900 160", harness.Jellyfin.Requests);
        await Assert.ThrowsAsync<ImageNotFoundException>(() =>
            harness.RunAsync<MediaImageService, MediaImageResult>(service => service.GetPersonImageAsync(dune.Id, 1, null, CancellationToken.None)));
        await Assert.ThrowsAsync<ImageNotFoundException>(() =>
            harness.RunAsync<MediaImageService, MediaImageResult>(service => service.GetPersonImageAsync(dune.Id, 2, null, CancellationToken.None)));
    }

    [Theory]
    [InlineData("year", 1700)]
    [InlineData("kind", 0)]
    public async Task OddFiltersAreRefusedByField(string field, int year)
    {
        var input = field == "year"
            ? new MediaItemQueryInput(null, null, null, null, null, null, Year: year)
            : new MediaItemQueryInput("boxset", null, null, null, null, null);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() => List(input));

        Assert.Equal(field, exception.Field);
    }

    private Task<MediaItemPageView> List(MediaItemQueryInput input) =>
        harness.RunAsync<MediaCatalogService, MediaItemPageView>(service => service.ListItemsAsync(input, CancellationToken.None));

    private Task<MediaItemDetailView> Detail(int id) =>
        harness.RunAsync<MediaCatalogService, MediaItemDetailView>(service => service.GetItemAsync(id, CancellationToken.None));
}
