using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Depot.Tests.TestSupport;

// A scripted qBittorrent Web API, as an HttpMessageHandler. Behaves like 5.x by default
// (QBT_SID_<port> cookie, stop/start); set Version4 for the 4.x names.
public sealed class ScriptedQBittorrent : HttpMessageHandler
{
    private int sessionNumber;
    private string? validSession;

    public string Username { get; set; } = "admin";

    public string Password { get; set; } = "secret";

    public bool Version4 { get; set; }

    // Forces the login answer, such as Forbidden for a banned address.
    public HttpStatusCode? RefuseLogins { get; set; }

    public int Logins;

    public ConcurrentQueue<string> Requests { get; } = [];

    public List<Dictionary<string, object?>> Torrents { get; } = [];

    public Dictionary<string, List<Dictionary<string, object?>>> Files { get; } = [];

    public ConcurrentQueue<(string Path, string Body)> Commands { get; } = [];

    // What /torrents/add answers, after the torrent was recorded in Commands.
    public Func<string, HttpResponseMessage>? AddResponse { get; set; }

    public void ExpireSessions() => validSession = null;

    public string CookieName => Version4 ? "SID" : "QBT_SID_8080";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        var cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join(";", cookies) : "";
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue($"{request.Method} {path} {cookie}");

        if (path.StartsWith("/api/v2/auth/login", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref Logins);
            if (RefuseLogins is { } refusal)
            {
                return Text(refusal, refusal == HttpStatusCode.OK ? "Fails." : "");
            }

            if (!body.Contains($"username={Uri.EscapeDataString(Username)}") || !body.Contains($"password={Uri.EscapeDataString(Password)}"))
            {
                return Version4 ? Text(HttpStatusCode.OK, "Fails.") : Text(HttpStatusCode.Unauthorized, "");
            }

            validSession = $"session-{Interlocked.Increment(ref sessionNumber)}";
            // 5.2 answers 204 with no body; 4.x answers "Ok.".
            var response = Version4 ? Text(HttpStatusCode.OK, "Ok.") : Text(HttpStatusCode.NoContent, "");
            response.Headers.Add("Set-Cookie", $"{CookieName}={validSession}; HttpOnly; path=/");
            return response;
        }

        if (path.StartsWith("/api/v2/auth/logout", StringComparison.Ordinal))
        {
            return Text(HttpStatusCode.OK, "");
        }

        if (validSession is null || !cookie.Contains($"={validSession}"))
        {
            return Text(HttpStatusCode.Forbidden, "Forbidden");
        }

        var route = request.RequestUri.AbsolutePath;
        switch (route)
        {
            case "/api/v2/app/version":
                return Text(HttpStatusCode.OK, Version4 ? "v4.6.7" : "v5.2.2");
            case "/api/v2/torrents/info":
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var hashes = query["hashes"]?.Split('|');
                var selected = hashes is null ? Torrents : Torrents.Where(torrent => hashes.Contains((string)torrent["hash"]!)).ToList();
                return Json(selected);
            case "/api/v2/torrents/files":
                var hash = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["hash"]!;
                return Files.TryGetValue(hash, out var files) ? Json(files) : Text(HttpStatusCode.NotFound, "");
            case "/api/v2/torrents/add":
                Commands.Enqueue((route, body));
                return AddResponse?.Invoke(body) ?? Text(HttpStatusCode.OK, "Ok.");
            case "/api/v2/torrents/stop" or "/api/v2/torrents/start" when Version4:
                return Text(HttpStatusCode.NotFound, "");
            case "/api/v2/torrents/pause" or "/api/v2/torrents/resume" when !Version4:
                return Text(HttpStatusCode.NotFound, "");
            case "/api/v2/torrents/stop" or "/api/v2/torrents/start" or "/api/v2/torrents/pause" or "/api/v2/torrents/resume" or "/api/v2/torrents/delete":
                Commands.Enqueue((route, body));
                return Text(HttpStatusCode.OK, "");
            default:
                return Text(HttpStatusCode.NotFound, "");
        }
    }

    public static HttpResponseMessage Text(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}
