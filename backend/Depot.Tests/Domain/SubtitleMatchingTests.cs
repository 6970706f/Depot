using Depot.Domain.Organization;

namespace Depot.Tests.Domain;

public sealed class SubtitleMatchingTests
{
    [Fact]
    public void ASubtitleNamedAfterTheVideoBelongsToIt()
    {
        var subtitles = MediaFiles.SubtitlesFor("Dune/Dune.mkv", ["Dune/Dune.mkv", "Dune/Dune.srt"]);

        Assert.Equal(["Dune/Dune.srt"], subtitles);
    }

    [Fact]
    public void KeepsEveryLanguageAndFormat()
    {
        string[] files =
        [
            "Dune/Dune.mkv",
            "Dune/Dune.srt",
            "Dune/Dune.en.srt",
            "Dune/Dune.pt-BR.forced.ass",
            "Dune/Dune.ja.ssa",
            "Dune/Dune.de.vtt",
            "Dune/Dune.idx",
            "Dune/Dune.sub",
            "Dune/Dune.fr.SUP",
            "Dune/Subs/Dune.es.srt",
        ];

        var subtitles = MediaFiles.SubtitlesFor("Dune/Dune.mkv", files);

        Assert.Equal(files[1..].Order(StringComparer.Ordinal), subtitles);
    }

    [Fact]
    public void AVideoWithoutSubtitlesGetsNone()
    {
        var subtitles = MediaFiles.SubtitlesFor("Dune/Dune.mkv", ["Dune/Dune.mkv", "Dune/Dune.nfo", "Dune/Dune.txt", "Dune/poster.jpg"]);

        Assert.Empty(subtitles);
    }

    [Fact]
    public void SubtitlesNotNamedAfterAVideoBelongToNone()
    {
        string[] files =
        [
            "Dune/Dune.mkv",
            "Dune/English.srt",
            "Dune/Subs/2_English.srt",
            "Dune/Dune2.srt",
            "Other/Dune.srt",
            "Dune/Extras/Dune.srt",
            "Dune/Dune.sample.srt",
        ];

        Assert.Empty(MediaFiles.SubtitlesFor("Dune/Dune.mkv", files));
    }

    [Fact]
    public void EachEpisodeGetsOnlyItsOwnSubtitles()
    {
        string[] files =
        [
            "Show.S01/Show.S01E01.mkv",
            "Show.S01/Show.S01E02.mkv",
            "Show.S01/Show.S01E01.en.srt",
            "Show.S01/Show.S01E02.en.srt",
            "Show.S01/SUBS/Show.S01E02.pt.srt",
        ];

        Assert.Equal(["Show.S01/Show.S01E01.en.srt"], MediaFiles.SubtitlesFor("Show.S01/Show.S01E01.mkv", files));
        Assert.Equal(
            ["Show.S01/SUBS/Show.S01E02.pt.srt", "Show.S01/Show.S01E02.en.srt"],
            MediaFiles.SubtitlesFor("Show.S01/Show.S01E02.mkv", files));
    }

    [Fact]
    public void AnAmbiguousSubtitleBelongsToNone()
    {
        string[] files = ["Dune/Dune.mkv", "Dune/Dune.mp4", "Dune/Dune.en.srt"];

        Assert.Empty(MediaFiles.SubtitlesFor("Dune/Dune.mkv", files));
        Assert.Empty(MediaFiles.SubtitlesFor("Dune/Dune.mp4", files));
    }

    [Fact]
    public void TheLongestMatchingVideoNameWins()
    {
        string[] files = ["Dune/Dune.mkv", "Dune/Dune.Extended.mkv", "Dune/Dune.en.srt", "Dune/Dune.Extended.en.srt"];

        Assert.Equal(["Dune/Dune.en.srt"], MediaFiles.SubtitlesFor("Dune/Dune.mkv", files));
        Assert.Equal(["Dune/Dune.Extended.en.srt"], MediaFiles.SubtitlesFor("Dune/Dune.Extended.mkv", files));
    }
}
