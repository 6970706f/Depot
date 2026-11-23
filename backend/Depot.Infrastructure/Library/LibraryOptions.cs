using Depot.Application.Library;
using Depot.Domain.Library;

namespace Depot.Infrastructure.Library;

// Defaults from configuration ("Library" section, or environment variables such as
// Library__MoviesPath). They apply until settings are saved in the app; saved settings win.
public sealed class LibraryOptions
{
    public const string SectionName = "Library";

    public string MoviesPath { get; init; } = "~/media/movies";

    public string SeriesPath { get; init; } = "~/media/series";

    public ImportMode ImportMode { get; init; } = ImportMode.Auto;

    public int PollingIntervalSeconds { get; init; } = 60;

    public List<PathMappingInput> PathMappings { get; init; } = [];
}

public sealed class SystemPathEnvironment : IPathEnvironment
{
    public string HomeDirectory { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
