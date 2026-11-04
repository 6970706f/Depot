using Depot.Application.Organization;
using Depot.Domain.Organization;

namespace Depot.Infrastructure.Organization;

// Defaults from configuration ("Organization" section, or environment variables such as
// Organization__MoviesPath). They apply until settings are saved in the app; saved settings
// win from then on.
public sealed class OrganizationOptions
{
    public const string SectionName = "Organization";

    public string MoviesPath { get; init; } = "~/media/movies";

    public string SeriesPath { get; init; } = "~/media/series";

    public ImportMode ImportMode { get; init; } = ImportMode.Auto;

    public int PollingIntervalSeconds { get; init; } = 60;

    public List<PathMappingOptions> PathMappings { get; init; } = [];
}

public sealed class PathMappingOptions
{
    public string? QbittorrentPath { get; init; }

    public string? DepotPath { get; init; }
}

public sealed class SystemPathEnvironment : IPathEnvironment
{
    // $HOME on Linux. Empty when unset, in which case "~/" paths are rejected with a message.
    public string HomeDirectory { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
