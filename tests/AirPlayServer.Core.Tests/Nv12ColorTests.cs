using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class Nv12ColorTests
{
    [Fact]
    public void Neutral_samples_stay_gray_and_the_two_ranges_disagree_on_studio_black()
    {
        foreach (var matrix in new[] { YuvMatrix.Bt709, YuvMatrix.Bt601 })
        {
            var full = new YuvColorSpace(matrix, YuvRange.Full);
            Expect(0, 128, 128, full, 0, 0, 0);
            Expect(16, 128, 128, full, 16, 16, 16);
            Expect(128, 128, 128, full, 128, 128, 128);
            Expect(235, 128, 128, full, 235, 235, 235);
            Expect(255, 128, 128, full, 255, 255, 255);

            var limited = new YuvColorSpace(matrix, YuvRange.Limited);
            Expect(16, 128, 128, limited, 0, 0, 0);
            Expect(235, 128, 128, limited, 255, 255, 255);
            Nv12Color.Convert(128, 128, 128, limited, out var b, out var g, out var r);
            Assert.Equal(b, g);
            Assert.Equal(g, r);
            Assert.NotEqual(16, b);
        }
    }

    [Fact]
    public void Samples_match_the_itu_formula_within_one_level()
    {
        foreach (var matrix in new[] { YuvMatrix.Bt709, YuvMatrix.Bt601 })
        {
            foreach (var range in new[] { YuvRange.Full, YuvRange.Limited })
            {
                var color = new YuvColorSpace(matrix, range);
                for (var y = 0; y < 256; y += 17)
                {
                    for (var cb = 0; cb < 256; cb += 17)
                    {
                        for (var cr = 0; cr < 256; cr += 17)
                        {
                            Nv12Color.Convert((byte)y, (byte)cb, (byte)cr, color, out var b, out var g, out var r);
                            var expected = Reference(y, cb, cr, color);
                            Assert.InRange(b, expected.B - 1, expected.B + 1);
                            Assert.InRange(g, expected.G - 1, expected.G + 1);
                            Assert.InRange(r, expected.R - 1, expected.R + 1);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void Full_range_bt709_is_not_the_old_limited_bt601_red()
    {
        Nv12Color.Convert(54, 99, 255, YuvColorSpace.AirPlayMirror, out var b709, out var g709, out var r709);
        Nv12Color.Convert(54, 99, 255, new YuvColorSpace(YuvMatrix.Bt601, YuvRange.Limited), out var b601, out var g601, out var r601);
        Assert.NotEqual((b601, g601, r601), (b709, g709, r709));
        Assert.True(r709 > g709);
        Assert.True(r709 > b709);
    }

    [Fact]
    public void TryCopy_uses_stride_crops_the_visible_picture_and_shares_a_chroma_pair()
    {
        var source = new byte[(6 * 2) + 6];
        source[0] = 255;
        source[1] = 0;
        source[2] = 200;
        source[12] = 128;
        source[13] = 128;
        source[14] = 160;
        source[15] = 200;
        var visible = new byte[2 * 1 * 4];
        Assert.True(Nv12Color.TryCopy(source, 4, 2, 6, 2, 1, YuvColorSpace.AirPlayMirror, visible));
        Assert.Equal(new byte[] { 255, 255, 255, 255, 0, 0, 0, 255 }, visible);

        var wide = new byte[3 * 1 * 4];
        Assert.True(Nv12Color.TryCopy(source, 4, 2, 6, 3, 1, YuvColorSpace.AirPlayMirror, wide));
        Nv12Color.Convert(200, 160, 200, YuvColorSpace.AirPlayMirror, out var b, out var g, out var r);
        Assert.Equal(b, wide[8]);
        Assert.Equal(g, wide[9]);
        Assert.Equal(r, wide[10]);
        Assert.Equal(255, wide[11]);

        Assert.False(Nv12Color.TryCopy(source, 4, 2, 6, 2, 1, YuvColorSpace.AirPlayMirror, new byte[4]));
        Assert.False(Nv12Color.TryCopy(source, 3, 2, 4, 2, 2, YuvColorSpace.AirPlayMirror, new byte[16]));
        Assert.False(Nv12Color.TryCopy(source, 4, 2, 5, 2, 1, YuvColorSpace.AirPlayMirror, visible));
        Assert.False(Nv12Color.TryCopy(source, 4, 1, 4, 4, 1, YuvColorSpace.AirPlayMirror, visible));
    }

    private static void Expect(byte y, byte cb, byte cr, YuvColorSpace color, byte b, byte g, byte r)
    {
        Nv12Color.Convert(y, cb, cr, color, out var actualB, out var actualG, out var actualR);
        Assert.Equal(b, actualB);
        Assert.Equal(g, actualG);
        Assert.Equal(r, actualR);
    }

    private static (byte B, byte G, byte R) Reference(int y8, int cb8, int cr8, YuvColorSpace color)
    {
        var kr = color.Matrix == YuvMatrix.Bt709 ? 0.2126 : 0.299;
        var kb = color.Matrix == YuvMatrix.Bt709 ? 0.0722 : 0.114;
        var kg = 1 - kr - kb;
        double y;
        double cb;
        double cr;
        if (color.Range == YuvRange.Limited)
        {
            y = (y8 - 16) / 219.0;
            cb = (cb8 - 128) / 224.0;
            cr = (cr8 - 128) / 224.0;
        }
        else
        {
            y = y8 / 255.0;
            cb = (cb8 - 128) / 255.0;
            cr = (cr8 - 128) / 255.0;
        }

        var r = y + (2 * (1 - kr) * cr);
        var g = y - ((2 * kb * (1 - kb) / kg) * cb) - ((2 * kr * (1 - kr) / kg) * cr);
        var b = y + (2 * (1 - kb) * cb);
        return (ToByte(b), ToByte(g), ToByte(r));
    }

    private static byte ToByte(double channel)
        => (byte)Math.Clamp((int)Math.Round(channel * 255.0), 0, 255);
}
