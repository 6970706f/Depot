using System.Collections.Concurrent;
using Depot.Application.QBittorrent;
using Depot.Domain.Downloads;

namespace Depot.Tests.TestSupport;

// Stands in for qBittorrent: torrents and their files are whatever a test puts here, plus
// what Depot adds. It refuses to remove torrents unless a test allows it, since organizing
// must never do that.
public sealed class FakeQBittorrentClient : IQBittorrentClient
{
    private readonly ConcurrentDictionary<string, (Torrent Torrent, List<TorrentContentFile> Files)> torrents = new(StringComparer.OrdinalIgnoreCase);

    public int RemoveCalls { get; private set; }

    public bool AllowRemove { get; set; }

    public IReadOnlyList<Torrent> Torrents => torrents.Values.Select(entry => entry.Torrent).ToList();

    public Torrent Add(
        string hash,
        string name,
        string savePath,
        IEnumerable<(string RelativePath, long Size)> files,
        TorrentStatus status = TorrentStatus.Completed,
        double progress = 1,
        string? category = null)
    {
        var fileList = files
            .Select((file, index) => new TorrentContentFile(index, file.RelativePath, file.Size, progress, 1))
            .ToList();
        var size = fileList.Sum(file => file.SizeBytes);
        var torrent = new Torrent(
            hash, new TorrentHashes(hash, null), name, status, size, (long)(size * progress), (long)(size * (1 - progress)),
            progress, 0, 0, null, category, savePath, DateTimeOffset.UnixEpoch);
        torrents[hash] = (torrent, fileList);
        return torrent;
    }

    public Task<string> GetVersionAsync(CancellationToken cancellationToken) => Task.FromResult("v5.2.2");

    public Task<IReadOnlyList<Torrent>> GetTorrentsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Torrents);

    public Task<Torrent?> GetTorrentAsync(string hash, CancellationToken cancellationToken) =>
        Task.FromResult(torrents.TryGetValue(hash, out var entry) ? entry.Torrent : null);

    public Task<IReadOnlyList<TorrentContentFile>> GetTorrentFilesAsync(string hash, CancellationToken cancellationToken) =>
        torrents.TryGetValue(hash, out var entry)
            ? Task.FromResult<IReadOnlyList<TorrentContentFile>>(entry.Files)
            : throw new QBittorrentException(QBittorrentFailure.TorrentNotFound, "This torrent isn't in qBittorrent anymore.");

    // What Depot sent to add, in order. Set RejectAdds to make qBittorrent refuse them, and
    // TimeOutAfterAccepting to make it take the torrent but answer too late.
    public ConcurrentQueue<(string Source, AddTorrentOptions Options)> Added { get; } = [];

    public bool RejectAdds { get; set; }

    public bool TimeOutAfterAccepting { get; set; }

    // How long qBittorrent takes to answer an add.
    public TimeSpan AddDelay { get; set; }

    public Task AddMagnetAsync(string magnetUri, AddTorrentOptions options, CancellationToken cancellationToken) =>
        RecordAddAsync(magnetUri, MagnetLink.Parse(magnetUri).Hashes.Id, options);

    public Task AddTorrentFileAsync(string fileName, byte[] content, AddTorrentOptions options, CancellationToken cancellationToken) =>
        RecordAddAsync(fileName, TorrentFile.Parse(fileName, content).Hashes.Id, options);

    public Task PauseAsync(string hash, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ResumeAsync(string hash, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task RemoveAsync(string hash, bool deleteFiles, CancellationToken cancellationToken)
    {
        RemoveCalls++;
        if (!AllowRemove)
        {
            throw new InvalidOperationException("Organizing media must never remove a torrent.");
        }

        torrents.TryRemove(hash, out _);
        return Task.CompletedTask;
    }

    // Like qBittorrent, a torrent it already has is refused.
    private async Task RecordAddAsync(string source, string hash, AddTorrentOptions options)
    {
        await Task.Delay(AddDelay);
        if (RejectAdds || torrents.ContainsKey(hash))
        {
            throw new QBittorrentException(QBittorrentFailure.TorrentRejected, "qBittorrent refused this torrent.");
        }

        Added.Enqueue((source, options));
        Add(hash, source, "/downloads", [], TorrentStatus.Downloading, progress: 0, options.Category);
        if (TimeOutAfterAccepting)
        {
            throw new QBittorrentException(QBittorrentFailure.Timeout, "qBittorrent didn't answer within 10 seconds.");
        }
    }
}
