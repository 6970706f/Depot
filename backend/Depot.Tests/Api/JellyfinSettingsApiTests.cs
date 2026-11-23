using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Api;

public sealed class JellyfinSettingsApiTests : IDisposable
{
    private readonly ApiFactory factory = new();

    public void Dispose() => factory.Dispose();

    [Fact]
    public async Task The_key_is_never_returned()
    {
        using var client = factory.CreateAuthorizedClient();

        var saved = await client.PutAsJsonAsync("/api/settings/jellyfin", new { url = "http://localhost:8096", apiKey = ScriptedJellyfin.ApiKey });
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings/jellyfin");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True(settings.GetProperty("hasApiKey").GetBoolean());
        Assert.DoesNotContain(ScriptedJellyfin.ApiKey, settings.GetRawText());
    }

    [Fact]
    public async Task Status_reports_both_services_even_when_neither_is_set_up()
    {
        using var client = factory.CreateAuthorizedClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/status");

        Assert.False(status.GetProperty("qBittorrent").GetProperty("configured").GetBoolean());
        Assert.False(status.GetProperty("jellyfin").GetProperty("configured").GetBoolean());
    }
}
