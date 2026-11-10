using System.Text.Json;
using System.Text.RegularExpressions;
using Depot.Application.Jellyfin;
using Depot.Domain.Media;
using Depot.Domain.Settings;

namespace Depot.Infrastructure.Jellyfin;

// Reads Jellyfin's answers into domain types, trusting nothing: ids must be GUIDs, numbers must
// be in range and text is trimmed to a sane length. A whole answer that doesn't look like
// Jellyfin's is an UnexpectedResponse; a single item that can't be read is skipped, so one odd
// item doesn't stop a library.
public static partial class JellyfinResponses
{
    private const int MaxNameLength = 500;
    private const int MaxOverviewLength = 20_000;
    private const int MaxPathLength = 4096;
    private const int MaxShortTextLength = 100;
    private const int MaxListEntries = 50;
    private const int MaxFiles = 50;
    private const int MaxStreams = 100;
    private const int MaxBackdrops = 20;
    private const int MaxCast = 30;
    private static readonly TimeSpan MaxRuntime = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static JellyfinServerInfo ReadServerInfo(JsonElement root, JellyfinSettings settings)
    {
        var info = Deserialize<SystemInfoDto>(root, settings);
        if (info is null || NormalizeId(info.Id) is null || Text(info.Version, MaxShortTextLength) is not { } version)
        {
            throw NotJellyfin(settings);
        }

        return new JellyfinServerInfo(Text(info.ServerName, MaxNameLength) ?? "Jellyfin", version);
    }

    // The unauthenticated system info, which says whether the server is Jellyfin at all.
    public static void CheckPublicInfo(JsonElement root, JellyfinSettings settings)
    {
        var info = Deserialize<SystemInfoDto>(root, settings);
        if (info is null || NormalizeId(info.Id) is null
            || (info.ProductName is not null && !info.ProductName.Contains("Jellyfin", StringComparison.OrdinalIgnoreCase)))
        {
            throw NotJellyfin(settings);
        }

        if (info.StartupWizardCompleted == false)
        {
            throw new JellyfinException(
                JellyfinFailure.UnexpectedResponse, $"Jellyfin at {settings.Url} hasn't finished its setup wizard. Finish it, then create an API key.");
        }
    }

    public static IReadOnlyList<MediaLibrary> ReadLibraries(JsonElement root, JellyfinSettings settings)
    {
        var folders = Deserialize<List<VirtualFolderDto?>>(root, settings) ?? throw NotJellyfin(settings);
        var libraries = new List<MediaLibrary>();
        foreach (var folder in folders)
        {
            if (folder is null || NormalizeId(folder.ItemId) is not { } id || Text(folder.Name, MaxNameLength) is not { } name)
            {
                continue;
            }

            if (libraries.Any(library => library.ExternalId == id))
            {
                continue;
            }

            var locations = (folder.Locations ?? [])
                .Select(location => Text(location, MaxPathLength))
                .OfType<string>()
                .Take(MaxListEntries)
                .ToList();
            libraries.Add(new MediaLibrary(id, name, ToLibraryKind(folder.CollectionType), locations));
        }

        return libraries;
    }

    public static JellyfinItemPage ReadItemPage(JsonElement root, JellyfinSettings settings)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetProperty(root, "Items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
        {
            throw NotJellyfin(settings);
        }

        var total = TryGetProperty(root, "TotalRecordCount", out var totalElement) && totalElement.TryGetInt32(out var count) && count >= 0
            ? count
            : itemsElement.GetArrayLength();

        var items = new List<MediaItem>();
        var received = 0;
        foreach (var element in itemsElement.EnumerateArray())
        {
            received++;
            if (ReadItem(element) is { } item)
            {
                items.Add(item);
            }
        }

        return new JellyfinItemPage(items, received, received - items.Count, total);
    }

    // Null for an entry that can't be read: no valid id, an unknown type or a malformed field.
    public static MediaItem? ReadItem(JsonElement element)
    {
        ItemDto? dto;
        try
        {
            dto = element.Deserialize<ItemDto>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dto is null || NormalizeId(dto.Id) is not { } id || ToItemKind(dto.Type) is not { } kind)
        {
            return null;
        }

        return new MediaItem
        {
            ExternalId = id,
            Kind = kind,
            Name = Text(dto.Name, MaxNameLength) ?? "Untitled",
            OriginalTitle = Text(dto.OriginalTitle, MaxNameLength),
            SortName = Text(dto.SortName, MaxNameLength),
            Overview = Text(dto.Overview, MaxOverviewLength),
            ProductionYear = dto.ProductionYear is >= 1800 and <= 3000 ? dto.ProductionYear : null,
            PremiereDate = ToDate(dto.PremiereDate),
            EndDate = ToDate(dto.EndDate),
            OfficialRating = Text(dto.OfficialRating, MaxShortTextLength),
            CommunityRating = dto.CommunityRating is >= 0 and <= 10 ? Math.Round(dto.CommunityRating.Value, 2) : null,
            Runtime = ToRuntime(dto.RunTimeTicks),
            Genres = TextList(dto.Genres),
            Studios = TextList(dto.Studios?.Select(studio => studio?.Name)),
            ProviderIds = ToProviderIds(dto.ProviderIds),
            SeriesStatus = kind == MediaItemKind.Series ? Text(dto.Status, MaxShortTextLength) : null,
            SeriesExternalId = kind is MediaItemKind.Season or MediaItemKind.Episode ? NormalizeId(dto.SeriesId) : null,
            SeasonExternalId = kind == MediaItemKind.Episode ? NormalizeId(dto.SeasonId) : null,
            IndexNumber = Index(dto.IndexNumber),
            IndexNumberEnd = kind == MediaItemKind.Episode ? Index(dto.IndexNumberEnd) : null,
            ParentIndexNumber = kind == MediaItemKind.Episode ? Index(dto.ParentIndexNumber) : null,
            Path = Text(dto.Path, MaxPathLength),
            AddedAt = dto.DateCreated is { Year: > 1900 } added ? added.ToUniversalTime() : null,
            Images = ToImageTags(dto.ImageTags, dto.BackdropImageTags),
            Files = kind is MediaItemKind.Movie or MediaItemKind.Episode ? ToFiles(dto.MediaSources) : [],
            People = ToPeople(dto.People),
            Tags = TextList(dto.Tags),
            ProductionLocations = TextList(dto.ProductionLocations),
        };
    }

    // Jellyfin's ids are GUIDs, written as 32 hexadecimal characters. Anything else is refused,
    // which also keeps every id that goes back into a URL free of characters that could alter it.
    public static string? NormalizeId(string? id) =>
        Guid.TryParse(id, out var guid) && guid != Guid.Empty ? guid.ToString("N") : null;

    // Everyone but actors, then the first actors: a long cast list is mostly minor roles.
    private static IReadOnlyList<MediaPerson> ToPeople(List<PersonDto?>? people)
    {
        var result = new List<MediaPerson>();
        var actors = 0;
        foreach (var person in people ?? [])
        {
            if (person is null || NormalizeId(person.Id) is not { } id || Text(person.Name, MaxNameLength) is not { } name)
            {
                continue;
            }

            var kind = ToPersonKind(person.Type);
            if (kind is PersonKind.Actor or PersonKind.GuestStar && ++actors > MaxCast)
            {
                continue;
            }

            if (result.Count < MaxCast * 2)
            {
                result.Add(new MediaPerson(id, name, kind, Text(person.Role, MaxNameLength), ImageTag(person.PrimaryImageTag)));
            }
        }

        return result;
    }

    private static PersonKind ToPersonKind(string? type) => type switch
    {
        "Director" => PersonKind.Director,
        "Writer" => PersonKind.Writer,
        "Actor" => PersonKind.Actor,
        "GuestStar" => PersonKind.GuestStar,
        "Producer" => PersonKind.Producer,
        "Composer" => PersonKind.Composer,
        _ => PersonKind.Other,
    };

    private static IReadOnlyList<MediaFile> ToFiles(List<MediaSourceDto?>? sources)
    {
        var files = new List<MediaFile>();
        foreach (var source in sources ?? [])
        {
            if (source is null || SourceId(source.Id) is not { } id || files.Any(file => file.ExternalId == id) || files.Count == MaxFiles)
            {
                continue;
            }

            files.Add(new MediaFile
            {
                ExternalId = id,
                Name = Text(source.Name, MaxNameLength),
                Path = Text(source.Path, MaxPathLength),
                Container = Text(source.Container, MaxShortTextLength),
                SizeBytes = source.Size is >= 0 ? source.Size : null,
                Bitrate = source.Bitrate is >= 0 ? source.Bitrate : null,
                Runtime = ToRuntime(source.RunTimeTicks),
                Streams = ToStreams(source.MediaStreams),
            });
        }

        return files;
    }

    private static IReadOnlyList<MediaStream> ToStreams(List<MediaStreamDto?>? streams) =>
        (streams ?? [])
            .Select(stream => stream is null ? null : ToStream(stream))
            .OfType<MediaStream>()
            .Take(MaxStreams)
            .ToList();

    private static MediaStream? ToStream(MediaStreamDto stream)
    {
        MediaStreamKind? kind = stream.Type switch
        {
            "Video" => MediaStreamKind.Video,
            "Audio" => MediaStreamKind.Audio,
            "Subtitle" => MediaStreamKind.Subtitle,
            // Embedded cover art, data and lyrics streams aren't tracks anyone picks.
            _ => null,
        };
        if (kind is not { } streamKind)
        {
            return null;
        }

        var isVideo = streamKind == MediaStreamKind.Video;
        var range = stream.VideoRangeType is { } rangeType and not "Unknown" ? rangeType : stream.VideoRange;
        return new MediaStream
        {
            Kind = streamKind,
            Index = stream.Index is >= 0 and <= 10_000 ? stream.Index.Value : 0,
            Codec = Text(stream.Codec, MaxShortTextLength),
            Language = Text(stream.Language, MaxShortTextLength),
            Title = Text(stream.DisplayTitle ?? stream.Title, MaxNameLength),
            IsDefault = stream.IsDefault ?? false,
            IsForced = stream.IsForced ?? false,
            IsExternal = stream.IsExternal ?? false,
            Width = isVideo && stream.Width is > 0 and <= 20_000 ? stream.Width : null,
            Height = isVideo && stream.Height is > 0 and <= 20_000 ? stream.Height : null,
            VideoRange = isVideo && range is not (null or "Unknown") ? Text(range, MaxShortTextLength) : null,
            Channels = streamKind == MediaStreamKind.Audio && stream.Channels is > 0 and <= 64 ? stream.Channels : null,
            Bitrate = stream.BitRate is > 0 ? stream.BitRate : null,
        };
    }

    private static MediaImageTags ToImageTags(Dictionary<string, string?>? tags, List<string?>? backdrops)
    {
        string? Tag(string kind) => tags is not null && tags.TryGetValue(kind, out var tag) ? ImageTag(tag) : null;
        var backdropTags = (backdrops ?? []).Select(ImageTag).OfType<string>().Take(MaxBackdrops).ToList();
        return new MediaImageTags(Tag("Primary"), Tag("Thumb"), Tag("Logo"), backdropTags);
    }

    private static IReadOnlyDictionary<string, string> ToProviderIds(Dictionary<string, string?>? providerIds)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (provider, value) in providerIds ?? [])
        {
            if (result.Count == MaxListEntries)
            {
                break;
            }

            if (Text(provider, MaxShortTextLength) is { } name && Text(value, MaxShortTextLength) is { } id)
            {
                result[name] = id;
            }
        }

        return result;
    }

    private static MediaLibraryKind ToLibraryKind(string? collectionType) => collectionType?.ToLowerInvariant() switch
    {
        "movies" => MediaLibraryKind.Movies,
        "tvshows" => MediaLibraryKind.Shows,
        "boxsets" => MediaLibraryKind.Collections,
        null or "" or "mixed" => MediaLibraryKind.Mixed,
        _ => MediaLibraryKind.Other,
    };

    private static MediaItemKind? ToItemKind(string? type) => type switch
    {
        "Movie" => MediaItemKind.Movie,
        "Series" => MediaItemKind.Series,
        "Season" => MediaItemKind.Season,
        "Episode" => MediaItemKind.Episode,
        "BoxSet" => MediaItemKind.Collection,
        _ => null,
    };

    public static string ItemType(MediaItemKind kind) => kind == MediaItemKind.Collection ? "BoxSet" : kind.ToString();

    private static DateOnly? ToDate(DateTimeOffset? value) =>
        value is { Year: >= 1800 and <= 3000 } date ? DateOnly.FromDateTime(date.UtcDateTime) : null;

    private static TimeSpan? ToRuntime(long? ticks) =>
        ticks is > 0 && ticks < MaxRuntime.Ticks ? TimeSpan.FromTicks(ticks.Value) : null;

    private static int? Index(int? number) => number is >= 0 and <= 100_000 ? number : null;

    private static string? SourceId(string? id) =>
        NormalizeId(id) ?? (id is not null && SafeIdPattern().IsMatch(id) ? id : null);

    private static string? ImageTag(string? tag) => tag is not null && SafeIdPattern().IsMatch(tag) ? tag : null;

    private static IReadOnlyList<string> TextList(IEnumerable<string?>? values) =>
        (values ?? [])
            .Select(value => Text(value, MaxShortTextLength))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(MaxListEntries)
            .ToList();

    // Trimmed, without control characters other than line breaks, and cut to maxLength. Null
    // when nothing is left.
    private static string? Text(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = new string(value.Where(character => !char.IsControl(character) || character is '\n').ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static T? Deserialize<T>(JsonElement root, JellyfinSettings settings)
    {
        try
        {
            return root.Deserialize<T>(JsonOptions);
        }
        catch (JsonException)
        {
            throw NotJellyfin(settings);
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static JellyfinException NotJellyfin(JellyfinSettings settings) => new(
        JellyfinFailure.UnexpectedResponse, $"The server at {settings.Url} answered, but not like Jellyfin. Check the URL in Settings.");

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeIdPattern();

    // Only the fields Depot uses, in Jellyfin's PascalCase.
    private sealed record SystemInfoDto(string? Id, string? ServerName, string? Version, string? ProductName, bool? StartupWizardCompleted);

    private sealed record VirtualFolderDto(string? Name, string? CollectionType, string? ItemId, List<string?>? Locations);

    private sealed record NameDto(string? Name);

    private sealed record ItemDto(
        string? Id,
        string? Name,
        string? Type,
        string? OriginalTitle,
        string? SortName,
        string? Overview,
        int? ProductionYear,
        DateTimeOffset? PremiereDate,
        DateTimeOffset? EndDate,
        string? OfficialRating,
        double? CommunityRating,
        long? RunTimeTicks,
        List<string?>? Genres,
        List<NameDto?>? Studios,
        Dictionary<string, string?>? ProviderIds,
        string? Status,
        string? SeriesId,
        string? SeasonId,
        int? IndexNumber,
        int? IndexNumberEnd,
        int? ParentIndexNumber,
        string? Path,
        DateTimeOffset? DateCreated,
        Dictionary<string, string?>? ImageTags,
        List<string?>? BackdropImageTags,
        List<MediaSourceDto?>? MediaSources,
        List<PersonDto?>? People,
        List<string?>? Tags,
        List<string?>? ProductionLocations);

    private sealed record PersonDto(string? Id, string? Name, string? Role, string? Type, string? PrimaryImageTag);

    private sealed record MediaSourceDto(
        string? Id,
        string? Name,
        string? Path,
        string? Container,
        long? Size,
        long? Bitrate,
        long? RunTimeTicks,
        List<MediaStreamDto?>? MediaStreams);

    private sealed record MediaStreamDto(
        string? Type,
        int? Index,
        string? Codec,
        string? Language,
        string? DisplayTitle,
        string? Title,
        bool? IsDefault,
        bool? IsForced,
        bool? IsExternal,
        int? Width,
        int? Height,
        string? VideoRange,
        string? VideoRangeType,
        int? Channels,
        long? BitRate);
}
