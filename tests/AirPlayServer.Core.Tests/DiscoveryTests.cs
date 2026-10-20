using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using AirPlayServer.Core;
using AirPlayServer.Core.Discovery;

namespace AirPlayServer.Core.Tests;

public sealed class DiscoveryTests
{
    [Fact]
    public void Announcement_lists_both_services_and_stays_in_one_datagram()
    {
        var advertisement = Sample();
        var packet = MdnsMessages.BuildAnnouncement(advertisement);
        var text = Encoding.UTF8.GetString(packet);

        Assert.InRange(packet.Length, 12, 1400);
        Assert.Equal(0x8400, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal(7, MdnsMessages.CountWalkableRecords(packet));
        Assert.Contains("_airplay", text, StringComparison.Ordinal);
        Assert.Contains("_raop", text, StringComparison.Ordinal);
        Assert.Contains("_tcp", text, StringComparison.Ordinal);
        Assert.Contains(advertisement.DeviceId, text, StringComparison.Ordinal);
        Assert.Contains("pk=" + advertisement.PublicKeyBase64, text, StringComparison.Ordinal);
        Assert.Contains(AirPlayCompatibility.FeaturesText, text, StringComparison.Ordinal);
        Assert.Contains("AABBCCDDEEFF@客厅", text, StringComparison.Ordinal);
        Assert.All(MdnsMessages.AnswerTtls(packet), ttl => Assert.Equal(MdnsMessages.TtlSeconds, ttl));
    }

    [Fact]
    public void Announcement_can_publish_one_interface_address()
    {
        var packet = MdnsMessages.BuildAnnouncement(Sample(), IPAddress.Parse("192.168.5.15"));

        Assert.Equal(7, MdnsMessages.CountWalkableRecords(packet));
        Assert.True(ContainsSequence(packet, [192, 168, 5, 15]));
        Assert.False(ContainsSequence(packet, [10, 1, 2, 3]));
    }

    [Fact]
    public void Qu_bit_requests_a_unicast_response()
    {
        var query = MdnsMessages.BuildQuery(7, "_airplay");
        query[^2] = 0x80;
        var response = new byte[2048];

        Assert.True(MdnsMessages.TryBuildQueryResponse(query, Sample(), response, out _, out var unicast));
        Assert.True(unicast);
    }

    [Fact]
    public void Cadence_bursts_at_0_1_2_4_and_refreshes_before_ttl()
    {
        Assert.Equal(4500u, MdnsMessages.TtlSeconds);
        Assert.Equal(3600, MdnsCadence.TtlRefreshDeadlineSeconds);
        Assert.Equal(4, MdnsCadence.StartupAnnouncementCount);
        Assert.Equal([0, 1, 2, 4], MdnsCadence.StartupTimesSeconds());
        Assert.Equal([1, 1, 2], MdnsCadence.StartupGapSeconds);
        Assert.True(MdnsCadence.RefreshesBeforeTtl(MdnsCadence.SteadySeconds));
        Assert.True(MdnsCadence.RefreshesBeforeTtl(MdnsCadence.QueryBlindSeconds));
        Assert.True(MdnsCadence.QueryBlindSeconds < MdnsCadence.SteadySeconds);
        Assert.Equal(TimeSpan.FromSeconds(60), MdnsCadence.SteadyIntervalFor(receivingQueries: true));
        Assert.Equal(TimeSpan.FromSeconds(10), MdnsCadence.SteadyIntervalFor(receivingQueries: false));
        Assert.Equal(3, MdnsCadence.StartupGaps().Count);
    }

    [Fact]
    public void Startup_log_names_the_socket_the_address_and_the_cadence()
    {
        var receiving = MdnsCadence.DescribeStartup(true, true, "客厅", "aa:bb:cc:dd:ee:ff", "Ethernet=192.168.5.15", null);
        Assert.Contains("UDP 5353", receiving, StringComparison.Ordinal);
        Assert.Contains("客厅", receiving, StringComparison.Ordinal);
        Assert.Contains("aa:bb:cc:dd:ee:ff", receiving, StringComparison.Ordinal);
        Assert.Contains("192.168.5.15", receiving, StringComparison.Ordinal);
        Assert.Contains("0/1/2/4s", receiving, StringComparison.Ordinal);
        Assert.Contains("60s", receiving, StringComparison.Ordinal);
        Assert.Contains("4500s", receiving, StringComparison.Ordinal);
        Assert.Contains("3600s", receiving, StringComparison.Ordinal);

        var blind = MdnsCadence.DescribeStartup(false, true, "客厅", "aa:bb:cc:dd:ee:ff", "Wi-Fi=192.168.1.20", "AddressAlreadyInUse");
        Assert.Contains("AddressAlreadyInUse", blind, StringComparison.Ordinal);
        Assert.Contains("10s", blind, StringComparison.Ordinal);
        Assert.Contains("5353", blind, StringComparison.Ordinal);
        Assert.Contains("other port", blind, StringComparison.Ordinal);

        var announcement = MdnsCadence.DescribeAnnouncement(3, "客厅", "aa:bb:cc:dd:ee:ff", "Wi-Fi=192.168.1.20", 5353, false);
        Assert.Contains("通告 #3", announcement, StringComparison.Ordinal);
        Assert.Contains("sourcePort=5353", announcement, StringComparison.Ordinal);
        Assert.Contains("querySocket=off", announcement, StringComparison.Ordinal);

        var ephemeral = MdnsCadence.DescribeAnnouncement(1, "客厅", "aa:bb:cc:dd:ee:ff", "Wi-Fi=192.168.1.20", 40000, false);
        Assert.Contains("not 5353", ephemeral, StringComparison.Ordinal);

        var changed = MdnsCadence.DescribeAddressChange("客厅", "aa:bb:cc:dd:ee:ff", "Ethernet=192.168.5.15", "Wi-Fi=192.168.1.20");
        Assert.Contains("192.168.5.15", changed, StringComparison.Ordinal);
        Assert.Contains("192.168.1.20", changed, StringComparison.Ordinal);
        Assert.Contains("只再发一条", changed, StringComparison.Ordinal);
        Assert.Contains("not reset", changed, StringComparison.Ordinal);
    }

    [Fact]
    public void Unchanged_address_does_not_request_another_announcement()
    {
        var wifi = new AdvertisedNic(IPAddress.Parse("172.16.88.168"), 24, "Wi-Fi");
        var same = new AdvertisedNic(IPAddress.Parse("172.16.88.168"), 24, "Wi-Fi");
        Assert.False(MdnsCadence.ShouldRefreshAnnouncement([wifi], [same]));
        Assert.False(MdnsCadence.ShouldRefreshAnnouncement([wifi], [wifi]));

        var moved = new AdvertisedNic(IPAddress.Parse("172.16.88.50"), 24, "Wi-Fi");
        Assert.True(MdnsCadence.ShouldRefreshAnnouncement([wifi], [moved]));
        Assert.True(MdnsCadence.NetworkChangeDebounce >= TimeSpan.FromSeconds(2));
        Assert.True(MdnsCadence.AddressPollInterval >= TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Unchanged_hotspot_address_does_not_request_another_announcement()
    {
        var withHotspot = WifiAndHotspot(hotspot: "192.168.137.1");
        var same = WifiAndHotspot(hotspot: "192.168.137.1");
        var moved = WifiAndHotspot(hotspot: "192.168.137.2");

        var before = LocalNetwork.SelectAdvertisedNics(IPAddress.Any, withHotspot);
        var afterSame = LocalNetwork.SelectAdvertisedNics(IPAddress.Any, same);
        var afterMoved = LocalNetwork.SelectAdvertisedNics(IPAddress.Any, moved);

        Assert.Equal(2, before.Count);
        Assert.Equal(IPAddress.Parse("172.16.88.168"), before[0].Address);
        Assert.Equal(IPAddress.Parse("192.168.137.1"), before[1].Address);
        Assert.False(MdnsCadence.ShouldRefreshAnnouncement(before, afterSame));
        Assert.True(MdnsCadence.ShouldRefreshAnnouncement(before, afterMoved));
        Assert.Equal(IPAddress.Parse("172.16.88.168"), afterMoved[0].Address);
        Assert.Equal(IPAddress.Parse("192.168.137.2"), afterMoved[1].Address);
    }

    [Fact]
    public void Wifi_and_hotspot_are_separate_packets_and_answers()
    {
        var nics = LocalNetwork.SelectAdvertisedNics(IPAddress.Any, WifiAndHotspot(hotspot: "192.168.137.1", wifiIndex: 12, hotspotIndex: 4));
        var wifi = IPAddress.Parse("172.16.88.168");
        var hotspot = IPAddress.Parse("192.168.137.1");
        var wifiClient = IPAddress.Parse("172.16.88.20");
        var hotspotClient = IPAddress.Parse("192.168.137.20");

        Assert.Equal(2, nics.Count);
        Assert.Equal(wifi, nics[0].Address);
        Assert.Equal(hotspot, nics[1].Address);

        var wifiPacket = MdnsMessages.BuildAnnouncement(Sample(), nics[0].Address);
        var hotspotPacket = MdnsMessages.BuildAnnouncement(Sample(), nics[1].Address);
        AssertOnlyAddress(wifiPacket, [172, 16, 88, 168], [192, 168, 137, 1]);
        AssertOnlyAddress(hotspotPacket, [192, 168, 137, 1], [172, 16, 88, 168]);

        var wifiArrival = LocalNetwork.FindByInterface(12, nics);
        var hotspotArrival = LocalNetwork.FindByInterface(4, nics);
        Assert.Equal(wifi, wifiArrival?.Address);
        Assert.Equal(hotspot, hotspotArrival?.Address);
        Assert.Equal(wifi, LocalNetwork.AnswerAddress(wifiClient, nics, wifiArrival, boundAddress: null));
        Assert.Equal(hotspot, LocalNetwork.AnswerAddress(hotspotClient, nics, hotspotArrival, boundAddress: null));
        Assert.Equal(wifi, LocalNetwork.AnswerAddress(hotspotClient, nics, arrival: null, wifi));
        Assert.Equal(hotspot, LocalNetwork.AnswerAddress(wifiClient, nics, arrival: null, hotspot));
        Assert.Equal(wifi, LocalNetwork.AnswerAddress(wifiClient, nics, arrival: null, boundAddress: null));
        Assert.Equal(hotspot, LocalNetwork.AnswerAddress(hotspotClient, nics, arrival: null, boundAddress: null));

        var query = MdnsMessages.BuildQuery(0x2222, "_airplay");
        var response = new byte[2048];
        Assert.True(MdnsMessages.TryBuildQueryResponse(
            query,
            Sample(),
            response,
            out var hotspotWritten,
            out _,
            LocalNetwork.AnswerAddress(hotspotClient, nics, hotspotArrival, boundAddress: null)));
        var hotspotBody = response.AsSpan(0, hotspotWritten).ToArray();
        Assert.Equal(4, MdnsMessages.CountWalkableRecords(hotspotBody));
        Assert.True(ContainsSequence(hotspotBody, [192, 168, 137, 1]));
        Assert.False(ContainsSequence(hotspotBody, [172, 16, 88, 168]));
        Assert.False(ContainsSequence(hotspotBody, [10, 1, 2, 3]));

        Assert.True(MdnsMessages.TryBuildQueryResponse(
            query,
            Sample(),
            response,
            out var wifiWritten,
            out _,
            LocalNetwork.AnswerAddress(wifiClient, nics, wifiArrival, boundAddress: null)));
        var wifiBody = response.AsSpan(0, wifiWritten).ToArray();
        Assert.Equal(4, MdnsMessages.CountWalkableRecords(wifiBody));
        Assert.True(ContainsSequence(wifiBody, [172, 16, 88, 168]));
        Assert.False(ContainsSequence(wifiBody, [192, 168, 137, 1]));
        Assert.False(ContainsSequence(wifiBody, [10, 1, 2, 3]));

        var onHotspot = LocalNetwork.PlanReply(quBit: true, remotePort: 5353, queryWasMulticast: true, hotspotClient, nics, hotspotArrival);
        Assert.Equal("qu", onHotspot.Reason);
        Assert.True(onHotspot.SendUnicast);
        Assert.False(onHotspot.SendMulticast);

        var overlaid = LocalNetwork.PlanReply(quBit: true, remotePort: 5353, queryWasMulticast: true, IPAddress.Parse("172.16.93.211"), nics, wifiArrival);
        Assert.Equal("qu-overlaid", overlaid.Reason);
        Assert.False(overlaid.SendUnicast);
        Assert.True(overlaid.SendMulticast);
    }

    [Fact]
    public void Each_lan_announcement_names_only_that_nic_and_never_the_hotspot()
    {
        var wifi = MdnsMessages.BuildAnnouncement(Sample(), IPAddress.Parse("172.16.88.168"));
        var ethernet = MdnsMessages.BuildAnnouncement(Sample(), IPAddress.Parse("172.16.93.10"));

        Assert.Equal(7, MdnsMessages.CountWalkableRecords(wifi));
        Assert.Equal(7, MdnsMessages.CountWalkableRecords(ethernet));
        Assert.True(ContainsSequence(wifi, [172, 16, 88, 168]));
        Assert.False(ContainsSequence(wifi, [172, 16, 93, 10]));
        Assert.False(ContainsSequence(wifi, [192, 168, 137, 1]));
        Assert.True(ContainsSequence(ethernet, [172, 16, 93, 10]));
        Assert.False(ContainsSequence(ethernet, [172, 16, 88, 168]));
    }

    [Fact]
    public void Off_subnet_multicast_query_is_answered_on_the_arrival_nic()
    {
        var nics = LocalNetwork.SelectAdvertisedNics(IPAddress.Any,
        [
            new AdvertisementCandidate(
                "Local Area Connection* 2",
                "Microsoft Wi-Fi Direct Virtual Adapter",
                NetworkInterfaceType.Wireless80211,
                OperationalStatus.Up,
                IPAddress.Parse("192.168.137.1"),
                HasGateway: true,
                PrefixLength: 24,
                InterfaceIndex: 4),
            new AdvertisementCandidate(
                "Wi-Fi",
                "Intel(R) Wi-Fi 6 AX201",
                NetworkInterfaceType.Wireless80211,
                OperationalStatus.Up,
                IPAddress.Parse("172.16.88.168"),
                HasGateway: true,
                PrefixLength: 24,
                InterfaceIndex: 12),
        ]);
        var querier = IPAddress.Parse("172.16.93.211");
        var arrival = LocalNetwork.FindByInterface(12, nics);
        var plan = LocalNetwork.PlanReply(quBit: false, remotePort: 5353, queryWasMulticast: true, querier, nics, arrival);
        Assert.True(plan.SendMulticast);
        Assert.False(plan.SendUnicast);
        Assert.True(MdnsSockets.IsMulticastDestination(IPAddress.Parse("224.0.0.251")));
        Assert.False(MdnsSockets.IsMulticastDestination(IPAddress.Parse("172.16.88.168")));

        var answer = LocalNetwork.AnswerAddress(querier, nics, arrival, boundAddress: null);
        var query = MdnsMessages.BuildQuery(0x2222, "_airplay");
        var response = new byte[2048];
        Assert.True(MdnsMessages.TryBuildQueryResponse(query, Sample(), response, out var written, out var quBit, answer));
        Assert.False(quBit);
        Assert.Equal(4, MdnsMessages.CountWalkableRecords(response.AsSpan(0, written)));
        Assert.Equal(0x2222, BinaryPrimitives.ReadUInt16BigEndian(response));
        MdnsMessages.StampMulticastId(response.AsSpan(0, written));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(response));
        var body = response.AsSpan(0, written).ToArray();
        Assert.True(ContainsSequence(body, [172, 16, 88, 168]));
        Assert.False(ContainsSequence(body, [192, 168, 137, 1]));
        Assert.False(ContainsSequence(body, [10, 1, 2, 3]));
    }

    [Fact]
    public void Chosen_lan_is_the_only_address_in_the_announcement()
    {
        var chosen = LocalNetwork.SelectAdvertisementAddress(IPAddress.Any,
        [
            new AdvertisementCandidate(
                "Local Area Connection* 2",
                "Microsoft Wi-Fi Direct Virtual Adapter",
                NetworkInterfaceType.Wireless80211,
                OperationalStatus.Up,
                IPAddress.Parse("192.168.137.1"),
                HasGateway: true,
                PrefixLength: 24),
            new AdvertisementCandidate(
                "Wi-Fi",
                "Intel(R) Wi-Fi 6 AX201",
                NetworkInterfaceType.Wireless80211,
                OperationalStatus.Up,
                IPAddress.Parse("172.16.88.168"),
                HasGateway: true,
                PrefixLength: 24),
        ]);
        var packet = MdnsMessages.BuildAnnouncement(Sample(), chosen);

        Assert.Equal(IPAddress.Parse("172.16.88.168"), chosen);
        Assert.True(ContainsSequence(packet, [172, 16, 88, 168]));
        Assert.False(ContainsSequence(packet, [192, 168, 137, 1]));
    }

    [Fact]
    public void Shared_udp_sockets_can_bind_the_same_port()
    {
        using var first = MdnsSockets.CreateSharedUdp();
        first.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)first.LocalEndPoint!).Port;
        using var second = MdnsSockets.CreateSharedUdp();
        second.Bind(new IPEndPoint(IPAddress.Loopback, port));

        Assert.Equal(port, ((IPEndPoint)second.LocalEndPoint!).Port);
        Assert.NotEqual(0, port);
    }

    [Fact]
    public void Hevc_announcement_sets_features_bit_42_and_stays_in_one_datagram()
    {
        var advertisement = new ReceiverAdvertisement
        {
            InstanceName = "客厅",
            HostLabel = "living-room",
            DeviceId = "aa:bb:cc:dd:ee:ff",
            RaopInstance = "AABBCCDDEEFF@客厅",
            AirPlayPort = 7001,
            RaopPort = 5001,
            Address = IPAddress.Parse("10.1.2.3"),
            PublicKeyBase64 = "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqo=",
            AcceptHevc = true,
        };
        var packet = MdnsMessages.BuildAnnouncement(advertisement);
        var text = Encoding.UTF8.GetString(packet);

        Assert.InRange(packet.Length, 12, 1400);
        Assert.Contains("0x5A7FFEE6,0x400", text, StringComparison.Ordinal);
        Assert.Equal(2, Count(text, "0x5A7FFEE6,0x400"));
        Assert.DoesNotContain("0x5A7FFEE6,0x0", text, StringComparison.Ordinal);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    [Fact]
    public void Query_for_airplay_is_answered_and_unrelated_names_are_ignored()
    {
        var advertisement = Sample();
        var query = MdnsMessages.BuildQuery(0x1234, "_airplay");
        var response = new byte[2048];

        Assert.True(MdnsMessages.TryBuildQueryResponse(query, advertisement, response, out var written, out var unicast));
        Assert.False(unicast);
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(response));
        Assert.Equal(4, MdnsMessages.CountWalkableRecords(response.AsSpan(0, written)));
        Assert.Contains("客厅", Encoding.UTF8.GetString(response.AsSpan(0, written)), StringComparison.Ordinal);

        var other = MdnsMessages.BuildQuery(1, "_http");
        Assert.False(MdnsMessages.TryBuildQueryResponse(other, advertisement, response, out _, out _));
        Assert.False(MdnsMessages.TryBuildQueryResponse([0, 1, 2], advertisement, response, out _, out _));
    }

    [Fact]
    public void Names_fit_dns_labels()
    {
        Assert.Equal("living-room", ReceiverNames.HostLabel("Living Room"));
        var instance = ReceiverNames.InstanceLabel(new string('测', 40));
        Assert.InRange(Encoding.UTF8.GetByteCount(instance), 1, 50);
        Assert.Equal("02:11:22:33:44:55", ReceiverNames.FormatDeviceId([0x02, 0x11, 0x22, 0x33, 0x44, 0x55]));
    }

    private static void AssertOnlyAddress(byte[] packet, byte[] present, byte[] absent)
    {
        Assert.Equal(7, MdnsMessages.CountWalkableRecords(packet));
        Assert.True(ContainsSequence(packet, present));
        Assert.False(ContainsSequence(packet, absent));
        Assert.False(ContainsSequence(packet, [10, 1, 2, 3]));
    }

    private static AdvertisementCandidate[] WifiAndHotspot(string hotspot, int wifiIndex = 12, int hotspotIndex = 4)
        =>
        [
            new(
                "Local Area Connection* 2",
                "Microsoft Wi-Fi Direct Virtual Adapter",
                NetworkInterfaceType.Wireless80211,
                OperationalStatus.Up,
                IPAddress.Parse(hotspot),
                HasGateway: true,
                PrefixLength: 24,
                InterfaceIndex: hotspotIndex),
            new(
                "Wi-Fi",
                "Intel(R) Wi-Fi 6 AX201",
                NetworkInterfaceType.Wireless80211,
                OperationalStatus.Up,
                IPAddress.Parse("172.16.88.168"),
                HasGateway: true,
                PrefixLength: 24,
                InterfaceIndex: wifiIndex),
        ];

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    private static ReceiverAdvertisement Sample() => new()
    {
        InstanceName = "客厅",
        HostLabel = "living-room",
        DeviceId = "aa:bb:cc:dd:ee:ff",
        RaopInstance = "AABBCCDDEEFF@客厅",
        AirPlayPort = 7001,
        RaopPort = 5001,
        Address = IPAddress.Parse("10.1.2.3"),
        PublicKeyBase64 = "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqo=",
    };
}
