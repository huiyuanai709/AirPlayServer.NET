namespace AirPlayServer.Core.Video;

/// <summary>
/// Picks the H.264 decoder for this process. Windows uses Media Foundation.
/// Every other publish target keeps <see cref="UnavailableH264Decoder"/>, so a
/// linux-x64 Native AOT binary does not reference mfplat or user32.
/// </summary>
public static class H264Decoders
{
    public static IH264Decoder CreatePlatformDefault(IAirPlayLog? log = null)
    {
#if WINDOWS_HOST
        if (OperatingSystem.IsWindows())
        {
            return new MediaFoundationH264Decoder(log);
        }
#else
        _ = log;
#endif
        return new UnavailableH264Decoder();
    }

    internal static bool NegotiatedOutput(IH264Decoder decoder)
    {
#if WINDOWS_HOST
        if (decoder is MediaFoundationH264Decoder mediaFoundation)
        {
            return mediaFoundation.HasNegotiatedOutput;
        }
#else
        _ = decoder;
#endif
        return false;
    }
}
