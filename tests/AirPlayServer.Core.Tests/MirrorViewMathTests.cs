using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class MirrorViewMathTests
{
    [Fact]
    public void Letterbox_matches_the_fitted_picture()
    {
        Assert.Equal(new MirrorViewMath.Placement(240, 0, 480, 1040), MirrorViewMath.Letterbox(960, 1040, 480, 1040));
        Assert.Equal(new MirrorViewMath.Placement(54, 0, 92, 200), MirrorViewMath.Letterbox(200, 200, 480, 1040));
        Assert.Equal(new MirrorViewMath.Placement(0, 0, 0, 0), MirrorViewMath.Letterbox(0, 10, 480, 1040));
    }

    [Fact]
    public void Hover_on_the_picture_reports_tapclock_points_and_bars_do_not()
    {
        Assert.False(MirrorViewMath.TryHoverReadout(10, 100, 200, 200, 480, 1040, 430, 932, out var ignored));
        Assert.Equal("", ignored);
        Assert.True(MirrorViewMath.TryHoverReadout(54, 0, 200, 200, 480, 1040, 430, 932, out var origin));
        Assert.Equal("TapClock 0, 0", origin);
        Assert.True(MirrorViewMath.TryHoverReadout(323, 850, 480, 1040, 480, 1040, 430, 932, out var sample));
        Assert.Equal("TapClock 289, 761", sample);
    }

    [Fact]
    public void Hover_without_a_model_reports_frame_pixels()
    {
        Assert.True(MirrorViewMath.TryHoverReadout(323, 850, 480, 1040, 480, 1040, 0, 0, out var pixels));
        Assert.Equal("像素 323, 850", pixels);
        Assert.True(MirrorViewMath.TryHoverReadout(323, 850, 480, 1040, 480, 1040, 430, 0, out var partial));
        Assert.Equal("像素 323, 850", partial);
    }

    [Fact]
    public void Titles_keep_the_device_name_and_lead_with_the_readout()
    {
        Assert.Equal("AirPlayServer.NET — mirror", MirrorViewMath.DeviceTitle(null));
        Assert.Equal("AirPlayServer.NET — mirror", MirrorViewMath.DeviceTitle("  "));
        Assert.Equal("AirPlayServer.NET — Phone", MirrorViewMath.DeviceTitle("  Phone  "));
        Assert.Equal(
            "TapClock 289, 761 — AirPlayServer.NET — Phone",
            MirrorViewMath.HoverTitle(MirrorViewMath.DeviceTitle("Phone"), "TapClock 289, 761"));
    }

    [Fact]
    public void Device_pixels_truncate_toward_zero_in_the_same_space_as_the_pointer()
    {
        Assert.Equal(201, MirrorViewMath.DevicePixels(100.9, 2));
        Assert.Equal(10, MirrorViewMath.DevicePixels(10.9, 1));
        Assert.Equal(-1, MirrorViewMath.DevicePixels(-1.2, 1));
        Assert.Equal(0, MirrorViewMath.DevicePixels(double.NaN, 1));
        Assert.Equal(0, MirrorViewMath.DevicePixels(10, 0));
        Assert.Equal(0, MirrorViewMath.DevicePixels(10, double.PositiveInfinity));
    }

    [Fact]
    public void Compose_is_top_down_letterboxed_and_forces_opaque_alpha()
    {
        var source = new byte[2 * 2 * 4];
        Paint(source, 0, 0, 2, 1, 2, 3, 0);
        Paint(source, 1, 0, 2, 4, 5, 6, 0);
        Paint(source, 0, 1, 2, 7, 8, 9, 0);
        Paint(source, 1, 1, 2, 10, 11, 12, 0);
        var destination = new byte[6 * 2 * 4];
        destination.AsSpan().Fill(7);

        Assert.True(MirrorViewMath.TryComposeLetterbox(source, 2, 2, 8, destination, 6, 2, 24));

        AssertPixel(destination, 2, 0, 6, 1, 2, 3, 255);
        AssertPixel(destination, 3, 0, 6, 4, 5, 6, 255);
        AssertPixel(destination, 2, 1, 6, 7, 8, 9, 255);
        AssertPixel(destination, 3, 1, 6, 10, 11, 12, 255);
        AssertPixel(destination, 0, 0, 6, 0, 0, 0, 255);
        AssertPixel(destination, 1, 1, 6, 0, 0, 0, 255);
        AssertPixel(destination, 5, 0, 6, 0, 0, 0, 255);
        Assert.False(MirrorViewMath.TryComposeLetterbox(source, 2, 2, 8, destination, 0, 2, 24));
    }

    [Fact]
    public void Compose_keeps_the_top_source_row_at_the_top_of_the_client()
    {
        var source = new byte[1 * 2 * 4];
        Paint(source, 0, 0, 1, 9, 8, 7, 1);
        Paint(source, 0, 1, 1, 1, 2, 3, 4);
        var destination = new byte[1 * 2 * 4];

        Assert.True(MirrorViewMath.TryComposeLetterbox(source, 1, 2, 4, destination, 1, 2, 4));
        AssertPixel(destination, 0, 0, 1, 9, 8, 7, 255);
        AssertPixel(destination, 0, 1, 1, 1, 2, 3, 255);
    }

    private static void Paint(byte[] pixels, int x, int y, int width, byte b, byte g, byte r, byte a)
    {
        var index = ((y * width) + x) * 4;
        pixels[index] = b;
        pixels[index + 1] = g;
        pixels[index + 2] = r;
        pixels[index + 3] = a;
    }

    private static void AssertPixel(byte[] pixels, int x, int y, int width, byte b, byte g, byte r, byte a)
    {
        var index = ((y * width) + x) * 4;
        Assert.Equal(b, pixels[index]);
        Assert.Equal(g, pixels[index + 1]);
        Assert.Equal(r, pixels[index + 2]);
        Assert.Equal(a, pixels[index + 3]);
    }
}
