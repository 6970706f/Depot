using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Depot.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace Depot.Tests.Api;

public sealed class QBittorrentSettingsApiTests : IDisposable
{
    private readonly ApiFactory factory = new();

    public void Dispose() => factory.Dispose();

    [Fact]
    public async Task Settings_start_empty()
    {
        using var client = factory.CreateAuthorizedClient();

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings/qbittorrent");

        Assert.Equal(JsonValueKind.Null, settings.GetProperty("url").ValueKind);
        Assert.False(settings.GetProperty("hasPassword").GetBoolean());
    }

    [Fact]
    public async Task Saved_settings_never_return_or_store_the_password_in_clear()
    {
        using var client = factory.CreateAuthorizedClient();

        var saved = await client.PutAsJsonAsync("/api/settings/qbittorrent", new { url = "http://localhost:8080", username = "admin", password = "hunter2-secret" });
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings/qbittorrent");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("http://localhost:8080/", settings.GetProperty("url").GetString());
        Assert.True(settings.GetProperty("hasPassword").GetBoolean());
        Assert.DoesNotContain("hunter2", settings.GetRawText());
        Assert.DoesNotContain("hunter2", ReadDatabase());
    }

    [Fact]
    public async Task Invalid_values_name_the_field()
    {
        using var client = factory.CreateAuthorizedClient();

        var response = await client.PutAsJsonAsync("/api/settings/qbittorrent", new { url = "nope", username = "admin", password = "x" });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("url", out _));
    }

    [Fact]
    public async Task Testing_an_unreachable_server_answers_502_with_the_reason()
    {
        using var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/api/settings/qbittorrent/test", new { url = "http://127.0.0.1:1", username = "admin", password = "x" });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("unreachable", problem.GetProperty("code").GetString());
    }

    private string ReadDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(factory.DataDirectory, "depot.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(Value) FROM Settings";
        return command.ExecuteScalar() as string ?? "";
    }
}
