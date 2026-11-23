using System.Net;
using Depot.Application.QBittorrent;
using Depot.Domain.Settings;
using Depot.Infrastructure.QBittorrent;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Infrastructure;

public sealed class QBittorrentConnectionTesterTests
{
    private readonly ScriptedQBittorrent server = new();

    [Theory]
    [InlineData(false, "v5.2.2")]
    [InlineData(true, "v4.6.7")]
    public async Task Logs_in_and_reads_the_version(bool version4, string expected)
    {
        server.Version4 = version4;

        Assert.Equal(expected, await Tester().TestAsync(Settings("secret"), CancellationToken.None));
        Assert.Contains(server.Requests, request => request.StartsWith("POST /api/v2/auth/logout"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_wrong_password_is_reported_as_invalid_credentials(bool version4)
    {
        server.Version4 = version4;

        var error = await Assert.ThrowsAsync<QBittorrentException>(() => Tester().TestAsync(Settings("wrong"), CancellationToken.None));

        Assert.Equal(QBittorrentFailure.InvalidCredentials, error.Failure);
        Assert.DoesNotContain("wrong", error.Message);
    }

    [Fact]
    public async Task A_ban_is_reported()
    {
        server.RefuseLogins = HttpStatusCode.Forbidden;

        var error = await Assert.ThrowsAsync<QBittorrentException>(() => Tester().TestAsync(Settings("secret"), CancellationToken.None));

        Assert.Equal(QBittorrentFailure.Banned, error.Failure);
    }

    [Fact]
    public async Task A_server_that_is_not_qbittorrent_is_reported()
    {
        var tester = new QBittorrentConnectionTester(new QBittorrentHttp(new HttpClient(new StaticHandler(HttpStatusCode.NotFound))));

        var error = await Assert.ThrowsAsync<QBittorrentException>(() => tester.TestAsync(Settings("secret"), CancellationToken.None));

        Assert.Equal(QBittorrentFailure.UnexpectedResponse, error.Failure);
    }

    [Fact]
    public async Task An_unreachable_address_is_reported()
    {
        var tester = new QBittorrentConnectionTester(new QBittorrentHttp(new HttpClient()));
        var settings = QBittorrentSettings.Create("http://127.0.0.1:1", "admin", "secret");

        var error = await Assert.ThrowsAsync<QBittorrentException>(() => tester.TestAsync(settings, CancellationToken.None));

        Assert.Equal(QBittorrentFailure.Unreachable, error.Failure);
    }

    private QBittorrentConnectionTester Tester() => new(new QBittorrentHttp(new HttpClient(server)));

    private static QBittorrentSettings Settings(string password) =>
        QBittorrentSettings.Create("http://qbittorrent.test:8080", "admin", password);

    private sealed class StaticHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(ScriptedQBittorrent.Text(status, "<html>not here</html>"));
    }
}
