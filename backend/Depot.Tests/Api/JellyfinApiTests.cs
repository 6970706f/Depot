using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Depot.Application.Jellyfin;
using Depot.Domain.Media;
using Depot.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using static Depot.Tests.TestSupport.FakeJellyfinClient;

namespace Depot.Tests.Api;

public sealed class JellyfinApiTests : IDisposable
{
    private const string Key = "a683810a6b6f459a849a7126a2673247";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    private readonly ApiFactory factory = new();

    public void Dispose() => factory.Dispose();

    [Theory]
    [InlineData("GET", "/api/settings/jellyfin")]
    [InlineData("PUT", "/api/settings/jellyfin")]
    [InlineData("POST", "/api/settings/jellyfin/test")]
    [InlineData("GET", "/api/jellyfin/status")]
    [InlineData("GET", "/api/jellyfin/libraries")]
    [InlineData("GET", "/api/jellyfin/items")]
    [InlineData("GET", "/api/jellyfin/items/1")]
    [InlineData("GET", "/api/jellyfin/items/1/images/primary")]
    [InlineData("GET", "/api/jellyfin/items/1/people/0/image")]
    [InlineData("GET", "/api/jellyfin/filters")]
    [InlineData("GET", "/api/jellyfin/stats")]
    [InlineData("POST", "/api/jellyfin/sync")]
    [InlineData("GET", "/api/jellyfin/sync")]
    [InlineData("GET", "/api/jellyfin/sync/runs")]
    [InlineData("GET", "/api/jellyfin/sync/runs/1")]
    public async Task EveryJellyfinEndpointNeedsTheToken(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET")
        {
            request.Content = JsonContent.Create(new { url = "http://localhost:8096", apiKey = Key });
        }

        using var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Jellyfin.Requests);
    }

    [Fact]
    public async Task TheKeyIsStoredEncryptedAndNeverSentBack()
    {
        var client = factory.CreateAuthorizedClient();

        using var saved = await client.PutAsJsonAsync("/api/settings/jellyfin", new { url = "http://localhost:8096", apiKey = Key });
        var savedBody = await saved.Content.ReadAsStringAsync();
        var read = await client.GetStringAsync("/api/settings/jellyfin");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("""{"url":"http://localhost:8096/","hasApiKey":true}""", savedBody);
        Assert.Equal(savedBody, read);
        var stored = ReadStoredKey();
        Assert.NotEqual(Key, stored);
        Assert.DoesNotContain(Key, stored);
    }

    [Fact]
    public async Task ChangingTheUrlNeedsTheKeyAgain()
    {
        var client = factory.CreateAuthorizedClient();
        await client.PutAsJsonAsync("/api/settings/jellyfin", new { url = "http://localhost:8096", apiKey = Key });

        using var moved = await client.PutAsJsonAsync("/api/settings/jellyfin", new { url = "http://attacker.example:8096", apiKey = "" });
        using var tested = await client.PostAsJsonAsync("/api/settings/jellyfin/test", new { url = "http://attacker.example:8096" });
        using var metadata = await client.PutAsJsonAsync("/api/settings/jellyfin", new { url = "http://169.254.169.254", apiKey = Key });

        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
        Assert.True(Errors(await moved.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("apiKey", out _));
        Assert.Equal(HttpStatusCode.BadRequest, tested.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, metadata.StatusCode);
        Assert.True(Errors(await metadata.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("url", out _));
        Assert.Equal("""{"url":"http://localhost:8096/","hasApiKey":true}""", await client.GetStringAsync("/api/settings/jellyfin"));
    }

    [Fact]
    public async Task TheRealClientFollowsNoRedirectAndTheKeyNeverReachesTheLogs()
    {
        using var elsewhere = new TinyHttpServer(_ => (200, "Content-Type: application/json\r\n", "{}"));
        using var jellyfin = new TinyHttpServer(path => path switch
        {
            "/System/Info/Public" => (200, "Content-Type: application/json\r\n",
                """{"ServerName":"nas","Version":"10.11.11","ProductName":"Jellyfin Server","Id":"748d0cbbde53417ca38d9ae059c39712","StartupWizardCompleted":true}"""),
            "/System/Info" => (302, $"Location: {elsewhere.Url}/steal\r\n", ""),
            _ => (401, "", ""),
        });
        using var real = new ApiFactory { UseRealJellyfin = true };
        var client = real.CreateAuthorizedClient();

        using var redirected = await client.PostAsJsonAsync("/api/settings/jellyfin/test", new { url = jellyfin.Url, apiKey = Key });
        await client.PutAsJsonAsync("/api/settings/jellyfin", new { url = jellyfin.Url, apiKey = Key });
        using var status = await client.GetAsync("/api/jellyfin/status");
        using var sync = await client.PostAsync("/api/jellyfin/sync", null);
        await WaitForSyncAsync(client);
        using var badKey = await client.PostAsJsonAsync("/api/settings/jellyfin/test", new { url = jellyfin.Url, apiKey = "bad-key-" + Key });

        var problem = await redirected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.BadGateway, redirected.StatusCode);
        Assert.Contains("redirects", problem.GetProperty("detail").GetString());
        Assert.Empty(elsewhere.Requests);
        // The key went to Jellyfin, in the header only.
        Assert.Contains(jellyfin.Requests, request => request.Contains($"Token=\"{Key}\"") && !request.Split("\r\n")[0].Contains(Key));
        Assert.Equal(HttpStatusCode.Accepted, sync.StatusCode);
        Assert.NotEmpty(real.Logs.Lines);
        Assert.DoesNotContain(real.Logs.Lines, line => line.Contains(Key));
        Assert.DoesNotContain(Key, await status.Content.ReadAsStringAsync());
        Assert.DoesNotContain(Key, await badKey.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StatusReportsJellyfinBesideQbittorrent()
    {
        var client = factory.CreateAuthorizedClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");
        var jellyfin = await client.GetFromJsonAsync<JsonElement>("/api/jellyfin/status");

        Assert.True(status.GetProperty("qbittorrent").GetProperty("connected").GetBoolean());
        Assert.Equal("10.11.11", status.GetProperty("jellyfin").GetProperty("version").GetString());
        Assert.Equal("Test server", jellyfin.GetProperty("serverName").GetString());
    }

    [Fact]
    public async Task ASyncNeedsJellyfinSetUp()
    {
        using var response = await factory.CreateAuthorizedClient().PostAsync("/api/jellyfin/sync", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_configured", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ASyncRunsInTheBackgroundAndTheLibraryCanBeBrowsed()
    {
        var client = await ConfiguredClientAsync();
        factory.Jellyfin.AddLibrary(Id(1), "Movies", MediaLibraryKind.Movies);
        factory.Jellyfin.AddItem(Id(1), Movie(3, "Dune", MovieFile(3, "1080p"), MovieFile(3, "720p")));
        factory.Jellyfin.AddItem(Id(1), Movie(4, "Arrival"));

        using var started = await client.PostAsync("/api/jellyfin/sync", null);
        var run = await started.Content.ReadFromJsonAsync<JsonElement>();
        var finished = await WaitForSyncAsync(client);

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.Equal($"/api/jellyfin/sync/runs/{run.GetProperty("id").GetInt32()}", started.Headers.Location!.ToString());
        Assert.Equal("succeeded", finished.GetProperty("status").GetString());
        Assert.Equal(2, finished.GetProperty("itemsAdded").GetInt32());

        var libraries = await client.GetFromJsonAsync<JsonElement>("/api/jellyfin/libraries");
        Assert.Equal(2, libraries[0].GetProperty("movies").GetInt32());

        var items = await client.GetFromJsonAsync<JsonElement>("/api/jellyfin/items?search=dun");
        Assert.Equal(1, items.GetProperty("total").GetInt32());
        var dune = items.GetProperty("items")[0];
        Assert.Equal(("movie", 2), (dune.GetProperty("kind").GetString(), dune.GetProperty("fileCount").GetInt32()));

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/jellyfin/items/{dune.GetProperty("id").GetInt32()}");
        Assert.Equal(2, detail.GetProperty("files").GetArrayLength());
        Assert.Equal("video", detail.GetProperty("files")[0].GetProperty("streams")[0].GetProperty("kind").GetString());

        var runs = await client.GetFromJsonAsync<JsonElement>("/api/jellyfin/sync/runs");
        Assert.Equal(1, runs.GetArrayLength());
    }

    [Fact]
    public async Task ASecondSyncWhileOneRunsIsRefused()
    {
        var client = await ConfiguredClientAsync();
        factory.Jellyfin.AddLibrary(Id(1), "Movies", MediaLibraryKind.Movies);
        using var hold = new SemaphoreSlim(0);
        factory.Jellyfin.BeforePage = (_, _) => hold.Wait(TimeSpan.FromSeconds(10));

        using var first = await client.PostAsync("/api/jellyfin/sync", null);
        using var second = await client.PostAsync("/api/jellyfin/sync", null);
        var running = await client.GetFromJsonAsync<JsonElement>("/api/jellyfin/sync");
        hold.Release();
        await WaitForSyncAsync(client);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sync_running", problem.GetProperty("code").GetString());
        Assert.Equal((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32(), problem.GetProperty("runId").GetInt32());
        Assert.True(running.GetProperty("isRunning").GetBoolean());
    }

    [Fact]
    public async Task ImagesComeThroughDepotCachedPrivately()
    {
        var client = await ConfiguredClientAsync();
        factory.Jellyfin.AddLibrary(Id(1), "Movies", MediaLibraryKind.Movies);
        factory.Jellyfin.AddItem(Id(1), Movie(3, "Dune"));
        factory.Jellyfin.Images[$"{Id(3)}/Primary/0/poster3"] = new MediaImageContent("image/jpeg", Jpeg);
        await client.PostAsync("/api/jellyfin/sync", null);
        await WaitForSyncAsync(client);
        var id = (await client.GetFromJsonAsync<JsonElement>("/api/jellyfin/items")).GetProperty("items")[0].GetProperty("id").GetInt32();

        using var versioned = await client.GetAsync($"/api/jellyfin/items/{id}/images/primary?width=320&v=poster3");
        using var unversioned = await client.GetAsync($"/api/jellyfin/items/{id}/images/primary?width=320");
        using var revalidate = new HttpRequestMessage(HttpMethod.Get, $"/api/jellyfin/items/{id}/images/primary?width=320");
        revalidate.Headers.TryAddWithoutValidation("If-None-Match", versioned.Headers.ETag!.ToString());
        using var notModified = await client.SendAsync(revalidate);
        using var missing = await client.GetAsync($"/api/jellyfin/items/{id}/images/thumb");
        using var unknown = await client.GetAsync("/api/jellyfin/items/9999/images/primary");

        Assert.Equal(HttpStatusCode.OK, versioned.StatusCode);
        Assert.Equal("image/jpeg", versioned.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Jpeg, await versioned.Content.ReadAsByteArrayAsync());
        var cache = versioned.Headers.CacheControl!;
        Assert.Equal((true, TimeSpan.FromDays(365)), (cache.Private, cache.MaxAge!.Value));
        Assert.Contains(cache.Extensions, extension => extension.Name == "immutable");
        Assert.Equal("nosniff", Assert.Single(versioned.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal((true, true), (unversioned.Headers.CacheControl!.Private, unversioned.Headers.CacheControl.NoCache));
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("image_not_found", (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("item_not_found", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        // Fetched from Jellyfin once; the rest came from Depot's cache.
        Assert.Equal(1, factory.Jellyfin.ImageRequests);
    }

    [Fact]
    public async Task TheLibraryPagesGetTheirFiltersAndTotals()
    {
        var client = await ConfiguredClientAsync();
        factory.Jellyfin.AddLibrary(Id(1), "Movies", MediaLibraryKind.Movies);
        factory.Jellyfin.AddItem(Id(1), Movie(3, "Dune") with { Genres = ["Science Fiction"], ProductionYear = 2021 });
        factory.Jellyfin.AddItem(Id(1), Movie(4, "Spirited Away") with { Genres = ["Animation"], ProductionYear = 2001, ProductionLocations = ["Japan"] });
        await client.PostAsync("/api/jellyfin/sync", null);
        await WaitForSyncAsync(client);

        var filters = await client.GetFromJsonAsync<JsonElement>("/api/jellyfin/filters?kind=movie");
        var stats = await client.GetFromJsonAsync<JsonElement>("/api/jellyfin/stats");
        var anime = await client.GetFromJsonAsync<JsonElement>("/api/jellyfin/items?anime=true&sort=release");
        var genre = await client.GetFromJsonAsync<JsonElement>("/api/jellyfin/items?genre=Science%20Fiction&year=2021");

        Assert.Equal(2, filters.GetProperty("genres").GetArrayLength());
        Assert.Equal(2021, filters.GetProperty("years")[0].GetProperty("value").GetInt32());
        Assert.Equal((2, 1), (stats.GetProperty("movies").GetInt32(), stats.GetProperty("animeMovies").GetInt32()));
        Assert.Equal("Spirited Away", anime.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.True(anime.GetProperty("items")[0].GetProperty("isAnime").GetBoolean());
        Assert.Equal("Dune", genre.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("/api/jellyfin/items?kind=folder", "kind")]
    [InlineData("/api/jellyfin/items?sort=random", "sort")]
    [InlineData("/api/jellyfin/items?page=0", "page")]
    [InlineData("/api/jellyfin/items?pageSize=1000", "pageSize")]
    [InlineData("/api/jellyfin/items/1/images/primary?width=0", "width")]
    [InlineData("/api/jellyfin/items?year=99", "year")]
    [InlineData("/api/jellyfin/filters?kind=folder", "kind")]
    public async Task QueriesAreValidatedByField(string path, string field)
    {
        using var response = await factory.CreateAuthorizedClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(Errors(await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty(field, out _));
    }

    [Fact]
    public async Task ThePageMayShowImagesItMadeFromWhatItFetched()
    {
        using var response = await factory.CreateClient().GetAsync("/");

        Assert.Contains("img-src 'self' data: blob:;", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    private async Task<HttpClient> ConfiguredClientAsync()
    {
        var client = factory.CreateAuthorizedClient();
        using var saved = await client.PutAsJsonAsync("/api/settings/jellyfin", new { url = "http://localhost:8096", apiKey = Key });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return client;
    }

    private static async Task<JsonElement> WaitForSyncAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var status = await client.GetFromJsonAsync<JsonElement>("/api/jellyfin/sync");
            if (!status.GetProperty("isRunning").GetBoolean())
            {
                return status.GetProperty("latestRun");
            }

            Assert.True(DateTime.UtcNow < deadline, "The sync didn't finish in time.");
            await Task.Delay(20);
        }
    }

    private string ReadStoredKey()
    {
        using var connection = new SqliteConnection($"Data Source={factory.DatabasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProtectedApiKey FROM JellyfinSettings";
        return (string)command.ExecuteScalar()!;
    }

    private static JsonElement Errors(JsonElement problem) => problem.GetProperty("errors");
}
