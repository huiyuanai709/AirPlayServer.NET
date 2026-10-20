using System.Net;

namespace AirPlayServer.Core.Discovery;

internal sealed class ReceiverAdvertisement
{
    public required string InstanceName { get; init; }

    public required string HostLabel { get; init; }

    public required string DeviceId { get; init; }

    public required string RaopInstance { get; init; }

    public required ushort AirPlayPort { get; init; }

    public required ushort RaopPort { get; init; }

    public IPAddress? Address { get; set; }

    public required string PublicKeyBase64 { get; init; }

    public int DisplayWidth { get; init; } = AirPlayCompatibility.DefaultDisplayWidth;

    public int DisplayHeight { get; init; } = AirPlayCompatibility.DefaultDisplayHeight;

    public int MaxFramesPerSecond { get; init; } = AirPlayCompatibility.DefaultMaxFramesPerSecond;

    /// <summary>When set, mDNS and /info include Features bit 42.</summary>
    public bool AcceptHevc { get; init; }

    public long FeaturesInteger => AirPlayCompatibility.FeaturesInteger(AcceptHevc);

    public string FeaturesText => AirPlayCompatibility.FormatFeatures(AcceptHevc);

    public string AirPlayInstanceFqdn => InstanceName + "._airplay._tcp.local";

    public string RaopInstanceFqdn => RaopInstance + "._raop._tcp.local";

    public string HostFqdn => HostLabel + ".local";
}
