using System.Net;
using Depot.Application.QBittorrent;
using Depot.Domain.Settings;

namespace Depot.Infrastructure.QBittorrent;

// The HTTP level of the qBittorrent Web API v2: logging in and out, sending requests and
// turning transport failures into QBittorrentException. The HttpClient has cookies
// disabled, so the session cookie is passed explicitly.
public sealed class QBittorrentHttp(HttpClient httpClient)
{
    // The largest answer Depot reads. A torrent list of several thousand torrents fits easily;
    // anything bigger isn't qBittorrent behaving normally.
    public const int MaxResponseBytes = 32 * 1024 * 1024;

    // Returns the session cookie as "name=value", or null when qBittorrent skips
    // authentication for Depot's address.
    public async Task<string?> LogInAsync(QBittorrentSettings settings, CancellationToken cancellationToken)
    {
        var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("username", settings.Username),
            new KeyValuePair<string, string>("password", settings.Password),
        ]);
        using var response = await SendAsync(settings.Url, HttpMethod.Post, "api/v2/auth/login", sessionCookie: null, content, cancellationToken);
        var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();

        switch (response.StatusCode)
        {
            case HttpStatusCode.Forbidden:
                throw new QBittorrentException(
                    QBittorrentFailure.Banned,
                    "qBittorrent blocked Depot's address after too many failed logins. Wait for the ban to expire or restart qBittorrent, then try again.");
            case HttpStatusCode.Unauthorized:
                throw InvalidCredentials();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw UnexpectedResponse(response);
        }

        // Older versions answer 200 with "Fails." for a wrong username or password.
        if (body.Equals("Fails.", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidCredentials();
        }

        var sessionCookie = ReadSessionCookie(response);
        if (sessionCookie is null && !body.Equals("Ok.", StringComparison.OrdinalIgnoreCase))
        {
            throw UnexpectedResponse(response);
        }

        return sessionCookie;
    }

    // Ends the session so it doesn't linger in qBittorrent. Failures don't matter.
    public async Task LogOutAsync(Uri url, string? sessionCookie)
    {
        if (sessionCookie is null)
        {
            return;
        }

        try
        {
            using var response = await SendAsync(url, HttpMethod.Post, "api/v2/auth/logout", sessionCookie, content: null, CancellationToken.None);
        }
        catch (QBittorrentException)
        {
        }
    }

    public async Task<HttpResponseMessage> SendAsync(
        Uri url,
        HttpMethod method,
        string path,
        string? sessionCookie,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var baseUrl = WithTrailingSlash(url);
        using var request = new HttpRequestMessage(method, new Uri(baseUrl, path)) { Content = content };
        // qBittorrent's CSRF protection rejects requests whose Referer doesn't match its own host.
        request.Headers.Referrer = baseUrl;
        if (sessionCookie is not null)
        {
            request.Headers.Add("Cookie", sessionCookie);
        }

        try
        {
            return await httpClient.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new QBittorrentException(
                QBittorrentFailure.Timeout,
                $"qBittorrent at {baseUrl} didn't answer within {httpClient.Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            throw new QBittorrentException(
                QBittorrentFailure.UnexpectedResponse,
                $"The server at {baseUrl} sent an answer larger than {MaxResponseBytes / (1024 * 1024)} MB, which isn't what the qBittorrent Web UI sends.");
        }
        catch (HttpRequestException)
        {
            throw new QBittorrentException(
                QBittorrentFailure.Unreachable,
                $"Can't reach qBittorrent at {baseUrl}. Check the URL and that the Web UI is enabled in qBittorrent.");
        }
    }

    public static QBittorrentException InvalidCredentials() =>
        new(QBittorrentFailure.InvalidCredentials,
            "qBittorrent rejected the username or password. Check that they match Tools > Options > Web UI in qBittorrent.");

    public static QBittorrentException UnexpectedResponse(HttpResponseMessage response)
    {
        var server = response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "qBittorrent's address";
        return new QBittorrentException(
            QBittorrentFailure.UnexpectedResponse,
            $"The server at {server} answered, but not like the qBittorrent Web UI (HTTP {(int)response.StatusCode}). Check the URL in Settings.");
    }

    // qBittorrent 4 names the cookie "SID"; version 5 names it "QBT_SID_<port>".
    private static string? ReadSessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        return cookies
            .Select(cookie => cookie.Split(';', 2)[0].Trim())
            .FirstOrDefault(pair =>
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0 || separator == pair.Length - 1)
                {
                    return false;
                }

                var name = pair[..separator];
                return name == "SID" || name.StartsWith("QBT_SID", StringComparison.Ordinal);
            });
    }

    private static Uri WithTrailingSlash(Uri url) =>
        url.AbsolutePath.EndsWith('/') ? url : new Uri($"{url.GetLeftPart(UriPartial.Path)}/");
}
