namespace Depot.Application.Downloads;

// MediaType is "movie", "series", or "other" (null works too) for a download that is only
// saved on the server. Title is the movie's or the series' folder in the library; Season is
// required for a series.
public sealed record AddMagnetInput(string? Magnet, string? MediaType = null, string? Title = null, int? Season = null);

// Content is null when no file was sent.
public sealed record AddTorrentFileInput(
    string? FileName,
    byte[]? Content,
    string? MediaType = null,
    string? Title = null,
    int? Season = null);

// Name is the magnet's display name, or the hash when the link has none. The real name
// is known only after qBittorrent fetches the torrent's metadata.
public sealed record DownloadAddedView(string Hash, string Name);

// What a removal did, so the interface can state it: FilesDeleted says whether the
// downloaded files in SavePath were deleted or kept.
public sealed record RemovedDownloadView(string Hash, string Name, bool FilesDeleted, string SavePath);
