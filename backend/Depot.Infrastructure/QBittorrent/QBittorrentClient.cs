using System.Net;
using System.Text.RegularExpressions;
using Depot.Application.QBittorrent;
using Depot.Application.Settings;
using Depot.Domain.Downloads;

namespace Depot.Infrastructure.QBittorrent;

public sealed partial class QBittorrentClient(
    QBittorrentHttp http,
    QBittorrentSessionStore sessions,
    IQBittorrentSettingsRepository settingsRepository) : IQBittorrentClient
{
    public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/v2/app/version", createContent: null, cancellationToken);
        return await QBittorrentResponses.ReadVersionAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<Torrent>> GetTorrentsAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/v2/torrents/info", createContent: null, cancellationToken);
        return await QBittorrentResponses.ReadTorrentsAsync(response, cancellationToken);
    }

    public Task AddMagnetAsync(string magnetUri, AddTorrentOptions options, CancellationToken cancellationToken) =>
        AddAsync(() =>
        {
            var form = CreateAddForm(options);
            form.Add(new StringContent(magnetUri), "urls");
            return form;
        }, cancellationToken);

    public Task AddTorrentFileAsync(string fileName, byte[] content, AddTorrentOptions options, CancellationToken cancellationToken) =>
        AddAsync(() =>
        {
            var form = CreateAddForm(options);
            form.Add(new ByteArrayContent(content), "torrents", fileName);
            return form;
        }, cancellationToken);

    // qBittorrent 5 renamed pause/resume to stop/start. The new name is tried first.
    public Task PauseAsync(string hash, CancellationToken cancellationToken) =>
        RunTorrentCommandAsync(hash, ["api/v2/torrents/stop", "api/v2/torrents/pause"], extraFields: [], cancellationToken);

    public Task ResumeAsync(string hash, CancellationToken cancellationToken) =>
        RunTorrentCommandAsync(hash, ["api/v2/torrents/start", "api/v2/torrents/resume"], extraFields: [], cancellationToken);

    public Task RemoveAsync(string hash, bool deleteFiles, CancellationToken cancellationToken) =>
        RunTorrentCommandAsync(
            hash,
            ["api/v2/torrents/delete"],
            [new KeyValuePair<string, string>("deleteFiles", deleteFiles ? "true" : "false")],
            cancellationToken);

    private async Task AddAsync(Func<HttpContent> createContent, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/v2/torrents/add", createContent, cancellationToken);
        // 409 (Web API 2.11+) means invalid or already added; 415 means an unreadable .torrent file.
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnsupportedMediaType)
        {
            throw TorrentRejected();
        }

        var body = await QBittorrentResponses.ReadSuccessBodyAsync(response, cancellationToken);
        if (!QBittorrentResponses.IsAddAccepted(body))
        {
            throw TorrentRejected();
        }
    }

    private static MultipartFormDataContent CreateAddForm(AddTorrentOptions options)
    {
        var form = new MultipartFormDataContent();
        if (!string.IsNullOrWhiteSpace(options.Category))
        {
            form.Add(new StringContent(options.Category), "category");
        }

        if (!string.IsNullOrWhiteSpace(options.SavePath))
        {
            form.Add(new StringContent(options.SavePath), "savepath");
        }

        return form;
    }

    // qBittorrent answers 200 even for hashes it doesn't know, so the torrent is looked up first.
    private async Task RunTorrentCommandAsync(
        string hash,
        string[] paths,
        KeyValuePair<string, string>[] extraFields,
        CancellationToken cancellationToken)
    {
        await EnsureTorrentExistsAsync(hash, cancellationToken);

        foreach (var path in paths)
        {
            using var response = await SendAsync(
                HttpMethod.Post,
                path,
                () => new FormUrlEncodedContent([new KeyValuePair<string, string>("hashes", hash), .. extraFields]),
                cancellationToken);

            // A 404 means this qBittorrent version uses the other name for the command.
            if (response.StatusCode == HttpStatusCode.NotFound && path != paths[^1])
            {
                continue;
            }

            await QBittorrentResponses.ReadSuccessBodyAsync(response, cancellationToken);
            return;
        }
    }

    public async Task<Torrent?> GetTorrentAsync(string hash, CancellationToken cancellationToken)
    {
        // The hash goes into qBittorrent's "a|b" list syntax, so anything but hex is refused here.
        if (!TorrentHashPattern().IsMatch(hash))
        {
            return null;
        }

        using var response = await SendAsync(
            HttpMethod.Get, $"api/v2/torrents/info?hashes={hash}", createContent: null, cancellationToken);
        var torrents = await QBittorrentResponses.ReadTorrentsAsync(response, cancellationToken);
        return torrents.Count == 0 ? null : torrents[0];
    }

    public async Task<IReadOnlyList<TorrentContentFile>> GetTorrentFilesAsync(string hash, CancellationToken cancellationToken)
    {
        if (!TorrentHashPattern().IsMatch(hash))
        {
            throw TorrentNotFound();
        }

        using var response = await SendAsync(
            HttpMethod.Get, $"api/v2/torrents/files?hash={hash}", createContent: null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw TorrentNotFound();
        }

        return await QBittorrentResponses.ReadFilesAsync(response, cancellationToken);
    }

    private async Task EnsureTorrentExistsAsync(string hash, CancellationToken cancellationToken)
    {
        if (await GetTorrentAsync(hash, cancellationToken) is null)
        {
            throw TorrentNotFound();
        }
    }

    // Sends with the shared session. A rejected session (expired, or qBittorrent restarted)
    // gets one new login and one retry; createContent builds a fresh body for that retry.
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        Func<HttpContent>? createContent,
        CancellationToken cancellationToken)
    {
        var settings = await settingsRepository.GetAsync(cancellationToken)
            ?? throw new QBittorrentException(
                QBittorrentFailure.NotConfigured,
                "qBittorrent isn't set up yet. Add its URL, username and password in Settings.");

        var sessionCookie = await sessions.GetSessionCookieAsync(settings, http.LogInAsync, cancellationToken);
        var response = await http.SendAsync(settings.Url, method, path, sessionCookie, createContent?.Invoke(), cancellationToken);
        if (!IsSessionRejected(response))
        {
            return response;
        }

        response.Dispose();
        await sessions.InvalidateAsync(sessionCookie);
        sessionCookie = await sessions.GetSessionCookieAsync(settings, http.LogInAsync, cancellationToken);
        response = await http.SendAsync(settings.Url, method, path, sessionCookie, createContent?.Invoke(), cancellationToken);
        if (IsSessionRejected(response))
        {
            response.Dispose();
            throw QBittorrentHttp.InvalidCredentials();
        }

        return response;
    }

    private static bool IsSessionRejected(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;

    private static QBittorrentException TorrentNotFound() =>
        new(QBittorrentFailure.TorrentNotFound, "This torrent isn't in qBittorrent anymore.");

    private static QBittorrentException TorrentRejected() =>
        new(QBittorrentFailure.TorrentRejected, "qBittorrent didn't accept the torrent. It may be invalid or already in qBittorrent.");

    // A v1 info hash (40 hex characters) or a v2 one (64).
    [GeneratedRegex("^([0-9a-fA-F]{40}|[0-9a-fA-F]{64})$")]
    private static partial Regex TorrentHashPattern();
}
