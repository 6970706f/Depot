using System.Net;
using System.Net.Http.Json;
using Depot.Tests.TestSupport;

namespace Depot.Tests.Api;

public sealed class HealthTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public HealthTests(ApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task Health_answers_without_a_token()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("healthy", body!["status"]);
    }

    [Fact]
    public async Task Responses_carry_the_security_headers()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }
}
