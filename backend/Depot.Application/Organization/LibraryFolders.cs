using Depot.Domain.Organization;

namespace Depot.Application.Organization;

// Works out the folder a video goes to from the library choice, its placement and what's
// already on disk: a movie gets "<Title>", an episode "<Title>/<season folder>", reusing a
// season folder the series already has ("Season 1" stays "Season 1") and naming a new one
// "Season 01", and a series extra "<Title>/<extras folder>", where Jellyfin looks for extras.
// Folder listings are remembered, so one instance can serve a whole list of jobs.
public sealed class LibraryFolders(IMediaOrganizer organizer, ResolvedOrganizationPaths paths)
{
    private readonly Dictionary<string, IReadOnlyList<string>> listings = new(StringComparer.Ordinal);

    public ResolvedOrganizationPaths Paths => paths;

    public string RootFor(LibraryTarget target) => paths.LibraryRootFor(target.Type);

    // The folder for the target, relative to its library root and "/"-separated.
    public async Task<string> RelativeFolderAsync(LibraryTarget target, VideoPlacement placement, CancellationToken cancellationToken)
    {
        if (target.Type == MediaType.Movie)
        {
            return target.Title;
        }

        if (placement is { Kind: VideoKind.Extra, ExtrasFolder: { } extrasFolder })
        {
            return $"{target.Title}/{extrasFolder}";
        }

        var wanted = placement.Season ?? target.Season;
        var seriesFolder = Path.Join(paths.SeriesRoot, target.Title);
        if (!listings.TryGetValue(seriesFolder, out var folders))
        {
            folders = await organizer.ListFoldersAsync(seriesFolder, cancellationToken);
            listings[seriesFolder] = folders;
        }

        var season = folders.FirstOrDefault(folder => SeasonFolders.Parse(folder) == wanted)
            ?? SeasonFolders.NameFor(wanted!.Value);
        return $"{target.Title}/{season}";
    }
}
