using Depot.Application.Organization;
using Depot.Domain.Downloads;
using Depot.Domain.Organization;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Application;

public sealed class OrganizationFlowTests
{
    private const string MovieHash = "1111111111111111111111111111111111111111";
    private const string SeriesHash = "2222222222222222222222222222222222222222";

    private static readonly LibraryTarget Dune = new(MediaType.Movie, "Dune (2021)", null);
    private static readonly LibraryTarget LastOfUs = new(MediaType.Series, "The Last of Us", 1);

    [Fact]
    public async Task IncompleteTorrentsAreNotImported()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        await harness.ChooseAsync(MovieHash, Dune);
        harness.AddTorrent(MovieHash, "Dune.2021", harness.Downloads, [("Dune.2021/Dune.2021.mkv", 4096)],
            TorrentStatus.Paused, progress: 0.5);

        await harness.RunPassAsync();

        Assert.Empty(await harness.JobsAsync());
        Assert.Empty(Directory.GetFileSystemEntries(harness.Movies));
    }

    [Fact]
    public async Task TorrentsWithoutALibraryChoiceAreNeverTouched()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        harness.AddDownloadedTorrent(MovieHash, "Dune.2021.1080p", "movies", ("Dune.2021.1080p/Dune.2021.1080p.mkv", 1024));
        harness.AddDownloadedTorrent(SeriesHash, "The.Last.of.Us.S01", "series", ("The.Last.of.Us.S01/The.Last.of.Us.S01E01.mkv", 1024));

        await harness.RunPassAsync();

        Assert.Empty(await harness.JobsAsync());
        Assert.Empty(Directory.GetFileSystemEntries(harness.Movies));
        Assert.Empty(Directory.GetFileSystemEntries(harness.Series));
    }

    [Fact]
    public async Task AMovieGoesIntoTheChosenFolderWithItsOwnFileName()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        await harness.ChooseAsync(MovieHash, Dune);
        harness.AddDownloadedTorrent(MovieHash, "Dune.2021.1080p", "movies",
            ("Dune.2021.1080p/Dune.2021.1080p.mkv", 4096),
            ("Dune.2021.1080p/Dune.2021.1080p.en.srt", 64),
            ("Dune.2021.1080p/Sample/dune-sample.mkv", 128),
            ("Dune.2021.1080p/RARBG.txt", 16));
        var source = Path.Join(harness.Downloads, "Dune.2021.1080p", "Dune.2021.1080p.mkv");
        var original = await File.ReadAllBytesAsync(source);

        await harness.RunPassAsync();

        var job = Assert.Single(await harness.JobsAsync());
        Assert.Equal(OrganizationJobStatus.Completed, job.Status);
        Assert.Equal(ImportMode.Hardlink, job.ImportedWith);
        var destination = Path.Join(harness.Movies, "Dune (2021)", "Dune.2021.1080p.mkv");
        Assert.Equal(destination, job.DestinationPath);
        // Only the video: subtitles, samples and anything else stay in the download.
        Assert.Equal([destination], Directory.GetFiles(Path.Join(harness.Movies, "Dune (2021)")));
        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        // The torrent stays in qBittorrent and its files stay where it seeds them from.
        Assert.Single(harness.QBittorrent.Torrents);
        Assert.Equal(0, harness.QBittorrent.RemoveCalls);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task EpisodesGoIntoTheChosenSeasonWhateverTheyAreCalled()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        await harness.ChooseAsync(SeriesHash, LastOfUs);
        harness.AddDownloadedTorrent(SeriesHash, "[Group] TLOU Batch", "series",
            ("[Group] TLOU Batch/[Group] TLOU - 01 [1080p].mkv", 2048),
            ("[Group] TLOU Batch/[Group] TLOU - 01 [1080p].ass", 64),
            ("[Group] TLOU Batch/Extras/whatever.mp4", 2048),
            ("[Group] TLOU Batch/notes.nfo", 16));

        await harness.RunPassAsync();

        var season = Path.Join(harness.Series, "The Last of Us", "Season 01");
        Assert.All(await harness.JobsAsync(), job => Assert.Equal(OrganizationJobStatus.Completed, job.Status));
        Assert.Equal(
            ["[Group] TLOU - 01 [1080p].mkv", "whatever.mp4"],
            Directory.GetFiles(season).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnExistingSeasonFolderIsReused()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        harness.Root.CreateFolder("library", "series", "The Last of Us", "Season 1");
        await harness.ChooseAsync(SeriesHash, LastOfUs);
        harness.AddDownloadedTorrent(SeriesHash, "ep", "series", ("ep2.mkv", 1024));

        await harness.RunPassAsync();

        Assert.True(File.Exists(Path.Join(harness.Series, "The Last of Us", "Season 1", "ep2.mkv")));
        Assert.False(Directory.Exists(Path.Join(harness.Series, "The Last of Us", "Season 01")));
    }

    [Fact]
    public async Task TorrentsFinishedWhileDepotWasOffAreFound()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        await harness.ChooseAsync(MovieHash, Dune);
        harness.AddTorrent(MovieHash, "Dune", harness.Downloads, [("Dune.mkv", 1024)], TorrentStatus.Downloading, progress: 0.3);
        await harness.RunPassAsync();
        Assert.Empty(await harness.JobsAsync());

        harness.AddDownloadedTorrent(MovieHash, "Dune", "movies", ("Dune.mkv", 1024));
        await harness.RunPassAsync();

        Assert.Equal(OrganizationJobStatus.Completed, Assert.Single(await harness.JobsAsync()).Status);
    }

    [Fact]
    public async Task RepeatedPassesNeverDuplicateJobsOrFiles()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        await harness.ChooseAsync(MovieHash, Dune);
        harness.AddDownloadedTorrent(MovieHash, "Dune.2021", "movies", ("Dune.2021/Dune.2021.mkv", 1024));

        await harness.RunPassAsync();
        await harness.RunPassAsync();
        await harness.RunPassAsync();

        Assert.Single(await harness.JobsAsync());
        Assert.Single(Directory.GetDirectories(harness.Movies));
        Assert.Single(Directory.GetFiles(Path.Join(harness.Movies, "Dune (2021)")));
    }

    [Fact]
    public async Task AFileAlreadyInTheLibraryIsNeverOverwritten()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        var existing = harness.Root.WriteFile("library/series/The Last of Us/Season 01/ep1.mkv", "already here");
        await harness.ChooseAsync(SeriesHash, LastOfUs);
        harness.AddDownloadedTorrent(SeriesHash, "ep", "series", ("ep1.mkv", 1024));

        await harness.RunPassAsync();

        var job = Assert.Single(await harness.JobsAsync());
        Assert.Equal(OrganizationJobStatus.Failed, job.Status);
        Assert.Contains("already exists", job.Message);
        Assert.Equal("already here", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task AnImportInterruptedByARestartIsInspectedAndRunAgain()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        harness.AddDownloadedTorrent(MovieHash, "Dune.2021", "movies", ("Dune.2021/Dune.2021.mkv", 1024));
        await harness.ChooseAsync(MovieHash, Dune);
        // Found while the library folder is missing, so the job waits instead of importing.
        Directory.Delete(harness.Movies);
        await harness.RunPassAsync();
        Directory.CreateDirectory(harness.Movies);

        // As if Depot had stopped in the middle of the import.
        var job = Assert.Single(await harness.JobsAsync());
        await harness.RunAsync<IOrganizationJobRepository>(async repository =>
        {
            var stored = (await repository.GetAsync(job.Id, CancellationToken.None))!;
            stored.StartProcessing(job.DestinationPath!, DateTimeOffset.UtcNow);
            await repository.UpdateAsync(stored, CancellationToken.None);
        });
        var leftover = harness.Root.WriteFile($"library/movies/Dune (2021)/.depot-job-{job.Id}-v.tmp", "half a movie");

        await harness.RecoverAsync();
        var recovered = Assert.Single(await harness.JobsAsync());
        Assert.Equal(OrganizationJobStatus.Discovered, recovered.Status);
        Assert.False(File.Exists(leftover));

        await harness.RunPassAsync();
        Assert.Equal(OrganizationJobStatus.Completed, Assert.Single(await harness.JobsAsync()).Status);
        Assert.Equal(1024, new FileInfo(Path.Join(harness.Movies, "Dune (2021)", "Dune.2021.mkv")).Length);
    }

    [Fact]
    public async Task ImportsWaitWhileALibraryFolderIsMissing()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        Directory.Delete(harness.Movies);
        await harness.ChooseAsync(MovieHash, Dune);
        harness.AddDownloadedTorrent(MovieHash, "Dune.2021", "movies", ("Dune.2021/Dune.2021.mkv", 1024));

        await harness.RunPassAsync();
        Assert.Equal(OrganizationJobStatus.Discovered, Assert.Single(await harness.JobsAsync()).Status);
        Assert.Equal(OrganizationRunState.Paused, harness.Get<OrganizationRuntimeStatus>().Current.State);

        Directory.CreateDirectory(harness.Movies);
        await harness.RunPassAsync();
        Assert.Equal(OrganizationJobStatus.Completed, Assert.Single(await harness.JobsAsync()).Status);
    }

    [Fact]
    public async Task PathMappingsTranslateWhereQbittorrentSavesFiles()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        harness.Root.CreateFolder("other");
        await harness.SaveSettingsAsync(mappings: [new PathMappingInput("/data/elsewhere", "~/other")]);
        await harness.ChooseAsync(MovieHash, Dune);
        // qBittorrent runs in a container and reports its own path for the downloads.
        harness.AddTorrent(MovieHash, "Dune.2021", "/downloads", [("Dune.2021/Dune.2021.mkv", 1024)], category: "movies");

        await harness.RunPassAsync();
        var waiting = Assert.Single(await harness.JobsAsync());
        Assert.Equal(OrganizationJobStatus.Discovered, waiting.Status);
        Assert.Contains("no path mapping covers", waiting.Message);

        await harness.SaveSettingsAsync(mappings: [new PathMappingInput("/downloads", "~/downloads")]);
        await harness.RunPassAsync();
        Assert.Equal(OrganizationJobStatus.Completed, Assert.Single(await harness.JobsAsync()).Status);
    }

    [Fact]
    public async Task AFailedImportCanBeRetriedOnceTheProblemIsFixed()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        await harness.ChooseAsync(MovieHash, Dune);
        harness.AddDownloadedTorrent(MovieHash, "Dune.2021", "movies", ("Dune.2021/Dune.2021.mkv", 1024));
        var source = Path.Join(harness.Downloads, "Dune.2021", "Dune.2021.mkv");
        File.Move(source, source + ".moved");

        await harness.RunPassAsync();
        var failed = Assert.Single(await harness.JobsAsync());
        Assert.Equal(OrganizationJobStatus.Failed, failed.Status);
        Assert.Contains("isn't there", failed.Message);

        File.Move(source + ".moved", source);
        await harness.RunAsync<OrganizationJobService, OrganizationJobView>(service => service.RetryAsync(failed.Id, CancellationToken.None));
        await harness.RunPassAsync();
        Assert.Equal(OrganizationJobStatus.Completed, Assert.Single(await harness.JobsAsync()).Status);
    }

    [Fact]
    public async Task SkippingDoesNotTouchAnyFile()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        await harness.ChooseAsync(MovieHash, Dune);
        harness.AddDownloadedTorrent(MovieHash, "video", "movies", ("video.mp4", 1024));
        var source = Path.Join(harness.Downloads, "video.mp4");
        File.Move(source, source + ".moved");
        await harness.RunPassAsync();
        var job = Assert.Single(await harness.JobsAsync());

        var skipped = await harness.RunAsync<OrganizationJobService, OrganizationJobView>(service => service.SkipAsync(job.Id, CancellationToken.None));
        File.Move(source + ".moved", source);
        await harness.RunPassAsync();

        Assert.Equal(OrganizationJobStatus.Skipped, skipped.Status);
        Assert.Equal(OrganizationJobStatus.Skipped, Assert.Single(await harness.JobsAsync()).Status);
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetFileSystemEntries(harness.Movies));
    }

    [Fact]
    public async Task ListsTheSeriesInTheLibraryWithTheirSeasons()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        harness.Root.CreateFolder("library", "series", "The Last of Us", "Season 1");
        harness.Root.CreateFolder("library", "series", "The Last of Us", "Season 02");
        harness.Root.CreateFolder("library", "series", "The Last of Us", "Extras");
        harness.Root.CreateFolder("library", "series", "arcane");

        var series = await harness.RunAsync<LibraryService, IReadOnlyList<LibrarySeriesView>>(
            service => service.ListSeriesAsync(CancellationToken.None));

        Assert.Equal(["arcane", "The Last of Us"], series.Select(entry => entry.Name));
        Assert.Empty(series[0].Seasons);
        Assert.Equal([1, 2], series[1].Seasons);
    }
}
