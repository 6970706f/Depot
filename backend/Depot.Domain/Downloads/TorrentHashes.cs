namespace Depot.Domain.Downloads;

// The info hashes of one torrent, as 40-character lowercase hex. V1 is the SHA-1 hash;
// V2 is the SHA-256 hash cut to 40 characters, which is how torrent clients show it.
// A hybrid torrent has both.
public sealed record TorrentHashes
{
    public TorrentHashes(string? v1, string? v2)
    {
        if (v1 is null && v2 is null)
        {
            throw new ArgumentException("A torrent needs at least one info hash.");
        }

        V1 = v1;
        V2 = v2;
    }

    public string? V1 { get; }

    public string? V2 { get; }

    // The id qBittorrent gives the torrent. With libtorrent 2 (qBittorrent 4.4 and later)
    // that is the v2 hash whenever there is one, so a hybrid torrent is known by its v2 hash.
    public string Id => V2 ?? V1!;

    // Whether both describe the same torrent: any info hash they both know is equal.
    public bool SharesHashWith(TorrentHashes other) =>
        (V1 is not null && string.Equals(V1, other.V1, StringComparison.OrdinalIgnoreCase))
        || (V2 is not null && string.Equals(V2, other.V2, StringComparison.OrdinalIgnoreCase));

    // Whether a torrent client id refers to this torrent. Both hashes are compared so the
    // answer is right whichever hash the client chose as the id.
    public bool Matches(string torrentId) =>
        string.Equals(torrentId, V1, StringComparison.OrdinalIgnoreCase)
        || string.Equals(torrentId, V2, StringComparison.OrdinalIgnoreCase);
}
