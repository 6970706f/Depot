namespace Depot.Domain.Media;

public enum MediaStreamKind
{
    Video,
    Audio,
    Subtitle,
}

// One file of a movie or episode, with what the source found out about it.
public sealed record MediaFile
{
    // The source's id for this file (Jellyfin's media source id).
    public required string ExternalId { get; init; }

    // The version's name, such as "1080p", when an item has several.
    public string? Name { get; init; }

    public string? Path { get; init; }

    public string? Container { get; init; }

    public long? SizeBytes { get; init; }

    // Bits per second, all streams together.
    public long? Bitrate { get; init; }

    public TimeSpan? Runtime { get; init; }

    public IReadOnlyList<MediaStream> Streams { get; init; } = [];
}

// A video, audio or subtitle track of a file. Subtitles can also be separate files (IsExternal).
public sealed record MediaStream
{
    public required MediaStreamKind Kind { get; init; }

    public int Index { get; init; }

    public string? Codec { get; init; }

    // Such as "eng" or "pt-BR".
    public string? Language { get; init; }

    // A readable summary, such as "1080p H264 SDR" or "English - AAC - 5.1".
    public string? Title { get; init; }

    public bool IsDefault { get; init; }

    public bool IsForced { get; init; }

    public bool IsExternal { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    // SDR, HDR10, DOVI...
    public string? VideoRange { get; init; }

    public int? Channels { get; init; }

    public long? Bitrate { get; init; }
}
