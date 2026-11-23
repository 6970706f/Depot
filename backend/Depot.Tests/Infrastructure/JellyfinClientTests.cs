using System.Net;
using Depot.Application.Jellyfin;
using Depot.Domain.Settings;
using Depot.Infrastructure.Jellyfin;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Infrastructure;

public sealed class JellyfinClientTests
{
    private readonly ScriptedJellyfin server = new();

    [Fact]
    public async Task Testing_checks_the_public_info_then_the_key()
    {
        var info = await Tester().TestAsync(Settings(ScriptedJellyfin.ApiKey), CancellationToken.None);

        Assert.Equal(new JellyfinServerInfo("Media box", "10.11.11"), info);
        Assert.Equal(["/System/Info/Public", "/System/Info"], server.Requests.Select(request => request.PathAndQuery));
        Assert.DoesNotContain("Token", server.Requests.First().Authorization);
    }

    [Fact]
    public async Task A_refused_key_is_reported()
    {
        var error = await Assert.ThrowsAsync<JellyfinException>(() =>
            Tester().TestAsync(Settings("ffffffffffffffffffffffffffffffff"), CancellationToken.None));

        Assert.Equal(JellyfinFailure.InvalidApiKey, error.Failure);
    }

    [Fact]
    public async Task A_server_that_has_not_finished_its_setup_is_reported()
    {
        server.WizardCompleted = false;

        var error = await Assert.ThrowsAsync<JellyfinException>(() =>
            Tester().TestAsync(Settings(ScriptedJellyfin.ApiKey), CancellationToken.None));

        Assert.Contains("setup wizard", error.Message);
    }

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        server.Responses["/System/Info"] = () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://elsewhere/") } };

        var error = await Assert.ThrowsAsync<JellyfinException>(() => Client().GetServerInfoAsync(CancellationToken.None));

        Assert.Contains("redirects", error.Message);
    }

    [Fact]
    public async Task Html_is_not_mistaken_for_jellyfin()
    {
        server.Responses["/System/Info"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>", System.Text.Encoding.UTF8, "text/html") };

        var error = await Assert.ThrowsAsync<JellyfinException>(() => Client().GetServerInfoAsync(CancellationToken.None));

        Assert.Equal(JellyfinFailure.UnexpectedResponse, error.Failure);
    }

    [Fact]
    public async Task Refresh_posts_with_the_key_only_in_the_header()
    {
        await Client().RefreshLibraryAsync(CancellationToken.None);

        var request = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/Library/Refresh", request.PathAndQuery);
        Assert.Contains($"Token=\"{ScriptedJellyfin.ApiKey}\"", request.Authorization);
    }

    [Fact]
    public async Task Without_settings_the_client_says_so()
    {
        var client = new JellyfinClient(new JellyfinHttp(new HttpClient(server)), new FixedJellyfinSettings(null));

        var error = await Assert.ThrowsAsync<JellyfinException>(() => client.GetServerInfoAsync(CancellationToken.None));

        Assert.Equal(JellyfinFailure.NotConfigured, error.Failure);
    }

    private JellyfinConnectionTester Tester() => new(new JellyfinHttp(new HttpClient(server)));

    private JellyfinClient Client() =>
        new(new JellyfinHttp(new HttpClient(server)), new FixedJellyfinSettings(Settings(ScriptedJellyfin.ApiKey)));

    private static JellyfinSettings Settings(string key) => JellyfinSettings.Create("http://jellyfin.test:8096", key);
}

public sealed class FixedJellyfinSettings(JellyfinSettings? settings) : IJellyfinSettingsRepository
{
    public Task<JellyfinSettings?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

    public Task SaveAsync(JellyfinSettings saved, CancellationToken cancellationToken) => Task.CompletedTask;
}
