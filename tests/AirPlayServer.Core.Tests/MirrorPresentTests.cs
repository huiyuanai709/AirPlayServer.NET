using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class MirrorPresentTests
{
    [Fact]
    public void Hardware_stall_keeps_one_silent_startup_unit_then_releases()
    {
        Assert.False(HardwareDecodeStall.ShouldAbandon(picturesProduced: 0, consecutiveMisses: 0));
        Assert.False(HardwareDecodeStall.ShouldAbandon(picturesProduced: 0, consecutiveMisses: 1));
        Assert.True(HardwareDecodeStall.ShouldAbandon(picturesProduced: 0, consecutiveMisses: 2));
        Assert.False(HardwareDecodeStall.ShouldAbandon(picturesProduced: 1, consecutiveMisses: 0));
        Assert.True(HardwareDecodeStall.ShouldAbandon(picturesProduced: 1, consecutiveMisses: 1));
        Assert.True(HardwareDecodeStall.ShouldAbandon(picturesProduced: 8, consecutiveMisses: 3));
        Assert.False(HardwareDecodeStall.ShouldAbandon(picturesProduced: -1, consecutiveMisses: 4));
    }

    [Fact]
    public void Letterboxed_bgra_lands_upright_and_leaves_the_bars_black()
    {
        var source = new byte[4 * 2 * 4];
        Paint(source, 0, 0, 4, 10, 20, 30);
        Paint(source, 3, 0, 4, 40, 50, 60);
        Paint(source, 0, 1, 4, 70, 80, 90);
        Paint(source, 3, 1, 4, 100, 110, 120);
        var destination = new byte[6 * 4 * 4];
        destination.AsSpan().Fill(7);

        Assert.True(BgraScale.PaintLetterbox(
            source,
            sourceWidth: 4,
            sourceHeight: 2,
            sourceStride: 16,
            destination,
            destinationWidth: 6,
            destinationHeight: 4,
            destinationStride: 24,
            originX: 1,
            originY: 1,
            viewWidth: 4,
            viewHeight: 2,
            bottomUp: false));

        AssertUnpainted(destination, 0, 0, 6);
        AssertPixel(destination, 1, 1, 6, 10, 20, 30);
        AssertPixel(destination, 4, 1, 6, 40, 50, 60);
        AssertPixel(destination, 1, 2, 6, 70, 80, 90);
        AssertPixel(destination, 4, 2, 6, 100, 110, 120);
        AssertUnpainted(destination, 5, 3, 6);
        Assert.Equal(255, destination[(1 * 6 + 1) * 4 + 3]);
    }

    [Fact]
    public void Bottom_up_window_buffer_puts_the_top_row_at_the_end()
    {
        var source = new byte[2 * 2 * 4];
        Paint(source, 0, 0, 2, 1, 2, 3);
        Paint(source, 1, 1, 2, 4, 5, 6);
        var destination = new byte[2 * 2 * 4];

        Assert.True(BgraScale.PaintLetterbox(
            source,
            2,
            2,
            8,
            destination,
            2,
            2,
            8,
            0,
            0,
            2,
            2,
            bottomUp: true));

        AssertPixel(destination, 0, 1, 2, 1, 2, 3);
        AssertPixel(destination, 1, 0, 2, 4, 5, 6);
    }

    [Fact]
    public void Tall_phone_frame_samples_the_top_and_bottom()
    {
        var source = new byte[2 * 8 * 4];
        Paint(source, 0, 0, 2, 11, 0, 0);
        Paint(source, 0, 7, 2, 0, 22, 0);
        var destination = new byte[2 * 4 * 4];

        Assert.True(BgraScale.PaintLetterbox(source, 2, 8, 8, destination, 2, 4, 8, 0, 0, 2, 4, bottomUp: false));
        AssertPixel(destination, 0, 0, 2, 11, 0, 0);
        AssertPixel(destination, 0, 3, 2, 0, 22, 0);
        Assert.False(BgraScale.PaintLetterbox(source, 2, 8, 8, destination, 2, 4, 8, 0, 0, 0, 4, bottomUp: false));
    }

    private static void Paint(byte[] pixels, int x, int y, int width, byte b, byte g, byte r)
    {
        var index = ((y * width) + x) * 4;
        pixels[index] = b;
        pixels[index + 1] = g;
        pixels[index + 2] = r;
        pixels[index + 3] = 1;
    }

    private static void AssertUnpainted(byte[] pixels, int x, int y, int width)
    {
        var index = ((y * width) + x) * 4;
        Assert.Equal(7, pixels[index]);
        Assert.Equal(7, pixels[index + 1]);
        Assert.Equal(7, pixels[index + 2]);
        Assert.Equal(7, pixels[index + 3]);
    }

    private static void AssertPixel(byte[] pixels, int x, int y, int width, byte b, byte g, byte r)
    {
        var index = ((y * width) + x) * 4;
        Assert.Equal(b, pixels[index]);
        Assert.Equal(g, pixels[index + 1]);
        Assert.Equal(r, pixels[index + 2]);
        Assert.Equal(255, pixels[index + 3]);
    }
}
