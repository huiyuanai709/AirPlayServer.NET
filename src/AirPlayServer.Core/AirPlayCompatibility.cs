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

    /// <summary>
    /// Unofficial AirPlay feature. Bit 42 tells the sender this receiver can
    /// negotiate more than one screen codec, which is the HEVC mirror path.
    /// </summary>
    public const int SupportsScreenMultiCodecBit = 42;

    public const string Model = "AppleTV14,1";
    public const string SourceVersion = "845.5.1";
    public const string ServerHeader = "AirTunes/845.5.1";
    public const int DefaultDisplayWidth = 1920;
    public const int DefaultDisplayHeight = 1080;
    public const int DefaultMaxFramesPerSecond = 60;

    /// <summary>Feature text with HEVC left off. Existing advertisements stay on this value.</summary>
    public static string FeaturesText => FormatFeatures(acceptHevc: false);

    public static uint FeaturesHighWord(bool acceptHevc)
        => acceptHevc ? FeaturesHigh | (1u << (SupportsScreenMultiCodecBit - 32)) : FeaturesHigh;

    public static long FeaturesInteger(bool acceptHevc)
        => (long)(((ulong)FeaturesHighWord(acceptHevc) << 32) | FeaturesLow);

    public static string FormatFeatures(bool acceptHevc)
        => $"0x{FeaturesLow:X},0x{FeaturesHighWord(acceptHevc):X}";
}
