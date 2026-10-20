using AirPlayServer.Core;

namespace AirPlayServer.Core.Tests;

public sealed class DecodeTimingTests
{
    [Fact]
    public void Names_round_trip()
    {
        Assert.True(DecodeTiming.TryParse("sample", out var sample));
        Assert.Equal(DecodeTimingMode.Sample, sample);
        Assert.True(DecodeTiming.TryParse("EVERY", out var every));
        Assert.Equal(DecodeTimingMode.Every, every);
        Assert.True(DecodeTiming.TryParse("0", out var off));
        Assert.Equal(DecodeTimingMode.Off, off);
        Assert.False(DecodeTiming.TryParse("fast", out _));
        Assert.Contains("us", DecodeTiming.Describe(DecodeTimingMode.Sample), StringComparison.Ordinal);
        Assert.Contains("every", DecodeTiming.Describe(DecodeTimingMode.Every), StringComparison.Ordinal);
    }

    [Fact]
    public void Sample_mode_prints_the_first_frame_every_thirtieth_and_slow_access_units()
    {
        var log = new ListLog();
        var timing = new DecodeTimingLog(DecodeTimingMode.Sample, log);
        timing.Note("media-foundation", 1_800, picture: false, 0, 0, 0);
        Assert.Empty(log.Lines);

        timing.Note("media-foundation", 4_200, picture: true, 1170, 2532, 1);
        Assert.Single(log.Lines);
        Assert.Contains("media-foundation", log.Lines[0], StringComparison.Ordinal);
        Assert.Contains("4200 us", log.Lines[0], StringComparison.Ordinal);
        Assert.Contains("1170x2532", log.Lines[0], StringComparison.Ordinal);
        Assert.Contains("第 1 帧", log.Lines[0], StringComparison.Ordinal);
        Assert.Contains("frame 1", log.Lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("media-foundation-hardware", log.Lines[0], StringComparison.Ordinal);

        for (var frame = 2; frame <= 29; frame++)
        {
            timing.Note("media-foundation", 3_000, picture: true, 1170, 2532, frame);
        }

        Assert.Single(log.Lines);
        timing.Note("media-foundation-hardware", 2_500, picture: true, 1170, 2532, 30);
        Assert.Equal(2, log.Lines.Count);
        Assert.Contains("media-foundation-hardware", log.Lines[1], StringComparison.Ordinal);
        Assert.Contains("第 30 帧", log.Lines[1], StringComparison.Ordinal);
        Assert.Contains("2500 us", log.Lines[1], StringComparison.Ordinal);
        Assert.Contains("平均", log.Lines[1], StringComparison.Ordinal);

        timing.Note("ffmpeg-hevc", DecodeTiming.SlowMicroseconds, picture: false, 0, 0, 30);
        Assert.Equal(3, log.Lines.Count);
        Assert.Contains("ffmpeg-hevc", log.Lines[2], StringComparison.Ordinal);
        Assert.Contains("无画面", log.Lines[2], StringComparison.Ordinal);
        Assert.Contains("20000 us", log.Lines[2], StringComparison.Ordinal);
        Assert.Contains("no picture", log.Lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Every_mode_prints_each_access_unit_and_off_prints_nothing()
    {
        var log = new ListLog();
        var every = new DecodeTimingLog(DecodeTimingMode.Every, log);
        every.Note("ffmpeg-h264", 900, picture: false, 0, 0, 0);
        every.Note("ffmpeg-h264", 8_800, picture: true, 1290, 2796, 1);
        Assert.Equal(2, log.Lines.Count);
        Assert.Contains("ffmpeg-h264", log.Lines[0], StringComparison.Ordinal);
        Assert.Contains("访问单元 1", log.Lines[0], StringComparison.Ordinal);
        Assert.Contains("1290x2796", log.Lines[1], StringComparison.Ordinal);
        Assert.Contains("8800 us", log.Lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("平均", log.Lines[1], StringComparison.Ordinal);

        var quiet = new ListLog();
        new DecodeTimingLog(DecodeTimingMode.Off, quiet).Note("media-foundation", 50_000, picture: true, 16, 16, 1);
        Assert.Empty(quiet.Lines);
    }

    [Fact]
    public void Microseconds_are_zero_for_a_non_positive_tick_span()
    {
        Assert.Equal(0, DecodeTiming.TicksToMicroseconds(0));
        Assert.Equal(0, DecodeTiming.TicksToMicroseconds(-5));
        if (System.Diagnostics.Stopwatch.Frequency > 0)
        {
            var oneMillisecond = System.Diagnostics.Stopwatch.Frequency / 1000;
            var microseconds = DecodeTiming.TicksToMicroseconds(oneMillisecond);
            Assert.InRange(microseconds, 900, 1100);
        }
    }

    private sealed class ListLog : IAirPlayLog
    {
        public List<string> Lines { get; } = [];

        public void Log(AirPlayLogLevel level, string message) => Lines.Add(message);
    }
}
