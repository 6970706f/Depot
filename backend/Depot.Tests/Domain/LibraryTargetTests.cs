using Depot.Domain.Common;
using Depot.Domain.Organization;

namespace Depot.Tests.Domain;

public sealed class LibraryTargetTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("other")]
    [InlineData(" Other ")]
    public void OtherMeansNoLibrary(string? mediaType)
    {
        Assert.Null(LibraryTarget.Create(mediaType, "ignored", 3));
    }

    [Fact]
    public void AMovieNeedsOnlyItsName()
    {
        var target = LibraryTarget.Create("movie", "  Dune (2021) ", 4);

        Assert.Equal(new LibraryTarget(MediaType.Movie, "Dune (2021)", null), target);
    }

    [Fact]
    public void ASeriesNeedsItsNameAndSeason()
    {
        Assert.Equal(new LibraryTarget(MediaType.Series, "The Last of Us", 2), LibraryTarget.Create("Series", "The Last of Us", 2));
        Assert.Equal(0, LibraryTarget.Create("series", "The Last of Us", 0)!.Season);

        Assert.Equal("season", Assert.Throws<DomainValidationException>(() => LibraryTarget.Create("series", "Show", null)).Field);
        Assert.Equal("season", Assert.Throws<DomainValidationException>(() => LibraryTarget.Create("series", "Show", -1)).Field);
        Assert.Equal("season", Assert.Throws<DomainValidationException>(() => LibraryTarget.Create("series", "Show", 1000)).Field);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("Show/Season 1")]
    [InlineData("..\\escape")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("tab\there")]
    public void RefusesFolderNamesThatArentOneVisibleFolder(string? title)
    {
        var exception = Assert.Throws<DomainValidationException>(() => LibraryTarget.Create("series", title, 1));

        Assert.Equal("title", exception.Field);
    }

    [Fact]
    public void RefusesUnknownTypes()
    {
        Assert.Equal("mediaType", Assert.Throws<DomainValidationException>(() => LibraryTarget.Create("anime", "x", 1)).Field);
    }

    [Theory]
    [InlineData("Season 1", 1)]
    [InlineData("Season 01", 1)]
    [InlineData("season 12", 12)]
    [InlineData("Season01", 1)]
    [InlineData("Specials", 0)]
    [InlineData("Season 00", 0)]
    [InlineData("Extras", null)]
    [InlineData("Season One", null)]
    [InlineData("S01", null)]
    public void ReadsSeasonFolders(string folder, int? season)
    {
        Assert.Equal(season, SeasonFolders.Parse(folder));
    }

    [Fact]
    public void NamesNewSeasonFoldersWithTwoDigits()
    {
        Assert.Equal("Season 01", SeasonFolders.NameFor(1));
        Assert.Equal("Season 12", SeasonFolders.NameFor(12));
        Assert.Equal("Season 100", SeasonFolders.NameFor(100));
    }
}
