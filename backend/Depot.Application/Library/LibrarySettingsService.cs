using Depot.Domain.Library;

namespace Depot.Application.Library;

public sealed record PathMappingInput(string? QbittorrentPath, string? DepotPath);

public sealed record LibrarySettingsInput(
    string? MoviesPath,
    string? SeriesPath,
    ImportMode ImportMode,
    int PollingIntervalSeconds,
    IReadOnlyList<PathMappingInput>? PathMappings);

public sealed record LibrarySettingsView(
    string MoviesPath,
    string SeriesPath,
    ImportMode ImportMode,
    int PollingIntervalSeconds,
    IReadOnlyList<PathMappingInput> PathMappings,
    bool IsSaved,
    LibraryCheckView Check);

public sealed class LibrarySettingsService(
    ILibrarySettingsRepository repository,
    LibraryCheckService checker,
    IPathEnvironment environment,
    IEnumerable<ILibrarySettingsListener> listeners)
{
    public async Task<LibrarySettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await repository.GetAsync(cancellationToken);
        return ToView(stored.Settings, stored.IsSaved);
    }

    // Saves even when a folder check fails, so settings can be prepared before the folders
    // exist; no import runs until every check passes.
    public async Task<LibrarySettingsView> SaveAsync(LibrarySettingsInput input, CancellationToken cancellationToken)
    {
        var settings = Create(input);
        await repository.SaveAsync(settings, cancellationToken);
        foreach (var listener in listeners)
        {
            listener.LibrarySettingsSaved();
        }

        return ToView(settings, isSaved: true);
    }

    // Checks the folders of settings that aren't saved yet.
    public LibraryCheckView Check(LibrarySettingsInput input) => checker.Check(Create(input));

    private LibrarySettings Create(LibrarySettingsInput input) => LibrarySettings.Create(
        input.MoviesPath,
        input.SeriesPath,
        input.ImportMode,
        input.PollingIntervalSeconds,
        (input.PathMappings ?? []).Select(mapping => (mapping.QbittorrentPath, mapping.DepotPath)).ToList(),
        environment.HomeDirectory);

    private LibrarySettingsView ToView(LibrarySettings settings, bool isSaved) => new(
        settings.MoviesPath,
        settings.SeriesPath,
        settings.ImportMode,
        settings.PollingIntervalSeconds,
        settings.PathMappings.Select(mapping => new PathMappingInput(mapping.QbittorrentPath, mapping.DepotPath)).ToList(),
        isSaved,
        checker.Check(settings));
}

// Told when the library settings change, such as the import worker, which runs a pass at once.
public interface ILibrarySettingsListener
{
    void LibrarySettingsSaved();
}
