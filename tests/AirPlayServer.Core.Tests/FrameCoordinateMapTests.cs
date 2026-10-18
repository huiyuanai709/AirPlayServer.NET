using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class FrameCoordinateMapTests
{
    [Fact]
    public void Stretch_fills_the_client_and_scales_into_frame_pixels()
    {
        var destination = FrameCoordinateMap.Stretch(960, 2080);
        Assert.Equal(new FrameCoordinateMap.FrameDestination(0, 0, 960, 2080), destination);

        Assert.True(FrameCoordinateMap.TryMapClientToFrame(0, 0, destination, 480, 1040, out var x, out var y));
        Assert.Equal((0, 0), (x, y));

        Assert.True(FrameCoordinateMap.TryMapClientToFrame(2, 2, destination, 480, 1040, out x, out y));
        Assert.Equal((1, 1), (x, y));

        Assert.True(FrameCoordinateMap.TryMapClientToFrame(959, 2079, destination, 480, 1040, out x, out y));
        Assert.Equal((479, 1039), (x, y));

        Assert.False(FrameCoordinateMap.TryMapClientToFrame(960, 0, destination, 480, 1040, out _, out _));
        Assert.False(FrameCoordinateMap.TryMapClientToFrame(-1, 10, destination, 480, 1040, out _, out _));
    }

    [Fact]
    public void Non_integer_stretch_stays_inside_the_frame()
    {
        var destination = FrameCoordinateMap.Stretch(100, 100);
        Assert.True(FrameCoordinateMap.TryMapClientToFrame(99, 99, destination, 480, 1040, out var x, out var y));
        Assert.Equal(99 * 480 / 100, x);
        Assert.Equal(99 * 1040 / 100, y);
        Assert.InRange(x, 0, 479);
        Assert.InRange(y, 0, 1039);

        Assert.True(FrameCoordinateMap.TryMapClientToFrame(49, 49, destination, 1, 1, out x, out y));
        Assert.Equal((0, 0), (x, y));
    }

    [Fact]
    public void Large_stretch_uses_the_full_frame_extent()
    {
        var destination = FrameCoordinateMap.Stretch(8000, 5000);
        Assert.True(FrameCoordinateMap.TryMapClientToFrame(7999, 4999, destination, 7680, 4320, out var x, out var y));
        Assert.Equal(7679, x);
        Assert.Equal(4319, y);
    }

    [Fact]
    public void Letterbox_ignores_bars_and_maps_the_fitted_picture()
    {
        var wide = FrameCoordinateMap.Letterbox(960, 1040, 480, 1040);
        Assert.Equal(new FrameCoordinateMap.FrameDestination(240, 0, 480, 1040), wide);
        Assert.False(FrameCoordinateMap.TryMapClientToFrame(239, 0, wide, 480, 1040, out _, out _));
        Assert.True(FrameCoordinateMap.TryMapClientToFrame(240, 0, wide, 480, 1040, out var x, out var y));
        Assert.Equal((0, 0), (x, y));
        Assert.True(FrameCoordinateMap.TryMapClientToFrame(719, 1039, wide, 480, 1040, out x, out y));
        Assert.Equal((479, 1039), (x, y));
        Assert.False(FrameCoordinateMap.TryMapClientToFrame(720, 1039, wide, 480, 1040, out _, out _));

        var tall = FrameCoordinateMap.Letterbox(200, 200, 480, 1040);
        Assert.Equal(new FrameCoordinateMap.FrameDestination(54, 0, 92, 200), tall);
        Assert.False(FrameCoordinateMap.TryMapClientToFrame(10, 100, tall, 480, 1040, out _, out _));
        Assert.True(FrameCoordinateMap.TryMapClientToFrame(54, 0, tall, 480, 1040, out x, out y));
        Assert.Equal((0, 0), (x, y));
        Assert.True(FrameCoordinateMap.TryMapClientToFrame(145, 199, tall, 480, 1040, out x, out y));
        Assert.Equal(91 * 480 / 92, x);
        Assert.Equal(199 * 1040 / 200, y);

        var shortFrame = FrameCoordinateMap.Letterbox(30, 40, 100, 50);
        Assert.Equal(new FrameCoordinateMap.FrameDestination(0, 12, 30, 15), shortFrame);
        Assert.False(FrameCoordinateMap.TryMapClientToFrame(0, 11, shortFrame, 100, 50, out _, out _));
        Assert.True(FrameCoordinateMap.TryMapClientToFrame(0, 12, shortFrame, 100, 50, out x, out y));
        Assert.Equal((0, 0), (x, y));
        Assert.True(FrameCoordinateMap.TryMapClientToFrame(29, 26, shortFrame, 100, 50, out x, out y));
        Assert.Equal((96, 46), (x, y));
        Assert.False(FrameCoordinateMap.TryMapClientToFrame(0, 27, shortFrame, 100, 50, out _, out _));

        Assert.Equal(new FrameCoordinateMap.FrameDestination(0, 49, 1, 1), FrameCoordinateMap.Letterbox(1, 100, 10000, 1));
        Assert.Equal(new FrameCoordinateMap.FrameDestination(49, 0, 1, 1), FrameCoordinateMap.Letterbox(100, 1, 1, 10000));
    }

    [Fact]
    public void Mouse_lparam_is_a_pair_of_signed_client_coordinates()
    {
        FrameCoordinateMap.ClientPoint(10 | (20 << 16), out var x, out var y);
        Assert.Equal((10, 20), (x, y));

        uint packed = unchecked((ushort)(short)-3);
        packed |= (uint)unchecked((ushort)(short)-8) << 16;
        FrameCoordinateMap.ClientPoint(unchecked((nint)packed), out x, out y);
        Assert.Equal((-3, -8), (x, y));
    }

    [Fact]
    public void Empty_sizes_do_not_map()
    {
        Assert.Equal(new FrameCoordinateMap.FrameDestination(0, 0, 0, 0), FrameCoordinateMap.Stretch(0, 10));
        Assert.Equal(new FrameCoordinateMap.FrameDestination(0, 0, 0, 0), FrameCoordinateMap.Letterbox(10, 10, 0, 10));
        var destination = FrameCoordinateMap.Stretch(10, 10);
        Assert.False(FrameCoordinateMap.TryMapClientToFrame(0, 0, destination, 0, 10, out _, out _));
        Assert.False(FrameCoordinateMap.TryMapClientToFrame(0, 0, new FrameCoordinateMap.FrameDestination(0, 0, 0, 10), 10, 10, out _, out _));
    }
}
