using System.Buffers.Binary;
using System.Net;
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
