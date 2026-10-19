namespace AirPlayServer.Core.Video;

/// <summary>
/// Nearest-neighbor copy of tightly packed BGRA into a window-sized buffer.
/// The mirror window uses this instead of <c>StretchDIBits</c> in HALFTONE:
/// that mode paints 32-bit BGRA white or not at all, which is a blank phone
/// screen even when the decoder published a frame. Alpha is forced to opaque
/// so a 32-bit DIB cannot treat the picture as transparent.
/// </summary>
internal static class BgraScale
{
    public static bool PaintLetterbox(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        Span<byte> destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride,
        int originX,
        int originY,
        int viewWidth,
        int viewHeight,
        bool bottomUp)
    {
        if (sourceWidth < 1 || sourceHeight < 1 || viewWidth < 1 || viewHeight < 1
            || destinationWidth < 1 || destinationHeight < 1
            || originX < 0 || originY < 0
            || sourceStride < sourceWidth * 4
            || destinationStride < destinationWidth * 4)
        {
            return false;
        }

        if (originX >= destinationWidth || originY >= destinationHeight)
        {
            return false;
        }

        if (originX > destinationWidth - viewWidth)
        {
            viewWidth = destinationWidth - originX;
        }

        if (originY > destinationHeight - viewHeight)
        {
            viewHeight = destinationHeight - originY;
        }

        var sourceBytes = (long)sourceStride * sourceHeight;
        var destinationBytes = (long)destinationStride * destinationHeight;
        if (sourceBytes > source.Length || destinationBytes > destination.Length || viewWidth < 1 || viewHeight < 1)
        {
            return false;
        }

        for (var y = 0; y < viewHeight; y++)
        {
            var sourceY = Sample(y, viewHeight, sourceHeight);
            var destY = originY + y;
            if (bottomUp)
            {
                destY = destinationHeight - 1 - destY;
            }

            var fromRow = sourceY * sourceStride;
            var toRow = destY * destinationStride + (originX * 4);
            for (var x = 0; x < viewWidth; x++)
            {
                var sourceX = Sample(x, viewWidth, sourceWidth);

                var from = fromRow + (sourceX * 4);
                var to = toRow + (x * 4);
                destination[to] = source[from];
                destination[to + 1] = source[from + 1];
                destination[to + 2] = source[from + 2];
                destination[to + 3] = 255;
            }
        }

        return true;
    }

    /// <summary>
    /// Maps a destination pixel onto the source so both edges are visible.
    /// Truncating <c>y * source / view</c> never samples the last source row.
    /// </summary>
    private static int Sample(int position, int view, int source)
    {
        if (view <= 1)
        {
            return source / 2;
        }

        var index = (int)((long)position * (source - 1) / (view - 1));
        return index >= source ? source - 1 : index;
    }
}
