using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AirPlayServer.Core.Media;

/// <summary>
/// Picks the address recorded as the mirror data peer. Windows AcceptEx can
/// cache the listener address in <c>Socket.RemoteEndPoint</c> when the data
/// port is bound to a specific NIC, so a remote phone is logged as the PC.
/// </summary>
internal static class MirrorDataPeer
{
    internal static IPAddress? Resolve(
        IPAddress? socketRemote,
        IPAddress? listenAddress,
        IPAddress? claimedControlPeer,
        IPAddress? peerName = null)
    {
        // RemoteEndPoint can be the listener address while getpeername still
        // has the phone. A syscall that agrees with that listener address is a
        // real local client: do not replace it with the RTSP control address.
        if (ReportsListenAddress(socketRemote, listenAddress))
        {
            if (peerName is not null && !ReportsListenAddress(peerName, listenAddress))
            {
                return peerName;
            }

            if (peerName is not null)
            {
                return socketRemote;
            }

            if (claimedControlPeer is not null && !ReportsListenAddress(claimedControlPeer, listenAddress))
            {
                return claimedControlPeer;
            }
        }

        if (socketRemote is not null && !ReportsListenAddress(socketRemote, listenAddress))
        {
            return socketRemote;
        }

        if (peerName is not null && !ReportsListenAddress(peerName, listenAddress))
        {
            return peerName;
        }

        if (claimedControlPeer is not null && !ReportsListenAddress(claimedControlPeer, listenAddress))
        {
            return claimedControlPeer;
        }

        return socketRemote ?? peerName ?? claimedControlPeer;
    }

    internal static bool ReportsListenAddress(IPAddress? peer, IPAddress? listenAddress)
    {
        if (peer is null)
        {
            return false;
        }

        if (peer.Equals(IPAddress.Any) || peer.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        return listenAddress is not null
            && !listenAddress.Equals(IPAddress.Any)
            && !listenAddress.Equals(IPAddress.IPv6Any)
            && peer.Equals(listenAddress);
    }
}

/// <summary>
/// Runtime <c>getpeername</c>. Loaded with <see cref="NativeLibrary"/> so a
/// Windows Native AOT link does not require libc, and a Linux link does not
/// require ws2_32.
/// </summary>
internal static class SocketPeerName
{
    private static readonly object Gate = new();
    private static nint _export;
    private static bool _resolved;

    internal static IPAddress? TryRead(Socket socket)
    {
        var export = Export();
        if (export == 0)
        {
            return null;
        }

        Span<byte> address = stackalloc byte[128];
        var length = address.Length;
        int result;
        unsafe
        {
            fixed (byte* pointer = address)
            {
                result = ((delegate* unmanaged[Cdecl]<nint, byte*, int*, int>)export)(socket.Handle, pointer, &length);
            }
        }

        if (result != 0 || length < 8)
        {
            return null;
        }

        var family = address[0] | (address[1] << 8);
        if (family != 2)
        {
            return null;
        }

        return new IPAddress(address.Slice(4, 4));
    }

    private static nint Export()
    {
        lock (Gate)
        {
            if (_resolved)
            {
                return _export;
            }

            _resolved = true;
            if (OperatingSystem.IsWindows())
            {
                _export = TryLoad("ws2_32.dll");
                return _export;
            }

            _export = TryLoad("libc.so.6");
            if (_export == 0)
            {
                _export = TryLoad("libc");
            }

            return _export;
        }
    }

    private static nint TryLoad(string library)
    {
        if (!NativeLibrary.TryLoad(library, out var handle))
        {
            return 0;
        }

        return NativeLibrary.TryGetExport(handle, "getpeername", out var export) ? export : 0;
    }
}
