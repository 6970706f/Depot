using Depot.Application.Library;
using Depot.Domain.Library;
using Depot.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Depot.Infrastructure.Library;

public sealed class LibrarySettingsRepository(SettingsStore store, IOptions<LibraryOptions> defaults, IPathEnvironment environment)
    : ILibrarySettingsRepository
{
    private const string Key = "library";

    public async Task<StoredLibrarySettings> GetAsync(CancellationToken cancellationToken)
    {
        var document = await store.GetAsync<Document>(Key, cancellationToken);
        if (document is not null)
        {
            return new StoredLibrarySettings(ToSettings(document), IsSaved: true);
        }

        var options = defaults.Value;
        return new StoredLibrarySettings(
            ToSettings(new Document(options.MoviesPath, options.SeriesPath, options.ImportMode, options.PollingIntervalSeconds, options.PathMappings)),
            IsSaved: false);
    }

    public Task SaveAsync(LibrarySettings settings, CancellationToken cancellationToken) =>
        store.SaveAsync(Key, new Document(
            settings.MoviesPath,
            settings.SeriesPath,
            settings.ImportMode,
            settings.PollingIntervalSeconds,
            settings.PathMappings.Select(mapping => new PathMappingInput(mapping.QbittorrentPath, mapping.DepotPath)).ToList()),
            cancellationToken);

    private LibrarySettings ToSettings(Document document) => LibrarySettings.Create(
        document.MoviesPath,
        document.SeriesPath,
        document.ImportMode,
        document.PollingIntervalSeconds,
        document.PathMappings.Select(mapping => (mapping.QbittorrentPath, mapping.DepotPath)).ToList(),
        environment.HomeDirectory);

    private sealed record Document(
        string MoviesPath,
        string SeriesPath,
        ImportMode ImportMode,
        int PollingIntervalSeconds,
        List<PathMappingInput> PathMappings);
}
