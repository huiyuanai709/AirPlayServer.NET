using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AirPlayServer.Core.Discovery;

internal static class LocalNetwork
{
    // Substrings matched against interface name and description. Hyper-V vEthernet
    // adapters are reported as Ethernet, so the type alone does not exclude them.
    private static readonly string[] VirtualInterfaceMarkers =
    [
        "vEthernet",
        "Hyper-V",
        "Default Switch",
        "WSL",
        "Docker",
        "VMware",
        "VirtualBox",
        "cfw-tap",
        "Clash",
        "TAP-Windows",
        "WireGuard",
        "VPN",
        "Teredo",
        "isatap",
        "6to4",
        // Windows Mobile Hotspot, ICS, and Wi-Fi Direct. The asterisk is part of
        // the adapter name ("Local Area Connection* 2"); a normal
        // "Local Area Connection" has no asterisk and is still eligible.
        "Local Area Connection*",
        "本地连接*",
        "本地连接＊",
        "Microsoft Wi-Fi Direct",
        "Wi-Fi Direct",
        "Microsoft Hosted Network",
        "Hosted Network Virtual",
        "Wireless Hosted Network",
        "Internet Sharing Device",
    ];

    public static IPAddress? SelectAdvertisementAddress(IPAddress listenAddress)
        => SelectAdvertisementAddress(listenAddress, EnumerateCandidates());

    /// <summary>
    /// Primary IPv4. An explicit unicast listen address is returned unchanged.
    /// <see cref="IPAddress.Any"/> returns the first address from
    /// <see cref="SelectAdvertisedNics"/> (a gateway LAN when one exists).
    /// The announcer publishes every address in that list, not only this one.
    /// </summary>
    internal static IPAddress? SelectAdvertisementAddress(
        IPAddress listenAddress,
        IReadOnlyList<AdvertisementCandidate> candidates)
    {
        var nics = SelectAdvertisedNics(listenAddress, candidates);
        return nics.Count == 0 ? null : nics[0].Address;
    }

    public static IReadOnlyList<AdvertisedNic> SelectAdvertisedNics(IPAddress listenAddress)
        => SelectAdvertisedNics(listenAddress, EnumerateCandidates());

    /// <summary>
    /// Every IPv4 the announcer should publish. An explicit unicast listen address
    /// stays a single entry. <see cref="IPAddress.Any"/> returns each up, non-virtual
    /// RFC1918 address, gateway Wi-Fi and Ethernet first. Windows Mobile Hotspot,
    /// ICS, and Wi-Fi Direct stay out of this list: a second A record for
    /// <c>192.168.137.1</c> leaves the RTSP session up while the picture waits for
    /// a keyframe. Virtual, hotspot, and link-local addresses are used only when
    /// nothing else is up.
    /// </summary>
    internal static IReadOnlyList<AdvertisedNic> SelectAdvertisedNics(
        IPAddress listenAddress,
        IReadOnlyList<AdvertisementCandidate> candidates)
    {
        if (listenAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return [];
        }

        if (!IPAddress.Any.Equals(listenAddress) && !IPAddress.IsLoopback(listenAddress))
        {
            return [new AdvertisedNic(listenAddress, PrefixLength: -1, Name: "listen")];
        }

        if (IPAddress.IsLoopback(listenAddress))
        {
            return [new AdvertisedNic(IPAddress.Loopback, PrefixLength: 8, Name: "loopback")];
        }

        return ChooseReachableNics(candidates);
    }

    public static byte[] SelectHardwareAddress(IPAddress? advertisementAddress, string stableName)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            var unicast = nic.GetIPProperties().UnicastAddresses;
            var matchesAddress = advertisementAddress is null
                || unicast.Any(entry => entry.Address.Equals(advertisementAddress));
            if (!matchesAddress && advertisementAddress is not null)
            {
                continue;
            }

            var physical = nic.GetPhysicalAddress().GetAddressBytes();
            if (IsUsableMac(physical))
            {
                return physical.AsSpan(0, 6).ToArray();
            }
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var physical = nic.GetPhysicalAddress().GetAddressBytes();
            if (IsUsableMac(physical))
            {
                return physical.AsSpan(0, 6).ToArray();
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("AirPlayServer.NET:" + stableName));
        var synthetic = hash.AsSpan(0, 6).ToArray();
        synthetic[0] = (byte)((synthetic[0] | 0x02) & 0xFE);
        return synthetic;
    }

    internal static IReadOnlyList<AdvertisedNic> ChooseReachableNics(IReadOnlyList<AdvertisementCandidate> candidates)
    {
        // Gateway LANs first, then any other real RFC1918 NIC. Stopping at the
        // first gateway (v0.1.21) dropped a second office subnet, so a phone on
        // that subnet never saw an on-link address. Hotspot and ICS never enter
        // these lists; they are not a second real LAN.
        var preferred = new List<AdvertisedNic>();
        var otherLan = new List<AdvertisedNic>();
        var other = new List<AdvertisedNic>();
        AdvertisedNic? fallback = null;
        var seen = new HashSet<IPAddress>();

        foreach (var candidate in candidates)
        {
            if (candidate.OperationalStatus != OperationalStatus.Up || !IsUsableUnicast(candidate.Address))
            {
                continue;
            }

            if (!seen.Add(candidate.Address))
            {
                continue;
            }

            var nic = new AdvertisedNic(candidate.Address, candidate.PrefixLength, candidate.Name);
            fallback ??= nic;
            if (IsIpv4LinkLocal(candidate.Address) || IsVirtualOrTunnel(candidate) || IsWindowsIcsSubnet(candidate.Address))
            {
                continue;
            }

            if (IsRfc1918(candidate.Address))
            {
                if (candidate.HasGateway && IsEthernetOrWireless(candidate.InterfaceType))
                {
                    preferred.Add(nic);
                }
                else
                {
                    otherLan.Add(nic);
                }

                continue;
            }

            other.Add(nic);
        }

        if (preferred.Count > 0 || otherLan.Count > 0)
        {
            var lans = new List<AdvertisedNic>(preferred.Count + otherLan.Count);
            lans.AddRange(preferred);
            lans.AddRange(otherLan);
            return lans;
        }

        if (other.Count > 0)
        {
            return other;
        }

        return fallback is null ? [] : [fallback.Value];
    }

    /// <summary>
    /// True when <paramref name="remote"/> shares a known prefix with an
    /// advertised NIC. A prefix of 0 or less is treated as unknown, not as
    /// "different subnet".
    /// </summary>
    internal static bool QuerierOnAdvertisedLan(IPAddress remote, IReadOnlyList<AdvertisedNic> nics)
    {
        foreach (var nic in nics)
        {
            if (nic.PrefixLength <= 0)
            {
                continue;
            }

            if (SharesPrefix(remote, nic.Address, nic.PrefixLength))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// QU-bit and legacy (source port other than 5353) queries are always unicast.
    /// A querier that sits on none of the advertised prefixes also gets a unicast
    /// copy: a multicast reply pinned to 172.16.88.168 does not reach 172.16.93.211.
    /// </summary>
    internal static bool ShouldReplyUnicast(bool quBit, int remotePort, IPAddress? remote, IReadOnlyList<AdvertisedNic> nics)
    {
        if (quBit || (remotePort is not 0 and not MdnsSockets.Port))
        {
            return true;
        }

        if (remote is null || QuerierOnAdvertisedLan(remote, nics) || !HasAdvertisedPrefix(nics))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Addresses to put in one response. <paramref name="preferred"/> is first so
    /// the on-link NIC leads when the querier matched one. The rest of the
    /// advertised set follows; hotspot addresses are never in <paramref name="nics"/>.
    /// </summary>
    internal static IPAddress[] ReplyAddresses(IPAddress? preferred, IReadOnlyList<AdvertisedNic> nics)
    {
        var list = new List<IPAddress>(nics.Count + 1);
        if (preferred is not null)
        {
            list.Add(preferred);
        }

        foreach (var nic in nics)
        {
            if (ContainsAddress(list, nic.Address))
            {
                continue;
            }

            list.Add(nic.Address);
        }

        return list.ToArray();
    }

    private static bool HasAdvertisedPrefix(IReadOnlyList<AdvertisedNic> nics)
    {
        foreach (var nic in nics)
        {
            if (nic.PrefixLength > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAddress(List<IPAddress> addresses, IPAddress address)
    {
        foreach (var candidate in addresses)
        {
            if (candidate.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Picks the advertised NIC that shares the longest prefix with a querier.
    /// Falls back to the primary address when none of the prefixes match.
    /// </summary>
    internal static IPAddress? MatchRemote(IPAddress remote, IReadOnlyList<AdvertisedNic> nics)
    {
        if (nics.Count == 0)
        {
            return null;
        }

        AdvertisedNic? best = null;
        var bestPrefix = -1;
        foreach (var nic in nics)
        {
            if (!SharesPrefix(remote, nic.Address, nic.PrefixLength))
            {
                continue;
            }

            if (nic.PrefixLength > bestPrefix)
            {
                best = nic;
                bestPrefix = nic.PrefixLength;
            }
        }

        return best?.Address ?? nics[0].Address;
    }

    internal static bool SharesPrefix(IPAddress left, IPAddress right, int prefixLength)
    {
        if (prefixLength <= 0 || prefixLength > 32)
        {
            return false;
        }

        if (left.AddressFamily != AddressFamily.InterNetwork || right.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var a = left.GetAddressBytes();
        var b = right.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        var bits = prefixLength % 8;
        if (bits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - bits));
        return (a[fullBytes] & mask) == (b[fullBytes] & mask);
    }

    internal static bool IsVirtualOrTunnel(AdvertisementCandidate candidate)
    {
        if (candidate.InterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
        {
            return true;
        }

        return ContainsVirtualMarker(candidate.Name) || ContainsVirtualMarker(candidate.Description);
    }

    private static List<AdvertisementCandidate> EnumerateCandidates()
    {
        var candidates = new List<AdvertisementCandidate>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = nic.GetIPProperties();
            var hasGateway = HasIpv4Gateway(properties);
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                candidates.Add(new AdvertisementCandidate(
                    nic.Name,
                    nic.Description,
                    nic.NetworkInterfaceType,
                    nic.OperationalStatus,
                    unicast.Address,
                    hasGateway,
                    PrefixLengthOf(unicast)));
            }
        }

        return candidates;
    }

    private static bool HasIpv4Gateway(IPInterfaceProperties properties)
    {
        foreach (var gateway in properties.GatewayAddresses)
        {
            var address = gateway.Address;
            if (address.AddressFamily != AddressFamily.InterNetwork || !IsUsableUnicast(address) || IsIpv4LinkLocal(address))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool ContainsVirtualMarker(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var marker in VirtualInterfaceMarkers)
        {
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEthernetOrWireless(NetworkInterfaceType type)
        => type is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.Ethernet3Megabit;

    internal static bool IsRfc1918(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (bytes[0] == 10)
        {
            return true;
        }

        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
        {
            return true;
        }

        return bytes[0] == 192 && bytes[1] == 168;
    }

    /// <summary>
    /// Windows Internet Connection Sharing and Mobile Hotspot use
    /// <c>192.168.137.0/24</c> by default. The adapter is often a Wi-Fi Direct
    /// virtual NIC, but the subnet is skipped even when the name looks ordinary.
    /// </summary>
    internal static bool IsWindowsIcsSubnet(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 192 && bytes[1] == 168 && bytes[2] == 137;
    }

    internal static bool IsIpv4LinkLocal(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static int PrefixLengthOf(UnicastIPAddressInformation unicast)
    {
        try
        {
            if (unicast.PrefixLength is > 0 and <= 32)
            {
                return unicast.PrefixLength;
            }
        }
        catch (NotImplementedException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }

        IPAddress? mask;
        try
        {
            mask = unicast.IPv4Mask;
        }
        catch (NotImplementedException)
        {
            return -1;
        }
        catch (PlatformNotSupportedException)
        {
            return -1;
        }

        if (mask is null || mask.AddressFamily != AddressFamily.InterNetwork)
        {
            return -1;
        }

        var length = 0;
        foreach (var value in mask.GetAddressBytes())
        {
            var bits = value;
            for (var bit = 0; bit < 8; bit++)
            {
                if ((bits & 0x80) == 0)
                {
                    return length;
                }

                length++;
                bits <<= 1;
            }
        }

        return length;
    }

    private static bool IsUsableUnicast(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
        {
            return false;
        }

        var first = address.GetAddressBytes()[0];
        return first is > 0 and < 224;
    }

    private static bool IsUsableMac(byte[] address)
        => address.Length >= 6 && address.AsSpan(0, 6).ContainsAnyExcept((byte)0);
}

internal readonly record struct AdvertisementCandidate(
    string Name,
    string Description,
    NetworkInterfaceType InterfaceType,
    OperationalStatus OperationalStatus,
    IPAddress Address,
    bool HasGateway,
    int PrefixLength = 24);

internal readonly record struct AdvertisedNic(IPAddress Address, int PrefixLength, string Name);
