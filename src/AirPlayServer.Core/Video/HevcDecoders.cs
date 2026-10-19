namespace AirPlayServer.Core.Video;

/// <summary>
/// Picks the HEVC decoder for this process. Windows uses Media Foundation.
/// Every other publish target keeps <see cref="UnavailableHevcDecoder"/>, so a
/// linux-x64 Native AOT binary does not reference mfplat.
/// </summary>
public static class HevcDecoders
{
    public static IHevcDecoder CreatePlatformDefault(IAirPlayLog? log = null)
    {
#if WINDOWS_HOST
        if (OperatingSystem.IsWindows())
        {
            return new MediaFoundationHevcDecoder(log);
        }
#else
        _ = log;
#endif
        return new UnavailableHevcDecoder();
    }
}
