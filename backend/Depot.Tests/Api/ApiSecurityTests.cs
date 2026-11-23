using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Depot.Api.Security;
using Depot.Infrastructure.Storage;
using Depot.Tests.TestSupport;
using Microsoft.Extensions.Configuration;

namespace Depot.Tests.Api;

public sealed class ApiSecurityTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ApiSecurityTests(ApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task Api_refuses_requests_without_the_token()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", await CodeOf(response));
    }

    [Fact]
    public async Task Api_refuses_a_wrong_token()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");

        var response = await client.GetAsync("/api/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_accepts_the_token()
    {
        using var client = factory.CreateAuthorizedClient();

        var response = await client.GetAsync("/api/session");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Api_refuses_other_origins_even_with_the_token()
    {
        using var client = factory.CreateAuthorizedClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Add("Origin", "https://evil.example");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("cross_origin", await CodeOf(response));
    }

    [Fact]
    public async Task Api_accepts_its_own_origin()
    {
        using var client = factory.CreateAuthorizedClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public void A_missing_token_file_is_created_private_and_reused()
    {
        using var folder = new TempDirectory();
        var storage = new StoragePaths(folder.Path);
        var configuration = new ConfigurationBuilder().Build();

        var first = AccessToken.Load(configuration, storage);
        var stored = File.ReadAllText(storage.AccessTokenPath).Trim();
        var second = AccessToken.Load(configuration, storage);

        Assert.True(stored.Length >= AccessToken.MinLength);
        Assert.True(first.Matches(stored));
        Assert.True(second.Matches(stored));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(StoragePaths.PrivateFileMode, File.GetUnixFileMode(storage.AccessTokenPath));
        }
    }

    [Fact]
    public void A_short_configured_token_is_refused()
    {
        using var folder = new TempDirectory();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Token"] = "short" })
            .Build();

        Assert.Throws<InvalidOperationException>(() => AccessToken.Load(configuration, new StoragePaths(folder.Path)));
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("code").GetString();
    }
}
