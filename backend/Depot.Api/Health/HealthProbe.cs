namespace Depot.Api.Health;

// "dotnet Depot.Api.dll --healthcheck" asks the Depot running in the same container for its
// health and exits with 0 when it answers 200 (healthy or degraded), 1 otherwise. The .NET
// runtime image has no curl, so Docker's HEALTHCHECK uses this.
public static class HealthProbe
{
    public static async Task<int> RunAsync()
    {
        // ASPNETCORE_HTTP_PORTS is how the .NET images choose the port, such as "8080" or "8080;8081".
        var port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080").Split(';', ',')[0].Trim();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync($"http://localhost:{port}/api/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
