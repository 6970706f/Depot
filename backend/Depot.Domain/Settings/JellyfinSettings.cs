using System.Net;
using System.Text.RegularExpressions;
using Depot.Domain.Common;

namespace Depot.Domain.Settings;

// Access data for a Jellyfin server. A class rather than a record so the compiler-generated
// ToString can never print the API key.
public sealed partial class JellyfinSettings
{
    private JellyfinSettings(Uri url, string apiKey)
    {
        Url = url;
        ApiKey = apiKey;
    }

    // Always ends with "/", so a server under a path (https://host/jellyfin/) keeps it.
    public Uri Url { get; }

    public string ApiKey { get; }

    public static JellyfinSettings Create(string? url, string? apiKey)
    {
        var parsedUrl = ParseUrl(url);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new DomainValidationException("apiKey", "Enter a Jellyfin API key (Dashboard > API Keys).");
        }

        return new JellyfinSettings(parsedUrl, ParseApiKey(apiKey));
    }

    // A blank key keeps the current one, but only for the same server: otherwise anyone using
    // the interface could send the stored key to an address they control.
    public JellyfinSettings With(string? url, string? newApiKey)
    {
        var parsedUrl = ParseUrl(url);
        if (!string.IsNullOrWhiteSpace(newApiKey))
        {
            return new JellyfinSettings(parsedUrl, ParseApiKey(newApiKey));
        }

        if (parsedUrl != Url)
        {
            throw new DomainValidationException("apiKey", "Enter the API key again to use it with a different URL.");
        }

        return new JellyfinSettings(parsedUrl, ApiKey);
    }

    // The address of the server's web interface: http or https, nothing that could carry
    // credentials or change the requests Depot builds on it (user info, query, fragment), and
    // no address that only reaches the machine's own network services (see ServerAddresses).
    // Host names are checked again when Depot connects, against the addresses they resolve to.
    private static Uri ParseUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(parsed.Host))
        {
            throw new DomainValidationException("url", "Enter a URL starting with http:// or https://, such as http://localhost:8096.");
        }

        if (parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            throw new DomainValidationException("url", "Enter only the server's address, without a user name, query or #fragment.");
        }

        if (IPAddress.TryParse(parsed.DnsSafeHost, out var address) && !ServerAddresses.IsAllowed(address))
        {
            throw new DomainValidationException("url", $"Depot doesn't connect to {parsed.Host}: it isn't an address a Jellyfin server can have.");
        }

        var path = parsed.AbsolutePath.TrimEnd('/');
        return new Uri($"{parsed.Scheme}://{parsed.Authority}{path}/");
    }

    // Jellyfin's keys are 32 hexadecimal characters. Anything that could break out of the
    // quoted value of the Authorization header is refused.
    private static string ParseApiKey(string apiKey)
    {
        var trimmed = apiKey.Trim();
        return ApiKeyPattern().IsMatch(trimmed)
            ? trimmed
            : throw new DomainValidationException("apiKey", "This doesn't look like a Jellyfin API key: it has 32 letters and numbers.");
    }

    [GeneratedRegex("^[A-Za-z0-9-]{16,128}$")]
    private static partial Regex ApiKeyPattern();
}
