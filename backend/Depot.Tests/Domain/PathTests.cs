using Depot.Domain.Common;
using Depot.Domain.Organization;

namespace Depot.Tests.Domain;

public sealed class PathTests
{
    private const string Home = "/home/tester";

    [Theory]
    [InlineData("~/containers/media/movies", "/home/tester/containers/media/movies")]
    [InlineData("~", "/home/tester")]
    [InlineData("/srv/media/../media/movies/", "/srv/media/movies")]
    [InlineData("  /srv/media/movies  ", "/srv/media/movies")]
    public void ResolvesHomeAndNormalizes(string configured, string expected)
    {
        Assert.Equal(expected, LibraryPaths.Resolve(configured, Home, "moviesPath", "movies folder"));
    }

    [Theory]
    [InlineData("~other/movies")]
    [InlineData("media/movies")]
    [InlineData("/")]
    [InlineData("")]
    public void RejectsPathsThatAreNotAbsolute(string configured)
    {
        var exception = Assert.Throws<DomainValidationException>(() => LibraryPaths.Resolve(configured, Home, "moviesPath", "movies folder"));

        Assert.Equal("moviesPath", exception.Field);
    }

    [Theory]
    [InlineData("/media/movies/Dune", "/media/movies", true)]
    [InlineData("/media/movies", "/media/movies", true)]
    [InlineData("/media/movies2", "/media/movies", false)]
    [InlineData("/media", "/media/movies", false)]
    public void ComparesWholeFolderNames(string path, string root, bool inside)
    {
        Assert.Equal(inside, LibraryPaths.IsSameOrInside(path, root));
    }

    [Theory]
    [InlineData("Show/../../etc/passwd")]
    [InlineData("../outside.mkv")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public void RefusesRelativePathsThatLeaveTheRoot(string relativePath)
    {
        Assert.Null(LibraryPaths.CombineInside("/downloads", relativePath));
    }

    [Fact]
    public void JoinsTorrentPathsInsideTheRoot()
    {
        Assert.Equal("/downloads/Show S01/Show.S01E01.mkv", LibraryPaths.CombineInside("/downloads", "Show S01/Show.S01E01.mkv"));
    }

    [Fact]
    public void MapsQbittorrentFoldersAtFolderBoundaries()
    {
        var mapper = new PathMapper([("/downloads", "/srv/torrents")]);

        Assert.Equal("/srv/torrents", mapper.ToDepotPath("/downloads"));
        Assert.Equal("/srv/torrents/movies", mapper.ToDepotPath("/downloads/movies/"));
        Assert.Null(mapper.ToDepotPath("/downloads2/movies"));
        Assert.Null(mapper.ToDepotPath("/other"));
        Assert.Null(mapper.ToDepotPath("/downloads/../etc"));
    }

    [Fact]
    public void TheLongestMappingWins()
    {
        var mapper = new PathMapper([("/downloads", "/a"), ("/downloads/tv", "/b")]);

        Assert.Equal("/b/show", mapper.ToDepotPath("/downloads/tv/show"));
        Assert.Equal("/a/movie", mapper.ToDepotPath("/downloads/movie"));
    }

    [Fact]
    public void MapsWindowsPathsFromQbittorrent()
    {
        var remote = PathMapping.NormalizeRemote(@"D:\Torrents\");
        var mapper = new PathMapper([(remote!, "/mnt/d/torrents")]);

        Assert.Equal("D:/Torrents", remote);
        Assert.Equal("/mnt/d/torrents/Show", mapper.ToDepotPath(@"D:\Torrents\Show"));
    }

    [Fact]
    public void WithoutMappingsNoPathIsUsed()
    {
        var mapper = new PathMapper([]);

        // The mappings are the folders Depot may read from, so none means nowhere.
        Assert.Null(mapper.ToDepotPath("/data/torrents/"));
        Assert.Null(mapper.ToDepotPath("/home/someone/.ssh"));
    }

    [Fact]
    public void AFolderCanBeMappedToItself()
    {
        var mapper = new PathMapper([("/srv/downloads", "/srv/downloads")]);

        Assert.Equal("/srv/downloads/Show", mapper.ToDepotPath("/srv/downloads/Show"));
        Assert.Null(mapper.ToDepotPath("/srv/other"));
    }

    [Fact]
    public void ValidatesSettings()
    {
        var settings = OrganizationSettings.Create(
            "~/containers/media/movies", "~/containers/media/series", ImportMode.Auto, 60,
            [("/downloads", "~/containers/media/downloads")], Home);

        var resolved = settings.Resolve(Home);

        Assert.Equal("/home/tester/containers/media/movies", resolved.MoviesRoot);
        Assert.Equal("/home/tester/containers/media/series", resolved.SeriesRoot);
        Assert.Equal("/home/tester/containers/media/downloads/x", resolved.Sources.ToDepotPath("/downloads/x"));
        Assert.Equal("~/containers/media/movies", settings.MoviesPath);
    }

    [Theory]
    [InlineData("/media/movies", "/media/movies", 60, "seriesPath")]
    [InlineData("/media/library", "/media/library/series", 60, "seriesPath")]
    [InlineData("/media/movies", "/media/series", 5, "pollingIntervalSeconds")]
    public void RejectsInvalidSettings(string movies, string series, int interval, string field)
    {
        var exception = Assert.Throws<DomainValidationException>(() =>
            OrganizationSettings.Create(movies, series, ImportMode.Auto, interval, [], Home));

        Assert.Equal(field, exception.Field);
    }

    [Fact]
    public void RejectsDuplicateAndRelativeMappings()
    {
        var duplicate = Assert.Throws<DomainValidationException>(() => OrganizationSettings.Create(
            "/media/movies", "/media/series", ImportMode.Auto, 60, [("/downloads", "/a"), ("/downloads/", "/b")], Home));
        var relative = Assert.Throws<DomainValidationException>(() => OrganizationSettings.Create(
            "/media/movies", "/media/series", ImportMode.Auto, 60, [("downloads", "/a")], Home));

        Assert.Equal("pathMappings[1].qbittorrentPath", duplicate.Field);
        Assert.Equal("pathMappings[0].qbittorrentPath", relative.Field);
    }
}
