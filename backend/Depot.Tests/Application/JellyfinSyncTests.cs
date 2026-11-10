using Depot.Application.Jellyfin;
using Depot.Application.Media;
using Depot.Domain.Media;
using Depot.Tests.TestSupport;
using static Depot.Tests.TestSupport.FakeJellyfinClient;

namespace Depot.Tests.Application;

// Whole syncs against a fake Jellyfin, with the real repositories on SQLite.
public sealed class JellyfinSyncTests : IAsyncLifetime
{
    private static readonly string Movies = Id(1);
    private static readonly string Shows = Id(2);

    private JellyfinHarness harness = null!;

    private FakeJellyfinClient Jellyfin => harness.Jellyfin;

    public async Task InitializeAsync()
    {
        harness = await JellyfinHarness.CreateAsync();
        await harness.ConfigureAsync();
        Jellyfin.AddLibrary(Movies, "Movies", MediaLibraryKind.Movies);
        Jellyfin.AddLibrary(Shows, "Shows", MediaLibraryKind.Shows);
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task TheFirstSyncAddsEveryItemAndTheNextOnlyWhatChanged()
    {
        AddShow(series: 20, seasons: 2, episodesPerSeason: 3);
        Jellyfin.AddItem(Movies, Movie(3, "Dune", MovieFile(3, "1080p"), MovieFile(3, "720p")));
        Jellyfin.AddItem(Movies, Movie(4, "Arrival"));

        var first = await harness.SyncAsync();
        Jellyfin.Replace(Movie(4, "Arrival") with { Overview = "Linguistics." });
        Jellyfin.RemoveItem(Id(3));
        Jellyfin.AddItem(Movies, Movie(5, "Sicario"));
        var second = await harness.SyncAsync();
        var third = await harness.SyncAsync();

        Assert.Equal(SyncRunStatus.Succeeded, first.Status);
        Assert.Equal((11, 11, 0, 0), (first.ItemsSeen, first.ItemsAdded, first.ItemsUpdated, first.ItemsRemoved));
        Assert.Equal((11, 1, 1, 1, 9), (second.ItemsSeen, second.ItemsAdded, second.ItemsUpdated, second.ItemsRemoved, second.ItemsUnchanged));
        Assert.Equal((0, 0, 0, 11), (third.ItemsAdded, third.ItemsUpdated, third.ItemsRemoved, third.ItemsUnchanged));

        var movies = await List("movie");
        Assert.Equal(["Arrival", "Sicario"], movies.Items.Select(item => item.Name));
        var libraries = await harness.RunAsync<MediaCatalogService, IReadOnlyList<MediaLibraryView>>(service => service.ListLibrariesAsync(CancellationToken.None));
        var shows = libraries.Single(library => library.Name == "Shows");
        Assert.Equal((1, 2, 6), (shows.Series, shows.Seasons, shows.Episodes));
        Assert.NotNull(shows.LastSyncedAt);
    }

    [Fact]
    public async Task LargeLibrariesAreReadPageByPage()
    {
        var count = JellyfinLibrarySync.PageSize * 2 + 7;
        for (var number = 0; number < count; number++)
        {
            Jellyfin.AddItem(Movies, Movie(1000 + number, $"Movie {number}"));
        }

        var run = await harness.SyncAsync();

        Assert.Equal((count, count), (run.ItemsSeen, run.ItemsAdded));
        Assert.Equal(["items 00000000000000000000000000000001 0", "items 00000000000000000000000000000001 100", "items 00000000000000000000000000000001 200"],
            Jellyfin.Requests.Where(request => request.StartsWith($"items {Movies}", StringComparison.Ordinal)));
        Assert.Equal(count, (await List("movie")).Total);
    }

    [Fact]
    public async Task AnItemMissedBecauseThePagesShiftedIsNotRemoved()
    {
        for (var number = 0; number < JellyfinLibrarySync.PageSize + 10; number++)
        {
            Jellyfin.AddItem(Movies, Movie(1000 + number, $"Movie {number:000}"));
        }

        await harness.SyncAsync();
        // While the second page is read, an item of the first page goes away: the first item of
        // the second page moves into the first and is never sent.
        Jellyfin.BeforePage = (library, start) =>
        {
            if (library == Movies && start == JellyfinLibrarySync.PageSize)
            {
                Jellyfin.RemoveItem(Id(1000));
            }
        };

        var shifted = await harness.SyncAsync();
        var shiftedNames = (await List("movie", pageSize: 200)).Items.Select(item => item.Name).ToList();
        Jellyfin.BeforePage = null;
        var next = await harness.SyncAsync();
        var nextNames = (await List("movie", pageSize: 200)).Items.Select(item => item.Name).ToList();

        // The item that was never sent is confirmed by id and kept; the one removed was seen on
        // the first page, so it goes on the next sync.
        Assert.Equal(SyncRunStatus.Succeeded, shifted.Status);
        Assert.Equal(0, shifted.ItemsRemoved);
        Assert.Contains($"Movie {JellyfinLibrarySync.PageSize:000}", shiftedNames);
        Assert.Equal(JellyfinLibrarySync.PageSize + 10, shiftedNames.Count);
        Assert.Contains(Jellyfin.Requests, request => request == "ids 1");
        Assert.Equal(1, next.ItemsRemoved);
        Assert.DoesNotContain("Movie 000", nextNames);
        Assert.Equal(JellyfinLibrarySync.PageSize + 9, nextNames.Count);
    }

    [Fact]
    public async Task ALibraryThatFailsHalfwayKeepsItsItemsAndTheOthersAreStillRead()
    {
        for (var number = 0; number < JellyfinLibrarySync.PageSize + 10; number++)
        {
            Jellyfin.AddItem(Movies, Movie(1000 + number, $"Movie {number}"));
        }

        AddShow(series: 20, seasons: 1, episodesPerSeason: 2);
        await harness.SyncAsync();
        Jellyfin.RemoveItem(Id(1000 + JellyfinLibrarySync.PageSize + 5));
        Jellyfin.BeforePage = (library, start) =>
        {
            if (library == Movies && start > 0)
            {
                throw new JellyfinException(JellyfinFailure.UnexpectedResponse, "Page 2 is broken.");
            }
        };
        Jellyfin.AddItem(Shows, Episode(30, series: 20, season: 21, episodeNumber: 3));

        var run = await harness.SyncAsync();

        Assert.Equal(SyncRunStatus.CompletedWithErrors, run.Status);
        Assert.Equal((1, 1), (run.LibrariesCompleted, run.LibrariesFailed));
        Assert.Equal(new LibrarySyncError("Movies", "Page 2 is broken."), Assert.Single(run.Errors));
        // Not read to the end, so nothing of Movies is removed, not even what's really gone.
        Assert.Equal(0, run.ItemsRemoved);
        Assert.Equal(JellyfinLibrarySync.PageSize + 10, (await List("movie")).Total);
        Assert.Equal(3, (await List("episode")).Total);
    }

    [Fact]
    public async Task WhenJellyfinIsDownTheOtherLibrariesArentTried()
    {
        Jellyfin.AddItem(Movies, Movie(3, "Dune"));
        AddShow(series: 20, seasons: 1, episodesPerSeason: 1);
        await harness.SyncAsync();
        Jellyfin.Requests.Clear();
        Jellyfin.BeforePage = (_, _) => throw new JellyfinException(JellyfinFailure.Unreachable, "Can't reach Jellyfin.");

        var run = await harness.SyncAsync();

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Equal(2, run.LibrariesFailed);
        Assert.Equal(["libraries", $"items {Movies} 0"], Jellyfin.Requests);
        Assert.Equal("Shows", run.Errors[1].Library);
        Assert.Contains("Not read", run.Errors[1].Message);
        Assert.Equal(1, (await List("movie")).Total);
    }

    [Fact]
    public async Task WhenTheLibrariesCantBeListedNothingChanges()
    {
        Jellyfin.AddItem(Movies, Movie(3, "Dune"));
        await harness.SyncAsync();
        Jellyfin.LibrariesFailure = new JellyfinException(JellyfinFailure.InvalidApiKey, "Jellyfin refused the API key.");

        var run = await harness.SyncAsync();

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Equal("Jellyfin refused the API key.", run.Message);
        Assert.Equal(1, (await List("movie")).Total);
    }

    [Fact]
    public async Task ItemsAreOnlyRemovedOnceJellyfinConfirmsTheyreGone()
    {
        Jellyfin.AddItem(Movies, Movie(3, "Dune"));
        await harness.SyncAsync();
        Jellyfin.RemoveItem(Id(3));
        Jellyfin.LookupFailure = new JellyfinException(JellyfinFailure.Timeout, "Jellyfin didn't answer.");

        var failed = await harness.SyncAsync();
        Jellyfin.LookupFailure = null;
        var confirmed = await harness.SyncAsync();

        Assert.Equal(0, failed.ItemsRemoved);
        Assert.Equal(1, confirmed.ItemsRemoved);
        Assert.Equal(0, (await List("movie")).Total);
    }

    [Fact]
    public async Task ARemovedLibraryTakesItsItems()
    {
        Jellyfin.AddItem(Movies, Movie(3, "Dune"));
        AddShow(series: 20, seasons: 1, episodesPerSeason: 2);
        await harness.SyncAsync();
        Jellyfin.Libraries.RemoveAll(library => library.ExternalId == Shows);

        var run = await harness.SyncAsync();

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(4, run.ItemsRemoved);
        Assert.Equal(0, (await List("series")).Total);
        Assert.Equal(1, (await List(null)).Total);
    }

    [Fact]
    public async Task OnlyOneSyncRunsAtATime()
    {
        var first = await harness.RunAsync<JellyfinSyncService, SyncRunView>(service => service.StartAsync(SyncTrigger.Manual, CancellationToken.None));

        var second = await Assert.ThrowsAsync<SyncAlreadyRunningException>(() =>
            harness.RunAsync<JellyfinSyncService, SyncRunView>(service => service.StartAsync(SyncTrigger.Manual, CancellationToken.None)));
        var status = await harness.RunAsync<JellyfinSyncService, SyncStatusView>(service => service.GetStatusAsync(CancellationToken.None));

        Assert.Equal(first.Id, second.RunId);
        Assert.True(status.IsRunning);
        Assert.Equal(first.Id, status.LatestRun!.Id);
    }

    [Fact]
    public async Task ASyncNeedsJellyfinSetUp()
    {
        await using var fresh = await JellyfinHarness.CreateAsync();

        var exception = await Assert.ThrowsAsync<JellyfinException>(() =>
            fresh.RunAsync<JellyfinSyncService, SyncRunView>(service => service.StartAsync(SyncTrigger.Manual, CancellationToken.None)));

        Assert.Equal(JellyfinFailure.NotConfigured, exception.Failure);
        Assert.False(fresh.Get<JellyfinSyncQueue>().IsBusy);
    }

    [Fact]
    public async Task ARunLeftUnfinishedByARestartIsMarkedInterrupted()
    {
        var run = await harness.RunAsync<JellyfinSyncService, SyncRunView>(service => service.StartAsync(SyncTrigger.Manual, CancellationToken.None));
        harness.Get<JellyfinSyncQueue>().Release();

        await harness.RunAsync<JellyfinSyncService>(service => service.InterruptUnfinishedAsync(CancellationToken.None));
        var after = await harness.RunAsync<JellyfinSyncService, SyncRunView>(service => service.GetAsync(run.Id, CancellationToken.None));

        Assert.Equal(SyncRunStatus.Interrupted, after.Status);
        Assert.NotNull(after.FinishedAt);
    }

    [Fact]
    public async Task ACancelledSyncIsInterruptedAndKeepsWhatItSaved()
    {
        Jellyfin.AddItem(Movies, Movie(3, "Dune"));
        using var stopping = new CancellationTokenSource();
        Jellyfin.BeforePage = (library, _) =>
        {
            if (library == Shows)
            {
                stopping.Cancel();
            }
        };
        var run = await harness.RunAsync<JellyfinSyncService, SyncRunView>(service => service.StartAsync(SyncTrigger.Manual, CancellationToken.None));

        await harness.RunAsync<JellyfinLibrarySync>(sync => sync.RunAsync(run.Id, stopping.Token));
        var after = await harness.RunAsync<JellyfinSyncService, SyncRunView>(service => service.GetAsync(run.Id, CancellationToken.None));

        Assert.Equal(SyncRunStatus.Interrupted, after.Status);
        Assert.Equal(1, (await List("movie")).Total);
    }

    [Fact]
    public async Task DetailsLinkASeriesItsSeasonsAndEpisodes()
    {
        AddShow(series: 20, seasons: 2, episodesPerSeason: 2);
        await harness.SyncAsync();
        var series = Assert.Single((await List("series")).Items);

        var detail = await Detail(series.Id);
        var season = await Detail(detail.Children[1].Id);
        var episode = await Detail(season.Children[0].Id);

        Assert.Equal(["Season 1", "Season 2"], detail.Children.Select(child => child.Name));
        Assert.Equal((series.Id, "Show 20"), (season.Series!.Id, season.Series.Name));
        Assert.Equal([1, 2], season.Children.Select(child => child.EpisodeNumber!.Value));
        Assert.Equal((2, 1), (episode.SeasonNumber!.Value, episode.EpisodeNumber!.Value));
        Assert.Equal(season.Id, episode.Season!.Id);
        Assert.Single(episode.Files);
    }

    private void AddShow(int series, int seasons, int episodesPerSeason)
    {
        Jellyfin.AddItem(Shows, new MediaItem { ExternalId = Id(series), Kind = MediaItemKind.Series, Name = $"Show {series}", SeriesStatus = "Ended" });
        for (var season = 1; season <= seasons; season++)
        {
            var seasonNumber = series + season;
            Jellyfin.AddItem(Shows, new MediaItem
            {
                ExternalId = Id(seasonNumber), Kind = MediaItemKind.Season, Name = $"Season {season}", IndexNumber = season, SeriesExternalId = Id(series),
            });
            for (var episode = 1; episode <= episodesPerSeason; episode++)
            {
                Jellyfin.AddItem(Shows, Episode(100 * seasonNumber + episode, series, seasonNumber, episode) with { ParentIndexNumber = season });
            }
        }
    }

    private static MediaItem Episode(int number, int series, int season, int episodeNumber) => new()
    {
        ExternalId = Id(number),
        Kind = MediaItemKind.Episode,
        Name = $"Episode {episodeNumber}",
        IndexNumber = episodeNumber,
        ParentIndexNumber = 1,
        SeriesExternalId = Id(series),
        SeasonExternalId = Id(season),
        Files = [MovieFile(number, "1080p")],
    };

    private Task<MediaItemPageView> List(string? kind, int pageSize = 50) =>
        harness.RunAsync<MediaCatalogService, MediaItemPageView>(service =>
            service.ListItemsAsync(new MediaItemQueryInput(kind, null, null, null, null, pageSize), CancellationToken.None));

    private Task<MediaItemDetailView> Detail(int id) =>
        harness.RunAsync<MediaCatalogService, MediaItemDetailView>(service => service.GetItemAsync(id, CancellationToken.None));
}
