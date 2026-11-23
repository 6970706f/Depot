using Depot.Infrastructure.Persistence;

namespace Depot.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Open to monitoring without the access token, so it reveals nothing but the status. Depot
        // is healthy when its database answers; qBittorrent and Jellyfin are in /api/status.
        app.MapGet("/api/health", async (DepotDbContext database, CancellationToken cancellationToken) =>
            await database.Database.CanConnectAsync(cancellationToken)
                ? Results.Ok(new { status = "healthy" })
                : Results.Json(new { status = "unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable));

        // Lets the interface check a token before keeping it. The guard has already checked it.
        app.MapGet("/api/session", () => Results.NoContent());
    }

    // "dotnet Depot.Api.dll --healthcheck" asks the Depot running in the same container for its
    // health and exits with 0 when it answers 200. The .NET runtime image has no curl, so the
    // Docker HEALTHCHECK uses this.
    public static async Task<int> ProbeAsync()
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
