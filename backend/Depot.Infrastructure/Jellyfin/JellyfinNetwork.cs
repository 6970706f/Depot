using System.Net;
using System.Net.Sockets;
using Depot.Domain.Settings;

namespace Depot.Infrastructure.Jellyfin;

// Opens the connections to Jellyfin, only to addresses ServerAddresses allows. The URL is
// checked when it's saved, but a host name can resolve to anything later (or differently on
// every lookup, as in DNS rebinding), so the check is repeated here, on the addresses the
// connection actually uses.
public static class JellyfinNetwork
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await ResolveAllowedAsync(context.DnsEndPoint.Host, cancellationToken);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // The host's addresses that Depot may connect to. Throws AddressNotAllowedException when
    // there are none.
    public static async Task<IPAddress[]> ResolveAllowedAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
        var allowed = addresses.Where(ServerAddresses.IsAllowed).ToArray();
        return allowed.Length > 0 ? allowed : throw new AddressNotAllowedException(host);
    }
}

public sealed class AddressNotAllowedException(string host)
    : Exception($"{host} only leads to addresses Depot doesn't connect to, such as link-local or multicast ones.")
{
    public string Host { get; } = host;
}
