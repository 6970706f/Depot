namespace Depot.Domain.Organization;

// Which files of a torrent are videos. Paths are torrent-relative and "/"-separated.
public static class MediaFiles
{
    // The only list to extend when a new format should be imported.
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".wmv", ".webm", ".mpg", ".mpeg", ".ts", ".m2ts",
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
