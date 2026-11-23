using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Depot.Tests.TestSupport;

// A scripted Jellyfin REST API, as an HttpMessageHandler. Routes answer with Responses[path]
// (path without the query) when present.
public sealed class ScriptedJellyfin : HttpMessageHandler
{
    public const string ApiKey = "0123456789abcdef0123456789abcdef";

    public ConcurrentQueue<(HttpMethod Method, string PathAndQuery, string? Authorization)> Requests { get; } = [];

    public Dictionary<string, Func<HttpResponseMessage>> Responses { get; } = [];

    public bool WizardCompleted { get; set; } = true;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var authorization = request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null;
        var path = request.RequestUri!.AbsolutePath;
        Requests.Enqueue((request.Method, request.RequestUri.PathAndQuery, authorization));

        if (path == "/System/Info/Public")
        {
            // 10.11 answers this one in camelCase.
            // 10.11 answers this one in camelCase, unlike System/Info.
            return Task.FromResult(Json($$"""{"id":"f0e1d2","productName":"Jellyfin Server","version":"10.11.11","startupWizardCompleted":{{(WizardCompleted ? "true" : "false")}}}"""));
        }

        if (authorization is null || !authorization.Contains($"Token=\"{ApiKey}\""))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        if (Responses.TryGetValue(path, out var respond))
        {
            return Task.FromResult(respond());
        }

        return Task.FromResult(path switch
        {
            "/System/Info" => Json("""{"Id":"f0e1d2","ServerName":"Media box","Version":"10.11.11"}"""),
            "/Library/Refresh" when request.Method == HttpMethod.Post => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
    }

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
