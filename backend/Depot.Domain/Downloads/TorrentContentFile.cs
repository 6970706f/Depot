namespace Depot.Domain.Downloads;

// One file inside a torrent, as the torrent client lists it. RelativePath is relative to the
// torrent's save path and always uses "/" as the separator.
public sealed record TorrentContentFile(int Index, string RelativePath, long SizeBytes, double Progress, int Priority)
{
    // Priority 0 means the file was deselected, so it's never downloaded.
    public bool IsDownloaded => Priority > 0 && Progress >= 1;
}
