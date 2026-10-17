using System.Net;

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
    }
}
