using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AirPlayServer.Core;

/// <summary>
/// How often a mirror session prints the time spent inside one decode call.
/// Off by default. The host turns it on with <c>--decode-timing</c> or
/// <c>AIRPLAY_DECODE_TIMING</c>.
/// </summary>
public enum DecodeTimingMode
{
    /// <summary>No timing lines.</summary>
    Off = 0,

    /// <summary>
    /// The first published frame, then every 30th, plus any access unit that
    /// takes at least <see cref="DecodeTiming.SlowMicroseconds"/>.
    /// </summary>
    Sample = 1,

    /// <summary>Every access unit, including ones that do not produce a picture.</summary>
    Every = 2,
}

/// <summary>
/// Decode-call timing. The unit is always microseconds. Strings are built only
/// when a line is actually emitted.
/// </summary>
public static class DecodeTiming
{
    /// <summary>Sample mode prints a line when <c>DecodedFrames</c> is a multiple of this.</summary>
    public const int SampleEvery = 30;

    /// <summary>Access units at least this slow are printed even between samples.</summary>
    public const long SlowMicroseconds = 20_000;

    public static bool TryParse(string? text, out DecodeTimingMode mode)
    {
        mode = DecodeTimingMode.Off;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        switch (text.Trim().ToLowerInvariant())
        {
            case "0":
            case "off":
            case "false":
                mode = DecodeTimingMode.Off;
                return true;
            case "1":
            case "sample":
            case "true":
                mode = DecodeTimingMode.Sample;
                return true;
            case "every":
            case "all":
                mode = DecodeTimingMode.Every;
                return true;
            default:
                return false;
        }
    }

    public static string Describe(DecodeTimingMode mode)
        => mode switch
        {
            DecodeTimingMode.Every => "解码耗时已打开：每个访问单元一行，单位 us。 Decode timing is on: every access unit, unit us.",
            DecodeTimingMode.Sample => "解码耗时已打开：单位 us。第 1 帧和之后每 " + SampleEvery.ToString(CultureInfo.InvariantCulture)
                + " 帧一行，达到 " + SlowMicroseconds.ToString(CultureInfo.InvariantCulture)
                + " us 的访问单元会立刻打印。--decode-timing every 则每个访问单元一行。 Decode timing is on: unit us, frame 1 and then every "
                + SampleEvery.ToString(CultureInfo.InvariantCulture)
                + " frames, plus any access unit at or above "
                + SlowMicroseconds.ToString(CultureInfo.InvariantCulture)
                + " us. --decode-timing every logs every access unit.",
            _ => "解码耗时关闭。 Decode timing is off.",
        };

    internal static long TicksToMicroseconds(long ticks)
    {
        if (ticks <= 0)
        {
            return 0;
        }

        return ticks * 1_000_000L / Stopwatch.Frequency;
    }
}

/// <summary>
/// Per-session timing log. One mirror pipe decodes on one thread, so the
/// counters are not shared.
/// </summary>
internal sealed class DecodeTimingLog
{
    private readonly DecodeTimingMode _mode;
    private readonly IAirPlayLog _log;
    private readonly StringBuilder _line = new(192);
    private int _accessUnits;
    private long _windowSum;
    private int _windowCount;

    internal DecodeTimingLog(DecodeTimingMode mode, IAirPlayLog log)
    {
        _mode = mode;
        _log = log;
    }

    internal bool Enabled => _mode != DecodeTimingMode.Off;

    internal void Note(string decoder, long microseconds, bool picture, int width, int height, int decodedFrames)
    {
        if (_mode == DecodeTimingMode.Off)
        {
            return;
        }

        var accessUnit = ++_accessUnits;
        if (picture)
        {
            _windowSum += microseconds;
            _windowCount++;
        }

        var slow = microseconds >= DecodeTiming.SlowMicroseconds;
        var sampled = picture && (decodedFrames == 1 || (decodedFrames > 0 && decodedFrames % DecodeTiming.SampleEvery == 0));
        if (_mode != DecodeTimingMode.Every && !slow && !sampled)
        {
            return;
        }

        var mean = _windowCount > 0 ? _windowSum / _windowCount : microseconds;
        var window = _windowCount;
        if (picture && _mode == DecodeTimingMode.Sample)
        {
            _windowSum = 0;
            _windowCount = 0;
        }

        _log.Log(
            AirPlayLogLevel.Information,
            picture
                ? FormatPicture(decoder, microseconds, width, height, decodedFrames, _mode == DecodeTimingMode.Sample, mean, window)
                : FormatMiss(decoder, microseconds, accessUnit, decodedFrames));
    }

    private string FormatPicture(string decoder, long microseconds, int width, int height, int frame, bool withMean, long mean, int window)
    {
        _line.Clear();
        _line.Append("解码 ");
        _line.Append(decoder);
        _line.Append(' ');
        Append(microseconds);
        _line.Append(" us ");
        Append(width);
        _line.Append('x');
        Append(height);
        _line.Append(" 第 ");
        Append(frame);
        _line.Append(" 帧");
        if (withMean)
        {
            _line.Append("，近 ");
            Append(window);
            _line.Append(" 帧平均 ");
            Append(mean);
            _line.Append(" us");
        }

        _line.Append("。 Decode ");
        _line.Append(decoder);
        _line.Append(' ');
        Append(microseconds);
        _line.Append(" us ");
        Append(width);
        _line.Append('x');
        Append(height);
        _line.Append(" frame ");
        Append(frame);
        if (withMean)
        {
            _line.Append(", mean ");
            Append(mean);
            _line.Append(" us over ");
            Append(window);
        }

        _line.Append('.');
        return _line.ToString();
    }

    private string FormatMiss(string decoder, long microseconds, int accessUnit, int decodedFrames)
    {
        _line.Clear();
        _line.Append("解码 ");
        _line.Append(decoder);
        _line.Append(' ');
        Append(microseconds);
        _line.Append(" us 无画面，访问单元 ");
        Append(accessUnit);
        _line.Append("，已出画 ");
        Append(decodedFrames);
        _line.Append("。 Decode ");
        _line.Append(decoder);
        _line.Append(' ');
        Append(microseconds);
        _line.Append(" us no picture, access unit ");
        Append(accessUnit);
        _line.Append(", decoded ");
        Append(decodedFrames);
        _line.Append('.');
        return _line.ToString();
    }

    private void Append(long value)
    {
        Span<char> buffer = stackalloc char[20];
        value.TryFormat(buffer, out var written, provider: CultureInfo.InvariantCulture);
        _line.Append(buffer[..written]);
    }
}
