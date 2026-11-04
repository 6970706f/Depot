using System.Globalization;
using System.Text.RegularExpressions;

namespace Depot.Domain.Organization;

// The season folders inside a series folder, named the way Jellyfin reads them.
public static partial class SeasonFolders
{
    // The name for a season that has no folder yet: "Season 01".
    public static string NameFor(int season) => $"Season {season.ToString("00", CultureInfo.InvariantCulture)}";

    // The season of an existing folder named like "Season 1", "Season 01" or "Specials" (0).
    // Null for any other folder.
    public static int? Parse(string folderName)
    {
        var name = folderName.Trim();
        if (name.Equals("Specials", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var match = SeasonPattern().Match(name);
        return match.Success && int.TryParse(match.Groups["number"].Value, CultureInfo.InvariantCulture, out var season)
            && season <= LibraryTarget.MaxSeason
                ? season
                : null;
    }

    [GeneratedRegex(@"^Season\s*(?<number>\d{1,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPattern();
}
