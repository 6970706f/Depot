using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Api;

// Lowercase enum values in query strings, as the interface sends them.
public sealed class QueryValueTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public QueryValueTests(ApiFactory factory) => this.factory = factory;

    [Theory]
    [InlineData("/api/imports?status=failed")]
    [InlineData("/api/imports?status=Waiting")]
    public async Task Import_filters_accept_lowercase(string url)
    {
        using var client = factory.CreateAuthorizedClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Media_kinds_accept_lowercase_and_reach_the_jellyfin_check()
    {
        using var client = factory.CreateAuthorizedClient();

        var response = await client.GetAsync("/api/media?kind=series&sort=added&search=");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Jellyfin isn't set up in tests, which is the next thing checked.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_configured", problem.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/api/imports?status=gone", "status")]
    [InlineData("/api/media?kind=music", "kind")]
    [InlineData("/api/media?kind=1", "kind")]
    public async Task Unknown_values_name_the_field(string url, string field)
    {
        using var client = factory.CreateAuthorizedClient();

        var response = await client.GetAsync(url);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task An_id_that_is_not_a_jellyfin_id_is_not_found()
    {
        using var client = factory.CreateAuthorizedClient();

        var response = await client.GetAsync("/api/media/not-an-id/images/primary");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
