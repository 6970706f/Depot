using Depot.Application.QBittorrent;
using Depot.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Depot.Tests.Api;

// The whole API in memory, with its data and frontend in a temporary folder and qBittorrent
// replaced by a fake.
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Token = "test-token-0123456789abcdefghijklmnopqrstuvwxyz";

    private readonly TempDirectory root = new();

    public ApiFactory()
    {
        root.WriteFile("frontend/index.html", "<!doctype html><title>MediaFlow test page</title>");
    }

    public FakeQBittorrentClient QBittorrent { get; } = new();

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
        builder.ConfigureTestServices(services => services.AddSingleton<IQBittorrentClient>(QBittorrent));
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
