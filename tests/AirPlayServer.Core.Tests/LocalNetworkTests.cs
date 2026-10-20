using System.Net;
using System.Net.NetworkInformation;
using AirPlayServer.Core.Discovery;

namespace AirPlayServer.Core.Tests;

public sealed class LocalNetworkTests
{
    [Fact]
    public void Any_prefers_ethernet_over_hyperv_clash_and_link_local()
    {
        // Order observed on a Windows host: Hyper-V Default Switch is enumerated first.
        AdvertisementCandidate[] candidates =
        [
            Nic("vEthernet (Default Switch)", "172.17.96.1", gateway: true, description: "Hyper-V Virtual Ethernet Adapter"),
            Nic("vEthernet (Wi-Fi)", "172.17.112.1", gateway: true, description: "Hyper-V Virtual Ethernet Adapter"),
            Nic("cfw-tap", "10.0.0.1", gateway: true, description: "TAP-Windows Adapter V9"),
            Nic("vEthernet (cfw-tap)", "172.25.8.1", gateway: true, description: "Hyper-V Virtual Ethernet Adapter"),
            Nic("Ethernet 2", "169.254.83.12", description: "Realtek PCIe GbE Family Controller"),
            Nic("Ethernet", "192.168.5.15", gateway: true, description: "Realtek PCIe GbE Family Controller"),
        ];

        Assert.Equal(IPAddress.Parse("192.168.5.15"), LocalNetwork.SelectAdvertisementAddress(IPAddress.Any, candidates));
        Assert.Equal(IPAddress.Parse("192.168.5.15"), LocalNetwork.SelectAdvertisementAddress(IPAddress.Parse("0.0.0.0"), candidates));
    }

    [Fact]
    public void Explicit_listen_address_is_used_unchanged()
    {
        var candidates = HyperVThenLan();
        Assert.Equal(
            IPAddress.Parse("192.168.5.15"),
            LocalNetwork.SelectAdvertisementAddress(IPAddress.Parse("192.168.5.15"), candidates));
        Assert.Equal(
            IPAddress.Parse("172.17.96.1"),
            LocalNetwork.SelectAdvertisementAddress(IPAddress.Parse("172.17.96.1"), candidates));
        Assert.Equal(IPAddress.Loopback, LocalNetwork.SelectAdvertisementAddress(IPAddress.Loopback, candidates));
        Assert.Equal(IPAddress.Loopback, LocalNetwork.SelectAdvertisementAddress(IPAddress.Parse("127.0.0.1"), candidates));
        Assert.Null(LocalNetwork.SelectAdvertisementAddress(IPAddress.IPv6Any, candidates));
        Assert.Null(LocalNetwork.SelectAdvertisementAddress(IPAddress.IPv6Loopback, candidates));
    }

    [Fact]
    public void Virtual_only_falls_back_to_the_first_non_loopback()
    {
        AdvertisementCandidate[] candidates =
        [
            Nic("Loopback Pseudo-Interface 1", "127.0.0.1", type: NetworkInterfaceType.Loopback),
            Nic("vEthernet (Default Switch)", "172.17.96.1", gateway: true, description: "Hyper-V Virtual Ethernet Adapter"),
            Nic("vEthernet (Wi-Fi)", "172.17.112.1", description: "Hyper-V Virtual Ethernet Adapter"),
        ];

        Assert.Equal(IPAddress.Parse("172.17.96.1"), LocalNetwork.SelectAdvertisementAddress(IPAddress.Any, candidates));
    }

    [Fact]
    public void Lan_without_a_gateway_still_beats_a_virtual_gateway()
    {
        AdvertisementCandidate[] candidates =
        [
            Nic("vEthernet (Default Switch)", "172.17.96.1", gateway: true, description: "Hyper-V Virtual Ethernet Adapter"),
            Nic("Ethernet", "192.168.5.15", description: "Realtek PCIe GbE Family Controller"),
            Nic("Wi-Fi", "10.0.0.8", type: NetworkInterfaceType.Wireless80211),
        ];

        Assert.Equal(IPAddress.Parse("192.168.5.15"), LocalNetwork.SelectAdvertisementAddress(IPAddress.Any, candidates));
    }

    [Fact]
    public void Rfc1918_lan_with_gateway_beats_an_earlier_public_address()
    {
        AdvertisementCandidate[] candidates =
        [
            Nic("Wi-Fi", "203.0.113.10", type: NetworkInterfaceType.Wireless80211, gateway: true),
            Nic("Ethernet", "192.168.5.15", gateway: true),
        ];

        Assert.Equal(IPAddress.Parse("192.168.5.15"), LocalNetwork.SelectAdvertisementAddress(IPAddress.Any, candidates));
    }

    [Fact]
    public void Office_172_16_lan_is_not_treated_as_hyperv()
    {
        AdvertisementCandidate[] candidates =
        [
            Nic("vEthernet (Default Switch)", "172.17.96.1", gateway: true, description: "Hyper-V Virtual Ethernet Adapter"),
            Nic("Ethernet", "172.16.0.20", gateway: true, description: "Intel(R) Ethernet Connection"),
        ];

        Assert.Equal(IPAddress.Parse("172.16.0.20"), LocalNetwork.SelectAdvertisementAddress(IPAddress.Any, candidates));
    }

    [Fact]
    public void Wifi_with_gateway_beats_ethernet_without_one()
    {
        AdvertisementCandidate[] candidates =
        [
            Nic("Ethernet", "192.168.5.15"),
            Nic("Wi-Fi", "192.168.1.20", type: NetworkInterfaceType.Wireless80211, gateway: true),
        ];

        Assert.Equal(IPAddress.Parse("192.168.1.20"), LocalNetwork.SelectAdvertisementAddress(IPAddress.Any, candidates));
    }

    [Fact]
    public void Down_and_link_local_interfaces_are_skipped_when_a_lan_exists()
    {
        AdvertisementCandidate[] candidates =
        [
            Nic("Ethernet", "192.168.5.15", gateway: true, status: OperationalStatus.Down),
            Nic("Ethernet", "169.254.1.20"),
            Nic("Wi-Fi", "192.168.1.8", type: NetworkInterfaceType.Wireless80211, gateway: true, description: "Intel(R) Wi-Fi 6 AX201"),
        ];

        Assert.Equal(IPAddress.Parse("192.168.1.8"), LocalNetwork.SelectAdvertisementAddress(IPAddress.Any, candidates));
    }

    [Fact]
    public void Any_advertises_each_gateway_lan_and_skips_hyperv()
    {
        AdvertisementCandidate[] candidates =
        [
            Nic("vEthernet (Default Switch)", "172.17.96.1", gateway: true, description: "Hyper-V Virtual Ethernet Adapter"),
            Nic("Ethernet", "192.168.5.15", gateway: true, description: "Realtek PCIe GbE Family Controller"),
            Nic("Wi-Fi", "192.168.1.20", type: NetworkInterfaceType.Wireless80211, gateway: true),
        ];

        var nics = LocalNetwork.SelectAdvertisedNics(IPAddress.Any, candidates);

        Assert.Equal(2, nics.Count);
        Assert.Equal(IPAddress.Parse("192.168.5.15"), nics[0].Address);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), nics[1].Address);
        Assert.Equal(IPAddress.Parse("192.168.5.15"), LocalNetwork.SelectAdvertisementAddress(IPAddress.Any, candidates));
    }

    [Fact]
    public void Gateway_lan_is_listed_before_a_lan_without_a_gateway()
    {
        var nics = LocalNetwork.SelectAdvertisedNics(IPAddress.Any,
        [
            Nic("Ethernet", "192.168.5.15"),
            Nic("Wi-Fi", "192.168.1.20", type: NetworkInterfaceType.Wireless80211, gateway: true),
        ]);

        Assert.Equal(2, nics.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), nics[0].Address);
        Assert.Equal(IPAddress.Parse("192.168.5.15"), nics[1].Address);
    }

    [Fact]
    public void Wifi_plus_mobile_hotspot_advertises_only_wifi()
    {
        // Hotspot is enumerated first and has a gateway, which is how Windows
        // ICS often shows up next to the real Wi-Fi adapter.
        AdvertisementCandidate[] candidates =
        [
            Nic("Local Area Connection* 2", "192.168.137.1", gateway: true, description: "Microsoft Wi-Fi Direct Virtual Adapter"),
            Nic("Wi-Fi", "172.16.88.168", type: NetworkInterfaceType.Wireless80211, gateway: true, description: "Intel(R) Wi-Fi 6 AX201"),
        ];

        var nics = LocalNetwork.SelectAdvertisedNics(IPAddress.Any, candidates);

        Assert.Single(nics);
        Assert.Equal("Wi-Fi", nics[0].Name);
        Assert.Equal(IPAddress.Parse("172.16.88.168"), nics[0].Address);
        Assert.Equal(IPAddress.Parse("172.16.88.168"), LocalNetwork.SelectAdvertisementAddress(IPAddress.Any, candidates));
    }

    [Fact]
    public void Hotspot_markers_and_ics_subnet_lose_to_a_lan()
    {
        // Listed first, with a gateway, under names that would otherwise win.
        AdvertisementCandidate[] hotspotFirst =
        [
            Nic("Ethernet", "192.168.137.1", gateway: true, description: "Intel(R) Ethernet Connection"),
            Nic("以太网 2", "10.42.0.1", gateway: true, description: "Microsoft Wi-Fi Direct Virtual Adapter #2"),
            Nic("本地连接* 1", "192.168.50.1", gateway: true, description: "WAN Miniport"),
            Nic("Hotspot", "192.168.2.1", gateway: true, description: "Remote NDIS based Internet Sharing Device"),
            Nic("Wi-Fi", "172.16.88.168", type: NetworkInterfaceType.Wireless80211, gateway: true, description: "Intel(R) Wi-Fi 6 AX201"),
        ];

        var nics = LocalNetwork.SelectAdvertisedNics(IPAddress.Any, hotspotFirst);
        Assert.Single(nics);
        Assert.Equal(IPAddress.Parse("172.16.88.168"), nics[0].Address);
        Assert.True(LocalNetwork.IsWindowsIcsSubnet(IPAddress.Parse("192.168.137.1")));
        Assert.True(LocalNetwork.IsWindowsIcsSubnet(IPAddress.Parse("192.168.137.255")));
        Assert.False(LocalNetwork.IsWindowsIcsSubnet(IPAddress.Parse("192.168.136.1")));
        Assert.False(LocalNetwork.IsWindowsIcsSubnet(IPAddress.Parse("172.16.88.168")));
    }

    [Fact]
    public void Second_office_lan_is_advertised_and_hotspot_is_not()
    {
        // The querier subnet from the v0.1.21 report, plus the Wi-Fi that was
        // the only published address, plus the hotspot that froze video.
        var nics = LocalNetwork.SelectAdvertisedNics(IPAddress.Any,
        [
            Nic("Local Area Connection* 2", "192.168.137.1", gateway: true, description: "Microsoft Wi-Fi Direct Virtual Adapter"),
            Nic("Wi-Fi", "172.16.88.168", type: NetworkInterfaceType.Wireless80211, gateway: true, description: "Intel(R) Wi-Fi 6 AX201"),
            Nic("Ethernet", "172.16.93.10", gateway: true, description: "Realtek PCIe GbE Family Controller"),
        ]);

        Assert.Equal(2, nics.Count);
        Assert.Equal(IPAddress.Parse("172.16.88.168"), nics[0].Address);
        Assert.Equal(IPAddress.Parse("172.16.93.10"), nics[1].Address);
        Assert.DoesNotContain(nics, nic => nic.Address.Equals(IPAddress.Parse("192.168.137.1")));
    }

    [Fact]
    public void Off_subnet_querier_is_answered_unicast_and_on_subnet_stays_multicast()
    {
        AdvertisedNic[] wifiOnly =
        [
            new(IPAddress.Parse("172.16.88.168"), 24, "Wi-Fi"),
        ];
        var querier = IPAddress.Parse("172.16.93.211");

        Assert.False(LocalNetwork.QuerierOnAdvertisedLan(querier, wifiOnly));
        Assert.True(LocalNetwork.ShouldReplyUnicast(quBit: false, remotePort: 5353, querier, wifiOnly));
        Assert.False(LocalNetwork.ShouldReplyUnicast(quBit: false, remotePort: 5353, IPAddress.Parse("172.16.88.50"), wifiOnly));
        Assert.True(LocalNetwork.ShouldReplyUnicast(quBit: true, remotePort: 5353, IPAddress.Parse("172.16.88.50"), wifiOnly));
        Assert.True(LocalNetwork.ShouldReplyUnicast(quBit: false, remotePort: 5300, IPAddress.Parse("172.16.88.50"), wifiOnly));

        AdvertisedNic[] bothLans =
        [
            new(IPAddress.Parse("172.16.88.168"), 24, "Wi-Fi"),
            new(IPAddress.Parse("172.16.93.10"), 24, "Ethernet"),
        ];
        Assert.True(LocalNetwork.QuerierOnAdvertisedLan(querier, bothLans));
        Assert.False(LocalNetwork.ShouldReplyUnicast(quBit: false, remotePort: 5353, querier, bothLans));
        var answers = LocalNetwork.ReplyAddresses(LocalNetwork.MatchRemote(querier, bothLans), bothLans);
        Assert.Equal(IPAddress.Parse("172.16.93.10"), answers[0]);
        Assert.Equal(IPAddress.Parse("172.16.88.168"), answers[1]);
        Assert.DoesNotContain(answers, address => address.Equals(IPAddress.Parse("192.168.137.1")));
    }

    [Fact]
    public void Hotspot_is_a_last_resort_when_nothing_else_is_up()
    {
        var nics = LocalNetwork.SelectAdvertisedNics(IPAddress.Any,
        [
            Nic("Local Area Connection* 2", "192.168.137.1", gateway: true, description: "Microsoft Wi-Fi Direct Virtual Adapter"),
        ]);

        Assert.Single(nics);
        Assert.Equal(IPAddress.Parse("192.168.137.1"), nics[0].Address);
    }

    [Fact]
    public void Explicit_listen_does_not_add_other_nics()
    {
        var nics = LocalNetwork.SelectAdvertisedNics(IPAddress.Parse("192.168.5.15"), HyperVThenLan());

        Assert.Single(nics);
        Assert.Equal(IPAddress.Parse("192.168.5.15"), nics[0].Address);
    }

    [Fact]
    public void Virtual_only_advertises_one_fallback_not_every_hyperv_nic()
    {
        var nics = LocalNetwork.SelectAdvertisedNics(IPAddress.Any,
        [
            Nic("vEthernet (Default Switch)", "172.17.96.1", gateway: true, description: "Hyper-V Virtual Ethernet Adapter"),
            Nic("vEthernet (Wi-Fi)", "172.17.112.1", gateway: true, description: "Hyper-V Virtual Ethernet Adapter"),
        ]);

        Assert.Single(nics);
        Assert.Equal(IPAddress.Parse("172.17.96.1"), nics[0].Address);
    }

    [Fact]
    public void Query_response_uses_the_nic_on_the_querier_subnet()
    {
        AdvertisedNic[] nics =
        [
            new(IPAddress.Parse("192.168.5.15"), 24, "Ethernet"),
            new(IPAddress.Parse("10.1.2.3"), 24, "Wi-Fi"),
            new(IPAddress.Parse("10.1.2.1"), 16, "Other"),
        ];

        Assert.Equal(IPAddress.Parse("10.1.2.3"), LocalNetwork.MatchRemote(IPAddress.Parse("10.1.2.50"), nics));
        Assert.Equal(IPAddress.Parse("192.168.5.15"), LocalNetwork.MatchRemote(IPAddress.Parse("192.168.5.40"), nics));
        Assert.Equal(IPAddress.Parse("192.168.5.15"), LocalNetwork.MatchRemote(IPAddress.Parse("8.8.8.8"), nics));
        Assert.True(LocalNetwork.SharesPrefix(IPAddress.Parse("172.16.5.10"), IPAddress.Parse("172.16.1.1"), 20));
        Assert.False(LocalNetwork.SharesPrefix(IPAddress.Parse("172.16.5.10"), IPAddress.Parse("172.16.16.1"), 20));
        Assert.False(LocalNetwork.SharesPrefix(IPAddress.Parse("192.168.1.2"), IPAddress.Parse("192.168.1.2"), 0));
    }

    [Fact]
    public void Link_local_is_used_when_it_is_the_only_address()
    {
        AdvertisementCandidate[] candidates =
        [
            Nic("Ethernet", "169.254.1.2"),
        ];

        Assert.Equal(IPAddress.Parse("169.254.1.2"), LocalNetwork.SelectAdvertisementAddress(IPAddress.Any, candidates));
        Assert.Null(LocalNetwork.SelectAdvertisementAddress(IPAddress.Any, []));
        Assert.Null(LocalNetwork.SelectAdvertisementAddress(IPAddress.Any, [Nic("Ethernet", "192.168.1.2", status: OperationalStatus.Down)]));
    }

    [Fact]
    public void Virtual_markers_and_tunnels_lose_to_a_lan_address()
    {
        string[] names =
        [
            "vEthernet (Default Switch)",
            "vEthernet (Wi-Fi)",
            "vethernet (wsl)",
            "Hyper-V Virtual Switch",
            "Default Switch",
            "WSL",
            "docker0",
            "VMware Network Adapter VMnet8",
            "VirtualBox Host-Only Network",
            "cfw-tap",
            "Clash",
            "TAP-Windows Adapter V9",
            "WireGuard Tunnel",
            "VPN",
            "Teredo Tunneling Pseudo-Interface",
            "isatap",
            "6to4",
            "Local Area Connection* 2",
            "本地连接* 1",
            "Microsoft Wi-Fi Direct Virtual Adapter",
            "Wi-Fi Direct Virtual Adapter #2",
            "Microsoft Hosted Network Virtual Adapter",
            "Wireless Hosted Network Virtual Adapter",
            "Remote NDIS based Internet Sharing Device",
        ];

        foreach (var name in names)
        {
            var chosen = LocalNetwork.SelectAdvertisementAddress(IPAddress.Any,
            [
                Nic(name, "172.17.96.1", gateway: true),
                Nic("Ethernet", "192.168.5.15", gateway: true, description: "Realtek PCIe GbE Family Controller"),
            ]);
            Assert.True(IPAddress.Parse("192.168.5.15").Equals(chosen), name + " advertised " + chosen);
        }

        var fromDescription = LocalNetwork.SelectAdvertisementAddress(IPAddress.Any,
        [
            Nic("Ethernet 3", "10.1.1.1", gateway: true, description: "Hyper-V Virtual Ethernet Adapter"),
            Nic("以太网", "192.168.5.15", gateway: true, description: "Realtek PCIe GbE Family Controller"),
        ]);
        Assert.Equal(IPAddress.Parse("192.168.5.15"), fromDescription);

        var tunnel = LocalNetwork.SelectAdvertisementAddress(IPAddress.Any,
        [
            Nic("Ethernet 4", "10.8.0.2", type: NetworkInterfaceType.Tunnel, gateway: true, description: "WAN Miniport"),
            Nic("PPP Adapter", "10.9.0.2", type: NetworkInterfaceType.Ppp, gateway: true, description: "WAN Miniport (PPTP)"),
            Nic("Wi-Fi", "192.168.1.50", type: NetworkInterfaceType.Wireless80211, gateway: true),
        ]);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), tunnel);
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.17.96.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.5.15", true)]
    [InlineData("192.169.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("203.0.113.10", false)]
    public void Rfc1918_ranges(string text, bool expected)
    {
        Assert.Equal(expected, LocalNetwork.IsRfc1918(IPAddress.Parse(text)));
    }

    private static AdvertisementCandidate[] HyperVThenLan()
        =>
        [
            Nic("vEthernet (Default Switch)", "172.17.96.1", gateway: true, description: "Hyper-V Virtual Ethernet Adapter"),
            Nic("Ethernet", "192.168.5.15", gateway: true, description: "Realtek PCIe GbE Family Controller"),
        ];

    private static AdvertisementCandidate Nic(
        string name,
        string address,
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        bool gateway = false,
        OperationalStatus status = OperationalStatus.Up,
        string? description = null)
        => new(
            name,
            description ?? name,
            type,
            status,
            IPAddress.Parse(address),
            gateway);
}
