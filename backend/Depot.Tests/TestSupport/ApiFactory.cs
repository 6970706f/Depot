using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Depot.Tests.TestSupport;

// The real app on a private data folder, so tests never touch data/.
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string Token = "test-token-0123456789abcdefghijklmnopqrstuvwxyz";

    private readonly TempDirectory data = new();

    public string DataDirectory => data.Path;

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:DataDirectory", data.Path);
        builder.UseSetting("Auth:Token", Token);
        builder.UseSetting("AllowedHosts", "*");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            data.Dispose();
        }
    }
}
