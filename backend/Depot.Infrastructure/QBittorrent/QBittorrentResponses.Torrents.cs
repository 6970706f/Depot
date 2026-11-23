using System.Text.Json;
using Depot.Domain.Downloads;

namespace Depot.Infrastructure.QBittorrent;

public static partial class QBittorrentResponses
{
    // qBittorrent reports an unknown ETA as 100 days.
    private const long InfiniteEtaSeconds = 8_640_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task<IReadOnlyList<Torrent>> ReadTorrentsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var torrents = await ReadJsonAsync<List<TorrentInfo>>(response, cancellationToken);
        if (torrents is null || torrents.Any(torrent => string.IsNullOrEmpty(torrent.Hash)))
        {
            throw QBittorrentHttp.UnexpectedResponse(response);
        }

        return torrents.Select(ToTorrent).ToList();
    }

    public static async Task<IReadOnlyList<TorrentContentFile>> ReadFilesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var files = await ReadJsonAsync<List<ContentFileInfo>>(response, cancellationToken);
        if (files is null || files.Any(file => string.IsNullOrEmpty(file.Name)))
        {
            throw QBittorrentHttp.UnexpectedResponse(response);
        }

        // "index" appeared in Web API 2.8.2; older versions list the files in index order.
        return files
            .Select((file, position) => new TorrentContentFile(
                file.Index ?? position, file.Name!.Replace('\\', '/'), file.Size, file.Progress, file.Priority))
            .ToList();
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await ReadSuccessBodyAsync(response, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException)
        {
            throw QBittorrentHttp.UnexpectedResponse(response);
        }
    }

    private static Torrent ToTorrent(TorrentInfo info)
    {
        var status = ToStatus(info.State, info.Progress);
        var eta = status == TorrentStatus.Downloading && info.Eta is >= 0 and < InfiniteEtaSeconds
            ? TimeSpan.FromSeconds(info.Eta)
            : (TimeSpan?)null;

        return new Torrent(
            info.Hash!.ToLowerInvariant(),
            ReadHashes(info),
            info.Name ?? info.Hash!,
            status,
            info.Size,
            info.Completed,
            info.AmountLeft,
            info.Progress,
            info.Dlspeed,
            info.Upspeed,
            eta,
            string.IsNullOrEmpty(info.Category) ? null : info.Category,
            info.SavePath ?? string.Empty,
            string.IsNullOrEmpty(info.ContentPath) ? null : info.ContentPath,
            DateTimeOffset.FromUnixTimeSeconds(info.AddedOn));
    }

    // qBittorrent 4.4+ reports both hashes (v2 in full, 64 characters). Older versions only
    // report the id, which is then the v1 hash.
    private static TorrentHashes ReadHashes(TorrentInfo info)
    {
        var v1 = string.IsNullOrEmpty(info.InfohashV1) ? null : info.InfohashV1.ToLowerInvariant();
        var v2 = info.InfohashV2 is { Length: >= 40 } fullV2 ? fullV2[..40].ToLowerInvariant() : null;
        return v1 is null && v2 is null ? new TorrentHashes(info.Hash!.ToLowerInvariant(), null) : new TorrentHashes(v1, v2);
    }

    // qBittorrent 5 renamed the paused states to "stopped"; both are accepted.
    private static TorrentStatus ToStatus(string? state, double progress) => state switch
    {
        "error" or "missingFiles" => TorrentStatus.Error,
        "pausedDL" or "stoppedDL" => TorrentStatus.Paused,
        "queuedDL" => TorrentStatus.Queued,
        "checkingDL" or "checkingUP" or "checkingResumeData" or "moving" or "allocating" => TorrentStatus.Checking,
        "downloading" or "forcedDL" or "metaDL" or "forcedMetaDL" or "stalledDL" => TorrentStatus.Downloading,
        "uploading" or "forcedUP" or "stalledUP" or "queuedUP" or "pausedUP" or "stoppedUP" => TorrentStatus.Completed,
        _ => progress >= 1 ? TorrentStatus.Completed : TorrentStatus.Downloading,
    };

    // One entry of GET /api/v2/torrents/files. Name is relative to the torrent's save path.
    private sealed record ContentFileInfo(int? Index, string? Name, long Size, double Progress, int Priority);

    // One entry of GET /api/v2/torrents/info. Only the fields Depot uses.
    private sealed record TorrentInfo(
        string? Hash,
        string? InfohashV1,
        string? InfohashV2,
        string? Name,
        string? State,
        long Size,
        long Completed,
        long AmountLeft,
        double Progress,
        long Dlspeed,
        long Upspeed,
        long Eta,
        string? Category,
        string? SavePath,
        string? ContentPath,
        long AddedOn);
}
