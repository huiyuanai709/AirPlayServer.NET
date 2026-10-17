using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AirPlayServer.Core.Discovery;

internal static class LocalNetwork
{
    public static IPAddress? SelectAdvertisementAddress(IPAddress listenAddress)
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

        foreach (var address in EnumerateUnicast())
        {
            if (!IPAddress.IsLoopback(address))
            {
                return address;
            }
        }

        return null;
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

    private static IEnumerable<IPAddress> EnumerateUnicast()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    yield return unicast.Address;
                }
            }
        }
    }

    private static bool IsUsableMac(byte[] address)
        => address.Length >= 6 && address.AsSpan(0, 6).ContainsAnyExcept((byte)0);
}
