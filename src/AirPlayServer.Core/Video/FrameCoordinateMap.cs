namespace AirPlayServer.Core.Video;

/// <summary>
/// Maps a window client point into the mirrored frame's pixel space.
/// The frame is painted into <see cref="FrameDestination"/>: stretch uses the
/// whole client, letterbox uses the fitted picture and leaves bars outside it.
/// A point outside that rectangle is not on the picture. A point inside maps with
/// <c>frame = (client - origin) * frameSize / destinationSize</c> (integer
/// truncation, the same sample COLORONCOLOR <c>StretchDIBits</c> paints), then
/// clamps to <c>[0, frameSize)</c>. Y grows downward in both spaces because the
/// window blits a top-down DIB.
/// </summary>
internal static class FrameCoordinateMap
{
    internal readonly record struct FrameDestination(int X, int Y, int Width, int Height);

    public static FrameDestination Stretch(int clientWidth, int clientHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0)
        {
            return new FrameDestination(0, 0, 0, 0);
        }

        return new FrameDestination(0, 0, clientWidth, clientHeight);
    }

    public static FrameDestination Letterbox(int clientWidth, int clientHeight, int frameWidth, int frameHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0 || frameWidth <= 0 || frameHeight <= 0)
        {
            return new FrameDestination(0, 0, 0, 0);
        }

        // Width is the tighter fit when clientWidth/frameWidth <= clientHeight/frameHeight.
        if ((long)clientWidth * frameHeight <= (long)clientHeight * frameWidth)
        {
            var height = (int)((long)clientWidth * frameHeight / frameWidth);
            if (height < 1)
            {
                height = 1;
            }

            if (height > clientHeight)
            {
                height = clientHeight;
            }

            return new FrameDestination(0, (clientHeight - height) / 2, clientWidth, height);
        }

        var width = (int)((long)clientHeight * frameWidth / frameHeight);
        if (width < 1)
        {
            width = 1;
        }

        if (width > clientWidth)
        {
            width = clientWidth;
        }

        return new FrameDestination((clientWidth - width) / 2, 0, width, clientHeight);
    }

    public static bool TryMapClientToFrame(
        int clientX,
        int clientY,
        FrameDestination destination,
        int frameWidth,
        int frameHeight,
        out int frameX,
        out int frameY)
    {
        frameX = 0;
        frameY = 0;
        if (frameWidth <= 0 || frameHeight <= 0 || destination.Width <= 0 || destination.Height <= 0)
        {
            return false;
        }

        var localX = clientX - destination.X;
        var localY = clientY - destination.Y;
        if ((uint)localX >= (uint)destination.Width || (uint)localY >= (uint)destination.Height)
        {
            return false;
        }

        frameX = (int)((long)localX * frameWidth / destination.Width);
        frameY = (int)((long)localY * frameHeight / destination.Height);
        if ((uint)frameX >= (uint)frameWidth)
        {
            frameX = frameWidth - 1;
        }

        if ((uint)frameY >= (uint)frameHeight)
        {
            frameY = frameHeight - 1;
        }

        return true;
    }

    /// <summary>
    /// Decodes the client point packed in <c>WM_MOUSEMOVE</c>'s <c>lParam</c>.
    /// Each coordinate is a signed 16-bit value.
    /// </summary>
    public static void ClientPoint(nint packed, out int x, out int y)
    {
        var value = unchecked((int)packed);
        x = (short)(value & 0xFFFF);
        y = (short)((value >> 16) & 0xFFFF);
    }
}
