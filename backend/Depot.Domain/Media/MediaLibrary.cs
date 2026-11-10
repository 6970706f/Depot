namespace Depot.Domain.Media;

public enum MediaLibraryKind
{
    Movies,
    Shows,
    // Movies and shows together.
    Mixed,
    // Music, books, photos and the like: listed, but their items aren't kept.
    Other,
}

// A library of the media server, as its source reports it. ExternalId is the source's id for it.
public sealed record MediaLibrary(string ExternalId, string Name, MediaLibraryKind Kind, IReadOnlyList<string> Locations)
{
    public bool IsSynced => Kind is not MediaLibraryKind.Other;

    // The kinds of item Depot keeps for this library.
    public IReadOnlyList<MediaItemKind> ItemKinds => Kind switch
    {
        MediaLibraryKind.Movies => [MediaItemKind.Movie],
        MediaLibraryKind.Shows => [MediaItemKind.Series, MediaItemKind.Season, MediaItemKind.Episode],
        MediaLibraryKind.Mixed => [MediaItemKind.Movie, MediaItemKind.Series, MediaItemKind.Season, MediaItemKind.Episode],
        _ => [],
    };
}
