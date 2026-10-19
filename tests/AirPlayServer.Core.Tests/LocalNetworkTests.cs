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
