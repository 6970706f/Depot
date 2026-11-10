using Depot.Application.Jellyfin;
using Depot.Application.QBittorrent;
using Depot.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Depot.Tests.Api;

// The whole API in memory, with its data and frontend in a temporary folder and qBittorrent
// and Jellyfin replaced by fakes. Every log line is kept in Logs.
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Token = "test-token-0123456789abcdefghijklmnopqrstuvwxyz";

    private readonly TempDirectory root = new();

    public ApiFactory()
    {
        root.WriteFile("frontend/index.html", "<!doctype html><title>MediaFlow test page</title>");
    }

    public FakeQBittorrentClient QBittorrent { get; } = new();

    public FakeJellyfinClient Jellyfin { get; } = new();

    // Replaces the fake with the real Jellyfin client, for tests of what really goes over HTTP.
    public bool UseRealJellyfin { get; init; }

    public LogCapture Logs { get; } = new();

    public string DatabasePath => root.Combine("data", "depot.db");

    // A client that sends the access token, as the interface does.
    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:DataDirectory", root.Combine("data"));
        builder.UseSetting("Frontend:Path", root.Combine("frontend"));
        builder.UseSetting("Auth:Token", Token);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs).AddFilter<LogCapture>(null, LogLevel.Trace));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IQBittorrentClient>(QBittorrent);
            if (!UseRealJellyfin)
            {
                services.AddSingleton<IJellyfinClient>(Jellyfin);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            root.Dispose();
        }
    }
}
