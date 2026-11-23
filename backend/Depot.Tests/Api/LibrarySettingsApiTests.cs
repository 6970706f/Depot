using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Api;

public sealed class LibrarySettingsApiTests : IDisposable
{
    private readonly ApiFactory factory = new();
    private readonly TempDirectory media = new();

    public void Dispose()
    {
        factory.Dispose();
        media.Dispose();
    }

    [Fact]
    public async Task Defaults_apply_until_settings_are_saved()
    {
        using var client = factory.CreateAuthorizedClient();

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings/library");

        Assert.False(settings.GetProperty("isSaved").GetBoolean());
        Assert.Equal("~/media/movies", settings.GetProperty("moviesPath").GetString());
        Assert.False(settings.GetProperty("check").GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Saved_settings_are_checked_and_ready_when_every_folder_works()
    {
        foreach (var folder in new[] { "movies", "series", "downloads" })
        {
            Directory.CreateDirectory(media.Combine(folder));
        }

        using var client = factory.CreateAuthorizedClient();
        var response = await client.PutAsJsonAsync("/api/settings/library", Input(media.Combine("movies")));
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>();
        var reloaded = await client.GetFromJsonAsync<JsonElement>("/api/settings/library");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(saved.GetProperty("check").GetProperty("ready").GetBoolean());
        Assert.True(reloaded.GetProperty("isSaved").GetBoolean());
        Assert.Equal("hardlink", reloaded.GetProperty("importMode").GetString());
        Assert.Equal("/downloads", reloaded.GetProperty("pathMappings")[0].GetProperty("qbittorrentPath").GetString());
    }

    [Fact]
    public async Task Checking_reports_a_missing_folder_without_saving()
    {
        using var client = factory.CreateAuthorizedClient();

        var check = await (await client.PostAsJsonAsync("/api/settings/library/check", Input(media.Combine("missing"))))
            .Content.ReadFromJsonAsync<JsonElement>();
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings/library");

        Assert.False(check.GetProperty("ready").GetBoolean());
        var movies = check.GetProperty("folders").EnumerateArray().First(folder => folder.GetProperty("role").GetString() == "movies");
        Assert.Contains("doesn't exist", movies.GetProperty("problem").GetString());
        Assert.False(settings.GetProperty("isSaved").GetBoolean());
    }

    [Fact]
    public async Task Nested_libraries_are_refused_with_the_field()
    {
        using var client = factory.CreateAuthorizedClient();
        var input = Input(media.Combine("movies"));

        var response = await client.PutAsJsonAsync("/api/settings/library", input with { SeriesPath = media.Combine("movies", "series") });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("seriesPath", out _));
    }

    private LibraryInput Input(string moviesPath) => new(
        moviesPath,
        media.Combine("series"),
        "hardlink",
        30,
        [new MappingInput("/downloads", media.Combine("downloads"))]);

    private sealed record LibraryInput(string MoviesPath, string SeriesPath, string ImportMode, int PollingIntervalSeconds, MappingInput[] PathMappings);

    private sealed record MappingInput(string QbittorrentPath, string DepotPath);
}
