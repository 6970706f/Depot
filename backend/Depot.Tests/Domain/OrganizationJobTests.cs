using Depot.Domain.Organization;

namespace Depot.Tests.Domain;

public sealed class OrganizationJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static readonly LibraryTarget Show = new(MediaType.Series, "The Last of Us", 1);

    [Fact]
    public void ANewJobWaitsToBeImportedWithItsLibraryChoice()
    {
        var job = New();

        Assert.Equal(OrganizationJobStatus.Discovered, job.Status);
        Assert.True(job.IsReadyToImport);
        Assert.Equal(Show, job.Target);
    }

    [Fact]
    public void GoesThroughAnImport()
    {
        var job = New();

        job.StartProcessing("/media/series/The Last of Us/Season 01/ep1.mkv", Now);
        Assert.Equal(OrganizationJobStatus.Processing, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.False(job.IsReadyToImport);

        job.Complete(ImportMode.Hardlink, null, Now);
        Assert.Equal(OrganizationJobStatus.Completed, job.Status);
        Assert.Equal(ImportMode.Hardlink, job.ImportedWith);
        Assert.Equal(Now, job.CompletedAt);
        Assert.False(job.CanRetry);
        Assert.False(job.CanSkip);
    }

    [Fact]
    public void AFailedJobCanBeRetriedOrSkipped()
    {
        var job = New();
        job.StartProcessing("/media/series/x.mkv", Now);
        job.Fail("Disk full.", Now);

        Assert.True(job.CanRetry);
        Assert.True(job.CanSkip);

        job.Retry(Now);
        Assert.Equal(OrganizationJobStatus.Discovered, job.Status);
        Assert.Null(job.Message);
        Assert.Null(job.DestinationPath);
    }

    [Fact]
    public void ASkippedJobIsOnlyImportedAfterARetry()
    {
        var job = New();
        job.Skip(Now);

        Assert.False(job.IsReadyToImport);
        Assert.Throws<InvalidJobStateException>(() => job.StartProcessing("/media/series/x.mkv", Now));

        job.Retry(Now);
        Assert.True(job.IsReadyToImport);
    }

    [Fact]
    public void AnInterruptedImportReturnsToTheQueue()
    {
        var job = New();
        job.StartProcessing("/media/series/x.mkv", Now);

        job.ReturnToQueue("Depot stopped.", Now);

        Assert.Equal(OrganizationJobStatus.Discovered, job.Status);
        Assert.Null(job.DestinationPath);
        Assert.Equal("Depot stopped.", job.Message);
    }

    [Fact]
    public void TheExpectedFileBelongsToOneAttempt()
    {
        var job = New();
        Assert.Throws<InvalidJobStateException>(() => job.ExpectFile(42, Now));

        job.StartProcessing("/media/series/x.mkv", Now);
        job.ExpectFile(42, Now);
        Assert.Equal(42, job.ExpectedFileId);

        job.ReturnToQueue("Depot stopped.", Now);
        Assert.Null(job.ExpectedFileId);

        job.StartProcessing("/media/series/x.mkv", Now);
        job.ExpectFile(43, Now);
        job.Fail("Disk full.", Now);
        job.Retry(Now);
        Assert.Null(job.ExpectedFileId);
    }

    [Fact]
    public void ACompletedJobCantBeSkippedOrRetried()
    {
        var job = New();
        job.StartProcessing("/media/series/x.mkv", Now);
        job.Complete(ImportMode.Copy, null, Now);

        var skip = Assert.Throws<InvalidJobStateException>(() => job.Skip(Now));
        Assert.Throws<InvalidJobStateException>(() => job.Retry(Now));

        Assert.Contains("already imported", skip.Message);
    }

    [Fact]
    public void AnUncertainVideoWaitsForReviewAndIsNeverImportedBeforeIt()
    {
        var job = New(VideoPlacement.Extra("featurettes"), "It's in an extras folder, but it's numbered like an episode.");

        Assert.Equal(OrganizationJobStatus.NeedsReview, job.Status);
        Assert.False(job.IsReadyToImport);
        Assert.True(job.CanClassify);
        Assert.True(job.CanSkip);
        Assert.Throws<InvalidJobStateException>(() => job.StartProcessing("/media/series/x.mkv", Now));

        job.Classify(VideoPlacement.Episode(0), Now);

        Assert.Equal(OrganizationJobStatus.Discovered, job.Status);
        Assert.Equal(VideoPlacement.Episode(0), job.Placement);
        Assert.Null(job.Message);
        Assert.True(job.IsReadyToImport);
    }

    [Fact]
    public void APlacementCanBeCorrectedUntilTheImportStarts()
    {
        var job = New();
        job.Classify(VideoPlacement.Extra("trailers"), Now);
        job.StartProcessing("/media/series/x.mkv", Now);
        Assert.False(job.CanClassify);
        Assert.Throws<InvalidJobStateException>(() => job.Classify(VideoPlacement.Episode(1), Now));

        job.Fail("Disk full.", Now);
        job.Classify(VideoPlacement.Episode(1), Now);
        Assert.Equal(OrganizationJobStatus.Discovered, job.Status);
        Assert.Null(job.DestinationPath);

        var movie = OrganizationJob.Discover(
            "hash", "Dune", "Dune/Dune.mkv", 1024, new LibraryTarget(MediaType.Movie, "Dune", null), VideoPlacement.Movie, null, Now);
        Assert.False(movie.CanClassify);
        Assert.Throws<InvalidJobStateException>(() => movie.Classify(VideoPlacement.Extra("extras"), Now));
    }

    [Theory]
    [InlineData("main", 0, null)]
    [InlineData("MAIN", 12, null)]
    [InlineData("extra", null, "Behind The Scenes")]
    public void AcceptsThePlacementsJellyfinUnderstands(string kind, int? season, string? extrasFolder)
    {
        var placement = VideoPlacement.ForSeries(kind, season, extrasFolder);

        Assert.Equal(kind.Equals("extra", StringComparison.OrdinalIgnoreCase) ? VideoKind.Extra : VideoKind.Main, placement.Kind);
        Assert.Equal(extrasFolder?.ToLowerInvariant(), placement.ExtrasFolder);
    }

    [Theory]
    [InlineData("main", null, null, "season")]
    [InlineData("main", 1000, null, "season")]
    [InlineData("extra", null, "Season 01", "extrasFolder")]
    [InlineData("extra", null, null, "extrasFolder")]
    [InlineData("sample", 1, null, "kind")]
    public void RefusesPlacementsJellyfinWouldMisread(string kind, int? season, string? extrasFolder, string field)
    {
        var error = Assert.Throws<Depot.Domain.Common.DomainValidationException>(() => VideoPlacement.ForSeries(kind, season, extrasFolder));

        Assert.Equal(field, error.Field);
    }

    private static OrganizationJob New(VideoPlacement? placement = null, string? reviewReason = null) =>
        OrganizationJob.Discover(
            "hash", "The.Last.of.Us.S01", "The.Last.of.Us.S01/ep1.mkv", 1024, Show, placement ?? VideoPlacement.Episode(1), reviewReason, Now);
}
