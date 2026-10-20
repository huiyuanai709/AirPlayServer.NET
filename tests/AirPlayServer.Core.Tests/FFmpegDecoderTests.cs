using AirPlayServer.Core;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class FFmpegDecoderTests
{
    [Fact]
    public void Missing_ffmpeg_keeps_both_decoders_unavailable_when_media_foundation_is_absent()
    {
        var h264 = H264Decoders.Create(null, _ => null);
        var hevc = HevcDecoders.Create(null, _ => null);
        using var h264Owned = h264 as IDisposable;
        using var hevcOwned = hevc as IDisposable;
        if (OperatingSystem.IsWindows() && h264.Name.StartsWith("media-foundation", StringComparison.Ordinal))
        {
            Assert.True(h264.IsAvailable);
        }
        else
        {
            Assert.IsType<UnavailableH264Decoder>(h264);
            Assert.False(h264.IsAvailable);
            Assert.Equal("unavailable", h264.Name);
        }

        if (OperatingSystem.IsWindows() && hevc.Name.StartsWith("media-foundation-hevc", StringComparison.Ordinal))
        {
            Assert.True(hevc.IsAvailable);
        }
        else
        {
            Assert.IsType<UnavailableHevcDecoder>(hevc);
            Assert.False(hevc.IsAvailable);
            Assert.Equal("unavailable", hevc.Name);
        }
    }

    [Fact]
    public void Requested_hevc_is_not_advertised_without_a_decoder()
    {
        var log = new ListLog();
        Assert.False(HevcDecoders.ShouldAdvertise(true, new UnavailableHevcDecoder(), log));
        Assert.False(HevcDecoders.ShouldAdvertise(true, null, log));
        Assert.Contains("不会宣告", log.Text, StringComparison.Ordinal);
        Assert.Contains("Features bit 42 is not advertised", log.Text, StringComparison.Ordinal);
        Assert.Contains("H.264", log.Text, StringComparison.Ordinal);
        Assert.Contains("CLSID_CMSH265DecoderMFT", log.Text, StringComparison.Ordinal);
        Assert.Contains("libavcodec", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Hevc_is_advertised_only_when_a_decoder_is_open()
    {
        var log = new ListLog();
        Assert.False(HevcDecoders.ShouldAdvertise(false, new OpenHevc(), log));
        Assert.Empty(log.Text);
        Assert.True(HevcDecoders.ShouldAdvertise(true, new OpenHevc(), log));
        Assert.Contains("ffmpeg-test", log.Text, StringComparison.Ordinal);
        Assert.Contains("bit 42", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unspecified_ffmpeg_color_stays_on_the_airplay_default()
    {
        var unspecified = FFmpegColor.Resolve(default, pixelFormat: 0, colorSpace: 2, colorRange: 0);
        Assert.Equal(YuvColorSpace.AirPlayMirror, unspecified);

        var fromFrame = FFmpegColor.Resolve(default, pixelFormat: 0, colorSpace: 5, colorRange: 1);
        Assert.Equal(new YuvColorSpace(YuvMatrix.Bt601, YuvRange.Limited), fromFrame);

        var jpeg = FFmpegColor.Resolve(default, pixelFormat: FFmpegLibrary.PixelYuvJ420P, colorSpace: 2, colorRange: 0);
        Assert.Equal(YuvRange.Full, jpeg.Range);

        var bitstream = new H264ColorSignals(true, 5, true, false);
        var fromBitstream = FFmpegColor.Resolve(bitstream, pixelFormat: 0, colorSpace: 1, colorRange: 2);
        Assert.Equal(new YuvColorSpace(YuvMatrix.Bt601, YuvRange.Limited), fromBitstream);
    }

    [Fact]
    public void Yuv420_and_nv12_planes_match_the_sample_converter()
    {
        byte[] y = [128, 140, 128, 140];
        byte[] u = [90, 160];
        byte[] v = [200, 40];
        byte[] nv12 = [90, 200, 160, 40];
        var planar = new byte[2 * 2 * 4];
        var interleaved = new byte[2 * 2 * 4];
        var color = YuvColorSpace.AirPlayMirror;
        Assert.True(YuvPlanarColor.TryCopyYuv420(y, 2, u, 2, v, 2, 2, 2, color, planar));
        Assert.True(YuvPlanarColor.TryCopyNv12(y, 2, nv12, 4, 2, 2, color, interleaved));
        Assert.Equal(planar, interleaved);
        Nv12Color.Convert(128, 90, 200, color, out var b, out var g, out var r);
        Assert.Equal(b, planar[0]);
        Assert.Equal(g, planar[1]);
        Assert.Equal(r, planar[2]);
        Assert.Equal(255, planar[3]);
        Assert.False(YuvPlanarColor.TryCopyYuv420(y, 2, u, 2, v, 2, 3, 2, color, planar));
    }

    [Fact]
    public void Ffmpeg_9_hevc_id_is_not_the_fic_slot()
    {
        Assert.Equal(173, FFmpegLibrary.HevcCodecId(58));
        Assert.Equal(173, FFmpegLibrary.HevcCodecId(62));
        Assert.Equal(172, FFmpegLibrary.HevcCodecId(63));
        Assert.Equal(173, FFmpegLibrary.CodecHevcThroughAvcodec62);
        Assert.Equal(172, FFmpegLibrary.CodecHevcFromAvcodec63);

        Assert.Equal(63, FFmpegLibrary.TryParseAvcodecMajor("avcodec-63.dll"));
        Assert.Equal(63, FFmpegLibrary.TryParseAvcodecMajor("libavcodec.so.63"));
        Assert.Equal(63, FFmpegLibrary.TryParseAvcodecMajor("libavcodec.so.63.1.102"));
        Assert.Equal(60, FFmpegLibrary.TryParseAvcodecMajor("/usr/lib/libavcodec.so.60"));
        Assert.Equal(0, FFmpegLibrary.TryParseAvcodecMajor("libavcodec.so"));
        Assert.Equal(0, FFmpegLibrary.TryParseAvcodecMajor("avcodec.dll"));
    }

    [Fact]
    public void Probe_order_tries_ffmpeg_9_sonames_before_older_majors()
    {
        Assert.Equal(
            [(63, 61, 7), (62, 60, 6), (61, 59, 5), (60, 58, 4), (59, 57, 4), (58, 56, 3)],
            FFmpegLibrary.SonameTriples);

        var newest = FFmpegLibrary.SonameTriples[0];
        Assert.Equal("avcodec-63.dll", FFmpegLibrary.LibraryFileName("avcodec", newest.Avcodec, windows: true));
        Assert.Equal("avutil-61.dll", FFmpegLibrary.LibraryFileName("avutil", newest.Avutil, windows: true));
        Assert.Equal("swresample-7.dll", FFmpegLibrary.LibraryFileName("swresample", newest.Swresample, windows: true));
        Assert.Equal("libavcodec.so.63", FFmpegLibrary.LibraryFileName("avcodec", newest.Avcodec, windows: false));
        Assert.Equal("libavutil.so.61", FFmpegLibrary.LibraryFileName("avutil", newest.Avutil, windows: false));
        Assert.Equal("libswresample.so.7", FFmpegLibrary.LibraryFileName("swresample", newest.Swresample, windows: false));
    }

    [Fact]
    public void Ffmpeg_decodes_annex_b_fixtures_when_the_libraries_are_present()
    {
        var log = new ListLog();
        var library = FFmpegLibrary.TryLoad(log);
        if (library is null)
        {
            // CI does not install FFmpeg. This environment must, so a silent skip
            // is only acceptable when AIRPLAY_REQUIRE_FFMPEG is unset.
            if (Environment.GetEnvironmentVariable("AIRPLAY_REQUIRE_FFMPEG") == "1")
            {
                Assert.Fail("FFmpeg libraries were not loaded.");
            }

            return;
        }

        var h264Path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "red-64x64.h264");
        var hevcPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "red-64x64.hevc");
        Assert.True(File.Exists(h264Path), "Missing H.264 fixture " + h264Path);
        Assert.True(File.Exists(hevcPath), "Missing HEVC fixture " + hevcPath);
        AssertDecodedRed(FFmpegH264Decoder.TryCreate(library, log, out var h264) ? h264 : null, File.ReadAllBytes(h264Path));
        AssertDecodedRed(FFmpegHevcDecoder.TryCreate(library, log, out var hevc) ? hevc : null, File.ReadAllBytes(hevcPath));
        Assert.Contains("opened decoder \"h264\"", log.Text, StringComparison.Ordinal);
        Assert.Contains("opened decoder \"hevc\"", log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("decoder \"fic\"", log.Text, StringComparison.Ordinal);
        if (library.AvcodecMajor >= 63)
        {
            // FFmpeg 9: 173 is FIC. A hardcoded 173 would not produce this line.
            Assert.Contains("AVCodecID 172", log.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("Zero slices found", log.Text, StringComparison.Ordinal);
        }
    }

    private static void AssertDecodedRed(IDisposable? decoder, byte[] annexB)
    {
        Assert.NotNull(decoder);
        using (decoder)
        {
            var decoded = decoder switch
            {
                IH264Decoder h264 => h264.TryDecode(annexB, true, out var frame) ? frame : default,
                IHevcDecoder hevc => hevc.TryDecode(annexB, true, out var frame) ? frame : default,
                _ => default,
            };
            Assert.Equal(64, decoded.Width);
            Assert.Equal(64, decoded.Height);
            var pixel = decoded.Bgra.Span;
            Assert.True(pixel.Length >= 4);
            Assert.True(pixel[2] > 180, "red channel " + pixel[2]);
            Assert.True(pixel[0] < 80, "blue channel " + pixel[0]);
            Assert.Equal(255, pixel[3]);
        }
    }

    private sealed class OpenHevc : IHevcDecoder
    {
        public bool IsAvailable => true;

        public string Name => "ffmpeg-test";

        public void Configure(ReadOnlySpan<byte> videoParameterSet, ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet)
        {
        }

        public void Reset()
        {
        }

        public bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame)
        {
            frame = default;
            return false;
        }
    }

    private sealed class ListLog : IAirPlayLog
    {
        public string Text { get; private set; } = "";

        public void Log(AirPlayLogLevel level, string message)
        {
            Text = string.IsNullOrEmpty(Text) ? message : Text + "\n" + message;
        }
    }
}
