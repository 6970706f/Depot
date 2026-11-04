namespace Depot.Application.Downloads;

// The torrent being added is already in qBittorrent. Adding it again is refused rather
// than ignored, so the person always learns what happened.
public sealed class DuplicateDownloadException(string hash, string existingName)
    : Exception($"This torrent is already in your downloads as \"{existingName}\".")
{
    public string Hash { get; } = hash;
}
