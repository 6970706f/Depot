using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Depot.Api.Health;

// Writes every health report in the same shape, e.g.
// {"status":"degraded","checks":{"database":"healthy","qbittorrent":"degraded"}},
// so the frontend never depends on the framework's default plain-text body. The endpoint is
// open to anyone, so the reasons stay out of it; /api/status explains qBittorrent's.
public static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(new HealthResponse(
            ToText(report.Status),
            report.Entries.ToDictionary(entry => entry.Key, entry => ToText(entry.Value.Status))));

    private static string ToText(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "healthy",
        HealthStatus.Degraded => "degraded",
        _ => "unhealthy",
    };
}

public sealed record HealthResponse(string Status, IReadOnlyDictionary<string, string> Checks);
