using System.Net;
using System.Text.Json;
using Depot.Application.Jellyfin;
using Depot.Domain.Settings;

namespace Depot.Infrastructure.Jellyfin;

// The HTTP level of the Jellyfin REST API: authenticating, sending requests, the size limits
// and turning transport failures into JellyfinException. The API key only travels in the
// Authorization header, never in a URL, so it can't end up in a log of addresses or in an
// error message.
public sealed class JellyfinHttp(HttpClient httpClient)
{
    // The largest JSON answer Depot reads: a page of items with their files and streams is a
    // few hundred kilobytes.
    public const int MaxResponseBytes = 32 * 1024 * 1024;

    // The largest image Depot passes on; Jellyfin's originals are rarely more than a few MB.
    public const int MaxImageBytes = 15 * 1024 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly string ClientVersion = typeof(JellyfinHttp).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    // The caller disposes the document.
    public async Task<JsonDocument> GetJsonAsync(JellyfinSettings settings, string pathAndQuery, bool authenticated, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            settings, pathAndQuery, authenticated, "application/json", HttpCompletionOption.ResponseContentRead, cancellationToken);
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

    // Null when the server has no such image (404, or 400 for an id it doesn't know).
    public async Task<MediaImageContent?> GetImageAsync(JellyfinSettings settings, string pathAndQuery, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            settings, pathAndQuery, authenticated: true, "image/webp,image/jpeg,image/png,image/gif", HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return null;
        }

        EnsureSuccess(settings, response);
        if (response.Content.Headers.ContentLength > MaxImageBytes)
        {
            throw TooLarge(settings, MaxImageBytes);
        }

        var content = await ReadLimitedAsync(settings, response, MaxImageBytes, cancellationToken);
        // The bytes decide, not the declared type: only the formats a browser shows as a
        // picture, and never SVG, which can carry scripts.
        var contentType = ImageFormats.Detect(content)
            ?? throw new JellyfinException(
                JellyfinFailure.UnexpectedResponse, $"Jellyfin at {settings.Url} sent an image in a format Depot doesn't pass on.");
        return new MediaImageContent(contentType, content);
    }

    private async Task<HttpResponseMessage> SendAsync(
        JellyfinSettings settings,
        string pathAndQuery,
        bool authenticated,
        string accept,
        HttpCompletionOption completion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(settings.Url, pathAndQuery));
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
        catch (HttpRequestException exception) when (FindAddressRefusal(exception) is { } refusal)
        {
            throw new JellyfinException(JellyfinFailure.AddressNotAllowed, $"Depot doesn't connect to Jellyfin at {settings.Url}: {refusal.Message}");
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            throw TooLarge(settings, MaxResponseBytes);
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

    private static async Task<byte[]> ReadLimitedAsync(JellyfinSettings settings, HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw TooLarge(settings, limit);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static AddressNotAllowedException? FindAddressRefusal(Exception exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is AddressNotAllowedException refusal)
            {
                return refusal;
            }
        }

        return null;
    }

    public static JellyfinException NotJellyfin(JellyfinSettings settings, HttpResponseMessage response) => new(
        JellyfinFailure.UnexpectedResponse,
        $"The server at {settings.Url} answered, but not like Jellyfin (HTTP {(int)response.StatusCode}). Check the URL in Settings.");

    private static JellyfinException TooLarge(JellyfinSettings settings, int limit) => new(
        JellyfinFailure.UnexpectedResponse,
        $"The server at {settings.Url} sent an answer larger than {limit / (1024 * 1024)} MB, which Depot doesn't read.");
}

// Recognizes the image formats Depot serves by their first bytes.
public static class ImageFormats
{
    public static string? Detect(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (content.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (content.Length >= 12 && content.StartsWith("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }
}
