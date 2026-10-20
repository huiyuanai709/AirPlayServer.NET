namespace AirPlayServer.Core.Video;

/// <summary>
/// One on-screen mirror. Non-Windows hosts do not create a window.
/// </summary>
internal interface IMirrorWindow : IAsyncDisposable
{
    void SetDeviceName(string? deviceName);
}

internal static class MirrorWindows
{
    public static IMirrorWindow? TryOpen(
        FrameSlot frames,
        string title,
        IAirPlayLog log,
        int tapClockScreenWidth,
        int tapClockScreenHeight,
        CancellationToken cancellationToken)
    {
#if WINDOWS_HOST
        if (OperatingSystem.IsWindows())
        {
            return WindowsDisplayWindow.Start(frames, title, log, tapClockScreenWidth, tapClockScreenHeight, cancellationToken);
        }
#else
        _ = frames;
        _ = title;
        _ = log;
        _ = tapClockScreenWidth;
        _ = tapClockScreenHeight;
        _ = cancellationToken;
#endif
        return null;
    }
}
