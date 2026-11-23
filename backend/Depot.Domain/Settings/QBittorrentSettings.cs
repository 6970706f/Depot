using Depot.Domain.Common;

namespace Depot.Domain.Settings;

// Access data for the qBittorrent Web UI. A class rather than a record so the
// compiler-generated ToString can never print the password.
public sealed class QBittorrentSettings
{
    private QBittorrentSettings(Uri url, string username, string password)
    {
        Url = url;
        Username = username;
        Password = password;
    }

    // Always ends with "/", so a Web UI under a path (https://host/qbittorrent/) keeps it.
    public Uri Url { get; }

    public string Username { get; }

    public string Password { get; }

    public static QBittorrentSettings Create(string? url, string? username, string? password)
    {
        var parsedUrl = ParseUrl(url);
        var trimmedUsername = RequireUsername(username);
        if (string.IsNullOrEmpty(password))
        {
            throw new DomainValidationException("password", "Enter the qBittorrent password.");
        }

        return new QBittorrentSettings(parsedUrl, trimmedUsername, password);
    }

    // A blank password keeps the current one, but only for the same server: otherwise anyone
    // using the interface could send the stored password to an address they control.
    public QBittorrentSettings With(string? url, string? username, string? newPassword)
    {
        var parsedUrl = ParseUrl(url);
        var trimmedUsername = RequireUsername(username);
        if (!string.IsNullOrEmpty(newPassword))
        {
            return new QBittorrentSettings(parsedUrl, trimmedUsername, newPassword);
        }

        if (parsedUrl != Url)
        {
            throw new DomainValidationException("password", "Enter the password again to use it with a different URL.");
        }

        return new QBittorrentSettings(parsedUrl, trimmedUsername, Password);
    }

    public static Uri ParseUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(parsed.Host))
        {
            throw new DomainValidationException("url", "Enter a URL starting with http:// or https://, such as http://localhost:8080.");
        }

        if (parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            throw new DomainValidationException("url", "Enter only the Web UI's address, without a user name, query or #fragment.");
        }

        return new Uri($"{parsed.Scheme}://{parsed.Authority}{parsed.AbsolutePath.TrimEnd('/')}/");
    }

    private static string RequireUsername(string? username)
    {
        var trimmed = username?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainValidationException("username", "Enter the qBittorrent username.");
        }

        return trimmed;
    }
}
