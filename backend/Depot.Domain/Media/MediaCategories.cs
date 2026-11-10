namespace Depot.Domain.Media;

// Categories that the source has no type for, worked out from the metadata it does have.
public static class MediaCategories
{
    // Anime is a movie or series that:
    //  - is in a library whose name says so ("Anime", "Anime Movies"...), the usual way people
    //    keep anime apart in Jellyfin;
    //  - or has "Anime" as a genre or a keyword, as anime metadata providers and TMDB keywords set;
    //  - or is animation made in Japan (by country of production, or an original title in
    //    Japanese script).
    // Animation alone isn't anime: a Pixar film is animation from elsewhere.
    public static bool IsAnime(MediaItem item, MediaLibrary library)
    {
        if (item.Kind is MediaItemKind.Collection)
        {
            return false;
        }

        if (library.Name.Contains("anime", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (item.Genres.Concat(item.Tags).Any(word => word.Equals("anime", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var isAnimation = item.Genres.Any(genre => genre.Equals("Animation", StringComparison.OrdinalIgnoreCase));
        return isAnimation
            && (item.ProductionLocations.Any(country => country.Equals("Japan", StringComparison.OrdinalIgnoreCase))
                || IsJapanese(item.OriginalTitle));
    }

    // Kana (both syllabaries) only: kanji alone could just as well be Chinese.
    private static bool IsJapanese(string? text) =>
        text is not null && text.Any(character => character is >= '぀' and <= 'ヿ');
}
