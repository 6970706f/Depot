using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Depot.Tests.Api;

public sealed class ApiSecurityTests : IDisposable
{
    private const string Hash = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";

    private readonly ApiFactory factory = new();

    public void Dispose() => factory.Dispose();

    [Fact]
    public async Task TheHealthCheckIsOpenAndReportsEachCheck()
    {
        using var response = await factory.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Equal("healthy", body.GetProperty("checks").GetProperty("database").GetString());
        Assert.Equal("healthy", body.GetProperty("checks").GetProperty("qbittorrent").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token-0123456789abcdefghijklmnopqrstuvwxyz")]
    public async Task TheApiNeedsTheAccessToken(string? token)
    {
        var client = factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        using var list = await client.GetAsync("/api/downloads");
        using var settings = await client.PutAsJsonAsync("/api/organization/settings", new { moviesPath = "/tmp/a", seriesPath = "/tmp/b" });

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal("unauthorized", (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, settings.StatusCode);
    }

    [Fact]
    public async Task WithTheTokenTheApiAnswers()
    {
        using var response = await factory.CreateAuthorizedClient().GetAsync("/api/downloads");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("null")]
    [InlineData("http://localhost.evil.example")]
    public async Task RequestsFromOtherSitesAreRefusedEvenWithTheToken(string origin)
    {
        var client = factory.CreateAuthorizedClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/downloads/magnet")
        {
            Content = JsonContent.Create(new { magnet = $"magnet:?xt=urn:btih:{Hash}" }),
        };
        request.Headers.Add("Origin", origin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.QBittorrent.Added);
    }

    [Fact]
    public async Task RequestsFromDepotsOwnPageAreAccepted()
    {
        var client = factory.CreateAuthorizedClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/downloads/magnet")
        {
            Content = JsonContent.Create(new { magnet = $"magnet:?xt=urn:btih:{Hash}" }),
        };
        request.Headers.Add("Origin", "http://localhost");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(factory.QBittorrent.Added);
    }

    [Fact]
    public async Task UnknownHostNamesAreRefused()
    {
        // A DNS rebinding page reaches Depot under its own name.
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = "attacker.example";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheFrontendIsServedFromTheSameOrigin()
    {
        using var response = await factory.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("MediaFlow test page", await response.Content.ReadAsStringAsync());
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Contains("frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task AMagnetWithASecondLineIsRefused()
    {
        using var response = await factory.CreateAuthorizedClient().PostAsJsonAsync(
            "/api/downloads/magnet", new { magnet = $"magnet:?xt=urn:btih:{Hash}\nhttp://example.com/other.torrent" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("magnet", out _));
        Assert.Empty(factory.QBittorrent.Added);
    }

    [Fact]
    public async Task RemovingNeedsDeleteFiles()
    {
        using var response = await factory.CreateAuthorizedClient().DeleteAsync($"/api/downloads/{Hash}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadsAreReadByContent()
    {
        var client = factory.CreateAuthorizedClient();
        var torrent = Encoding.ASCII.GetBytes("d4:infod4:name4:Dune6:pieces20:xxxxxxxxxxxxxxxxxxxxee");

        using var accepted = await client.PostAsync("/api/downloads/torrent", Upload(torrent, "movie", "Dune"));
        using var refused = await client.PostAsync("/api/downloads/torrent", Upload(Encoding.ASCII.GetBytes("not a torrent"), "other", null));

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal("movies", Assert.Single(factory.QBittorrent.Added).Options.Category);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.True((await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("file", out _));
    }

    [Fact]
    public async Task EndpointsThatMakeQbittorrentWorkAreRateLimited()
    {
        var client = factory.CreateAuthorizedClient();
        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 31; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/api/settings/qbittorrent/test", new { url = "not a url" });
            statuses.Add(response.StatusCode);
        }

        Assert.All(statuses[..30], status => Assert.Equal(HttpStatusCode.BadRequest, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[30]);
    }

    private static MultipartFormDataContent Upload(byte[] content, string mediaType, string? title)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(content), "file", "upload.torrent" }, { new StringContent(mediaType), "mediaType" } };
        if (title is not null)
        {
            form.Add(new StringContent(title), "title");
        }

        return form;
    }
}
