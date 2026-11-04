using System.Collections.Concurrent;
using System.Net;
using Depot.Application.QBittorrent;
using Depot.Application.Settings;
using Depot.Domain.Settings;
using Depot.Infrastructure;
using Depot.Infrastructure.QBittorrent;
using Depot.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Depot.Tests.Infrastructure;

// The real client and session store against a scripted qBittorrent Web API.
public sealed class QBittorrentClientTests
{
    private const string Hash = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";

    private readonly ManualClock clock = new(DateTimeOffset.UnixEpoch);
    private readonly ScriptedQBittorrent server = new();
    private readonly QBittorrentSessionStore sessions;
    private readonly QBittorrentClient client;

    public QBittorrentClientTests()
    {
        sessions = new QBittorrentSessionStore(clock);
        var httpClient = new HttpClient(server) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = QBittorrentHttp.MaxResponseBytes };
        client = new QBittorrentClient(new QBittorrentHttp(httpClient), sessions, new FixedSettings());
    }

    [Fact]
    public async Task LogsInOnceAndReusesTheSession()
    {
        Assert.Equal("v5.2.2", await client.GetVersionAsync(CancellationToken.None));
        Assert.Equal("v5.2.2", await client.GetVersionAsync(CancellationToken.None));

        Assert.Equal(1, server.Logins);
        Assert.All(server.Requests.Where(path => path.Contains("version")), path => Assert.Contains("SID=session-1", path));
    }

    [Fact]
    public async Task LogsInAgainOnceWhenTheSessionExpires()
    {
        await client.GetVersionAsync(CancellationToken.None);
        server.ExpireSessions();

        Assert.Equal("v5.2.2", await client.GetVersionAsync(CancellationToken.None));
        Assert.Equal(2, server.Logins);
    }

    [Fact]
    public async Task AWrongPasswordIsNotTriedAgainRightAway()
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
    public async Task RequestsAtOnceShareOneRefusedLogin()
    {
        server.RefuseLogins = HttpStatusCode.Unauthorized;
        server.LoginDelay = TimeSpan.FromMilliseconds(100);

        var attempts = Enumerable.Range(0, 10).Select(_ => Assert.ThrowsAsync<QBittorrentException>(() => client.GetVersionAsync(CancellationToken.None)));
        await Task.WhenAll(attempts);

        Assert.Equal(1, server.Logins);
    }

    [Fact]
    public async Task ABanIsNotTriedAgainEither()
    {
        server.RefuseLogins = HttpStatusCode.Forbidden;

        var first = await Assert.ThrowsAsync<QBittorrentException>(() => client.GetVersionAsync(CancellationToken.None));
        await Assert.ThrowsAsync<QBittorrentException>(() => client.GetVersionAsync(CancellationToken.None));

        Assert.Equal(QBittorrentFailure.Banned, first.Failure);
        Assert.Equal(1, server.Logins);
    }

    [Fact]
    public async Task SavingTheSettingsEndsTheWait()
    {
        server.RefuseLogins = HttpStatusCode.Unauthorized;
        await Assert.ThrowsAsync<QBittorrentException>(() => client.GetVersionAsync(CancellationToken.None));
        server.RefuseLogins = null;

        await sessions.ResetAsync();

        Assert.Equal("v5.2.2", await client.GetVersionAsync(CancellationToken.None));
        Assert.Equal(2, server.Logins);
    }

    [Fact]
    public async Task AnOversizedAnswerIsRefused()
    {
        server.TorrentList = new string(' ', QBittorrentHttp.MaxResponseBytes + 1);

        var exception = await Assert.ThrowsAsync<QBittorrentException>(() => client.GetTorrentsAsync(CancellationToken.None));

        Assert.Equal(QBittorrentFailure.UnexpectedResponse, exception.Failure);
        Assert.Contains("larger than", exception.Message);
    }

    [Fact]
    public async Task AHashThatIsntHexNeverReachesQbittorrent()
    {
        Assert.Null(await client.GetTorrentAsync($"{Hash}|{Hash}", CancellationToken.None));

        Assert.DoesNotContain(server.Requests, path => path.Contains("torrents/info"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "")]
    [InlineData(HttpStatusCode.OK, "Fails.")]
    [InlineData(HttpStatusCode.OK, "{\"success_count\":0,\"failure_count\":1,\"pending_count\":0}")]
    public async Task AnAddQbittorrentRefusesIsReported(HttpStatusCode status, string body)
    {
        server.AddAnswer = (status, body);

        var exception = await Assert.ThrowsAsync<QBittorrentException>(() =>
            client.AddMagnetAsync($"magnet:?xt=urn:btih:{Hash}", new AddTorrentOptions(), CancellationToken.None));

        Assert.Equal(QBittorrentFailure.TorrentRejected, exception.Failure);
    }

    [Fact]
    public void TheRealHttpClientFollowsNoRedirects()
    {
        using var data = new TempDirectory();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DataDirectory"] = data.Path })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration, new HostEnvironment(data.Path));
        using var provider = services.BuildServiceProvider();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(QBittorrentHttp));
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.False(sockets.UseCookies);
    }

    private sealed class FixedSettings : IQBittorrentSettingsRepository
    {
        public Task<QBittorrentSettings?> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<QBittorrentSettings?>(QBittorrentSettings.Create("http://qbittorrent:8080", "admin", "secret"));

        public Task SaveAsync(QBittorrentSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class HostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Depot.Tests";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    // Answers like qBittorrent 5: a login hands out SID=session-N, and other requests need the
    // latest session. Requests holds each request's path and cookie.
    private sealed class ScriptedQBittorrent : HttpMessageHandler
    {
        private int logins;
        private int validSession;

        public int Logins => logins;

        public ConcurrentQueue<string> Requests { get; } = [];

        public HttpStatusCode? RefuseLogins { get; set; }

        public TimeSpan LoginDelay { get; set; }

        public string TorrentList { get; set; } = "[]";

        public (HttpStatusCode Status, string Body) AddAnswer { get; set; } = (HttpStatusCode.OK, "Ok.");

        public void ExpireSessions() => validSession = -1;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            var cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join(";", cookies) : "";
            Requests.Enqueue($"{path} {cookie}");

            if (path == "/api/v2/auth/login")
            {
                var number = Interlocked.Increment(ref logins);
                await Task.Delay(LoginDelay, cancellationToken);
                if (RefuseLogins is { } refusal)
                {
                    return new HttpResponseMessage(refusal);
                }

                validSession = number;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
                response.Headers.Add("Set-Cookie", $"SID=session-{number}; HttpOnly; path=/");
                return response;
            }

            if (cookie != $"SID=session-{validSession}")
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            return path switch
            {
                "/api/v2/app/version" => Answer(HttpStatusCode.OK, "v5.2.2"),
                "/api/v2/torrents/info" => Answer(HttpStatusCode.OK, TorrentList),
                "/api/v2/torrents/add" => Answer(AddAnswer.Status, AddAnswer.Body),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Answer(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };
    }
}
