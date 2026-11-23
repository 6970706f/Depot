namespace Depot.Application.Downloads;

// MediaType is "movie", "series", or "other" (null works too) for a download that is only
// saved on the server. Title is the movie's or the series' folder in the library; Season is
// required for a series.
public sealed record AddMagnetInput(string? Magnet, string? MediaType = null, string? Title = null, int? Season = null);

// Content is null when no file was sent.
public sealed record AddTorrentFileInput(string? FileName, byte[]? Content, string? MediaType = null, string? Title = null, int? Season = null);

// Name is the magnet's display name, or the hash when the link has none: the real name is only
// known once qBittorrent fetches the metadata.
public sealed record DownloadAddedView(string Hash, string Name);

// The torrent being added is already in qBittorrent. Adding it again is refused rather than
// ignored, so the person always learns what happened.
// What a removal did, so the interface can say it: FilesDeleted says whether the downloaded
// files were deleted or kept.
public sealed record RemovedDownloadView(string Hash, string Name, bool FilesDeleted);

public sealed class DuplicateDownloadException(string hash, string existingName)
    : Exception($"This torrent is already in your downloads as \"{existingName}\".")
{
    public string Hash { get; } = hash;
}
