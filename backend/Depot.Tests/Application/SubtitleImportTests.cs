using Depot.Domain.Organization;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Application;

public sealed class SubtitleImportTests
{
    private const string MovieHash = "1111111111111111111111111111111111111111";
    private const string SeriesHash = "2222222222222222222222222222222222222222";

    private static readonly LibraryTarget Dune = new(MediaType.Movie, "Dune (2021)", null);
    private static readonly LibraryTarget LastOfUs = new(MediaType.Series, "The Last of Us", 1);

    [Fact]
    public async Task AMovieBringsItsSubtitlesAndNothingElse()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        await harness.ChooseAsync(MovieHash, Dune);
        harness.AddDownloadedTorrent(MovieHash, "Dune", "movies",
            ("Dune/Dune.mkv", 4096), ("Dune/Dune.srt", 300), ("Dune/Dune.en.srt", 310), ("Dune/English.srt", 320), ("Dune/Dune.nfo", 50));
        var originals = Snapshot(harness);

        await harness.RunPassAsync();

        var job = Assert.Single(await harness.JobsAsync());
        Assert.Equal(OrganizationJobStatus.Completed, job.Status);
        Assert.Equal("Subtitles imported: Dune.en.srt, Dune.srt.", job.Message);
        var folder = Path.Join(harness.Movies, "Dune (2021)");
        Assert.Equal(["Dune.en.srt", "Dune.mkv", "Dune.srt"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        foreach (var name in new[] { "Dune.mkv", "Dune.srt", "Dune.en.srt" })
        {
            Assert.Equal(originals[$"Dune/{name}"], await File.ReadAllBytesAsync(Path.Join(folder, name)));
        }

        AssertUnchanged(harness, originals);
    }

    [Fact]
    public async Task EachEpisodeBringsOnlyItsOwnSubtitles()
    {
        await using var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync(ImportMode.Copy);
        await harness.ChooseAsync(SeriesHash, LastOfUs);
        harness.AddDownloadedTorrent(SeriesHash, "TLOU.S01", "series",
            ("TLOU.S01/TLOU.S01E01.mkv", 2048),
            ("TLOU.S01/TLOU.S01E01.en.srt", 100),
            ("TLOU.S01/Subs/TLOU.S01E01.pt-BR.ass", 110),
            ("TLOU.S01/TLOU.S01E02.mkv", 2048),
            ("TLOU.S01/TLOU.S01E03.en.srt", 120));
        var originals = Snapshot(harness);

        await harness.RunPassAsync();

        var jobs = await harness.JobsAsync();
        Assert.All(jobs, job => Assert.Equal(OrganizationJobStatus.Completed, job.Status));
        Assert.Null(jobs.Single(job => job.RelativePath.EndsWith("E02.mkv", StringComparison.Ordinal)).Message);
        var season = Path.Join(harness.Series, "The Last of Us", "Season 01");
        Assert.Equal(
            ["TLOU.S01E01.en.srt", "TLOU.S01E01.mkv", "TLOU.S01E01.pt-BR.ass", "TLOU.S01E02.mkv"],
            Directory.GetFiles(season).Select(Path.GetFileName).Order());
        AssertUnchanged(harness, originals);
    }

    // Subtitles are placed before the video, so a crash leaves either a half-written temporary
    // subtitle or finished subtitles without their video. Either way the job runs again and
    // ends with one copy of each file.
    [Theory]
    [InlineData(CrashStep.CopyHalfway)]
    [InlineData(CrashStep.BeforeTheFileIsRecorded)]
    public async Task AnImportStoppedAmongItsSubtitlesEndsWithOneCopyOfEach(CrashStep step)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var crash = new Crash(step);
        await using var harness = await OrganizationHarness.CreateAsync(crash);
        await harness.SaveSettingsAsync(ImportMode.Copy);
        await harness.ChooseAsync(MovieHash, Dune);
        harness.AddDownloadedTorrent(MovieHash, "Dune", "movies", ("Dune/Dune.mkv", 4096), ("Dune/Dune.en.srt", 300), ("Dune/Dune.pt.srt", 300));
        var originals = Snapshot(harness);

        var pass = harness.RunPassAsync();
        Assert.Same(crash.Reached, await Task.WhenAny(crash.Reached, pass).WaitAsync(TimeSpan.FromSeconds(10)));
        var folder = Path.Join(harness.Movies, "Dune (2021)");
        Assert.False(File.Exists(Path.Join(folder, "Dune.mkv")));

        await using var restarted = await harness.RestartAsync();
        await restarted.RecoverAsync();
        Assert.Equal(OrganizationJobStatus.Discovered, Assert.Single(await restarted.JobsAsync()).Status);
        Assert.Empty(Directory.GetFiles(folder, ".depot-job-*"));

        await restarted.RunPassAsync();

        var job = Assert.Single(await restarted.JobsAsync());
        Assert.Equal(OrganizationJobStatus.Completed, job.Status);
        Assert.Equal("Subtitles imported: Dune.en.srt, Dune.pt.srt.", job.Message);
        Assert.Equal(["Dune.en.srt", "Dune.mkv", "Dune.pt.srt"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        foreach (var name in new[] { "Dune.mkv", "Dune.en.srt", "Dune.pt.srt" })
        {
            Assert.Equal(originals[$"Dune/{name}"], await File.ReadAllBytesAsync(Path.Join(folder, name)));
        }

        AssertUnchanged(restarted, originals);
    }

    private static Dictionary<string, byte[]> Snapshot(OrganizationHarness harness) =>
        Directory.EnumerateFiles(harness.Downloads, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(harness.Downloads, path).Replace('\\', '/'), File.ReadAllBytes);

    private static void AssertUnchanged(OrganizationHarness harness, Dictionary<string, byte[]> originals)
    {
        var now = Snapshot(harness);
        Assert.Equal(originals.Keys.Order(), now.Keys.Order());
        Assert.All(originals, original => Assert.Equal(original.Value, now[original.Key]));
    }
}
