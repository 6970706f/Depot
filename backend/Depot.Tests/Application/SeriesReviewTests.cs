using Depot.Application.Organization;
using Depot.Domain.Organization;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Application;

// Series downloads laid out in different ways: nothing that might be an extra, a special or
// another season is imported as a regular episode without a person confirming it.
public sealed class SeriesReviewTests
{
    private const string SeriesHash = "2222222222222222222222222222222222222222";

    private static readonly LibraryTarget LastOfUs = new(MediaType.Series, "The Last of Us", 1);

    [Fact]
    public async Task ExtrasGoToTheSeriesExtrasFoldersAndSamplesStayBehind()
    {
        await using var harness = await SeriesHarnessAsync(
            ("TLOU.S01/TLOU.S01E01.mkv", 4096),
            ("TLOU.S01/TLOU.S01E02.mkv", 4096),
            ("TLOU.S01/Featurettes/Inside the Episode.mkv", 1024),
            ("TLOU.S01/Deleted Scenes/Cut.mkv", 512),
            ("TLOU.S01/Sample/TLOU.S01E01.sample.mkv", 128));

        await harness.RunPassAsync();

        Assert.All(await harness.JobsAsync(), job => Assert.Equal(OrganizationJobStatus.Completed, job.Status));
        var series = Path.Join(harness.Series, "The Last of Us");
        Assert.Equal(["TLOU.S01E01.mkv", "TLOU.S01E02.mkv"], Files(series, "Season 01"));
        Assert.Equal(["Inside the Episode.mkv"], Files(series, "featurettes"));
        Assert.Equal(["Cut.mkv"], Files(series, "deleted scenes"));
        Assert.Equal(3, Directory.GetDirectories(series).Length);
    }

    [Fact]
    public async Task AMultiSeasonPackImportsTheChosenSeasonAndHoldsTheOthersForReview()
    {
        await using var harness = await SeriesHarnessAsync(
            ("TLOU/Season 1/TLOU.S01E01.mkv", 4096),
            ("TLOU/Season 2/TLOU.S02E01.mkv", 4096),
            ("TLOU/Season 2/TLOU.S02E02.mkv", 4096));

        await harness.RunPassAsync();
        await harness.RunPassAsync();

        var jobs = await harness.JobsAsync();
        Assert.Equal(OrganizationJobStatus.Completed, Single(jobs, "S01E01").Status);
        var held = jobs.Where(job => job.RelativePath.Contains("S02", StringComparison.Ordinal)).ToList();
        Assert.All(held, job =>
        {
            Assert.Equal(OrganizationJobStatus.NeedsReview, job.Status);
            Assert.Equal(new VideoPlacementView(VideoKind.Main, 2, null), job.Placement);
            Assert.Equal(Path.Join(harness.Series, "The Last of Us", "Season 02", Path.GetFileName(job.RelativePath)), job.DestinationPath);
            Assert.True(job.CanClassify);
        });
        Assert.False(Directory.Exists(Path.Join(harness.Series, "The Last of Us", "Season 02")));

        // One confirmation for the whole download accepts the suggested seasons.
        var confirmed = await harness.RunAsync<OrganizationJobService, IReadOnlyList<OrganizationJobView>>(
            service => service.ConfirmDownloadAsync(SeriesHash.ToUpperInvariant(), CancellationToken.None));
        await harness.RunPassAsync();

        Assert.Equal(2, confirmed.Count);
        Assert.All(await harness.JobsAsync(), job => Assert.Equal(OrganizationJobStatus.Completed, job.Status));
        Assert.Equal(["TLOU.S02E01.mkv", "TLOU.S02E02.mkv"], Files(Path.Join(harness.Series, "The Last of Us"), "Season 02"));
    }

    [Fact]
    public async Task ASpecialHeldForReviewCanBeConfirmedIntoTheExistingSpecialsFolder()
    {
        await using var harness = await SeriesHarnessAsync(
            ("TLOU/TLOU.S01E01.mkv", 4096),
            ("TLOU/Specials/TLOU.S00E01.mkv", 4096));
        harness.Root.CreateFolder("library", "series", "The Last of Us", "Specials");

        await harness.RunPassAsync();
        var special = Single(await harness.JobsAsync(), "S00E01");
        Assert.Equal(OrganizationJobStatus.NeedsReview, special.Status);
        Assert.Equal(new VideoPlacementView(VideoKind.Main, 0, null), special.Placement);

        await Classify(harness, special.Id, new VideoPlacementInput("main", 0, null));
        await harness.RunPassAsync();

        Assert.Equal(OrganizationJobStatus.Completed, Single(await harness.JobsAsync(), "S00E01").Status);
        Assert.Equal(["TLOU.S00E01.mkv"], Files(Path.Join(harness.Series, "The Last of Us"), "Specials"));
    }

    [Fact]
    public async Task APersonCanCorrectAnyUncertainVideo()
    {
        await using var harness = await SeriesHarnessAsync(
            ("TLOU/TLOU.S01E01.mkv", 4096),
            ("TLOU/TLOU.S01E02.mkv", 4096),
            ("TLOU/Recap.mkv", 512),
            ("TLOU/Official Trailer.mkv", 4096));

        await harness.RunPassAsync();
        var jobs = await harness.JobsAsync();
        var recap = Single(jobs, "Recap");
        var trailer = Single(jobs, "Trailer");
        Assert.Equal(OrganizationJobStatus.NeedsReview, recap.Status);
        Assert.Equal(new VideoPlacementView(VideoKind.Extra, null, "extras"), recap.Placement);
        Assert.Equal(new VideoPlacementView(VideoKind.Extra, null, "trailers"), trailer.Placement);

        // The recap is in fact an episode; the trailer isn't wanted at all.
        await Classify(harness, recap.Id, new VideoPlacementInput("main", 1, null));
        await harness.RunAsync<OrganizationJobService>(service => service.SkipAsync(trailer.Id, CancellationToken.None));
        await harness.RunPassAsync();

        Assert.Equal(OrganizationJobStatus.Completed, Single(await harness.JobsAsync(), "Recap").Status);
        Assert.Equal(OrganizationJobStatus.Skipped, Single(await harness.JobsAsync(), "Trailer").Status);
        Assert.Equal(["Recap.mkv", "TLOU.S01E01.mkv", "TLOU.S01E02.mkv"], Files(Path.Join(harness.Series, "The Last of Us"), "Season 01"));
        Assert.Equal(["Season 01"], Directory.GetDirectories(Path.Join(harness.Series, "The Last of Us")).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Join(harness.Downloads, "TLOU", "Official Trailer.mkv")));
    }

    [Fact]
    public async Task AnImportedVideoCanBeReclassifiedAfterAFailureButNotAfterItsImport()
    {
        await using var harness = await SeriesHarnessAsync(("TLOU/TLOU.S01E01.mkv", 4096), ("TLOU/Extras/Interview.mkv", 1024));
        harness.Root.WriteFile("library/series/The Last of Us/extras/Interview.mkv", "already here");

        await harness.RunPassAsync();
        var jobs = await harness.JobsAsync();
        var interview = Single(jobs, "Interview");
        Assert.Equal(OrganizationJobStatus.Failed, interview.Status);
        Assert.False(Single(jobs, "S01E01").CanClassify);
        await Assert.ThrowsAsync<InvalidJobStateException>(() => Classify(harness, Single(jobs, "S01E01").Id, new VideoPlacementInput("extra", null, "extras")));

        await Classify(harness, interview.Id, new VideoPlacementInput("extra", null, "interviews"));
        await harness.RunPassAsync();

        Assert.Equal(OrganizationJobStatus.Completed, Single(await harness.JobsAsync(), "Interview").Status);
        Assert.Equal(["Interview.mkv"], Files(Path.Join(harness.Series, "The Last of Us"), "interviews"));
        Assert.Equal("already here", await File.ReadAllTextAsync(Path.Join(harness.Series, "The Last of Us", "extras", "Interview.mkv")));
    }

    private static async Task<OrganizationHarness> SeriesHarnessAsync(params (string RelativePath, int Size)[] files)
    {
        var harness = await OrganizationHarness.CreateAsync();
        await harness.SaveSettingsAsync();
        await harness.ChooseAsync(SeriesHash, LastOfUs);
        harness.AddDownloadedTorrent(SeriesHash, "TLOU", "series", files);
        return harness;
    }

    private static Task<OrganizationJobView> Classify(OrganizationHarness harness, int id, VideoPlacementInput input) =>
        harness.RunAsync<OrganizationJobService, OrganizationJobView>(service => service.ClassifyAsync(id, input, CancellationToken.None));

    private static OrganizationJobView Single(IReadOnlyList<OrganizationJobView> jobs, string part) =>
        jobs.Single(job => job.RelativePath.Contains(part, StringComparison.Ordinal));

    private static IEnumerable<string?> Files(string folder, string name) =>
        Directory.GetFiles(Path.Join(folder, name)).Select(Path.GetFileName).Order(StringComparer.Ordinal);
}
