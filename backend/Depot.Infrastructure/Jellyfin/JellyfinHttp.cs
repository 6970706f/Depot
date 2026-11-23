using System.Net;
using System.Text.Json;
using Depot.Application.Jellyfin;
using Depot.Domain.Settings;

namespace Depot.Infrastructure.Jellyfin;

// The HTTP level of the Jellyfin REST API: authenticating, sending requests, the size limits and
// turning transport failures into JellyfinException. The API key only travels in the
// Authorization header, never in a URL, so it can't end up in a log of addresses or an error.
public sealed class JellyfinHttp(HttpClient httpClient)
{
    // A page of items with their details is a few hundred kilobytes.
    public const int MaxResponseBytes = 32 * 1024 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly string ClientVersion = typeof(JellyfinHttp).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    // The caller disposes the document.
    public async Task<JsonDocument> GetJsonAsync(JellyfinSettings settings, string pathAndQuery, bool authenticated, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            settings, HttpMethod.Get, pathAndQuery, authenticated, "application/json", HttpCompletionOption.ResponseContentRead, cancellationToken);
        EnsureSuccess(settings, response);
        if (response.Content.Headers.ContentType?.MediaType is not ("application/json" or "text/json"))
        {
            throw NotJellyfin(settings, response);
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        try
        {
            return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            throw NotJellyfin(settings, response);
        }
    }

    public async Task PostAsync(JellyfinSettings settings, string pathAndQuery, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            settings, HttpMethod.Post, pathAndQuery, authenticated: true, "application/json", HttpCompletionOption.ResponseContentRead, cancellationToken);
        EnsureSuccess(settings, response);
    }

    // The caller disposes the response; null when Jellyfin has no such resource (404, or 400 for an
    // id it doesn't know).
    public async Task<HttpResponseMessage?> GetStreamAsync(JellyfinSettings settings, string pathAndQuery, string accept, CancellationToken cancellationToken)
    {
        var response = await SendAsync(settings, HttpMethod.Get, pathAndQuery, authenticated: true, accept, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            response.Dispose();
            return null;
        }

        try
        {
            EnsureSuccess(settings, response);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        JellyfinSettings settings,
        HttpMethod method,
        string pathAndQuery,
        bool authenticated,
        string accept,
        HttpCompletionOption completion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(settings.Url, pathAndQuery));
        request.Headers.TryAddWithoutValidation("Accept", accept);
        // The key is checked to hold only letters, digits and dashes, so it can't break out of the quotes.
        var authorization = $"MediaBrowser Client=\"Depot\", Device=\"Depot\", DeviceId=\"depot\", Version=\"{ClientVersion}\"";
        request.Headers.TryAddWithoutValidation("Authorization", authenticated ? $"{authorization}, Token=\"{settings.ApiKey}\"" : authorization);

        try
        {
            return await httpClient.SendAsync(request, completion, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new JellyfinException(
                JellyfinFailure.Timeout, $"Jellyfin at {settings.Url} didn't answer within {httpClient.Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            throw new JellyfinException(
                JellyfinFailure.UnexpectedResponse,
                $"The server at {settings.Url} sent an answer larger than {MaxResponseBytes / (1024 * 1024)} MB, which Depot doesn't read.");
        }
        catch (HttpRequestException)
        {
            throw new JellyfinException(
                JellyfinFailure.Unreachable, $"Can't reach Jellyfin at {settings.Url}. Check the URL and that Jellyfin is running.");
        }
    }

    private static void EnsureSuccess(JellyfinSettings settings, HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new JellyfinException(
                JellyfinFailure.InvalidApiKey,
                "Jellyfin refused the API key. Check it in Jellyfin under Dashboard > API Keys, or create a new one."),
            // Following a redirect could take the key to another server, so Depot never does.
            _ when status is >= 300 and < 400 => new JellyfinException(
                JellyfinFailure.UnexpectedResponse,
                $"Jellyfin at {settings.Url} redirects to another address (HTTP {status}). Depot doesn't follow redirects: enter the final address in Settings."),
            // Jellyfin answers 503 while it starts up.
            HttpStatusCode.ServiceUnavailable => new JellyfinException(
                JellyfinFailure.Unreachable, $"Jellyfin at {settings.Url} isn't ready yet (HTTP 503). It may still be starting; try again in a moment."),
            _ => NotJellyfin(settings, response),
        };
    }

    public static JellyfinException NotJellyfin(JellyfinSettings settings, HttpResponseMessage? response = null) => new(
        JellyfinFailure.UnexpectedResponse,
        response is null
            ? $"The server at {settings.Url} answered, but not like Jellyfin. Check the URL in Settings."
            : $"The server at {settings.Url} answered, but not like Jellyfin (HTTP {(int)response.StatusCode}). Check the URL in Settings.");
}
