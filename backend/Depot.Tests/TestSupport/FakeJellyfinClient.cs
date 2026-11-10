using System.Collections.Concurrent;
using Depot.Application.Jellyfin;
using Depot.Domain.Media;

namespace Depot.Tests.TestSupport;

// Stands in for Jellyfin: its libraries and items are whatever a test puts here. Pages are cut
// from the items in order, as Jellyfin does, and a test can make any request fail.
public sealed class FakeJellyfinClient : IJellyfinClient
{
    private readonly Lock gate = new();

    public List<MediaLibrary> Libraries { get; } = [];

    // Items by library external id, in the order pages are cut from.
    public Dictionary<string, List<MediaItem>> Items { get; } = [];

    // Images by "itemId/kind/index/tag".
    public ConcurrentDictionary<string, MediaImageContent> Images { get; } = [];

    // Every request, such as "libraries", "items lib-1 0" or "ids 3".
    public ConcurrentQueue<string> Requests { get; } = [];

    // Called before each page of items is answered; can throw or change the items.
    public Action<string, int>? BeforePage { get; set; }

    public JellyfinException? LibrariesFailure { get; set; }

    public JellyfinException? LookupFailure { get; set; }

    public int ImageRequests => Requests.Count(request => request.StartsWith("image", StringComparison.Ordinal));

    public MediaLibrary AddLibrary(string id, string name, MediaLibraryKind kind)
    {
        var library = new MediaLibrary(id, name, kind, [$"/media/{name.ToLowerInvariant()}"]);
        lock (gate)
        {
            Libraries.Add(library);
            Items[id] = [];
        }

        return library;
    }

    public MediaItem AddItem(string libraryId, MediaItem item)
    {
        lock (gate)
        {
            Items[libraryId].Add(item);
        }

        return item;
    }

    public void RemoveItem(string externalId)
    {
        lock (gate)
        {
            foreach (var items in Items.Values)
            {
                items.RemoveAll(item => item.ExternalId == externalId);
            }
        }
    }

    public void Replace(MediaItem item)
    {
        lock (gate)
        {
            foreach (var items in Items.Values)
            {
                var index = items.FindIndex(existing => existing.ExternalId == item.ExternalId);
                if (index >= 0)
                {
                    items[index] = item;
                }
            }
        }
    }

    public Task<JellyfinServerInfo> GetServerInfoAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new JellyfinServerInfo("Test server", "10.11.11"));

    public Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken cancellationToken)
    {
        Requests.Enqueue("libraries");
        if (LibrariesFailure is { } failure)
        {
            throw failure;
        }

        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<MediaLibrary>>(Libraries.ToList());
        }
    }

    public Task<JellyfinItemPage> GetItemsAsync(
        string libraryId,
        IReadOnlyCollection<MediaItemKind> kinds,
        int startIndex,
        int limit,
        CancellationToken cancellationToken)
    {
        Requests.Enqueue($"items {libraryId} {startIndex}");
        BeforePage?.Invoke(libraryId, startIndex);
        lock (gate)
        {
            var matching = Items[libraryId].Where(item => kinds.Contains(item.Kind)).ToList();
            var page = matching.Skip(startIndex).Take(limit).ToList();
            return Task.FromResult(new JellyfinItemPage(page, page.Count, 0, matching.Count));
        }
    }

    public Task<IReadOnlyList<MediaItem>> GetItemsByIdAsync(IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken)
    {
        Requests.Enqueue($"ids {itemIds.Count}");
        if (LookupFailure is { } failure)
        {
            throw failure;
        }

        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<MediaItem>>(
                Items.Values.SelectMany(items => items).Where(item => itemIds.Contains(item.ExternalId)).ToList());
        }
    }

    public Task<MediaImageContent?> GetImageAsync(
        string itemId,
        ImageKind kind,
        int index,
        string tag,
        int? maxWidth,
        CancellationToken cancellationToken)
    {
        Requests.Enqueue($"image {itemId} {kind} {index} {tag} {maxWidth}");
        return Task.FromResult(Images.TryGetValue($"{itemId}/{kind}/{index}/{tag}", out var image) ? image : null);
    }

    // A Jellyfin id (32 hex characters) made from a number, so tests can read them.
    public static string Id(int number) => number.ToString("x32");

    public static MediaItem Movie(int number, string name, params MediaFile[] files) => new()
    {
        ExternalId = Id(number),
        Kind = MediaItemKind.Movie,
        Name = name,
        SortName = name.ToLowerInvariant(),
        ProductionYear = 2000 + number % 30,
        Genres = ["Drama"],
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = number.ToString(), ["Imdb"] = $"tt{number}" },
        AddedAt = DateTimeOffset.UnixEpoch.AddDays(number),
        Images = new MediaImageTags($"poster{number}", null, null, [$"backdrop{number}"]),
        Files = files.Length > 0 ? files : [MovieFile(number, "1080p")],
    };

    public static MediaFile MovieFile(int number, string version) => new()
    {
        ExternalId = Id(10_000 + number) + version,
        Name = version,
        Path = $"/media/movies/{number}/{version}.mkv",
        Container = "mkv",
        SizeBytes = 1_000_000 + number,
        Runtime = TimeSpan.FromMinutes(100),
        Streams =
        [
            new MediaStream { Kind = MediaStreamKind.Video, Index = 0, Codec = "h264", Width = 1920, Height = 1080, VideoRange = "SDR" },
            new MediaStream { Kind = MediaStreamKind.Audio, Index = 1, Codec = "aac", Language = "eng", Channels = 6 },
        ],
    };
}
