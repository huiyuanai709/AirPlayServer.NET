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

    [Fact]
    public void Bulk_copy_matches_convert_for_odd_widths_padding_and_both_matrices()
    {
        var random = new Random(709);
        foreach (var matrix in new[] { YuvMatrix.Bt709, YuvMatrix.Bt601 })
        {
            foreach (var range in new[] { YuvRange.Full, YuvRange.Limited })
            {
                var color = new YuvColorSpace(matrix, range);
                const int codedWidth = 18;
                const int codedHeight = 8;
                const int stride = 24;
                const int width = 17;
                const int height = 7;
                var source = new byte[stride * codedHeight + stride * (codedHeight / 2)];
                random.NextBytes(source);
                var destination = new byte[width * height * 4];
                Assert.True(Nv12Color.TryCopy(source, codedWidth, codedHeight, stride, width, height, color, destination));
                var pixel = 0;
                for (var y = 0; y < height; y++)
                {
                    var yRow = y * stride;
                    var uvRow = stride * codedHeight + (y / 2) * stride;
                    for (var x = 0; x < width; x++)
                    {
                        var cbIndex = uvRow + (x & ~1);
                        Nv12Color.Convert(source[yRow + x], source[cbIndex], source[cbIndex + 1], color, out var b, out var g, out var r);
                        Assert.Equal(b, destination[pixel]);
                        Assert.Equal(g, destination[pixel + 1]);
                        Assert.Equal(r, destination[pixel + 2]);
                        Assert.Equal(255, destination[pixel + 3]);
                        pixel += 4;
                    }
                }
            }
        }
    }

    [Fact]
    public void Tall_frame_matches_convert_on_both_sides_of_a_row_split()
    {
        const int width = 1280;
        const int height = 720;
        var random = new Random(30);
        var yPlane = new byte[width * height];
        var uvPlane = new byte[width * (height / 2)];
        random.NextBytes(yPlane);
        random.NextBytes(uvPlane);
        var destination = new byte[width * height * 4];
        var color = YuvColorSpace.AirPlayMirror;
        Assert.True(YuvPlanarColor.TryCopyNv12(yPlane, width, uvPlane, width, width, height, color, destination));
        foreach (var y in new[] { 0, 1, 179, 180, 359, 360, 539, 540, 718, 719 })
        {
            for (var x = 0; x < width; x += 17)
            {
                var cbIndex = ((y / 2) * width) + (x & ~1);
                Nv12Color.Convert(yPlane[(y * width) + x], uvPlane[cbIndex], uvPlane[cbIndex + 1], color, out var b, out var g, out var r);
                var pixel = ((y * width) + x) * 4;
                Assert.Equal(b, destination[pixel]);
                Assert.Equal(g, destination[pixel + 1]);
                Assert.Equal(r, destination[pixel + 2]);
                Assert.Equal(255, destination[pixel + 3]);
            }
        }
    }

    [Fact]
    public async Task Two_large_frames_convert_together_without_mixing_pixels()
    {
        var first = Task.Run(() => ConvertSolid(80));
        var second = Task.Run(() => ConvertSolid(200));
        var results = await Task.WhenAll(first, second);
        Assert.Equal(80, results[0]);
        Assert.Equal(200, results[1]);
    }

    private static byte ConvertSolid(byte luma)
    {
        const int width = 1280;
        const int height = 720;
        var yPlane = new byte[width * height];
        var uvPlane = new byte[width * (height / 2)];
        Array.Fill(yPlane, luma);
        Array.Fill(uvPlane, (byte)128);
        var destination = new byte[width * height * 4];
        Assert.True(YuvPlanarColor.TryCopyNv12(yPlane, width, uvPlane, width, width, height, YuvColorSpace.AirPlayMirror, destination));
        Nv12Color.Convert(luma, 128, 128, YuvColorSpace.AirPlayMirror, out var b, out var g, out var r);
        Assert.Equal(b, destination[0]);
        Assert.Equal(g, destination[1]);
        Assert.Equal(r, destination[2]);
        var last = destination.Length - 4;
        Assert.Equal(b, destination[last]);
        Assert.Equal(g, destination[last + 1]);
        Assert.Equal(r, destination[last + 2]);
        return destination[2];
    }

    [Fact]
    public void Planar_and_nv12_phone_rows_match_and_can_be_timed()
    {
        const int width = 1290;
        const int height = 64;
        var random = new Random(2796);
        var y = new byte[width * height];
        var u = new byte[(width / 2) * (height / 2)];
        var v = new byte[u.Length];
        var uv = new byte[width * (height / 2)];
        random.NextBytes(y);
        random.NextBytes(u);
        random.NextBytes(v);
        for (var row = 0; row < height / 2; row++)
        {
            for (var col = 0; col < width / 2; col++)
            {
                uv[(row * width) + (col * 2)] = u[(row * (width / 2)) + col];
                uv[(row * width) + (col * 2) + 1] = v[(row * (width / 2)) + col];
            }
        }

        var planar = new byte[width * height * 4];
        var interleaved = new byte[planar.Length];
        var color = YuvColorSpace.AirPlayMirror;
        Assert.True(YuvPlanarColor.TryCopyYuv420(y, width, u, width / 2, v, width / 2, width, height, color, planar));
        Assert.True(YuvPlanarColor.TryCopyNv12(y, width, uv, width, width, height, color, interleaved));
        Assert.Equal(planar, interleaved);
        Nv12Color.Convert(y[0], u[0], v[0], color, out var b, out var g, out var r);
        Assert.Equal(b, planar[0]);
        Assert.Equal(g, planar[1]);
        Assert.Equal(r, planar[2]);

        if (Environment.GetEnvironmentVariable("AIRPLAY_BENCH_YUV") != "1")
        {
            return;
        }

        const int tall = 2796;
        var yTall = new byte[width * tall];
        var uvTall = new byte[width * (tall / 2)];
        random.NextBytes(yTall);
        random.NextBytes(uvTall);
        var bgra = new byte[width * tall * 4];
        for (var i = 0; i < 3; i++)
        {
            YuvPlanarColor.TryCopyNv12(yTall, width, uvTall, width, width, tall, color, bgra);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        const int passes = 8;
        for (var i = 0; i < passes; i++)
        {
            YuvPlanarColor.TryCopyNv12(yTall, width, uvTall, width, width, tall, color, bgra);
        }

        watch.Stop();
        var microseconds = watch.ElapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency / passes;
        watch.Restart();
        for (var i = 0; i < passes; i++)
        {
            LegacyNv12(yTall, uvTall, width, tall, bgra);
        }

        watch.Stop();
        var legacy = watch.ElapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency / passes;
        Console.WriteLine("nv12 " + width + "x" + tall + " table " + microseconds + " us, scalar " + legacy + " us");
    }

    private static void LegacyNv12(byte[] yPlane, byte[] uvPlane, int width, int height, byte[] destination)
    {
        var to = 0;
        for (var y = 0; y < height; y++)
        {
            var yRow = y * width;
            var uvRow = (y / 2) * width;
            for (var x = 0; x < width; x++)
            {
                var cbIndex = uvRow + (x & ~1);
                LegacyConvert(yPlane[yRow + x], uvPlane[cbIndex], uvPlane[cbIndex + 1], out var b, out var g, out var r);
                destination[to++] = b;
                destination[to++] = g;
                destination[to++] = r;
                destination[to++] = 255;
            }
        }
    }

    private static void LegacyConvert(byte y8, byte cb8, byte cr8, out byte b, out byte g, out byte r)
    {
        var luma = y8 * 65536 / 255;
        var chromaB = (cb8 - 128) * 65536 / 255;
        var chromaR = (cr8 - 128) * 65536 / 255;
        r = LegacyByte(luma + LegacyScale(103206, chromaR));
        g = LegacyByte(luma - LegacyScale(12276, chromaB) - LegacyScale(30679, chromaR));
        b = LegacyByte(luma + LegacyScale(121609, chromaB));
    }

    private static int LegacyScale(int coefficient, int sample)
    {
        var product = (long)coefficient * sample;
        if (product >= 0)
        {
            return (int)((product + 32768) >> 16);
        }

        return -(int)(((-product) + 32768) >> 16);
    }

    private static byte LegacyByte(int q16)
    {
        var scaled = (long)q16 * 255;
        scaled += scaled >= 0 ? 32768 : -32768;
        return (byte)Math.Clamp((int)(scaled >> 16), 0, 255);
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
