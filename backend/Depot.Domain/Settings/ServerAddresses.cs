using System.Net;
using System.Net.Sockets;

namespace Depot.Domain.Settings;

// Which network addresses Depot may call on the user's behalf. Media servers usually live on
// the same machine or the local network, so loopback and private ranges are allowed; what is
// refused are the addresses no media server has and that reach somewhere else entirely: the
// link-local range (where cloud metadata services such as 169.254.169.254 answer), "this
// network" (0.0.0.0/8), multicast, broadcast and reserved ranges.
public static class ServerAddresses
{
    // AWS's metadata service over IPv6 sits in the unique local range, which LANs also use,
    // so only the address itself is refused.
    private static readonly IPAddress AwsMetadataV6 = IPAddress.Parse("fd00:ec2::254");

    public static bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsAllowedV4(address.GetAddressBytes());
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.Equals(AwsMetadataV6))
        {
            return false;
        }

        // NAT64 (64:ff9b::/96) carries an IPv4 address in its last four bytes.
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes[4..12].All(part => part == 0))
        {
            return IsAllowedV4(bytes[12..]);
        }

        return true;
    }

    private static bool IsAllowedV4(byte[] bytes) => bytes[0] switch
    {
        // "This network", such as 0.0.0.0, which some systems treat as the local machine.
        0 => false,
        // Link-local: cloud metadata services and other machine-local services.
        169 when bytes[1] == 254 => false,
        // Multicast (224/4), reserved (240/4) and broadcast.
        >= 224 => false,
        _ => true,
    };
}
