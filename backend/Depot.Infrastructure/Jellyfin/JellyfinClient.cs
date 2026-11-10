using Depot.Application.Jellyfin;
using Depot.Application.Settings;
using Depot.Domain.Media;
using Depot.Domain.Settings;

namespace Depot.Infrastructure.Jellyfin;

// The Jellyfin REST API with the saved settings. Items are read with /Items, which takes an
// API key without a user: filtered by library (ParentId) and type, or by id.
public sealed class JellyfinClient(JellyfinHttp http, IJellyfinSettingsRepository settingsRepository) : IJellyfinClient
{
    public const int MaxPageSize = 500;

    // Everything Depot keeps of an item. MediaSources carries each file with its streams.
    private const string ItemFields = "ProviderIds,Overview,Genres,Studios,DateCreated,Path,MediaSources,OriginalTitle,SortName";
    private const string ImageTypes = "Primary,Backdrop,Thumb,Logo";

    public async Task<JellyfinServerInfo> GetServerInfoAsync(CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(cancellationToken);
        using var document = await http.GetJsonAsync(settings, "System/Info", authenticated: true, cancellationToken);
        return JellyfinResponses.ReadServerInfo(document.RootElement, settings);
    }

    public async Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(cancellationToken);
        using var document = await http.GetJsonAsync(settings, "Library/VirtualFolders", authenticated: true, cancellationToken);
        return JellyfinResponses.ReadLibraries(document.RootElement, settings);
    }

    public async Task<JellyfinItemPage> GetItemsAsync(
        string libraryId,
        IReadOnlyCollection<MediaItemKind> kinds,
        int startIndex,
        int limit,
        CancellationToken cancellationToken)
    {
        var parentId = JellyfinResponses.NormalizeId(libraryId) ?? throw new ArgumentException("Not a Jellyfin id.", nameof(libraryId));
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);
        if (kinds.Count == 0)
        {
            throw new ArgumentException("At least one kind of item.", nameof(kinds));
        }

        var settings = await SettingsAsync(cancellationToken);
        // Sorted by date added, so items added while the library is read go to the end instead
        // of shifting the pages not read yet. Box sets are never collapsed: they'd hide movies.
        var query = string.Join('&',
            $"ParentId={parentId}",
            "Recursive=true",
            $"IncludeItemTypes={string.Join(',', kinds.Select(JellyfinResponses.ItemType))}",
            $"Fields={ItemFields}",
            $"EnableImageTypes={ImageTypes}",
            "EnableUserData=false",
            "EnableTotalRecordCount=true",
            "CollapseBoxSetItems=false",
            "SortBy=DateCreated,SortName",
            "SortOrder=Ascending",
            $"StartIndex={startIndex}",
            $"Limit={limit}");
        using var document = await http.GetJsonAsync(settings, $"Items?{query}", authenticated: true, cancellationToken);
        return JellyfinResponses.ReadItemPage(document.RootElement, settings);
    }

    public async Task<IReadOnlyList<MediaItem>> GetItemsByIdAsync(IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken)
    {
        var ids = itemIds.Select(JellyfinResponses.NormalizeId).OfType<string>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var settings = await SettingsAsync(cancellationToken);
        var query = string.Join('&',
            $"Ids={string.Join(',', ids)}",
            $"Fields={ItemFields}",
            $"EnableImageTypes={ImageTypes}",
            "EnableUserData=false",
            "CollapseBoxSetItems=false");
        using var document = await http.GetJsonAsync(settings, $"Items?{query}", authenticated: true, cancellationToken);
        var requested = ids.ToHashSet(StringComparer.Ordinal);
        // Only the items asked for: a server that answers with others can't make Depot keep them.
        return JellyfinResponses.ReadItemPage(document.RootElement, settings).Items
            .Where(item => requested.Contains(item.ExternalId))
            .ToList();
    }

    public async Task<MediaImageContent?> GetImageAsync(
        string itemId,
        ImageKind kind,
        int index,
        string tag,
        int? maxWidth,
        CancellationToken cancellationToken)
    {
        var id = JellyfinResponses.NormalizeId(itemId) ?? throw new ArgumentException("Not a Jellyfin id.", nameof(itemId));
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var settings = await SettingsAsync(cancellationToken);
        var query = $"tag={Uri.EscapeDataString(tag)}&quality=90{(maxWidth is { } width ? $"&maxWidth={width}" : "")}";
        return await http.GetImageAsync(settings, $"Items/{id}/Images/{kind}/{index}?{query}", cancellationToken);
    }

    private async Task<JellyfinSettings> SettingsAsync(CancellationToken cancellationToken) =>
        await settingsRepository.GetAsync(cancellationToken)
        ?? throw new JellyfinException(JellyfinFailure.NotConfigured, "Jellyfin isn't set up yet. Add its URL and an API key in Settings.");
}

// Checks settings that may not be saved yet: first that the URL leads to Jellyfin (its public
// info needs no key), then that the key is accepted.
public sealed class JellyfinConnectionTester(JellyfinHttp http) : IJellyfinConnectionTester
{
    public async Task<JellyfinServerInfo> TestAsync(JellyfinSettings settings, CancellationToken cancellationToken)
    {
        using (var publicInfo = await http.GetJsonAsync(settings, "System/Info/Public", authenticated: false, cancellationToken))
        {
            JellyfinResponses.CheckPublicInfo(publicInfo.RootElement, settings);
        }

        using var info = await http.GetJsonAsync(settings, "System/Info", authenticated: true, cancellationToken);
        return JellyfinResponses.ReadServerInfo(info.RootElement, settings);
    }
}
