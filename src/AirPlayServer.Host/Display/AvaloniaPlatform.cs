using Avalonia;

namespace AirPlayServer.Host.Display;

/// <summary>
/// Picks one Avalonia window system at compile time. <c>UsePlatformDetect</c>
/// roots Win32, X11, and AppKit together, and a linux Native AOT publish would
/// then keep <c>user32</c> in the binary. The host project sets
/// <c>AVALONIA_WIN</c>, <c>AVALONIA_OSX</c>, or <c>AVALONIA_LINUX</c> from the
/// runtime identifier.
/// </summary>
internal static class AvaloniaPlatform
{
    public static AppBuilder UseHostPlatform(this AppBuilder builder)
    {
#if AVALONIA_WIN
        return builder.UseWin32().UseSkia().UseHarfBuzz();
#elif AVALONIA_OSX
        return builder.UseAvaloniaNative().UseSkia().UseHarfBuzz();
#else
        return builder.UseX11().UseSkia().UseHarfBuzz();
#endif
    }
}
