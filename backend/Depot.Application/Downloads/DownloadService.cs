using Depot.Application.Library;
using Depot.Application.QBittorrent;
using Depot.Domain.Common;
using Depot.Domain.Downloads;
using Depot.Domain.Library;
using Microsoft.Extensions.Logging;

namespace Depot.Application.Downloads;

public sealed class DownloadService(
    IQBittorrentClient qBittorrent,
    ILibraryChoiceRepository choices,
    TimeProvider clock,
    ILogger<DownloadService> logger)
{
    private static readonly TimeSpan StateChangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StateCheckInterval = TimeSpan.FromMilliseconds(250);

    // Shared by every request: adds must not overlap (see AddWithChoiceAsync).
    private static readonly SemaphoreSlim AddGate = new(1, 1);

    // An empty list means qBittorrent answered and has no torrents. Any failure to get an answer
    // throws QBittorrentException instead, so the two are never confused.
    public async Task<IReadOnlyList<DownloadView>> ListAsync(CancellationToken cancellationToken)
    {
        var torrents = await qBittorrent.GetTorrentsAsync(cancellationToken);
        var targets = await choices.GetAllAsync(cancellationToken);
        return torrents
            .OrderByDescending(torrent => torrent.AddedAt)
            .ThenBy(torrent => torrent.Name, StringComparer.OrdinalIgnoreCase)
            .Select(torrent => DownloadView.From(torrent, targets.GetValueOrDefault(torrent.Hash)))
            .ToList();
    }

    public async Task<DownloadAddedView> AddMagnetAsync(AddMagnetInput input, CancellationToken cancellationToken)
    {
        var magnet = MagnetLink.Parse(input.Magnet);
        var target = LibraryTarget.Create(input.MediaType, input.Title, input.Season);
        await AddWithChoiceAsync(magnet.Hashes, target, options =>
            qBittorrent.AddMagnetAsync(magnet.Uri, options, cancellationToken), cancellationToken);
        return new DownloadAddedView(magnet.Hashes.Id, magnet.DisplayName ?? magnet.Hashes.Id);
    }

    public async Task<DownloadAddedView> AddTorrentFileAsync(AddTorrentFileInput input, CancellationToken cancellationToken)
    {
        var torrentFile = TorrentFile.Parse(input.FileName, input.Content);
        var target = LibraryTarget.Create(input.MediaType, input.Title, input.Season);
        await AddWithChoiceAsync(torrentFile.Hashes, target, options =>
            qBittorrent.AddTorrentFileAsync(torrentFile.FileName, torrentFile.Content, options, cancellationToken), cancellationToken);
        return new DownloadAddedView(torrentFile.Hashes.Id, torrentFile.Name);
    }

    // Returns the download once qBittorrent reports it stopped, so the caller can show the new
    // state right away. A completed torrent stays "completed" when paused.
    public async Task<DownloadView> PauseAsync(string hash, CancellationToken cancellationToken)
    {
        await qBittorrent.PauseAsync(hash, cancellationToken);
        return await WaitForStateAsync(
            hash, torrent => torrent.Status is not (TorrentStatus.Downloading or TorrentStatus.Queued or TorrentStatus.Checking), cancellationToken);
    }

    public async Task<DownloadView> ResumeAsync(string hash, CancellationToken cancellationToken)
    {
        await qBittorrent.ResumeAsync(hash, cancellationToken);
        return await WaitForStateAsync(hash, torrent => torrent.Status != TorrentStatus.Paused, cancellationToken);
    }

    // deleteFiles has no default: the caller must say what happens to the files, so they are
    // never deleted, or kept, by omission.
    public async Task<RemovedDownloadView> RemoveAsync(string hash, bool? deleteFiles, CancellationToken cancellationToken)
    {
        if (deleteFiles is not { } shouldDeleteFiles)
        {
            throw new DomainValidationException(
                "deleteFiles", "Say whether to delete the downloaded files: deleteFiles=true or deleteFiles=false.");
        }

        var torrent = await qBittorrent.GetTorrentAsync(hash, cancellationToken) ?? throw TorrentNotFound();
        await qBittorrent.RemoveAsync(torrent.Hash, shouldDeleteFiles, cancellationToken);
        // The choice goes with the download, so a later download of the same torrent starts
        // without it. Imports already made stay in the library.
        await choices.RemoveAsync(torrent.Hash, CancellationToken.None);

        // Removal is asynchronous too; waiting keeps the next list from showing the torrent again.
        var deadline = clock.GetUtcNow() + StateChangeTimeout;
        while (clock.GetUtcNow() < deadline && await qBittorrent.GetTorrentAsync(torrent.Hash, cancellationToken) is not null)
        {
            await Task.Delay(StateCheckInterval, cancellationToken);
        }

        return new RemovedDownloadView(torrent.Hash, torrent.Name, shouldDeleteFiles);
    }

    // The library choice is saved before the torrent is sent, so a download can never finish
    // before Depot knows where it goes. The category ("movies" or "series") lets qBittorrent pick
    // the save folder as set up there.
    //
    // Adds run one at a time, and each waits until qBittorrent lists the new torrent: two adds of
    // the same torrent would otherwise both pass the duplicate check, and the one qBittorrent
    // refuses would drop or overwrite the choice of the one it accepted.
    private async Task AddWithChoiceAsync(
        TorrentHashes hashes,
        LibraryTarget? target,
        Func<AddTorrentOptions, Task> add,
        CancellationToken cancellationToken)
    {
        var hash = hashes.Id;
        await AddGate.WaitAsync(cancellationToken);
        try
        {
            if (await FindExistingAsync(hashes, cancellationToken) is { } existing)
            {
                throw new DuplicateDownloadException(existing.Hash, existing.Name);
            }

            // The torrent isn't in qBittorrent, so whatever is still recorded for it belongs to an
            // earlier download of it, and the new choice replaces it, Other included.
            if (target is null)
            {
                await choices.ForgetAsync(hash, cancellationToken);
            }
            else
            {
                await choices.SaveAsync(hash, target, clock.GetUtcNow(), cancellationToken);
            }

            try
            {
                await add(new AddTorrentOptions(target?.Type switch
                {
                    MediaType.Movie => "movies",
                    MediaType.Series => "series",
                    _ => null,
                }));
            }
            catch (Exception exception) when (target is not null)
            {
                await UndoChoiceAsync(hashes, exception);
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
    // dropped when the torrent isn't there.
    private async Task UndoChoiceAsync(TorrentHashes hashes, Exception failure)
    {
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
            logger.LogWarning("Couldn't check whether qBittorrent took {Hash} after a failed add; its library choice is kept.", hashes.Id);
            return;
        }

        if (existing is null)
        {
            await choices.RemoveAsync(hashes.Id, CancellationToken.None);
            return;
        }

        logger.LogWarning("Adding {Hash} failed ({Reason}), but qBittorrent has the torrent, so its library choice is kept.", hashes.Id, failure.Message);
    }

    // qBittorrent can take a moment to list a torrent it accepted. Returns either way.
    private async Task WaitUntilListedAsync(string hash, CancellationToken cancellationToken)
    {
        var deadline = clock.GetUtcNow() + StateChangeTimeout;
        while (await qBittorrent.GetTorrentAsync(hash, cancellationToken) is null && clock.GetUtcNow() < deadline)
        {
            await Task.Delay(StateCheckInterval, cancellationToken);
        }
    }

    // qBittorrent applies pause and resume asynchronously and can take a second or two to report
    // the new state. This polls briefly and returns the latest state either way.
    private async Task<DownloadView> WaitForStateAsync(string hash, Func<Torrent, bool> reached, CancellationToken cancellationToken)
    {
        var deadline = clock.GetUtcNow() + StateChangeTimeout;
        while (true)
        {
            var torrent = await qBittorrent.GetTorrentAsync(hash, cancellationToken) ?? throw TorrentNotFound();
            if (reached(torrent) || clock.GetUtcNow() >= deadline)
            {
                return DownloadView.From(torrent, await choices.GetAsync(torrent.Hash, cancellationToken));
            }

            await Task.Delay(StateCheckInterval, cancellationToken);
        }
    }

    private static QBittorrentException TorrentNotFound() =>
        new(QBittorrentFailure.TorrentNotFound, "This torrent isn't in qBittorrent anymore.");

    private async Task<Torrent?> FindExistingAsync(TorrentHashes hashes, CancellationToken cancellationToken) =>
        (await qBittorrent.GetTorrentsAsync(cancellationToken))
            .FirstOrDefault(torrent => hashes.SharesHashWith(torrent.InfoHashes) || hashes.Matches(torrent.Hash));
}
