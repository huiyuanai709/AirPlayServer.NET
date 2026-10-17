namespace AirPlayServer.Core;

/// <summary>
/// Wire compatibility values. The model string is what a sender expects to see;
/// it does not mean this process is an Apple TV. Flags and the low feature word
/// match the mirroring-and-audio advertisement of the upstream receiver.
/// </summary>
public static class AirPlayCompatibility
{
    public const uint FeaturesLow = 0x5A7FFEE6;
    public const uint FeaturesHigh = 0x00000000;
    public const string Model = "AppleTV14,1";
    public const string SourceVersion = "845.5.1";
    public const string ServerHeader = "AirTunes/845.5.1";
    public const int DefaultDisplayWidth = 1920;
    public const int DefaultDisplayHeight = 1080;
    public const int DefaultMaxFramesPerSecond = 60;

    public static string FeaturesText => $"0x{FeaturesLow:X},0x{FeaturesHigh:X}";
}
