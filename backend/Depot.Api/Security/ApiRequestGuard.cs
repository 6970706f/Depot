using Depot.Api.Errors;

namespace Depot.Api.Security;

// Lets an API request through only when it carries the access token and, if the browser says
// where it comes from (Origin), it comes from Depot's own page. Another site can't send the
// token header without a CORS preflight, which Depot never approves. The health check stays
// open for monitoring; it reveals nothing.
public sealed class ApiRequestGuard(RequestDelegate next, AccessToken token)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api") || path.StartsWithSegments("/api/health"))
        {
            await next(context);
            return;
        }

        if (!IsSameOrigin(context.Request))
        {
            await Problems.WriteAsync(context, StatusCodes.Status403Forbidden, "cross_origin", "Requests from other sites aren't accepted.");
            return;
        }

        if (!token.Matches(ReadBearerToken(context.Request)))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await Problems.WriteAsync(
                context, StatusCodes.Status401Unauthorized, "unauthorized",
                "Enter Depot's access token. Unless Auth:Token is set, it's in data/access-token on the server.");
            return;
        }

        await next(context);
    }

    // Browsers send Origin on every cross-site request and on same-site writes. Its absence
    // means a same-site read or a client that isn't a browser, which the token covers. Only the
    // host and port are compared, so a proxy that ends HTTPS in front of Depot works.
    private static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string Scheme = "Bearer ";
        return header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) ? header[Scheme.Length..].Trim() : null;
    }
}
