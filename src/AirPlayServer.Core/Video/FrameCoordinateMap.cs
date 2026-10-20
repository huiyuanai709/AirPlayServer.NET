using System.Globalization;

namespace AirPlayServer.Core.Video;

/// <summary>
/// Maps a window client point into the mirrored frame, then into TapClock's
/// absolute screen. The frame is painted into <see cref="FrameDestination"/>:
/// stretch uses the whole client, letterbox uses the fitted picture and leaves
/// bars outside it. The mirror window letterboxes and fills only those bars.
/// <see cref="LetterboxBars"/> lists the strips and does not include the picture.
/// A point outside that rectangle, including the bars, is not on the picture.
/// A point inside maps with
/// <c>frame = (client - origin) * frameSize / destinationSize</c> (integer
/// truncation), then clamps to <c>[0, frameSize)</c>. The window samples with
/// the same integer grid; this map does not follow a filtered pixel.
/// Frame pixels then use TapClock <c>ScreenMap.FromImage</c>:
/// <c>screen = frame * screenSize / frameSize</c>, also integer truncation, with
/// X and Y scaled on their own axes. A non-positive screen size means
/// <c>--model</c> was not set, so the frame size is the screen and the point is
/// unchanged. Y grows downward in both spaces because the window blits a top-down DIB.
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

    /// <summary>
    /// Black strips around <paramref name="picture"/> inside the client.
    /// Writes up to four rectangles, in order: top, bottom, left, right.
    /// A strip with no area is omitted. The picture rectangle is never written,
    /// so a blit can fill these and then stretch the frame without wiping it.
    /// When the picture already fills the client the count is zero.
    /// Integer centering can make the right or bottom strip one pixel larger
    /// than the left or top. Left and right sit beside the picture only, so
    /// they do not overlap the top and bottom strips.
    /// A non-positive client returns zero. A picture that covers no client
    /// pixel yields one strip, the whole client. <paramref name="bars"/> must
    /// have room for four rectangles.
    /// </summary>
    public static int LetterboxBars(
        int clientWidth,
        int clientHeight,
        FrameDestination picture,
        Span<FrameDestination> bars)
    {
        if (bars.Length < 4)
        {
            throw new ArgumentException("Letterbox bars need room for four rectangles.", nameof(bars));
        }

        if (clientWidth <= 0 || clientHeight <= 0)
        {
            return 0;
        }

        if (!TryClipPicture(clientWidth, clientHeight, picture, out var left, out var top, out var right, out var bottom))
        {
            bars[0] = new FrameDestination(0, 0, clientWidth, clientHeight);
            return 1;
        }

        var count = 0;
        if (top > 0)
        {
            bars[count++] = new FrameDestination(0, 0, clientWidth, top);
        }

        if (bottom < clientHeight)
        {
            bars[count++] = new FrameDestination(0, bottom, clientWidth, clientHeight - bottom);
        }

        var beside = bottom - top;
        if (left > 0)
        {
            bars[count++] = new FrameDestination(0, top, left, beside);
        }

        if (right < clientWidth)
        {
            bars[count++] = new FrameDestination(right, top, clientWidth - right, beside);
        }

        return count;
    }

    private static bool TryClipPicture(
        int clientWidth,
        int clientHeight,
        FrameDestination picture,
        out int left,
        out int top,
        out int right,
        out int bottom)
    {
        left = 0;
        top = 0;
        right = 0;
        bottom = 0;
        if (picture.Width <= 0 || picture.Height <= 0)
        {
            return false;
        }

        var startX = picture.X;
        var startY = picture.Y;
        var endX = (long)picture.X + picture.Width;
        var endY = (long)picture.Y + picture.Height;
        if (startX < 0)
        {
            startX = 0;
        }

        if (startY < 0)
        {
            startY = 0;
        }

        if (endX > clientWidth)
        {
            endX = clientWidth;
        }

        if (endY > clientHeight)
        {
            endY = clientHeight;
        }

        if (startX >= clientWidth || startY >= clientHeight || endX <= startX || endY <= startY)
        {
            return false;
        }

        left = startX;
        top = startY;
        right = (int)endX;
        bottom = (int)endY;
        return true;
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
    /// Maps one decoded-frame pixel into TapClock absolute screen space.
    /// <c>screen = image * screenSize / imageSize</c> (integer division), the same
    /// formula as TapClock <c>ScreenMap.FromImage</c>. Each axis is independent.
    /// When <paramref name="screenWidth"/> or <paramref name="screenHeight"/> is
    /// not positive, TapClock has no screen configured and the image size is the
    /// screen, so the point is returned unchanged. The result stays in
    /// <c>[0, screenSize)</c>.
    /// </summary>
    public static bool TryMapFrameToScreen(
        int frameX,
        int frameY,
        int frameWidth,
        int frameHeight,
        int screenWidth,
        int screenHeight,
        out int screenX,
        out int screenY)
    {
        screenX = 0;
        screenY = 0;
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return false;
        }

        if ((uint)frameX >= (uint)frameWidth || (uint)frameY >= (uint)frameHeight)
        {
            return false;
        }

        if (screenWidth <= 0 || screenHeight <= 0)
        {
            screenX = frameX;
            screenY = frameY;
            return true;
        }

        screenX = (int)((long)frameX * screenWidth / frameWidth);
        screenY = (int)((long)frameY * screenHeight / frameHeight);
        if ((uint)screenX >= (uint)screenWidth)
        {
            screenX = screenWidth - 1;
        }

        if ((uint)screenY >= (uint)screenHeight)
        {
            screenY = screenHeight - 1;
        }

        return true;
    }

    /// <summary>
    /// Client point to TapClock screen. Letterbox bars and other points outside
    /// the painted picture return false. The screen step is
    /// <see cref="TryMapFrameToScreen"/>.
    /// </summary>
    public static bool TryMapClientToScreen(
        int clientX,
        int clientY,
        FrameDestination destination,
        int frameWidth,
        int frameHeight,
        int screenWidth,
        int screenHeight,
        out int screenX,
        out int screenY)
    {
        if (!TryMapClientToFrame(clientX, clientY, destination, frameWidth, frameHeight, out var frameX, out var frameY))
        {
            screenX = 0;
            screenY = 0;
            return false;
        }

        return TryMapFrameToScreen(frameX, frameY, frameWidth, frameHeight, screenWidth, screenHeight, out screenX, out screenY);
    }

    /// <summary>
    /// Hover chip text. <paramref name="tapClockScreen"/> selects TapClock absolute
    /// screen points (<c>TapClock x, y</c>). Otherwise the numbers are decoded-frame
    /// pixels (<c>像素 x, y</c>), used when <c>--model</c> is unset.
    /// </summary>
    public static string FormatHoverReadout(int x, int y, bool tapClockScreen)
    {
        var prefix = tapClockScreen ? "TapClock " : "像素 ";
        return prefix
            + x.ToString(CultureInfo.InvariantCulture)
            + ", "
            + y.ToString(CultureInfo.InvariantCulture);
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
