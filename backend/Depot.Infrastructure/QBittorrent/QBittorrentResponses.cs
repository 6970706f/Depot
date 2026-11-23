namespace Depot.Infrastructure.QBittorrent;

// Reads qBittorrent's responses into domain types. Anything that doesn't look like the Web
// API's answer becomes an UnexpectedResponse failure.
public static partial class QBittorrentResponses
{
    public static async Task<string> ReadVersionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var version = await ReadSuccessBodyAsync(response, cancellationToken);
        if (version.Length == 0 || version.Length > 64 || version.StartsWith('<'))
        {
            throw QBittorrentHttp.UnexpectedResponse(response);
        }

        return version;
    }

    // qBittorrent 4 answers "Ok." or "Fails."; newer versions answer JSON with success and failure counts.
    public static bool IsAddAccepted(string body)
    {
        if (body.Equals("Fails.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!body.StartsWith('{'))
        {
            return true;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            var failures = root.TryGetProperty("failure_count", out var failureCount) ? failureCount.GetInt32() : 0;
            var successes = root.TryGetProperty("success_count", out var successCount) ? successCount.GetInt32() : 0;
            var pending = root.TryGetProperty("pending_count", out var pendingCount) ? pendingCount.GetInt32() : 0;
            return failures == 0 || successes + pending > 0;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return true;
        }
    }

    public static async Task<string> ReadSuccessBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw QBittorrentHttp.UnexpectedResponse(response);
        }

        return (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
    }
}
