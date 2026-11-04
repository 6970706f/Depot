using Depot.Domain.Organization;

namespace Depot.Tests.Domain;

public sealed class SeriesVideosTests
{
    private const long Episode = 1_500_000_000;

    [Fact]
    public void ASeasonPackWithExtrasFoldersIsSortedWithoutReview()
    {
        var result = Classify(1,
            ("Show.S01/Show.S01E01.mkv", Episode),
            ("Show.S01/Show.S01E02.mkv", Episode),
            ("Show.S01/Extras/Show.Gag.Reel.mkv", 90_000_000),
            ("Show.S01/Featurettes/Making the Show.mkv", 200_000_000),
            ("Show.S01/Behind the Scenes/Day 1.mkv", 200_000_000),
            ("Show.S01/Deleted.Scenes/Cut.mkv", 50_000_000),
            ("Show.S01/Special Features/Commentary.mkv", 900_000_000));

        Assert.All(result.Values, classification => Assert.False(classification.NeedsReview));
        Assert.Equal(VideoPlacement.Episode(1), result["Show.S01/Show.S01E01.mkv"].Placement);
        Assert.Equal(VideoPlacement.Extra("extras"), result["Show.S01/Extras/Show.Gag.Reel.mkv"].Placement);
        Assert.Equal(VideoPlacement.Extra("featurettes"), result["Show.S01/Featurettes/Making the Show.mkv"].Placement);
        Assert.Equal(VideoPlacement.Extra("behind the scenes"), result["Show.S01/Behind the Scenes/Day 1.mkv"].Placement);
        Assert.Equal(VideoPlacement.Extra("deleted scenes"), result["Show.S01/Deleted.Scenes/Cut.mkv"].Placement);
        Assert.Equal(VideoPlacement.Extra("extras"), result["Show.S01/Special Features/Commentary.mkv"].Placement);
    }

    [Fact]
    public void SeasonsOtherThanTheChosenOneWaitForReviewWithTheirOwnSeasonSuggested()
    {
        var result = Classify(1,
            ("Show/Season 1/Show.S01E01.mkv", Episode),
            ("Show/Season 2/Show.S02E01.mkv", Episode),
            ("Show/S03/Show.3x01.mkv", Episode));

        Assert.False(result["Show/Season 1/Show.S01E01.mkv"].NeedsReview);
        Assert.Equal(new VideoClassification(VideoPlacement.Episode(2), "It's numbered as season 2, but season 1 was chosen when adding the download."),
            result["Show/Season 2/Show.S02E01.mkv"]);
        Assert.Equal(VideoPlacement.Episode(3), result["Show/S03/Show.3x01.mkv"].Placement);
        Assert.True(result["Show/S03/Show.3x01.mkv"].NeedsReview);
    }

    [Theory]
    [InlineData("Show/Specials/Show.S00E01.Christmas.Special.mkv")]
    [InlineData("Show/Season 00/Show.S00E02.mkv")]
    [InlineData("Show/Season 0/Show.Pilot.Unaired.E01.mkv")]
    public void SpecialsAreEpisodesOfSeasonZeroAndNeverExtras(string path)
    {
        var withSeasonOne = Classify(1, ("Show/Season 01/Show.S01E01.mkv", Episode), (path, Episode))[path];
        var withSpecialsChosen = Classify(0, (path, Episode))[path];

        Assert.Equal(VideoPlacement.Episode(0), withSeasonOne.Placement);
        Assert.True(withSeasonOne.NeedsReview);
        Assert.Contains("the specials (season 0)", withSeasonOne.ReviewReason);
        Assert.Equal(new VideoClassification(VideoPlacement.Episode(0), null), withSpecialsChosen);
    }

    [Fact]
    public void AnEpisodeNumberInsideAnExtrasFolderIsAskedAbout()
    {
        // The series "Extras" (2005): its folder name alone can't decide.
        var result = Classify(1, ("Extras/Extras.S01E01.mkv", Episode), ("Extras/Extras.S01E02.mkv", Episode));

        Assert.All(result.Values, classification =>
        {
            Assert.True(classification.NeedsReview);
            Assert.Contains("numbered like an episode", classification.ReviewReason);
        });
    }

    [Fact]
    public void AWordInAnEpisodesNameIsAskedAboutNotDecided()
    {
        var result = Classify(1, ("Show.S01E05.The.Interview.mkv", Episode), ("Show.S01E06.mkv", Episode));

        var interview = result["Show.S01E05.The.Interview.mkv"];
        Assert.Equal(VideoPlacement.Episode(1), interview.Placement);
        Assert.Contains("\"interview\"", interview.ReviewReason);
        Assert.False(result["Show.S01E06.mkv"].NeedsReview);
    }

    [Fact]
    public void AnUnnumberedVideoAmongEpisodesIsAskedAboutWithItsSizeAsAHint()
    {
        var result = Classify(1,
            ("Show.S01E01.mkv", Episode),
            ("Show.S01E02.mkv", Episode),
            ("Recap.mkv", 100_000_000),
            ("Director's Cut.mkv", Episode),
            ("Show.Official.Trailer.mkv", 1_400_000_000));

        Assert.Equal(VideoPlacement.Extra("extras"), result["Recap.mkv"].Placement);
        Assert.Contains("much smaller", result["Recap.mkv"].ReviewReason);
        Assert.Equal(VideoPlacement.Episode(1), result["Director's Cut.mkv"].Placement);
        Assert.True(result["Director's Cut.mkv"].NeedsReview);
        // A word is enough for a suggestion, never for a decision.
        Assert.Equal(VideoPlacement.Extra("trailers"), result["Show.Official.Trailer.mkv"].Placement);
        Assert.True(result["Show.Official.Trailer.mkv"].NeedsReview);
    }

    [Fact]
    public void FreelyNamedVideosGoToTheChosenSeasonAsBefore()
    {
        var result = Classify(2, ("Pilot.mkv", Episode), ("The Return.mkv", Episode), ("Season 2/Finale.mkv", Episode));

        Assert.All(result.Values, classification => Assert.Equal(new VideoClassification(VideoPlacement.Episode(2), null), classification));
    }

    [Fact]
    public void RecognizesTheUsualEpisodeNumberings()
    {
        var result = Classify(1,
            ("Show.s01e01.mkv", Episode),
            ("Show S01.E02.mkv", Episode),
            ("[Group] Show - 03 [1080p].mkv", Episode),
            ("Show.E04.mkv", Episode),
            ("05 - Title.mkv", Episode),
            ("Show.Episode.6.mkv", Episode));

        Assert.All(result.Values, classification => Assert.Equal(new VideoClassification(VideoPlacement.Episode(1), null), classification));
    }

    [Fact]
    public void ResolutionsAndCodecsArentSeasons()
    {
        var result = Classify(1, ("Show.1920x1080.x264.E01.mkv", Episode), ("Show.E02.mkv", Episode));

        Assert.False(result["Show.1920x1080.x264.E01.mkv"].NeedsReview);
    }

    [Fact]
    public void ANameAndFolderThatDisagreeAreAskedAbout()
    {
        var result = Classify(1, ("Show/Season 1/Show.S02E01.mkv", Episode));

        Assert.Equal(VideoPlacement.Episode(2), result["Show/Season 1/Show.S02E01.mkv"].Placement);
        Assert.Contains("its folder says season 1", result["Show/Season 1/Show.S02E01.mkv"].ReviewReason);
    }

    private static IReadOnlyDictionary<string, VideoClassification> Classify(int season, params (string Path, long Size)[] videos) =>
        SeriesVideos.Classify(videos.Select(video => new SeriesVideo(video.Path, video.Size)).ToList(), season);
}
