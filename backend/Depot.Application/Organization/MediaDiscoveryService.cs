using Depot.Application.QBittorrent;
using Depot.Domain.Organization;
using Microsoft.Extensions.Logging;

namespace Depot.Application.Organization;

public sealed class MediaDiscoveryService(
    IQBittorrentClient qBittorrentClient,
    ILibraryTargetRepository libraryTargets,
    IOrganizationJobRepository jobs,
    TimeProvider clock,
    ILogger<MediaDiscoveryService> logger)
{
    // Examines every finished torrent that has a library choice and wasn't examined before,
    // including those that finished while Depot was off, and records one job per video.
    // Torrents without a choice are never touched. Returns how many were examined.
    public async Task<int> DiscoverAsync(CancellationToken cancellationToken)
    {
        var pending = await libraryTargets.GetPendingAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        var torrents = await qBittorrentClient.GetTorrentsAsync(cancellationToken);
        var examined = 0;
        foreach (var torrent in torrents.Where(torrent => torrent.IsFullyDownloaded))
        {
            if (!pending.TryGetValue(torrent.Hash, out var target))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<Depot.Domain.Downloads.TorrentContentFile> files;
            try
            {
                files = await qBittorrentClient.GetTorrentFilesAsync(torrent.Hash, cancellationToken);
            }
            catch (QBittorrentException exception) when (exception.Failure == QBittorrentFailure.TorrentNotFound)
            {
                continue;
            }

            // One job per video; its subtitles come along when it's imported. Samples, other
            // subtitles and everything else stay in the download.
            var now = clock.GetUtcNow();
            var videos = files
                .Where(file => file.IsDownloaded && MediaFiles.IsVideo(file.RelativePath) && !MediaFiles.IsSample(file.RelativePath))
                .ToList();
            // A series download is sorted into episodes, specials and extras; anything uncertain
            // waits for a person. A movie download goes to the movie's folder as before.
            var classifications = target.Type == MediaType.Series
                ? SeriesVideos.Classify(videos.Select(video => new SeriesVideo(video.RelativePath, video.SizeBytes)).ToList(), target.Season!.Value)
                : null;
            var newJobs = videos
                .Select(video => classifications?[video.RelativePath] ?? new VideoClassification(VideoPlacement.Movie, null))
                .Zip(videos, (classification, video) => OrganizationJob.Discover(
                    torrent.Hash, torrent.Name, video.RelativePath, video.SizeBytes, target,
                    classification.Placement, classification.ReviewReason, now))
                .ToList();
            await jobs.AddScanAsync(torrent.Hash, newJobs, now, cancellationToken);
            examined++;

            var toReview = newJobs.Count(job => job.Status == OrganizationJobStatus.NeedsReview);
            if (toReview > 0)
            {
                logger.LogInformation("{Count} video(s) of {TorrentName} wait for review before importing.", toReview, torrent.Name);
            }

            if (newJobs.Count == 0)
            {
                logger.LogWarning("Torrent {TorrentName} ({Hash}) is complete but has no videos; nothing to organize.", torrent.Name, torrent.Hash);
            }
            else
            {
                logger.LogInformation(
                    "Torrent {TorrentName} ({Hash}) is complete: {VideoCount} video(s) go to the {MediaType} library as \"{Title}\" (season {Season}).",
                    torrent.Name, torrent.Hash, newJobs.Count, target.Type, target.Title, target.Season);
            }
        }

        return examined;
    }
}
