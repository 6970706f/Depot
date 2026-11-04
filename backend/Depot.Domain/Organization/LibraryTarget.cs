using Depot.Domain.Common;

namespace Depot.Domain.Organization;

// Where a download goes in the library, chosen by a person when adding it. Title is the folder
// name: the movie's folder, or the series' folder. Season is set only for a series. The videos
// keep their own file names; Depot never derives anything from them.
public sealed record LibraryTarget(MediaType Type, string Title, int? Season)
{
    public const int MaxSeason = 999;

    // Null for "other": the download stays where qBittorrent saves it and is never organized.
    public static LibraryTarget? Create(string? mediaType, string? title, int? season)
    {
        var type = mediaType?.Trim().ToLowerInvariant() switch
        {
            null or "" or "other" => (MediaType?)null,
            "movie" => MediaType.Movie,
            "series" => MediaType.Series,
            _ => throw new DomainValidationException("mediaType", "Choose Movie, Series or Other."),
        };
        if (type is null)
        {
            return null;
        }

        var name = LibraryFolderName.Validate(title, "title", type == MediaType.Movie ? "the movie's name" : "the series' name");
        if (type == MediaType.Movie)
        {
            return new LibraryTarget(MediaType.Movie, name, null);
        }

        if (season is not { } number || number is < 0 or > MaxSeason)
        {
            throw new DomainValidationException("season", $"Enter the season number, from 0 (specials) to {MaxSeason}.");
        }

        return new LibraryTarget(MediaType.Series, name, number);
    }
}

// A folder name typed by a person, used as one folder inside a library.
public static class LibraryFolderName
{
    public const int MaxLength = 200;

    public static string Validate(string? text, string field, string description)
    {
        var name = text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw new DomainValidationException(field, $"Enter {description}.");
        }

        if (name.Length > MaxLength)
        {
            throw new DomainValidationException(field, $"Use at most {MaxLength} characters.");
        }

        if (name.IndexOfAny(['/', '\\']) >= 0 || name.Any(char.IsControl))
        {
            throw new DomainValidationException(field, "A folder name can't contain / or \\.");
        }

        // Hidden names would be invisible to Jellyfin, and "." or ".." would leave the library.
        if (name.StartsWith('.'))
        {
            throw new DomainValidationException(field, "A folder name can't start with a dot.");
        }

        return name;
    }
}
