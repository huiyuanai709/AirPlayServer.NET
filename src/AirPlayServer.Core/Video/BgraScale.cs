using System.Buffers;
using System.Runtime.CompilerServices;

namespace AirPlayServer.Core.Video;

/// <summary>
/// Nearest-neighbor copy of BGRA into a window-sized buffer.
/// The mirror window uses this instead of <c>StretchDIBits</c> in HALFTONE:
/// that mode paints 32-bit BGRA white or not at all, which is a blank phone
/// screen even when the decoder published a frame. Alpha is forced to opaque
/// so a 32-bit DIB cannot treat the picture as transparent.
/// Equal sizes copy whole rows. A scaled frame gathers through a source-x
/// map built once per paint, and repeated source rows are copied with
/// <see cref="Span{T}.CopyTo(Span{T})"/> instead of sampling again.
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
        if (!TryNormalize(
            source,
            sourceWidth,
            sourceHeight,
            sourceStride,
            destination,
            destinationWidth,
            destinationHeight,
            destinationStride,
            originX,
            originY,
            ref viewWidth,
            ref viewHeight))
        {
            return false;
        }

        if (viewWidth == sourceWidth)
        {
            PaintRows(source, sourceHeight, sourceStride, destination, destinationHeight, destinationStride, originX, originY, viewWidth, viewHeight, bottomUp, sampleVertical: viewHeight != sourceHeight);
            return true;
        }

        if (viewWidth <= 4096)
        {
            Span<int> map = stackalloc int[viewWidth];
            FillMap(map, viewWidth, sourceWidth);
            PaintMapped(source, sourceHeight, sourceStride, destination, destinationHeight, destinationStride, originX, originY, viewHeight, bottomUp, map);
            return true;
        }

        var rented = ArrayPool<int>.Shared.Rent(viewWidth);
        try
        {
            var map = rented.AsSpan(0, viewWidth);
            FillMap(map, viewWidth, sourceWidth);
            PaintMapped(source, sourceHeight, sourceStride, destination, destinationHeight, destinationStride, originX, originY, viewHeight, bottomUp, map);
            return true;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    private static bool TryNormalize(
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
        ref int viewWidth,
        ref int viewHeight)
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
        return viewWidth >= 1 && viewHeight >= 1
            && sourceBytes <= source.Length
            && destinationBytes <= destination.Length;
    }

    private static void FillMap(Span<int> map, int viewWidth, int sourceWidth)
    {
        for (var x = 0; x < viewWidth; x++)
        {
            map[x] = Sample(x, viewWidth, sourceWidth) * 4;
        }
    }

    private static void PaintRows(
        ReadOnlySpan<byte> source,
        int sourceHeight,
        int sourceStride,
        Span<byte> destination,
        int destinationHeight,
        int destinationStride,
        int originX,
        int originY,
        int viewWidth,
        int viewHeight,
        bool bottomUp,
        bool sampleVertical)
    {
        var previousSourceY = -1;
        var previousRow = -1;
        var rowBytes = viewWidth * 4;
        for (var y = 0; y < viewHeight; y++)
        {
            var sourceY = sampleVertical ? Sample(y, viewHeight, sourceHeight) : y;
            var row = DestinationRow(destinationHeight, destinationStride, originX, originY, y, bottomUp);
            if (sourceY == previousSourceY)
            {
                destination.Slice(previousRow, rowBytes).CopyTo(destination.Slice(row, rowBytes));
            }
            else
            {
                BgraRows.CopyOpaque(source.Slice(sourceY * sourceStride, rowBytes), destination.Slice(row, rowBytes), viewWidth);
            }

            previousSourceY = sourceY;
            previousRow = row;
        }
    }

    private static unsafe void PaintMapped(
        ReadOnlySpan<byte> source,
        int sourceHeight,
        int sourceStride,
        Span<byte> destination,
        int destinationHeight,
        int destinationStride,
        int originX,
        int originY,
        int viewHeight,
        bool bottomUp,
        ReadOnlySpan<int> map)
    {
        var previousSourceY = -1;
        var previousRow = -1;
        var rowBytes = map.Length * 4;
        var alpha = BitConverter.IsLittleEndian ? 0xFF000000u : 0x000000FFu;
        fixed (byte* sourceBase = source)
        fixed (byte* destinationBase = destination)
        fixed (int* mapBase = map)
        {
            for (var y = 0; y < viewHeight; y++)
            {
                var sourceY = Sample(y, viewHeight, sourceHeight);
                var row = DestinationRow(destinationHeight, destinationStride, originX, originY, y, bottomUp);
                if (sourceY == previousSourceY)
                {
                    Buffer.MemoryCopy(destinationBase + previousRow, destinationBase + row, rowBytes, rowBytes);
                }
                else
                {
                    var from = sourceBase + (sourceY * sourceStride);
                    var to = destinationBase + row;
                    var count = map.Length;
                    var x = 0;
                    for (; x + 3 < count; x += 4)
                    {
                        WritePixel(from, to, mapBase[x], x, alpha);
                        WritePixel(from, to, mapBase[x + 1], x + 1, alpha);
                        WritePixel(from, to, mapBase[x + 2], x + 2, alpha);
                        WritePixel(from, to, mapBase[x + 3], x + 3, alpha);
                    }

                    for (; x < count; x++)
                    {
                        WritePixel(from, to, mapBase[x], x, alpha);
                    }
                }

                previousSourceY = sourceY;
                previousRow = row;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WritePixel(byte* from, byte* to, int sourceOffset, int x, uint alpha)
    {
        Unsafe.WriteUnaligned(to + (x * 4), Unsafe.ReadUnaligned<uint>(from + sourceOffset) | alpha);
    }

    private static int DestinationRow(int destinationHeight, int destinationStride, int originX, int originY, int y, bool bottomUp)
    {
        var destY = originY + y;
        if (bottomUp)
        {
            destY = destinationHeight - 1 - destY;
        }

        return (destY * destinationStride) + (originX * 4);
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
