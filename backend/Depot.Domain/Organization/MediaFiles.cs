namespace Depot.Domain.Organization;

// Which files of a torrent are videos. Paths are torrent-relative and "/"-separated.
public static class MediaFiles
{
    // The only list to extend when a new format should be imported.
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".wmv", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts",
    };

    // External subtitle formats Jellyfin reads next to a video.
    public static readonly IReadOnlySet<string> SubtitleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".smi",
    };

    // Folders right below a video's folder that releases keep its subtitles in.
    private static readonly IReadOnlySet<string> SubtitleFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "subs", "subtitles",
    };

    private static readonly IReadOnlySet<string> SampleFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "sample", "samples",
    };

    public static bool IsVideo(string relativePath) => VideoExtensions.Contains(Extension(relativePath));

    // A short preview shipped with a release: "movie-sample.mkv" or anything in a "Sample" folder.
    public static bool IsSample(string relativePath) =>
        Folders(relativePath).Any(SampleFolders.Contains)
        || Stem(relativePath)
            .Split(['.', ' ', '-', '_', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Equals("sample", StringComparison.OrdinalIgnoreCase));

    public static bool IsSubtitle(string relativePath) => SubtitleExtensions.Contains(Extension(relativePath));

    // The subtitles among files that belong to video: in its folder, or in a Subs folder right
    // below it, and named after it ("Dune.srt", "Dune.en.srt", "Dune.pt-BR.forced.ass"), which
    // is also how Jellyfin pairs them. Being in the same folder isn't enough: "English.srt" next
    // to a video belongs to none. A subtitle named after more than one video goes to the longest
    // name ("Dune.Extended.srt" to "Dune.Extended.mkv", not "Dune.mkv"), and to none on a tie
    // ("Dune.srt" next to "Dune.mkv" and "Dune.mp4").
    public static IReadOnlyList<string> SubtitlesFor(string video, IEnumerable<string> files)
    {
        var all = files.ToList();
        var videos = all.Where(IsVideo).ToList();
        return all
            .Where(file => IsSubtitle(file) && !IsSample(file))
            .Where(subtitle =>
            {
                var owners = videos.Where(candidate => Owns(candidate, subtitle)).ToList();
                if (owners.Count == 0)
                {
                    return false;
                }

                var longest = owners.Max(owner => Stem(owner).Length);
                var best = owners.Where(owner => Stem(owner).Length == longest).ToList();
                return best.Count == 1 && best[0] == video;
            })
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static bool Owns(string video, string subtitle)
    {
        var videoFolders = Folders(video);
        var subtitleFolders = Folders(subtitle);
        var besideIt = subtitleFolders.SequenceEqual(videoFolders, StringComparer.Ordinal);
        var inItsSubsFolder = subtitleFolders.Count == videoFolders.Count + 1
            && SubtitleFolders.Contains(subtitleFolders[^1])
            && subtitleFolders.Take(videoFolders.Count).SequenceEqual(videoFolders, StringComparer.Ordinal);
        return (besideIt || inItsSubsFolder) && FileName(subtitle).StartsWith(Stem(video) + ".", StringComparison.Ordinal);
    }

    public static string FileName(string relativePath) => relativePath[(relativePath.LastIndexOf('/') + 1)..];

    // The file name without its extension: "Dune.2021" for "Movie/Dune.2021.mkv".
    public static string Stem(string relativePath)
    {
        var name = FileName(relativePath);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    // Lowercase, with the dot: ".mkv". Empty when the file has none.
    public static string Extension(string relativePath)
    {
        var name = FileName(relativePath);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[dot..].ToLowerInvariant() : string.Empty;
    }

    // The folders above the file, outermost first.
    public static IReadOnlyList<string> Folders(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments[..^1];
    }
}
