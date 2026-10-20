using System.Net;
using System.Net.Sockets;
using AirPlayServer.Core.Media;

namespace AirPlayServer.Core.Tests;

public sealed class MirrorDataPeerTests
{
    private static readonly IPAddress Listen = IPAddress.Parse("172.16.88.168");
    private static readonly IPAddress Phone = IPAddress.Parse("172.16.88.50");

    [Fact]
    public void A_real_remote_address_is_kept()
    {
        Assert.Equal(Phone, MirrorDataPeer.Resolve(Phone, Listen, IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void Listen_address_from_the_socket_yields_to_getpeername()
    {
        Assert.Equal(Phone, MirrorDataPeer.Resolve(Listen, Listen, IPAddress.Parse("10.0.0.9"), Phone));
    }

    [Fact]
    public void Listen_address_from_the_socket_yields_to_the_control_peer()
    {
        Assert.Equal(Phone, MirrorDataPeer.Resolve(Listen, Listen, Phone));
        Assert.Equal(Phone, MirrorDataPeer.Resolve(IPAddress.Any, Listen, Phone));
    }

    [Fact]
    public void A_local_client_on_the_listen_address_stays_when_getpeername_agrees()
    {
        Assert.Equal(Listen, MirrorDataPeer.Resolve(Listen, Listen, Listen, Listen));
        Assert.Equal(IPAddress.Loopback, MirrorDataPeer.Resolve(IPAddress.Loopback, IPAddress.Loopback, IPAddress.Loopback));
        // SETUP can name a different control address while the data TCP peer
        // really is loopback. getpeername confirming loopback must win.
        Assert.Equal(
            IPAddress.Loopback,
            MirrorDataPeer.Resolve(IPAddress.Loopback, IPAddress.Loopback, IPAddress.Parse("10.0.0.1"), IPAddress.Loopback));
    }

    [Fact]
    public void Any_listen_address_does_not_rewrite_a_normal_peer()
    {
        Assert.Equal(Phone, MirrorDataPeer.Resolve(Phone, IPAddress.Any, Listen));
    }

    [Fact]
    public async Task Getpeername_reads_the_accepted_client()
    {
        using var listen = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listen.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listen.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)listen.LocalEndPoint!);
        using var accepted = await listen.AcceptAsync();
        Assert.Equal(IPAddress.Loopback, SocketPeerName.TryRead(accepted));
    }
}
