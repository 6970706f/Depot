using Depot.Domain.Common;

namespace Depot.Domain.Organization;

public enum VideoKind
{
    // The movie itself, or an episode of a series (season 0 holds the specials).
    Main,
    // A trailer, featurette or other bonus video, kept in one of Jellyfin's extras folders.
    Extra,
}

// Where a video goes inside its movie or series folder. Season is set for an episode;
// ExtrasFolder for an extra, and is one of ExtrasFolders.Names.
public sealed record VideoPlacement(VideoKind Kind, int? Season, string? ExtrasFolder)
{
    public static VideoPlacement Movie { get; } = new(VideoKind.Main, null, null);

    public static VideoPlacement Episode(int season) => new(VideoKind.Main, season, null);

    public static VideoPlacement Extra(string extrasFolder) => new(VideoKind.Extra, null, extrasFolder);

    // A placement chosen by a person for a video of a series. kind is "main" (an episode, or a
    // special with season 0) or "extra".
    public static VideoPlacement ForSeries(string? kind, int? season, string? extrasFolder)
    {
        switch (kind?.Trim().ToLowerInvariant())
        {
            case "main":
                if (season is not { } number || number is < 0 or > LibraryTarget.MaxSeason)
                {
                    throw new DomainValidationException("season", $"Enter the season number, from 0 (specials) to {LibraryTarget.MaxSeason}.");
                }

                return Episode(number);
            case "extra":
                var folder = ExtrasFolders.Names.FirstOrDefault(name => name.Equals(extrasFolder?.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new DomainValidationException("extrasFolder", $"Choose one of: {string.Join(", ", ExtrasFolders.Names)}.");
                return Extra(folder);
            default:
                throw new DomainValidationException("kind", "Choose an episode (main) or an extra.");
        }
    }
}

// The folders Jellyfin reads extras from, inside a movie or series folder.
public static class ExtrasFolders
{
    public const string Default = "extras";

    public static readonly IReadOnlyList<string> Names =
        [Default, "featurettes", "behind the scenes", "deleted scenes", "trailers", "interviews"];
}
