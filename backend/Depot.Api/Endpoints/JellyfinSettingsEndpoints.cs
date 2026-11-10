using Depot.Api.Security;
using Depot.Application.Settings;

namespace Depot.Api.Endpoints;

public static class JellyfinSettingsEndpoints
{
    public static IEndpointRouteBuilder MapJellyfinSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/settings/jellyfin");

        // { url, hasApiKey }: the key itself is never returned.
        group.MapGet("/", (JellyfinSettingsService service, CancellationToken cancellationToken) =>
            service.GetAsync(cancellationToken));

        group.MapPut("/", (JellyfinSettingsInput input, JellyfinSettingsService service, CancellationToken cancellationToken) =>
            service.SaveAsync(input, cancellationToken))
            .RequireRateLimiting(RateLimits.Expensive);

        group.MapPost("/test", (JellyfinSettingsInput input, JellyfinSettingsService service, CancellationToken cancellationToken) =>
            service.TestConnectionAsync(input, cancellationToken))
            .RequireRateLimiting(RateLimits.Expensive);

        return routes;
    }
}
