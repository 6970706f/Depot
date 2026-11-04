using Depot.Domain.Common;

namespace Depot.Domain.Organization;

// Where organized media goes and how it gets there. Paths are kept as configured, so a
// "~/..." path keeps following the home folder of whoever runs Depot.
public sealed class OrganizationSettings
{
    public const int MinPollingIntervalSeconds = 10;
    public const int MaxPollingIntervalSeconds = 3600;

    private OrganizationSettings(
        string moviesPath,
        string seriesPath,
        ImportMode importMode,
        int pollingIntervalSeconds,
        IReadOnlyList<PathMapping> pathMappings)
    {
        MoviesPath = moviesPath;
        SeriesPath = seriesPath;
        ImportMode = importMode;
        PollingIntervalSeconds = pollingIntervalSeconds;
        PathMappings = pathMappings;
    }

    public string MoviesPath { get; }

    public string SeriesPath { get; }

    public ImportMode ImportMode { get; }

    public int PollingIntervalSeconds { get; }

    public IReadOnlyList<PathMapping> PathMappings { get; }

    public static OrganizationSettings Create(
        string? moviesPath,
        string? seriesPath,
        ImportMode importMode,
        int pollingIntervalSeconds,
        IReadOnlyList<(string? QbittorrentPath, string? DepotPath)> pathMappings,
        string homeDirectory)
    {
        var movies = LibraryPaths.Resolve(moviesPath, homeDirectory, "moviesPath", "movies folder");
        var series = LibraryPaths.Resolve(seriesPath, homeDirectory, "seriesPath", "series folder");
        if (LibraryPaths.IsSameOrInside(movies, series) || LibraryPaths.IsSameOrInside(series, movies))
        {
            throw new DomainValidationException(
                "seriesPath", "The movies and series folders must be separate: neither can be the same as, or inside, the other.");
        }

        if (!Enum.IsDefined(importMode))
        {
            throw new DomainValidationException("importMode", "Choose Auto, Copy or Hardlink.");
        }

        if (pollingIntervalSeconds is < MinPollingIntervalSeconds or > MaxPollingIntervalSeconds)
        {
            throw new DomainValidationException(
                "pollingIntervalSeconds",
                $"Choose an interval between {MinPollingIntervalSeconds} and {MaxPollingIntervalSeconds} seconds.");
        }

        var mappings = new List<PathMapping>();
        for (var index = 0; index < pathMappings.Count; index++)
        {
            var mapping = PathMapping.Create(pathMappings[index].QbittorrentPath, pathMappings[index].DepotPath, index);
            LibraryPaths.Resolve(mapping.DepotPath, homeDirectory, $"pathMappings[{index}].depotPath", "folder as Depot sees it");
            if (mappings.Any(existing => existing.QbittorrentPath == mapping.QbittorrentPath))
            {
                throw new DomainValidationException($"pathMappings[{index}].qbittorrentPath", "This qBittorrent folder is already mapped.");
            }

            mappings.Add(mapping);
        }

        return new OrganizationSettings(
            moviesPath!.Trim(), seriesPath!.Trim(), importMode, pollingIntervalSeconds, mappings);
    }

    // The absolute folders these settings point to for the given home folder.
    public ResolvedOrganizationPaths Resolve(string homeDirectory) => new(
        LibraryPaths.Resolve(MoviesPath, homeDirectory, "moviesPath", "movies folder"),
        LibraryPaths.Resolve(SeriesPath, homeDirectory, "seriesPath", "series folder"),
        new PathMapper(PathMappings.Select(mapping => (
            mapping.QbittorrentPath,
            LibraryPaths.Resolve(mapping.DepotPath, homeDirectory, "depotPath", "folder as Depot sees it")))));
}

public sealed record ResolvedOrganizationPaths(string MoviesRoot, string SeriesRoot, PathMapper Sources)
{
    public string LibraryRootFor(MediaType type) => type == MediaType.Movie ? MoviesRoot : SeriesRoot;
}
