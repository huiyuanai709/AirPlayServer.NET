namespace AirPlayServer.Core.Video;

/// <summary>
/// YUV 4:2:0 planes to tightly packed BGRA. Chroma is left-sited, matching
/// <see cref="Nv12Color"/>. FFmpeg software decode emits separate Y, U, and V
/// planes (<c>yuv420p</c>) or interleaved UV (<c>nv12</c>). 10-bit little-endian
/// samples are shifted down to 8-bit before the same matrix.
/// </summary>
internal static class YuvPlanarColor
{
    public static bool TryCopyYuv420(
        ReadOnlySpan<byte> yPlane,
        int yStride,
        ReadOnlySpan<byte> uPlane,
        int uStride,
        ReadOnlySpan<byte> vPlane,
        int vStride,
        int width,
        int height,
        YuvColorSpace color,
        Span<byte> destination)
    {
        if (!Covers(yPlane, yStride, width, height)
            || !Covers(uPlane, uStride, width / 2, height / 2)
            || !Covers(vPlane, vStride, width / 2, height / 2))
        {
            return false;
        }

        unsafe
        {
            fixed (byte* y = yPlane)
            fixed (byte* u = uPlane)
            fixed (byte* v = vPlane)
            {
                return TryCopyPlanes(
                    (nint)y,
                    yStride,
                    (nint)u,
                    uStride,
                    (nint)v,
                    vStride,
                    width,
                    height,
                    color,
                    sampleBytes: 1,
                    sampleShift: 0,
                    nv12: false,
                    swapUv: false,
                    destination);
            }
        }
    }

    public static bool TryCopyNv12(
        ReadOnlySpan<byte> yPlane,
        int yStride,
        ReadOnlySpan<byte> uvPlane,
        int uvStride,
        int width,
        int height,
        YuvColorSpace color,
        Span<byte> destination,
        bool swapUv = false)
    {
        if (!Covers(yPlane, yStride, width, height) || !Covers(uvPlane, uvStride, width, height / 2))
        {
            return false;
        }

        unsafe
        {
            fixed (byte* y = yPlane)
            fixed (byte* uv = uvPlane)
            {
                return TryCopyPlanes(
                    (nint)y,
                    yStride,
                    (nint)uv,
                    uvStride,
                    0,
                    0,
                    width,
                    height,
                    color,
                    sampleBytes: 1,
                    sampleShift: 0,
                    nv12: true,
                    swapUv,
                    destination);
            }
        }
    }

    internal static unsafe bool TryCopyPlanes(
        nint yPlane,
        int yStride,
        nint uPlane,
        int uStride,
        nint vPlane,
        int vStride,
        int width,
        int height,
        YuvColorSpace color,
        int sampleBytes,
        int sampleShift,
        bool nv12,
        bool swapUv,
        Span<byte> destination)
    {
        if (yPlane == 0 || uPlane == 0 || width < 2 || height < 2
            || (width & 1) != 0 || (height & 1) != 0
            || width > 8192 || height > 8192
            || sampleBytes is not (1 or 2) || sampleShift is < 0 or > 8
            || destination.Length < (long)width * height * 4)
        {
            return false;
        }

        if (!nv12 && vPlane == 0)
        {
            return false;
        }

        var lumaRowBytes = width * sampleBytes;
        var chromaRowBytes = nv12 ? width * sampleBytes : (width / 2) * sampleBytes;
        if (!RowFits(yStride, lumaRowBytes, height)
            || !RowFits(uStride, chromaRowBytes, height / 2)
            || (!nv12 && !RowFits(vStride, chromaRowBytes, height / 2)))
        {
            return false;
        }

        var to = 0;
        for (var y = 0; y < height; y++)
        {
            var luma = Row(yPlane, yStride, y, height);
            var chromaY = y / 2;
            var u = Row(uPlane, uStride, chromaY, height / 2);
            var v = nv12 ? u : Row(vPlane, vStride, chromaY, height / 2);
            for (var x = 0; x < width; x++)
            {
                byte cb;
                byte cr;
                if (nv12)
                {
                    var pair = (x & ~1) * sampleBytes;
                    cb = ReadSample(u, pair, sampleBytes, sampleShift);
                    cr = ReadSample(u, pair + sampleBytes, sampleBytes, sampleShift);
                }
                else
                {
                    var chromaX = (x / 2) * sampleBytes;
                    cb = ReadSample(u, chromaX, sampleBytes, sampleShift);
                    cr = ReadSample(v, chromaX, sampleBytes, sampleShift);
                }

                if (swapUv)
                {
                    (cb, cr) = (cr, cb);
                }

                Nv12Color.Convert(ReadSample(luma, x * sampleBytes, sampleBytes, sampleShift), cb, cr, color, out var b, out var g, out var r);
                destination[to++] = b;
                destination[to++] = g;
                destination[to++] = r;
                destination[to++] = 255;
            }
        }

        return true;
    }

    private static bool Covers(ReadOnlySpan<byte> plane, int stride, int width, int height)
    {
        if (stride < width || width <= 0 || height <= 0)
        {
            return false;
        }

        var need = (long)stride * (height - 1) + width;
        return need > 0 && plane.Length >= need;
    }

    private static bool RowFits(int stride, int rowBytes, int height)
    {
        if (rowBytes <= 0 || height <= 0 || stride == 0)
        {
            return false;
        }

        var step = stride < 0 ? -stride : stride;
        if (step < rowBytes)
        {
            return false;
        }

        var bytes = (long)step * height;
        return bytes > 0 && bytes <= int.MaxValue;
    }

    private static unsafe byte* Row(nint data, int stride, int y, int height)
    {
        if (stride >= 0)
        {
            return (byte*)data + (long)y * stride;
        }

        var row0 = (byte*)data + (long)(height - 1) * stride;
        return row0 + (long)y * -stride;
    }

    private static unsafe byte ReadSample(byte* row, int byteOffset, int sampleBytes, int sampleShift)
    {
        var sample = row + byteOffset;
        var value = sampleBytes == 1 ? sample[0] : sample[0] | (sample[1] << 8);
        return (byte)(value >> sampleShift);
    }
}
