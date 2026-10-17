using System.Net;
using AirPlayServer.Core.Export;
using AirPlayServer.Core.Media;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core;

public sealed class AirPlayReceiverOptions
{
    public string Name { get; set; } = "AirPlayServer.NET";

    public IPAddress ListenAddress { get; set; } = IPAddress.Any;

    public ushort AirPlayPort { get; set; } = 7001;

    public ushort RaopPort { get; set; } = 5001;

    public byte[]? HardwareAddress { get; set; }

    public byte[]? PairingSeed { get; set; }

    public bool EnableDiscovery { get; set; } = true;

    public int DisplayWidth { get; set; } = AirPlayCompatibility.DefaultDisplayWidth;

    public int DisplayHeight { get; set; } = AirPlayCompatibility.DefaultDisplayHeight;

    public int MaxFramesPerSecond { get; set; } = AirPlayCompatibility.DefaultMaxFramesPerSecond;

    public int MaxConnections { get; set; } = 8;

    public IAirPlayLog? Log { get; set; }

    /// <summary>
    /// Open the latest-frame window. The window is created only on Windows.
    /// Clicks in that window are not forwarded to the phone.
    /// </summary>
    public bool ShowWindow { get; set; }

    /// <summary>Publish one synthetic BGRA frame after start. It is not a mirrored screen.</summary>
    public bool PublishDemoFrame { get; set; }

    /// <summary>
    /// Optional 16-byte mirror content key for a lab sender. This is not a FairPlay unwrap.
    /// </summary>
    public byte[]? LabContentKey { get; set; }

    public IMediaKeyUnwrapper? MediaKeyUnwrapper { get; set; }

    /// <summary>
    /// Optional native DLL or shared library. See <c>NativeMediaKeyModule</c> for the C ABI.
    /// A missing export keeps FairPlay fail-closed.
    /// </summary>
    public string? MediaKeyPluginPath { get; set; }

    /// <summary>
    /// Optional in-process <c>POST /fp-setup</c> answer. Null, or a false return, is HTTP 501.
    /// </summary>
    public IFairPlaySetup? FairPlaySetup { get; set; }

    public IH264Decoder? Decoder { get; set; }

    /// <summary>TapClock JPEG export. Null or without an endpoint and directory disables it.</summary>
    public TapClockExportOptions? TapClock { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Receiver name is required.", nameof(Name));
        }

        if (ListenAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentException("This slice listens on IPv4 only.", nameof(ListenAddress));
        }

        if (AirPlayPort == RaopPort && AirPlayPort != 0)
        {
            throw new ArgumentException("AirPlay and RAOP ports must be different.");
        }

        if (HardwareAddress is not null && HardwareAddress.Length != 6)
        {
            throw new ArgumentException("Hardware address must be 6 bytes.", nameof(HardwareAddress));
        }

        if (PairingSeed is not null && PairingSeed.Length != 32)
        {
            throw new ArgumentException("Pairing seed must be 32 bytes.", nameof(PairingSeed));
        }

        if (DisplayWidth is < 16 or > 7680 || DisplayHeight is < 16 or > 4320)
        {
            throw new ArgumentException("Display size is outside the supported range.");
        }

        if (MaxFramesPerSecond is < 1 or > 120)
        {
            throw new ArgumentException("Max frame rate is outside the supported range.", nameof(MaxFramesPerSecond));
        }

        if (MaxConnections is < 1 or > 64)
        {
            throw new ArgumentException("Max connections is outside the supported range.", nameof(MaxConnections));
        }

        if (LabContentKey is not null && LabContentKey.Length != 16)
        {
            throw new ArgumentException("Lab content key must be 16 bytes.", nameof(LabContentKey));
        }

        TapClock?.Validate();
    }
}
