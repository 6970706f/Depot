using System.Globalization;
using System.Text.RegularExpressions;

namespace Depot.Domain.Organization;

public sealed record SeriesVideo(string RelativePath, long SizeBytes);

// Where a video should go, and why a person must confirm it first. ReviewReason is null when the
// download's layout leaves no doubt.
public sealed record VideoClassification(VideoPlacement Placement, string? ReviewReason)
{
    public bool NeedsReview => ReviewReason is not null;
}

// Sorts the videos of a finished series download into episodes, specials and extras. A person
// chose the series and season when adding it, and that choice stands unless the download's
// layout says otherwise. The layout is read from structure first: an extras folder, a season
// folder, an episode number. Words in a name ("trailer") and sizes only ever lead to a
// suggestion, never to a decision: whenever the clues disagree, or point away from the chosen
// season, the video waits for a person instead of being imported.
public static partial class SeriesVideos
{
    // Below this share of the episodes' typical size, an unnumbered video looks like an extra.
    public const double SmallShare = 0.4;

    // Folder names releases use for bonus videos, and the Jellyfin folder each maps to.
    private static readonly IReadOnlyDictionary<string, string> ExtrasFolderNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["extras"] = ExtrasFolders.Default,
        ["extra"] = ExtrasFolders.Default,
        ["bonus"] = ExtrasFolders.Default,
        ["bonus features"] = ExtrasFolders.Default,
        ["special features"] = ExtrasFolders.Default,
        ["featurettes"] = "featurettes",
        ["featurette"] = "featurettes",
        ["behind the scenes"] = "behind the scenes",
        ["making of"] = "behind the scenes",
        ["deleted scenes"] = "deleted scenes",
        ["trailers"] = "trailers",
        ["trailer"] = "trailers",
        ["interviews"] = "interviews",
    };

    // Words in a file name that suggest a bonus video, with the Jellyfin folder each suggests.
    private static readonly IReadOnlyList<ExtraWord> ExtraWords =
    [
        new("trailer", "trailers"),
        new("teaser", "trailers"),
        new("featurette", "featurettes"),
        new("behind the scenes", "behind the scenes"),
        new("making of", "behind the scenes"),
        new("deleted scene", "deleted scenes"),
        new("deleted scenes", "deleted scenes"),
        new("interview", "interviews"),
        new("bloopers", ExtrasFolders.Default),
        new("gag reel", ExtrasFolders.Default),
    ];

    public static IReadOnlyDictionary<string, VideoClassification> Classify(IReadOnlyList<SeriesVideo> videos, int chosenSeason)
    {
        var clues = videos.ToDictionary(video => video.RelativePath, video => Read(video.RelativePath), StringComparer.Ordinal);
        var episodeSizes = videos
            .Where(video => clues[video.RelativePath] is { IsNumbered: true, ExtrasFolder: null })
            .Select(video => video.SizeBytes)
            .Order()
            .ToList();
        var typicalEpisode = episodeSizes.Count == 0 ? (long?)null : episodeSizes[episodeSizes.Count / 2];

        return videos.ToDictionary(
            video => video.RelativePath,
            video => Decide(clues[video.RelativePath], video.SizeBytes, chosenSeason, typicalEpisode),
            StringComparer.Ordinal);
    }

    private static VideoClassification Decide(Clues clues, long size, int chosenSeason, long? typicalEpisode)
    {
        if (clues.ExtrasFolder is { } extrasFolder)
        {
            return clues.IsNumbered
                ? Review(VideoPlacement.Extra(extrasFolder), "It's in an extras folder, but it's numbered like an episode.")
                : Sure(VideoPlacement.Extra(extrasFolder));
        }

        if (clues is { NameSeason: { } named, FolderSeason: { } folder } && named != folder)
        {
            return Review(VideoPlacement.Episode(named), $"Its name says {Season(named)}, but its folder says {Season(folder)}.");
        }

        var season = clues.NameSeason ?? clues.FolderSeason;
        if (clues.IsNumbered)
        {
            var episode = VideoPlacement.Episode(season ?? chosenSeason);
            if (clues.ExtraWord is { } word)
            {
                return Review(episode, $"It's numbered like an episode, but its name mentions \"{word.Words}\".");
            }

            return season is { } found && found != chosenSeason
                ? Review(episode, $"It's numbered as {Season(found)}, but {Season(chosenSeason)} was chosen when adding the download.")
                : Sure(episode);
        }

        // Without an episode number, the chosen season stands only if nothing in the download
        // suggests otherwise, as when every video is named freely.
        var small = typicalEpisode is { } typical && size < typical * SmallShare;
        if (typicalEpisode is null && clues.ExtraWord is null && (season ?? chosenSeason) == chosenSeason)
        {
            return Sure(VideoPlacement.Episode(chosenSeason));
        }

        var suggestion = clues.ExtraWord is not null || small
            ? VideoPlacement.Extra(clues.ExtraWord?.Folder ?? ExtrasFolders.Default)
            : VideoPlacement.Episode(season ?? chosenSeason);
        if (clues.ExtraWord is { } extraWord)
        {
            return Review(suggestion, $"Its name mentions \"{extraWord.Words}\" and it has no episode number.");
        }

        if (typicalEpisode is not null)
        {
            return Review(suggestion, small
                ? "It has no episode number, unlike the other videos in this download, and it's much smaller than them."
                : "It has no episode number, unlike the other videos in this download.");
        }

        return Review(suggestion, $"It's in a folder for {Season(season!.Value)}, but {Season(chosenSeason)} was chosen when adding the download.");
    }

    private static Clues Read(string relativePath)
    {
        var folders = MediaFiles.Folders(relativePath).Select(Normalize).ToList();
        var extrasFolder = folders.Select(folder => ExtrasFolderNames.GetValueOrDefault(folder)).FirstOrDefault(name => name is not null);
        var folderSeason = folders.Select(SeasonOfFolder).LastOrDefault(season => season is not null);

        var stem = MediaFiles.Stem(relativePath);
        int? nameSeason = null;
        var numbered = false;
        if (SeasonAndEpisode().Match(stem) is { Success: true } match)
        {
            nameSeason = int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture);
            numbered = true;
        }
        else
        {
            numbered = EpisodeOnly().IsMatch(stem);
        }

        var words = $" {Normalize(stem)} ";
        var extraWord = ExtraWords.FirstOrDefault(entry => words.Contains($" {entry.Words} ", StringComparison.Ordinal));
        return new Clues(extrasFolder, folderSeason, nameSeason, numbered, extraWord);
    }

    // "Season 2", "Season 02", "S02", "Specials" or "Season 00"; null for any other folder.
    private static int? SeasonOfFolder(string normalizedFolder)
    {
        if (normalizedFolder == "specials")
        {
            return 0;
        }

        var match = SeasonFolder().Match(normalizedFolder);
        return match.Success && int.TryParse(match.Groups["season"].Value, CultureInfo.InvariantCulture, out var season)
            && season <= LibraryTarget.MaxSeason
                ? season
                : null;
    }

    // Lowercase words separated by single spaces: "Behind.the_Scenes" is "behind the scenes".
    private static string Normalize(string name) => Separators().Replace(name.ToLowerInvariant(), " ").Trim();

    private static string Season(int season) => season == 0 ? "the specials (season 0)" : $"season {season}";

    private static VideoClassification Sure(VideoPlacement placement) => new(placement, null);

    private static VideoClassification Review(VideoPlacement placement, string reason) => new(placement, reason);

    private sealed record Clues(string? ExtrasFolder, int? FolderSeason, int? NameSeason, bool IsNumbered, ExtraWord? ExtraWord);

    private sealed record ExtraWord(string Words, string Folder);

    // S01E02, s1e2, S01.E02, 1x02.
    [GeneratedRegex(@"(?<![a-z0-9])(?:s(?<season>\d{1,3})[ ._-]?e\d{1,4}|(?<season>\d{1,2})x\d{2,3}(?![a-z0-9]))", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonAndEpisode();

    // E02, Ep 2, Episode 2, "Show - 02", "02 - Title".
    [GeneratedRegex(@"(?<![a-z0-9])(?:e|ep|episode)[ ._-]?\d{1,4}(?![a-z0-9])|\s-\s\d{1,4}(?!\d)|^\d{1,3}(?=[ ._-])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeOnly();

    [GeneratedRegex(@"^(?:season|series|saison|staffel|temporada)\s*(?<season>\d{1,3})$|^s(?<season>\d{1,3})$")]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"[\s._\-\[\]()]+")]
    private static partial Regex Separators();
}
