using System.Net;
using Depot.Application.QBittorrent;
using Depot.Domain.Downloads;
using Depot.Domain.Settings;
using Depot.Infrastructure.QBittorrent;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Infrastructure;

// The real client and session store against a scripted qBittorrent Web API.
public sealed class QBittorrentClientTests
{
    private const string V1 = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";

    private readonly ManualClock clock = new(DateTimeOffset.UnixEpoch);
    private readonly ScriptedQBittorrent server = new();
    private readonly QBittorrentSessionStore sessions;
    private readonly FixedQBittorrentSettings settings = new(QBittorrentSettings.Create("http://qbittorrent.test:8080", "admin", "secret"));
    private readonly QBittorrentClient client;

    public QBittorrentClientTests()
    {
        sessions = new QBittorrentSessionStore(clock);
        client = new QBittorrentClient(new QBittorrentHttp(new HttpClient(server)), sessions, settings);
    }

    [Fact]
    public async Task Logs_in_once_and_reuses_the_session()
    {
        Assert.Equal("v5.2.2", await client.GetVersionAsync(CancellationToken.None));
        Assert.Equal("v5.2.2", await client.GetVersionAsync(CancellationToken.None));

        Assert.Equal(1, server.Logins);
        Assert.All(server.Requests.Where(request => request.Contains("version")), request => Assert.Contains("QBT_SID_8080=session-1", request));
    }

    [Fact]
    public async Task Logs_in_again_once_when_the_session_expires()
    {
        await client.GetVersionAsync(CancellationToken.None);
        server.ExpireSessions();

        Assert.Equal("v5.2.2", await client.GetVersionAsync(CancellationToken.None));
        Assert.Equal(2, server.Logins);
    }

    [Fact]
    public async Task Reads_the_version_4_session_cookie()
    {
        server.Version4 = true;

        Assert.Equal("v4.6.7", await client.GetVersionAsync(CancellationToken.None));
        Assert.Contains(server.Requests, request => request.Contains("version") && request.Contains("SID=session-1"));
    }

    [Fact]
    public async Task A_wrong_password_is_not_tried_again_right_away()
    {
        server.RefuseLogins = HttpStatusCode.Unauthorized;

        var first = await Assert.ThrowsAsync<QBittorrentException>(() => client.GetVersionAsync(CancellationToken.None));
        var second = await Assert.ThrowsAsync<QBittorrentException>(() => client.GetVersionAsync(CancellationToken.None));

        Assert.Equal(QBittorrentFailure.InvalidCredentials, first.Failure);
        Assert.Equal(QBittorrentFailure.InvalidCredentials, second.Failure);
        Assert.Contains("try again in 30 seconds", second.Message);
        Assert.Equal(1, server.Logins);

        // Each refusal in a row doubles the wait.
        clock.Advance(QBittorrentSessionStore.FirstLoginBackoff);
        await Assert.ThrowsAsync<QBittorrentException>(() => client.GetVersionAsync(CancellationToken.None));
        clock.Advance(QBittorrentSessionStore.FirstLoginBackoff);
        await Assert.ThrowsAsync<QBittorrentException>(() => client.GetVersionAsync(CancellationToken.None));
        Assert.Equal(2, server.Logins);

        clock.Advance(QBittorrentSessionStore.FirstLoginBackoff);
        server.RefuseLogins = null;
        Assert.Equal("v5.2.2", await client.GetVersionAsync(CancellationToken.None));
        Assert.Equal(3, server.Logins);
    }

    [Fact]
    public async Task Saving_the_settings_ends_the_wait()
    {
        server.RefuseLogins = HttpStatusCode.Unauthorized;
        await Assert.ThrowsAsync<QBittorrentException>(() => client.GetVersionAsync(CancellationToken.None));
        server.RefuseLogins = null;

        await sessions.SettingsSavedAsync();

        Assert.Equal("v5.2.2", await client.GetVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Without_settings_the_client_says_so()
    {
        settings.Settings = null;

        var error = await Assert.ThrowsAsync<QBittorrentException>(() => client.GetTorrentsAsync(CancellationToken.None));

        Assert.Equal(QBittorrentFailure.NotConfigured, error.Failure);
    }

    [Theory]
    [InlineData("downloading", 0.5, TorrentStatus.Downloading)]
    [InlineData("stalledDL", 0.5, TorrentStatus.Downloading)]
    [InlineData("metaDL", 0, TorrentStatus.Downloading)]
    [InlineData("pausedDL", 0.5, TorrentStatus.Paused)]
    [InlineData("stoppedDL", 0.5, TorrentStatus.Paused)]
    [InlineData("queuedDL", 0, TorrentStatus.Queued)]
    [InlineData("checkingUP", 1, TorrentStatus.Checking)]
    [InlineData("moving", 1, TorrentStatus.Checking)]
    [InlineData("stalledUP", 1, TorrentStatus.Completed)]
    [InlineData("stoppedUP", 1, TorrentStatus.Completed)]
    [InlineData("pausedUP", 1, TorrentStatus.Completed)]
    [InlineData("missingFiles", 1, TorrentStatus.Error)]
    [InlineData("somethingNew", 1, TorrentStatus.Completed)]
    public async Task Maps_the_states_of_both_versions(string state, double progress, TorrentStatus expected)
    {
        server.Torrents.Add(TorrentJson(V1, state, progress));

        var torrent = Assert.Single(await client.GetTorrentsAsync(CancellationToken.None));

        Assert.Equal(expected, torrent.Status);
    }

    [Fact]
    public async Task Reads_the_torrent_fields()
    {
        var json = TorrentJson(V1, "downloading", 0.25);
        json["eta"] = 120;
        json["content_path"] = "/downloads/Dune";
        server.Torrents.Add(json);

        var torrent = Assert.Single(await client.GetTorrentsAsync(CancellationToken.None));

        Assert.Equal(V1, torrent.Hash);
        Assert.Equal(V1, torrent.InfoHashes.V1);
        Assert.Equal(TimeSpan.FromSeconds(120), torrent.EstimatedTimeLeft);
        Assert.Equal("/downloads/Dune", torrent.ContentPath);
        Assert.Equal("movies", torrent.Category);
    }

    [Fact]
    public async Task An_unknown_eta_is_null()
    {
        var json = TorrentJson(V1, "downloading", 0.25);
        json["eta"] = 8_640_000;
        server.Torrents.Add(json);

        Assert.Null(Assert.Single(await client.GetTorrentsAsync(CancellationToken.None)).EstimatedTimeLeft);
    }

    [Fact]
    public async Task Finds_one_torrent_and_refuses_hashes_that_are_not_hex()
    {
        server.Torrents.Add(TorrentJson(V1, "stalledUP", 1));

        Assert.NotNull(await client.GetTorrentAsync(V1, CancellationToken.None));
        Assert.Null(await client.GetTorrentAsync(new string('0', 40), CancellationToken.None));
        Assert.Null(await client.GetTorrentAsync($"{V1}|other", CancellationToken.None));
    }

    [Theory]
    [InlineData(false, "/api/v2/torrents/stop", "/api/v2/torrents/start")]
    [InlineData(true, "/api/v2/torrents/pause", "/api/v2/torrents/resume")]
    public async Task Pause_and_resume_use_the_names_of_each_version(bool version4, string pause, string resume)
    {
        server.Version4 = version4;
        server.Torrents.Add(TorrentJson(V1, "downloading", 0.5));

        await client.PauseAsync(V1, CancellationToken.None);
        await client.ResumeAsync(V1, CancellationToken.None);

        Assert.Equal([pause, resume], server.Commands.Select(command => command.Path));
        Assert.All(server.Commands, command => Assert.Equal($"hashes={V1}", command.Body));
    }

    [Fact]
    public async Task Remove_says_whether_to_delete_the_files()
    {
        server.Torrents.Add(TorrentJson(V1, "stalledUP", 1));

        await client.RemoveAsync(V1, deleteFiles: true, CancellationToken.None);

        var command = Assert.Single(server.Commands);
        Assert.Equal("/api/v2/torrents/delete", command.Path);
        Assert.Contains("deleteFiles=true", command.Body);
    }

    [Fact]
    public async Task Commands_for_an_unknown_torrent_are_not_sent()
    {
        var error = await Assert.ThrowsAsync<QBittorrentException>(() => client.PauseAsync(V1, CancellationToken.None));

        Assert.Equal(QBittorrentFailure.TorrentNotFound, error.Failure);
        Assert.Empty(server.Commands);
    }

    public static Dictionary<string, object?> TorrentJson(string hash, string state, double progress) => new()
    {
        ["hash"] = hash,
        ["infohash_v1"] = hash,
        ["infohash_v2"] = "",
        ["name"] = "Dune.2021.1080p",
        ["state"] = state,
        ["size"] = 1000,
        ["completed"] = (long)(1000 * progress),
        ["amount_left"] = 1000 - (long)(1000 * progress),
        ["progress"] = progress,
        ["dlspeed"] = 10,
        ["upspeed"] = 5,
        ["eta"] = 0,
        ["category"] = "movies",
        ["save_path"] = "/downloads",
        ["content_path"] = "/downloads/Dune.2021.1080p",
        ["added_on"] = 1_790_000_000,
    };
}
