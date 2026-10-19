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
    ];

    public static IPAddress? SelectAdvertisementAddress(IPAddress listenAddress)
        => SelectAdvertisementAddress(listenAddress, EnumerateCandidates());

    /// <summary>
    /// Selects the IPv4 published in mDNS. An explicit unicast listen address is
    /// returned unchanged. <see cref="IPAddress.Any"/> prefers a LAN address a
    /// phone can reach, and falls back to the first non-loopback address when
    /// every up interface looks virtual.
    /// </summary>
    internal static IPAddress? SelectAdvertisementAddress(
        IPAddress listenAddress,
        IReadOnlyList<AdvertisementCandidate> candidates)
    {
        if (listenAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        if (!IPAddress.Any.Equals(listenAddress) && !IPAddress.IsLoopback(listenAddress))
        {
            return listenAddress;
        }

        if (IPAddress.IsLoopback(listenAddress))
        {
            return IPAddress.Loopback;
        }

        return ChooseReachableAddress(candidates);
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

    internal static IPAddress? ChooseReachableAddress(IReadOnlyList<AdvertisementCandidate> candidates)
    {
        IPAddress? fallback = null;
        IPAddress? firstReachable = null;

        foreach (var candidate in candidates)
        {
            if (candidate.OperationalStatus != OperationalStatus.Up || !IsUsableUnicast(candidate.Address))
            {
                continue;
            }

            fallback ??= candidate.Address;
            if (IsIpv4LinkLocal(candidate.Address) || IsVirtualOrTunnel(candidate))
            {
                continue;
            }

            if (IsEthernetOrWireless(candidate.InterfaceType)
                && IsRfc1918(candidate.Address)
                && candidate.HasGateway)
            {
                return candidate.Address;
            }

            firstReachable ??= candidate.Address;
        }

        return firstReachable ?? fallback;
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
                    hasGateway));
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

    internal static bool IsIpv4LinkLocal(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
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
    bool HasGateway);
