using System.Net;

namespace PerfSentinelHub.Api;

/// <summary>
///     One spelling of a client's address for every per-address rule: an IPv4-mapped address
///     is IPv4, an IPv6 address is its /64, since one host commonly holds a whole /64 and
///     would otherwise rotate through it. Trustworthy behind an ingress only once
///     Hub:Auth:TrustedProxyNetworks lets X-Forwarded-For through from it.
/// </summary>
public static class ClientAddress
{
    public static string Normalize(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4().ToString();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
