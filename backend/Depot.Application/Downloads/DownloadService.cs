using Depot.Application.Organization;
using Depot.Application.QBittorrent;
using Depot.Domain.Common;
using Depot.Domain.Downloads;
using Depot.Domain.Organization;
using Microsoft.Extensions.Logging;

namespace Depot.Application.Downloads;

public sealed class DownloadService(
    IQBittorrentClient qBittorrentClient,
    ILibraryTargetRepository libraryTargets,
    TimeProvider clock,
    ILogger<DownloadService> logger)
{
    private static readonly TimeSpan StateChangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StateCheckInterval = TimeSpan.FromMilliseconds(250);

    // Shared by every request: adds must not overlap (see AddWithTargetAsync).
    private static readonly SemaphoreSlim AddGate = new(1, 1);

    // An empty list means qBittorrent answered and has no torrents. Any failure to get
    // an answer throws QBittorrentException instead, so the two are never confused.
    public async Task<IReadOnlyList<DownloadView>> ListAsync(CancellationToken cancellationToken)
    {
        var torrents = await qBittorrentClient.GetTorrentsAsync(cancellationToken);
        var targets = await libraryTargets.GetAllAsync(cancellationToken);
        return torrents
            .OrderByDescending(torrent => torrent.AddedAt)
            .ThenBy(torrent => torrent.Name, StringComparer.OrdinalIgnoreCase)
            .Select(torrent => DownloadView.From(
                torrent, targets.TryGetValue(torrent.Hash, out var target) ? LibraryTargetView.From(target) : null))
            .ToList();
    }

    public async Task<DownloadAddedView> AddMagnetAsync(AddMagnetInput input, CancellationToken cancellationToken)
    {
        var magnet = MagnetLink.Parse(input.Magnet);
        var target = LibraryTarget.Create(input.MediaType, input.Title, input.Season);
        await AddWithTargetAsync(magnet.Hashes, target, options =>
            qBittorrentClient.AddMagnetAsync(magnet.Uri, options, cancellationToken), cancellationToken);
        return new DownloadAddedView(magnet.Hashes.Id, magnet.DisplayName ?? magnet.Hashes.Id);
    }

    public async Task<DownloadAddedView> AddTorrentFileAsync(AddTorrentFileInput input, CancellationToken cancellationToken)
    {
        var torrentFile = TorrentFile.Parse(input.FileName, input.Content);
        var target = LibraryTarget.Create(input.MediaType, input.Title, input.Season);
        await AddWithTargetAsync(torrentFile.Hashes, target, options =>
            qBittorrentClient.AddTorrentFileAsync(torrentFile.FileName, torrentFile.Content, options, cancellationToken), cancellationToken);
        return new DownloadAddedView(torrentFile.Hashes.Id, torrentFile.Name);
    }

    // Returns the download once qBittorrent reports it stopped, so the caller can show the
    // new state right away. A completed torrent stays "completed" when paused.
    public async Task<DownloadView> PauseAsync(string hash, CancellationToken cancellationToken)
    {
        await qBittorrentClient.PauseAsync(hash, cancellationToken);
        return await WaitForStateAsync(
            hash, torrent => torrent.Status is not (TorrentStatus.Downloading or TorrentStatus.Queued or TorrentStatus.Checking), cancellationToken);
    }

    // Also used to retry a torrent in error: starting it again makes qBittorrent retry.
    public async Task<DownloadView> ResumeAsync(string hash, CancellationToken cancellationToken)
    {
        await qBittorrentClient.ResumeAsync(hash, cancellationToken);
        return await WaitForStateAsync(hash, torrent => torrent.Status != TorrentStatus.Paused, cancellationToken);
    }

    // deleteFiles has no default: the caller must say what happens to the files, so they
    // are never deleted, or kept, by omission.
    public async Task<RemovedDownloadView> RemoveAsync(string hash, bool? deleteFiles, CancellationToken cancellationToken)
    {
        if (deleteFiles is not { } shouldDeleteFiles)
        {
            throw new DomainValidationException(
                "deleteFiles", "Say whether to delete the downloaded files: deleteFiles=true or deleteFiles=false.");
        }

        var torrent = await qBittorrentClient.GetTorrentAsync(hash, cancellationToken) ?? throw TorrentNotFound();
        await qBittorrentClient.RemoveAsync(torrent.Hash, shouldDeleteFiles, cancellationToken);
        // The choice goes with the download, so a later download of the same torrent starts
        // without it. Its jobs stay as the history of what was imported.
        await libraryTargets.RemoveAsync(torrent.Hash, CancellationToken.None);

        // Removal is asynchronous too; waiting keeps the next list from showing the torrent again.
        var deadline = DateTimeOffset.UtcNow + StateChangeTimeout;
        while (DateTimeOffset.UtcNow < deadline && await qBittorrentClient.GetTorrentAsync(torrent.Hash, cancellationToken) is not null)
        {
            await Task.Delay(StateCheckInterval, cancellationToken);
        }

        return new RemovedDownloadView(torrent.Hash, torrent.Name, shouldDeleteFiles, torrent.SavePath);
    }

    // The library choice is saved before the torrent is sent, so a download can never finish
    // before Depot knows where it goes. The qBittorrent category still picks the save folder, as
    // set up in qBittorrent.
    //
    // Adds run one at a time, and each waits until qBittorrent lists the new torrent: two adds
    // of the same torrent would otherwise both pass the duplicate check, and the one qBittorrent
    // refuses would drop or overwrite the choice of the one it accepted.
    private async Task AddWithTargetAsync(
        TorrentHashes hashes,
        LibraryTarget? target,
        Func<AddTorrentOptions, Task> add,
        CancellationToken cancellationToken)
    {
        var hash = hashes.Id;
        await AddGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureNotAddedAsync(hashes, cancellationToken);

            // The torrent isn't in qBittorrent, so whatever is still recorded for it belongs to an
            // earlier download of it, and the new choice replaces it, Other included.
            var previous = await libraryTargets.GetAsync(hash, cancellationToken);
            if (target is null)
            {
                await libraryTargets.ForgetAsync(hash, cancellationToken);
            }
            else
            {
                await libraryTargets.SaveAsync(hash, target, clock.GetUtcNow(), cancellationToken);
            }

            try
            {
                await add(target is null
                    ? new AddTorrentOptions()
                    : new AddTorrentOptions(target.Type == MediaType.Movie ? "movies" : "series"));
            }
            catch (Exception exception)
            {
                await UndoChoiceAsync(hashes, target, previous, exception);
                throw;
            }

            await WaitUntilListedAsync(hash, cancellationToken);
        }
        finally
        {
            AddGate.Release();
        }

        if (target is not null)
        {
            logger.LogInformation(
                "Download {Hash} added for the {MediaType} library as \"{Title}\" (season {Season}).",
                hash, target.Type, target.Title, target.Season);
        }
    }

    // A failed add doesn't always mean qBittorrent lacks the torrent: a timeout or a dropped
    // request can come after it accepted it. So qBittorrent is asked, and the choice is only
    // dropped when the torrent isn't there. If qBittorrent refused it because it already has
    // it, the duplicate is reported and the choice recorded before this add is put back.
    private async Task UndoChoiceAsync(TorrentHashes hashes, LibraryTarget? target, LibraryTarget? previous, Exception failure)
    {
        var hash = hashes.Id;
        Torrent? existing;
        try
        {
            using var timeout = new CancellationTokenSource(StateChangeTimeout);
            existing = await FindExistingAsync(hashes, timeout.Token);
        }
        catch (Exception exception) when (exception is QBittorrentException or OperationCanceledException)
        {
            // Can't tell. Keeping the choice is the safe side: if the torrent isn't there, the
            // choice is never used, and the next add of the torrent replaces it.
            logger.LogWarning("Couldn't check whether qBittorrent took {Hash} after a failed add; its library choice is kept.", hash);
            return;
        }

        if (existing is null)
        {
            await libraryTargets.RemoveAsync(hash, CancellationToken.None);
            return;
        }

        if (failure is QBittorrentException { Failure: QBittorrentFailure.TorrentRejected })
        {
            if (previous is null)
            {
                await libraryTargets.RemoveAsync(hash, CancellationToken.None);
            }
            else if (previous != target)
            {
                await libraryTargets.SaveAsync(hash, previous, clock.GetUtcNow(), CancellationToken.None);
            }

            throw new DuplicateDownloadException(existing.Hash, existing.Name);
        }

        logger.LogWarning(
            "Adding {Hash} failed ({Reason}), but qBittorrent has the torrent, so its library choice is kept.", hash, failure.Message);
    }

    // qBittorrent can take a moment to list a torrent it accepted. Returns either way.
    private async Task WaitUntilListedAsync(string hash, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + StateChangeTimeout;
        while (await qBittorrentClient.GetTorrentAsync(hash, cancellationToken) is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(StateCheckInterval, cancellationToken);
        }
    }

    // qBittorrent applies pause and resume asynchronously and can take a second or two to
    // report the new state. This polls briefly and returns the latest state either way.
    private async Task<DownloadView> WaitForStateAsync(
        string hash, Func<Torrent, bool> reached, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + StateChangeTimeout;
        while (true)
        {
            var torrent = await qBittorrentClient.GetTorrentAsync(hash, cancellationToken) ?? throw TorrentNotFound();
            if (reached(torrent) || DateTimeOffset.UtcNow >= deadline)
            {
                var targets = await libraryTargets.GetAllAsync(cancellationToken);
                return DownloadView.From(
                    torrent, targets.TryGetValue(torrent.Hash, out var target) ? LibraryTargetView.From(target) : null);
            }

            await Task.Delay(StateCheckInterval, cancellationToken);
        }
    }

    // Checked here so a duplicate always gets the same answer, whatever the
    // qBittorrent version would do with it.
    private async Task EnsureNotAddedAsync(TorrentHashes hashes, CancellationToken cancellationToken)
    {
        if (await FindExistingAsync(hashes, cancellationToken) is { } existing)
        {
            throw new DuplicateDownloadException(existing.Hash, existing.Name);
        }
    }

    private async Task<Torrent?> FindExistingAsync(TorrentHashes hashes, CancellationToken cancellationToken) =>
        (await qBittorrentClient.GetTorrentsAsync(cancellationToken))
            .FirstOrDefault(torrent => hashes.SharesHashWith(torrent.InfoHashes) || hashes.Matches(torrent.Hash));

    private static QBittorrentException TorrentNotFound() =>
        new(QBittorrentFailure.TorrentNotFound, "This torrent isn't in qBittorrent anymore.");
}
