using System.Text;

namespace Depot.Application.Settings;

// What the API returns. The password itself never leaves the backend; HasPassword only
// says whether one is stored.
public sealed record QBittorrentSettingsView(string? Url, string? Username, bool HasPassword);

// What the client sends. A blank password means "keep the stored one".
public sealed record QBittorrentSettingsInput(string? Url, string? Username, string? Password)
{
    // Keeps the password out of ToString, and so out of logs and debugger output.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Url = {Url}, Username = {Username}");
        return true;
    }
}

public sealed record QBittorrentConnectionResult(string Version);
