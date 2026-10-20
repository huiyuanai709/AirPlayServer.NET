using AirPlayServer.Core.Notify;

namespace AirPlayServer.Core.Video;

/// <summary>
/// One on-screen mirror. The host opens one surface per session.
/// Pointer presses, drags, and releases stay in the window.
/// <see cref="MirrorSurfaceRequest.ReportPointer"/> publishes them.
/// They are not forwarded to the phone.
/// </summary>
public interface IMirrorSurface : IAsyncDisposable
{
    void SetDeviceName(string? deviceName);
}

/// <summary>
/// What the host needs to open one mirror window. The frame slot is the same
/// latest-BGRA slot the decoder and TapClock already share.
/// </summary>
public sealed class MirrorSurfaceRequest
{
    public required FrameSlot Frames { get; init; }

    public required string Title { get; init; }

    public required IAirPlayLog Log { get; init; }

    /// <summary>TapClock UIKit points from <c>--model</c>. Zero when the model was not set.</summary>
    public int TapClockScreenWidth { get; init; }

    /// <summary>TapClock UIKit points from <c>--model</c>. Zero when the model was not set.</summary>
    public int TapClockScreenHeight { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Press, drag, and release on this window, already mapped. Null when the
    /// host does not publish pointer events. The callback must not send HID.
    /// </summary>
    public Action<MirrorPointerGesture>? ReportPointer { get; init; }
}

/// <summary>
/// Host-supplied opener. Called once for the primary session and once for each
/// extra mirror. A null return leaves that session headless.
/// </summary>
public delegate IMirrorSurface? MirrorSurfaceFactory(MirrorSurfaceRequest request);

/// <summary>
/// Letterbox, hover text, and top-down BGRA compose shared by the mirror window.
/// The window paints this buffer; it does not send clicks to the phone.
/// Client pixels and the pointer are in the same space (device pixels of the
/// client). <see cref="DevicePixels"/> converts a DIP position with the same
/// truncation the window uses for both axes.
/// </summary>
public static class MirrorViewMath
{
    public readonly record struct Placement(int X, int Y, int Width, int Height);

    public static string DeviceTitle(string? deviceName)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? "mirror" : deviceName.Trim();
        return "AirPlayServer.NET — " + name;
    }

    /// <summary>
    /// Coordinates lead the title so a narrow window does not clip them.
    /// </summary>
    public static string HoverTitle(string deviceTitle, string readout)
        => readout + " — " + deviceTitle;

    public static Placement Letterbox(int clientWidth, int clientHeight, int frameWidth, int frameHeight)
    {
        var box = FrameCoordinateMap.Letterbox(clientWidth, clientHeight, frameWidth, frameHeight);
        return new Placement(box.X, box.Y, box.Width, box.Height);
    }

    /// <summary>
    /// Truncates <paramref name="dip"/> * <paramref name="renderScaling"/> toward zero.
    /// Non-finite inputs become 0. The mirror window uses this for the pointer
    /// and for the client size so both stay in one pixel space.
    /// </summary>
    public static int DevicePixels(double dip, double renderScaling)
    {
        if (renderScaling <= 0
            || double.IsNaN(dip)
            || double.IsInfinity(dip)
            || double.IsNaN(renderScaling)
            || double.IsInfinity(renderScaling))
        {
            return 0;
        }

        var pixels = dip * renderScaling;
        if (pixels <= int.MinValue)
        {
            return int.MinValue;
        }

        if (pixels >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)pixels;
    }

    /// <summary>
    /// Pointer over the letterboxed picture. Bars and the area outside the
    /// picture return false. <paramref name="tapClockScreenWidth"/> and
    /// <paramref name="tapClockScreenHeight"/> are UIKit points; either one
    /// non-positive selects decoded-frame pixels (<c>像素 x, y</c>).
    /// </summary>
    public static bool TryHoverReadout(
        int pointerX,
        int pointerY,
        int clientWidth,
        int clientHeight,
        int frameWidth,
        int frameHeight,
        int tapClockScreenWidth,
        int tapClockScreenHeight,
        out string readout)
    {
        var destination = FrameCoordinateMap.Letterbox(clientWidth, clientHeight, frameWidth, frameHeight);
        var tapClock = tapClockScreenWidth > 0 && tapClockScreenHeight > 0;
        if (!FrameCoordinateMap.TryMapClientToScreen(
                pointerX,
                pointerY,
                destination,
                frameWidth,
                frameHeight,
                tapClock ? tapClockScreenWidth : 0,
                tapClock ? tapClockScreenHeight : 0,
                out var screenX,
                out var screenY))
        {
            readout = "";
            return false;
        }

        readout = FrameCoordinateMap.FormatHoverReadout(screenX, screenY, tapClock);
        return true;
    }

    /// <summary>
    /// Clears <paramref name="destination"/> and paints the frame letterboxed,
    /// top-down, with opaque alpha. Black bars are the cleared pixels.
    /// Returns false when the destination cannot hold the client or the frame
    /// does not fit a positive picture.
    /// </summary>
    public static bool TryComposeLetterbox(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride)
    {
        if (destinationWidth < 1
            || destinationHeight < 1
            || destinationStride < destinationWidth * 4
            || (destinationStride & 3) != 0)
        {
            return false;
        }

        var bytes = (long)destinationStride * destinationHeight;
        if (bytes <= 0 || bytes > destination.Length)
        {
            return false;
        }

        var box = FrameCoordinateMap.Letterbox(destinationWidth, destinationHeight, sourceWidth, sourceHeight);
        if (box.Width <= 0 || box.Height <= 0)
        {
            return false;
        }

        // Opaque black. A cleared alpha of 0 is transparent on a 32-bit window,
        // which shows the desktop in the bars instead of a letterbox.
        FillOpaqueBlack(destination.Slice(0, (int)bytes));
        return BgraScale.PaintLetterbox(
            source,
            sourceWidth,
            sourceHeight,
            sourceStride,
            destination,
            destinationWidth,
            destinationHeight,
            destinationStride,
            box.X,
            box.Y,
            box.Width,
            box.Height,
            bottomUp: false);
    }

    private static void FillOpaqueBlack(Span<byte> destination)
    {
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(destination);
        pixels.Fill(BitConverter.IsLittleEndian ? 0xFF000000u : 0x000000FFu);
    }
}
