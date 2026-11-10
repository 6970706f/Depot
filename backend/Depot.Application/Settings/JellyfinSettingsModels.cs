using System.Text;

namespace Depot.Application.Settings;

// What the API returns. The key itself never leaves the backend; HasApiKey only says whether
// one is stored.
public sealed record JellyfinSettingsView(string? Url, bool HasApiKey);

// What the client sends. A blank key means "keep the stored one".
public sealed record JellyfinSettingsInput(string? Url, string? ApiKey)
{
    // Keeps the key out of ToString, and so out of logs and debugger output.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Url = {Url}");
        return true;
    }
}

public sealed record JellyfinConnectionResult(string ServerName, string Version);
