using Depot.Domain.Organization;

namespace Depot.Application.Organization;

// What's already in the library, so a person can add a download to a series they have.
public sealed class LibraryService(
    IOrganizationSettingsRepository settingsRepository,
    IMediaOrganizer organizer,
    IPathEnvironment environment)
{
    // Every folder in the series library, by name, with the season folders found in it.
    // Empty when the series folder doesn't exist yet.
    public async Task<IReadOnlyList<LibrarySeriesView>> ListSeriesAsync(CancellationToken cancellationToken)
    {
        var paths = (await settingsRepository.GetAsync(cancellationToken)).Settings.Resolve(environment.HomeDirectory);
        var series = new List<LibrarySeriesView>();
        foreach (var name in await organizer.ListFoldersAsync(paths.SeriesRoot, cancellationToken))
        {
            var seasons = (await organizer.ListFoldersAsync(Path.Join(paths.SeriesRoot, name), cancellationToken))
                .Select(SeasonFolders.Parse)
                .OfType<int>()
                .Distinct()
                .Order()
                .ToList();
            series.Add(new LibrarySeriesView(name, seasons));
        }

        return series.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
