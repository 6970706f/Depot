using System.Text.Json;
using Depot.Application.Jellyfin;
using Depot.Domain.Settings;

namespace Depot.Infrastructure.Jellyfin;

// The Jellyfin REST API with the saved settings. Items are read with /Items, which takes an API
// key without a user. In 10.11 that query returns nothing for ParentId=<series> (and ignores
// SeriesIds), so a series' seasons are found by their SeriesId and episodes by their season.
public sealed class JellyfinClient(JellyfinHttp http, IJellyfinSettingsRepository settingsRepository) : IJellyfinClient
{
    // The largest image Depot passes on; Jellyfin's originals are rarely more than a few MB.
    public const int MaxImageBytes = 15 * 1024 * 1024;

    private const string DetailFields = "Overview,Genres,Studios,People,MediaSources,Path,ProviderIds,OriginalTitle,DateCreated";

    // Cast and crew shown on an item's page.
    private const int MaxPeople = 30;

    public async Task<JellyfinServerInfo> GetServerInfoAsync(CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(cancellationToken);
        using var document = await http.GetJsonAsync(settings, "System/Info", authenticated: true, cancellationToken);
        return ReadServerInfo(document.RootElement, settings);
    }

    public async Task RefreshLibraryAsync(CancellationToken cancellationToken) =>
        await http.PostAsync(await SettingsAsync(cancellationToken), "Library/Refresh", cancellationToken);

    public async Task<MediaPage> GetItemsAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(cancellationToken);
        var (sortBy, sortOrder) = query.Sort switch
        {
            MediaSort.Added => ("DateCreated,SortName", "Descending,Ascending"),
            MediaSort.Year => ("ProductionYear,SortName", "Descending,Ascending"),
            MediaSort.Rating => ("CommunityRating,SortName", "Descending,Ascending"),
            _ => ("SortName", "Ascending"),
        };
        var parameters = new List<string>
        {
            $"IncludeItemTypes={TypeName(query.Kind)}",
            "Recursive=true",
            $"SortBy={sortBy}",
            $"SortOrder={sortOrder}",
            $"StartIndex={(query.Page - 1) * query.PageSize}",
            $"Limit={query.PageSize}",
            "EnableImageTypes=Primary",
            "ImageTypeLimit=1",
            "EnableTotalRecordCount=true",
        };
        if (query.Search is not null)
        {
            parameters.Add($"SearchTerm={Uri.EscapeDataString(query.Search)}");
        }

        using var document = await http.GetJsonAsync(settings, $"Items?{string.Join('&', parameters)}", authenticated: true, cancellationToken);
        var root = document.RootElement;
        var items = Items(root).Select(ReadSummary).OfType<MediaSummary>().ToList();
        return new MediaPage(items, root.Int("TotalRecordCount") ?? items.Count, query.Page, query.PageSize);
    }

    public async Task<MediaCounts> GetCountsAsync(CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(cancellationToken);
        var movies = await CountAsync(settings, "Movie", cancellationToken);
        var series = await CountAsync(settings, "Series", cancellationToken);
        var episodes = await CountAsync(settings, "Episode", cancellationToken);
        return new MediaCounts(movies, series, episodes);
    }

    public async Task<MediaDetail?> GetItemAsync(string id, CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(cancellationToken);
        JsonElement item;
        using (var document = await http.GetJsonAsync(settings, $"Items?Ids={id}&Fields={DetailFields}", authenticated: true, cancellationToken))
        {
            var found = Items(document.RootElement).FirstOrDefault(candidate => KindOf(candidate) is not null);
            if (found.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            item = found.Clone();
        }

        var kind = KindOf(item)!.Value;
        var seasons = kind == MediaKind.Series ? await GetSeasonsAsync(settings, id, cancellationToken) : [];
        return new MediaDetail(
            Id(item)!,
            kind,
            item.Text("Name", 300) ?? "Untitled",
            item.Text("OriginalTitle", 300),
            item.Int("ProductionYear"),
            item.Double("CommunityRating"),
            item.Text("OfficialRating", 20),
            item.Text("Overview", 5000),
            Strings(item, "Genres"),
            Names(item, "Studios"),
            RuntimeMinutes(item),
            ImageTag(item, "Primary"),
            BackdropTag(item),
            People(item),
            kind == MediaKind.Movie ? Files(item) : [],
            ProviderIds(item),
            seasons);
    }

    public async Task<MediaImage?> GetImageAsync(string id, MediaImageType type, int width, string? tag, CancellationToken cancellationToken)
    {
        var settings = await SettingsAsync(cancellationToken);
        var path = $"Items/{id}/Images/{type}?maxWidth={width}&quality=90" + (tag is null ? "" : $"&tag={tag}");
        using var response = await http.GetStreamAsync(settings, path, "image/webp,image/jpeg,image/png,image/gif", cancellationToken);
        if (response is null)
        {
            return null;
        }

        if (response.Content.Headers.ContentLength > MaxImageBytes)
        {
            throw TooLarge(settings);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxImageBytes)
            {
                throw TooLarge(settings);
            }

            buffer.Write(chunk, 0, read);
        }

        var content = buffer.ToArray();
        // The bytes decide, not the declared type: only formats a browser shows as a picture, and
        // never SVG, which can carry scripts.
        var contentType = ImageFormats.Detect(content)
            ?? throw new JellyfinException(JellyfinFailure.UnexpectedResponse, $"Jellyfin at {settings.Url} sent an image in a format Depot doesn't pass on.");
        return new MediaImage(contentType, content);
    }

    private async Task<IReadOnlyList<MediaSeason>> GetSeasonsAsync(JellyfinSettings settings, string seriesId, CancellationToken cancellationToken)
    {
        List<JsonElement> seasons;
        using (var document = await http.GetJsonAsync(
            settings, "Items?IncludeItemTypes=Season&Recursive=true&SortBy=IndexNumber&EnableImageTypes=Primary&ImageTypeLimit=1", authenticated: true, cancellationToken))
        {
            seasons = Items(document.RootElement)
                .Where(season => NormalizeId(season.Text("SeriesId")) == seriesId && Id(season) is not null)
                .Select(season => season.Clone())
                .ToList();
        }

        var result = new List<MediaSeason>();
        foreach (var season in seasons.OrderBy(season => season.Int("IndexNumber") ?? int.MaxValue))
        {
            using var document = await http.GetJsonAsync(
                settings, $"Items?ParentId={Id(season)}&IncludeItemTypes=Episode&SortBy=IndexNumber&Fields=Overview,Path,MediaSources&EnableImageTypes=Primary&ImageTypeLimit=1",
                authenticated: true, cancellationToken);
            var episodes = Items(document.RootElement)
                .Where(episode => Id(episode) is not null)
                .Select(episode => new MediaEpisode(
                    Id(episode)!,
                    episode.Text("Name", 300) ?? "Episode",
                    episode.Int("ParentIndexNumber"),
                    episode.Int("IndexNumber"),
                    episode.Text("Overview", 3000),
                    RuntimeMinutes(episode),
                    ImageTag(episode, "Primary"),
                    Files(episode).FirstOrDefault()))
                .OrderBy(episode => episode.Number ?? int.MaxValue)
                .ToList();
            result.Add(new MediaSeason(Id(season)!, season.Text("Name", 200) ?? "Season", season.Int("IndexNumber"), ImageTag(season, "Primary"), episodes));
        }

        return result;
    }

    private async Task<int> CountAsync(JellyfinSettings settings, string type, CancellationToken cancellationToken)
    {
        using var document = await http.GetJsonAsync(
            settings, $"Items?IncludeItemTypes={type}&Recursive=true&Limit=0&EnableTotalRecordCount=true", authenticated: true, cancellationToken);
        return document.RootElement.Int("TotalRecordCount") ?? 0;
    }

    private async Task<JellyfinSettings> SettingsAsync(CancellationToken cancellationToken) =>
        await settingsRepository.GetAsync(cancellationToken) ?? throw JellyfinException.NotConfigured();

    public static JellyfinServerInfo ReadServerInfo(JsonElement root, JellyfinSettings settings)
    {
        var id = root.Text("Id");
        var version = root.Text("Version", 64);
        if (id is null || version is null)
        {
            throw JellyfinHttp.NotJellyfin(settings);
        }

        return new JellyfinServerInfo(root.Text("ServerName", 200) ?? "Jellyfin", version);
    }

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.TryGet("Items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];

    // An item that can't be read (no id, no known type) is skipped rather than failing the page.
    private static MediaSummary? ReadSummary(JsonElement item) =>
        Id(item) is { } id && KindOf(item) is { } kind
            ? new MediaSummary(id, kind, item.Text("Name", 300) ?? "Untitled", item.Int("ProductionYear"), item.Double("CommunityRating"),
                item.Text("OfficialRating", 20), ImageTag(item, "Primary"))
            : null;

    private static MediaKind? KindOf(JsonElement item) => item.Text("Type") switch
    {
        "Movie" => MediaKind.Movie,
        "Series" => MediaKind.Series,
        _ => null,
    };

    private static string TypeName(MediaKind kind) => kind == MediaKind.Movie ? "Movie" : "Series";

    private static string? Id(JsonElement item) => NormalizeId(item.Text("Id"));

    private static string? NormalizeId(string? id) => Guid.TryParse(id, out var guid) ? guid.ToString("N") : null;

    private static string? ImageTag(JsonElement item, string type) =>
        item.TryGet("ImageTags", out var tags) ? tags.Text(type, 64) : null;

    private static string? BackdropTag(JsonElement item) =>
        item.TryGet("BackdropImageTags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.GetArrayLength() > 0
            && tags[0].ValueKind == JsonValueKind.String
            ? tags[0].GetString()
            : null;

    // Jellyfin counts time in ticks of 100 ns.
    private static int? RuntimeMinutes(JsonElement item) =>
        item.Long("RunTimeTicks") is { } ticks && ticks > 0 ? (int)Math.Round(TimeSpan.FromTicks(ticks).TotalMinutes) : null;

    private static IReadOnlyList<string> Strings(JsonElement item, string name) =>
        item.TryGet(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).Take(20).ToList()
            : [];

    private static IReadOnlyList<string> Names(JsonElement item, string name) =>
        item.TryGet(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(value => value.Text("Name", 200)).OfType<string>().Take(20).ToList()
            : [];

    private static IReadOnlyList<MediaPerson> People(JsonElement item) =>
        item.TryGet("People", out var people) && people.ValueKind == JsonValueKind.Array
            ? people.EnumerateArray()
                .Select(person => person.Text("Name", 200) is { } name
                    ? new MediaPerson(name, person.Text("Role", 200), person.Text("Type", 40) ?? "Actor")
                    : null)
                .OfType<MediaPerson>()
                .Take(MaxPeople)
                .ToList()
            : [];

    private static IReadOnlyDictionary<string, string> ProviderIds(JsonElement item) =>
        item.TryGet("ProviderIds", out var ids) && ids.ValueKind == JsonValueKind.Object
            ? ids.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                .ToDictionary(property => property.Name, property => property.Value.GetString()!)
            : new Dictionary<string, string>();

    private static IReadOnlyList<MediaFileInfo> Files(JsonElement item)
    {
        if (!item.TryGet("MediaSources", out var sources) || sources.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return sources.EnumerateArray().Select(source =>
        {
            var streams = source.TryGet("MediaStreams", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [];
            string? Describe(JsonElement stream) => stream.Text("DisplayTitle", 200) ?? stream.Text("Codec", 40);
            var video = streams.FirstOrDefault(stream => stream.Text("Type") == "Video");
            return new MediaFileInfo(
                source.Text("Path", 1000),
                source.Long("Size"),
                source.Text("Container", 40),
                video.ValueKind == JsonValueKind.Object ? Describe(video) : null,
                streams.Where(stream => stream.Text("Type") == "Audio").Select(Describe).OfType<string>().ToList(),
                streams.Where(stream => stream.Text("Type") == "Subtitle").Select(Describe).OfType<string>().ToList());
        }).ToList();
    }

    private static JellyfinException TooLarge(JellyfinSettings settings) => new(
        JellyfinFailure.UnexpectedResponse, $"Jellyfin at {settings.Url} sent an image larger than {MaxImageBytes / (1024 * 1024)} MB.");
}

// Checks settings that may not be saved yet: first that the URL leads to Jellyfin (its public
// info needs no key), then that the key is accepted.
public sealed class JellyfinConnectionTester(JellyfinHttp http) : IJellyfinConnectionTester
{
    public async Task<JellyfinServerInfo> TestAsync(JellyfinSettings settings, CancellationToken cancellationToken)
    {
        using (var publicInfo = await http.GetJsonAsync(settings, "System/Info/Public", authenticated: false, cancellationToken))
        {
            var root = publicInfo.RootElement;
            if (root.Text("Id") is null || root.Text("ProductName") is { } product && !product.Contains("Jellyfin", StringComparison.OrdinalIgnoreCase))
            {
                throw JellyfinHttp.NotJellyfin(settings);
            }

            if (root.Bool("StartupWizardCompleted") == false)
            {
                throw new JellyfinException(
                    JellyfinFailure.UnexpectedResponse, $"Jellyfin at {settings.Url} hasn't finished its setup wizard. Finish it, then create an API key.");
            }
        }

        using var info = await http.GetJsonAsync(settings, "System/Info", authenticated: true, cancellationToken);
        return JellyfinClient.ReadServerInfo(info.RootElement, settings);
    }
}

// Recognizes the image formats Depot serves by their first bytes.
public static class ImageFormats
{
    public static string? Detect(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (content.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        return content.Length >= 12 && content.StartsWith("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8) ? "image/webp" : null;
    }
}
