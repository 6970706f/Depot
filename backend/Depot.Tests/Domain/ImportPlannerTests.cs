using Depot.Domain.Downloads;
using Depot.Domain.Library;

namespace Depot.Tests.Domain;

public sealed class ImportPlannerTests
{
    private static readonly LibraryTarget Movie = new(MediaType.Movie, "Dune (2021)", null);

    [Fact]
    public void A_movie_imports_its_videos_with_their_subtitles_into_the_movie_folder()
    {
        var plan = ImportPlanner.Plan(Movie, Files(
            "Dune.2021/Dune.2021.mkv",
            "Dune.2021/Dune.2021.en.srt",
            "Dune.2021/Subs/Dune.2021.pt-BR.srt",
            "Dune.2021/English.srt",
            "Dune.2021/Dune.nfo",
            "Dune.2021/poster.jpg"), []);

        var import = Assert.Single(plan);
        Assert.Equal("Dune.2021/Dune.2021.mkv", import.Video.RelativePath);
        Assert.Equal("Dune (2021)", import.Folder);
        Assert.Equal(["Dune.2021/Dune.2021.en.srt", "Dune.2021/Subs/Dune.2021.pt-BR.srt"], import.Subtitles.Select(file => file.RelativePath));
    }

    [Fact]
    public void Samples_and_files_not_downloaded_stay_out()
    {
        var files = Files("Dune/Dune.mkv", "Dune/Sample/dune.mkv", "Dune/dune-sample.mkv", "Dune/Extra.mkv").ToList();
        files[3] = files[3] with { Priority = 0, Progress = 0 };

        var plan = ImportPlanner.Plan(Movie, files, []);

        Assert.Equal(["Dune/Dune.mkv"], plan.Select(import => import.Video.RelativePath));
    }

    [Fact]
    public void A_single_file_torrent_works()
    {
        var plan = ImportPlanner.Plan(Movie, Files("Dune.2021.mkv"), []);

        Assert.Equal("Dune.2021.mkv", Assert.Single(plan).Video.RelativePath);
    }

    [Fact]
    public void Every_video_of_a_series_goes_to_the_chosen_season_with_its_original_name()
    {
        var target = new LibraryTarget(MediaType.Series, "The Last of Us", 2);

        var plan = ImportPlanner.Plan(target, Files("TLOU.S02/TLOU.S02E01.mkv", "TLOU.S02/TLOU.S02E02.mkv"), []);

        Assert.All(plan, import => Assert.Equal("The Last of Us/Season 02", import.Folder));
        Assert.Equal(2, plan.Count);
    }

    [Theory]
    [InlineData(1, new[] { "Season 1", "Season 02" }, "Show/Season 1")]
    [InlineData(2, new[] { "Season 1", "Season 02" }, "Show/Season 02")]
    [InlineData(3, new[] { "Season 1" }, "Show/Season 03")]
    [InlineData(0, new[] { "Specials" }, "Show/Specials")]
    [InlineData(0, new string[0], "Show/Season 00")]
    public void An_existing_season_folder_is_reused(int season, string[] existing, string expected)
    {
        Assert.Equal(expected, ImportPlanner.FolderFor(new LibraryTarget(MediaType.Series, "Show", season), existing));
    }

    private static IReadOnlyList<TorrentContentFile> Files(params string[] paths) =>
        paths.Select((path, index) => new TorrentContentFile(index, path, 100 + index, 1, 1)).ToList();
}
