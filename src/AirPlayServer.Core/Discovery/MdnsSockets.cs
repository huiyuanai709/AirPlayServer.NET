using System.Net;
using System.Net.Sockets;

namespace AirPlayServer.Core.Discovery;

/// <summary>
/// UDP sockets that can share port 5353 with Bonjour, Avahi, or systemd-resolved.
/// mDNS queries are sent to 224.0.0.251:5353, so a socket bound to any other port
/// can transmit announcements but cannot receive those queries.
/// </summary>
internal static class MdnsSockets
{
    public const int Port = 5353;

    // Linux socket.h SO_REUSEPORT. macOS uses the BSD value.
    private const int SoReusePortLinux = 15;
    private const int SoReusePortBsd = 0x0200;

    // Windows: ignore ICMP port-unreachable so one rejected send does not kill receives.
    private const int SioUdpConnReset = -1744830452;

    public static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");

    public static readonly IPEndPoint GroupEndpoint = new(Group, Port);

    public static Socket CreateSharedUdp()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            PrepareForSharedBind(socket);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static void PrepareForSharedBind(Socket socket)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Default exclusive use makes SO_REUSEADDR fail against Bonjour's shared 5353 socket.
                socket.ExclusiveAddressUse = false;
            }
            catch (SocketException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (!OperatingSystem.IsWindows())
        {
            var reusePort = OperatingSystem.IsMacOS() ? SoReusePortBsd : SoReusePortLinux;
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)reusePort, 1);
            }
            catch (SocketException)
            {
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            catch (SocketException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }
        }
    }

    public static void ConfigureMulticast(Socket socket)
        => socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);

    public static bool TryJoinGroup(Socket socket, IPAddress local)
    {
        try
        {
            var option = IPAddress.Any.Equals(local)
                ? new MulticastOption(Group)
                : new MulticastOption(Group, local);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static bool TryDropGroup(Socket socket, IPAddress local)
    {
        try
        {
            var option = IPAddress.Any.Equals(local)
                ? new MulticastOption(Group)
                : new MulticastOption(Group, local);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, option);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static bool TrySetMulticastInterface(Socket socket, IPAddress local)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork || IPAddress.Any.Equals(local) || IPAddress.IsLoopback(local))
        {
            return false;
        }

        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
