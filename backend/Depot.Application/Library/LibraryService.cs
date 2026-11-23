using Depot.Domain.Library;

namespace Depot.Application.Library;

public sealed record LibrarySeriesView(string Name, IReadOnlyList<int> Seasons);

// What's already in the library, so a person can add a download to a series they have.
public sealed class LibraryService(ILibrarySettingsRepository settingsRepository, ILibraryFileSystem fileSystem, IPathEnvironment environment)
{
    // Every folder in the series library with the season folders found in it. Empty when the
    // series folder doesn't exist yet.
    public async Task<IReadOnlyList<LibrarySeriesView>> ListSeriesAsync(CancellationToken cancellationToken)
    {
        var paths = (await settingsRepository.GetAsync(cancellationToken)).Settings.Resolve(environment.HomeDirectory);
        return fileSystem.ListFolders(paths.SeriesRoot)
            .Select(name => new LibrarySeriesView(
                name,
                fileSystem.ListFolders(Path.Join(paths.SeriesRoot, name))
                    .Select(SeasonFolders.Parse)
                    .OfType<int>()
                    .Distinct()
                    .Order()
                    .ToList()))
            .ToList();
    }
}
