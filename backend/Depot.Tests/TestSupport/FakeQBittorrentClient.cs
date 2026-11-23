using System.Collections.Concurrent;
using Depot.Application.QBittorrent;
using Depot.Domain.Downloads;

namespace Depot.Tests.TestSupport;

// Stands in for qBittorrent: torrents and their files are whatever a test puts here, plus what
// Depot adds.
public sealed class FakeQBittorrentClient : IQBittorrentClient
{
    private readonly ConcurrentDictionary<string, (Torrent Torrent, List<TorrentContentFile> Files)> torrents = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Torrent> Torrents => torrents.Values.Select(entry => entry.Torrent).ToList();

    // What Depot sent to add, in order.
    public ConcurrentQueue<(string Source, AddTorrentOptions Options)> Added { get; } = [];

    // Makes qBittorrent refuse adds.
    public bool RejectAdds { get; set; }

    // Makes qBittorrent take the torrent but answer too late.
    public bool TimeOutAfterAccepting { get; set; }

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
            progress, 0, 0, null, category, savePath, null, DateTimeOffset.UnixEpoch);
        torrents[hash] = (torrent, fileList);
        return torrent;
    }

    public void Replace(Torrent torrent) => torrents[torrent.Hash] = (torrent, torrents[torrent.Hash].Files);

    public void Remove(string hash) => torrents.TryRemove(hash, out _);

    public IReadOnlyList<TorrentContentFile> FilesOf(string hash) => torrents[hash].Files;

    public Task<string> GetVersionAsync(CancellationToken cancellationToken) => Task.FromResult("v5.2.2");

    public Task<IReadOnlyList<Torrent>> GetTorrentsAsync(CancellationToken cancellationToken) => Task.FromResult(Torrents);

    public Task<Torrent?> GetTorrentAsync(string hash, CancellationToken cancellationToken) =>
        Task.FromResult(torrents.TryGetValue(hash, out var entry) ? entry.Torrent : null);

    public Task<IReadOnlyList<TorrentContentFile>> GetTorrentFilesAsync(string hash, CancellationToken cancellationToken) =>
        torrents.TryGetValue(hash, out var entry)
            ? Task.FromResult<IReadOnlyList<TorrentContentFile>>(entry.Files)
            : throw new QBittorrentException(QBittorrentFailure.TorrentNotFound, "This torrent isn't in qBittorrent anymore.");

    public Task AddMagnetAsync(string magnetUri, AddTorrentOptions options, CancellationToken cancellationToken) =>
        RecordAdd(magnetUri, MagnetLink.Parse(magnetUri).Hashes.Id, options);

    public Task AddTorrentFileAsync(string fileName, byte[] content, AddTorrentOptions options, CancellationToken cancellationToken) =>
        RecordAdd(fileName, TorrentFile.Parse(fileName, content).Hashes.Id, options);

    public List<(string Hash, bool DeleteFiles)> Removed { get; } = [];

    public Task PauseAsync(string hash, CancellationToken cancellationToken) =>
        SetStatus(hash, torrent => torrent.Status == TorrentStatus.Completed ? TorrentStatus.Completed : TorrentStatus.Paused);

    public Task ResumeAsync(string hash, CancellationToken cancellationToken) =>
        SetStatus(hash, torrent => torrent.Progress >= 1 ? TorrentStatus.Completed : TorrentStatus.Downloading);

    public Task RemoveAsync(string hash, bool deleteFiles, CancellationToken cancellationToken)
    {
        if (!torrents.TryRemove(hash, out _))
        {
            throw new QBittorrentException(QBittorrentFailure.TorrentNotFound, "This torrent isn't in qBittorrent anymore.");
        }

        Removed.Add((hash, deleteFiles));
        return Task.CompletedTask;
    }

    private Task SetStatus(string hash, Func<Torrent, TorrentStatus> status)
    {
        if (!torrents.TryGetValue(hash, out var entry))
        {
            throw new QBittorrentException(QBittorrentFailure.TorrentNotFound, "This torrent isn't in qBittorrent anymore.");
        }

        torrents[hash] = (entry.Torrent with { Status = status(entry.Torrent) }, entry.Files);
        return Task.CompletedTask;
    }

    // Like qBittorrent, a torrent it already has is refused.
    private Task RecordAdd(string source, string hash, AddTorrentOptions options)
    {
        if (RejectAdds || torrents.ContainsKey(hash))
        {
            throw new QBittorrentException(QBittorrentFailure.TorrentRejected, "qBittorrent refused this torrent.");
        }

        Added.Enqueue((source, options));
        Add(hash, source, "/downloads", [], TorrentStatus.Downloading, progress: 0, options.Category);
        return TimeOutAfterAccepting
            ? throw new QBittorrentException(QBittorrentFailure.Timeout, "qBittorrent didn't answer within 10 seconds.")
            : Task.CompletedTask;
    }
}
