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
        // Software only. AngleEgl would load av_libglesv2.dll, which the UI
        // zip does not ship. The picture is a CPU BGRA blit.
        return builder.UseWin32()
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
            })
            .UseSkia()
            .UseHarfBuzz();
#elif AVALONIA_OSX
        return builder.UseAvaloniaNative().UseSkia().UseHarfBuzz();
#else
        return builder.UseX11().UseSkia().UseHarfBuzz();
#endif
    }
}
