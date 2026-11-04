using System.Net;
using Depot.Application.QBittorrent;
using Depot.Domain.Settings;

namespace Depot.Infrastructure.QBittorrent;

// Uses a one-off session, apart from QBittorrentClient's, because the settings being
// tested may not be the saved ones.
public sealed class QBittorrentConnectionTester(QBittorrentHttp http) : IQBittorrentConnectionTester
{
    public async Task<string> TestAsync(QBittorrentSettings settings, CancellationToken cancellationToken)
    {
        var sessionCookie = await http.LogInAsync(settings, cancellationToken);
        try
        {
            using var response = await http.SendAsync(
                settings.Url, HttpMethod.Get, "api/v2/app/version", sessionCookie, content: null, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                throw QBittorrentHttp.InvalidCredentials();
            }

            return await QBittorrentResponses.ReadVersionAsync(response, cancellationToken);
        }
        finally
        {
            await http.LogOutAsync(settings.Url, sessionCookie);
        }
    }
}
