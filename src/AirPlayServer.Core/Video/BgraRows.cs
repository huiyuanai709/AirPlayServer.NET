using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace AirPlayServer.Core.Video;

/// <summary>
/// Tight BGRA copies. Row <see cref="Span{T}.CopyTo(Span{T})"/> and 32-bit
/// stores replace per-byte loops on the decode and publish path.
/// </summary>
internal static class BgraRows
{
    internal static bool TryPack(ReadOnlySpan<byte> source, int sourceStride, Span<byte> destination, int width, int height)
    {
        var tight = (long)width * 4;
        if (width <= 0 || height <= 0 || tight > int.MaxValue || sourceStride < tight)
        {
            return false;
        }

        var bytes = tight * height;
        var sourceBytes = (long)sourceStride * height;
        if (bytes <= 0 || bytes > destination.Length || sourceBytes > source.Length || sourceBytes <= 0)
        {
            return false;
        }

        if (sourceStride == tight)
        {
            source.Slice(0, (int)bytes).CopyTo(destination.Slice(0, (int)bytes));
            return true;
        }

        var row = (int)tight;
        for (var y = 0; y < height; y++)
        {
            source.Slice(y * sourceStride, row).CopyTo(destination.Slice(y * row, row));
        }

        return true;
    }

    internal static bool TryCopyRgb32(
        ReadOnlySpan<byte> source,
        int codedWidth,
        int codedHeight,
        int stride,
        bool bottomUp,
        int width,
        int height,
        Span<byte> destination)
    {
        var tight = (long)codedWidth * 4;
        if (width <= 0 || height <= 0 || codedWidth < width || codedHeight < height
            || tight > int.MaxValue || stride < tight)
        {
            return false;
        }

        var needed = (long)stride * codedHeight;
        var output = (long)width * height * 4;
        if (needed > source.Length || output > destination.Length || needed <= 0 || output <= 0)
        {
            return false;
        }

        var rowBytes = width * 4;
        for (var y = 0; y < height; y++)
        {
            var sourceRow = bottomUp ? codedHeight - 1 - y : y;
            CopyOpaque(source.Slice(sourceRow * stride, rowBytes), destination.Slice(y * rowBytes, rowBytes), width);
        }

        return true;
    }

    internal static unsafe void CopyOpaque(ReadOnlySpan<byte> source, Span<byte> destination, int pixels)
    {
        if (pixels <= 0)
        {
            return;
        }

        fixed (byte* sourceBase = source)
        fixed (byte* destinationBase = destination)
        {
            CopyOpaque(sourceBase, destinationBase, pixels);
        }
    }

    private static unsafe void CopyOpaque(byte* source, byte* destination, int pixels)
    {
        var index = 0;
        if (BitConverter.IsLittleEndian && Vector256.IsHardwareAccelerated)
        {
            var mask = Vector256.Create(0xFF000000u);
            ref var sourcePixels = ref Unsafe.AsRef<uint>(source);
            ref var destinationPixels = ref Unsafe.AsRef<uint>(destination);
            for (; index + 8 <= pixels; index += 8)
            {
                var block = Vector256.LoadUnsafe(ref sourcePixels, (nuint)index);
                Vector256.StoreUnsafe(block | mask, ref destinationPixels, (nuint)index);
            }
        }
        else if (BitConverter.IsLittleEndian && Vector128.IsHardwareAccelerated)
        {
            var mask = Vector128.Create(0xFF000000u);
            ref var sourcePixels = ref Unsafe.AsRef<uint>(source);
            ref var destinationPixels = ref Unsafe.AsRef<uint>(destination);
            for (; index + 4 <= pixels; index += 4)
            {
                var block = Vector128.LoadUnsafe(ref sourcePixels, (nuint)index);
                Vector128.StoreUnsafe(block | mask, ref destinationPixels, (nuint)index);
            }
        }

        var alpha = BitConverter.IsLittleEndian ? 0xFF000000u : 0x000000FFu;
        for (; index < pixels; index++)
        {
            var offset = index * 4;
            Unsafe.WriteUnaligned(destination + offset, Unsafe.ReadUnaligned<uint>(source + offset) | alpha);
        }
    }
}
