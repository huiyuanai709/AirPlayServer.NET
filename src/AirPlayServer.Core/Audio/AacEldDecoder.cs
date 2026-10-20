using System.Runtime.InteropServices;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Audio;

internal interface IAacEldDecoder : IDisposable
{
    bool IsAvailable { get; }

    bool TryDecode(ReadOnlySpan<byte> accessUnit, Span<short> interleaved, out int samples, out int channels, out int sampleRate);

    void Flush();
}

internal sealed class UnavailableAacEldDecoder : IAacEldDecoder
{
    public bool IsAvailable => false;

    public bool TryDecode(ReadOnlySpan<byte> accessUnit, Span<short> interleaved, out int samples, out int channels, out int sampleRate)
    {
        samples = 0;
        channels = 0;
        sampleRate = 0;
        return false;
    }

    public void Flush()
    {
    }

    public void Dispose()
    {
    }
}

internal static class AacEldDecoders
{
    internal static IAacEldDecoder Create(IAirPlayLog log)
    {
        var library = FFmpegLibrary.TryLoad(log);
        if (library is not null && FFmpegAacEldDecoder.TryOpen(library, log, out var decoder) && decoder is not null)
        {
            return decoder;
        }

        log.Log(
            AirPlayLogLevel.Warning,
            "镜像伴音端口可以打开，但没有可用的 AAC-ELD 解码器。需要程序旁边或系统路径里的 FFmpeg（libavcodec / libavutil）。PCM 不会播出，画面不受影响。"
            + " Mirror audio can be accepted, but no AAC-ELD decoder is available. Place FFmpeg (libavcodec / libavutil) next to the host. PCM is not played. Video is unchanged.");
        return new UnavailableAacEldDecoder();
    }
}

/// <summary>
/// One FFmpeg AAC-ELD decoder for mirror audio. The AudioSpecificConfig is the
/// fixed 44.1 kHz stereo ELD config used by AirPlay. Layout offsets cover
/// libavcodec 58 through 63 (FFmpeg 4.4 through 9). FFmpeg 5.0 (avcodec 59.18)
/// still uses the old channel fields; 5.1 (59.37) and later use
/// <c>AVChannelLayout</c>.
/// </summary>
internal sealed unsafe class FFmpegAacEldDecoder : IAacEldDecoder
{
    private static ReadOnlySpan<byte> EldConfig => [0xF8, 0xE8, 0x50, 0x00];

    private readonly FFmpegLibrary _library;
    private readonly IAirPlayLog _log;
    private readonly object _gate = new();
    private nint _context;
    private nint _frame;
    private int _logged;
    private int _disposed;

    private FFmpegAacEldDecoder(FFmpegLibrary library, nint context, nint frame, IAirPlayLog log)
    {
        _library = library;
        _context = context;
        _frame = frame;
        _log = log;
    }

    public bool IsAvailable => Volatile.Read(ref _disposed) == 0 && _context != 0;

    internal static bool TryOpen(FFmpegLibrary library, IAirPlayLog log, out FFmpegAacEldDecoder? decoder)
    {
        decoder = null;
        if (!AacCodecLayout.TryFor(library.AvcodecMajor, library.AvcodecMinor, out var layout))
        {
            log.Log(
                AirPlayLogLevel.Warning,
                "这份 libavcodec 的版本不在 AAC-ELD 支持范围内（需要 58–63）。This libavcodec is outside the AAC-ELD layout range (58–63).");
            return false;
        }

        var codec = library.FindNamedDecoder("aac", AacCodecLayout.AacCodecId);
        if (codec == 0)
        {
            log.Log(AirPlayLogLevel.Warning, "这份 libavcodec 没有 AAC 解码器。This libavcodec build has no AAC decoder.");
            return false;
        }

        var context = library.AllocContext(codec);
        if (context == 0 || !Configure(library, context, layout, out var extradata))
        {
            if (context != 0)
            {
                library.FreeContext(&context);
            }

            return false;
        }

        var opened = library.Open(context, codec);
        if (opened < 0)
        {
            log.Log(AirPlayLogLevel.Warning, "AAC-ELD avcodec_open2 失败 (" + library.DescribeError(opened) + ").");
            library.Free(extradata);
            Marshal.WriteIntPtr(context, layout.Extradata, 0);
            library.FreeContext(&context);
            return false;
        }

        var frame = library.FrameAlloc();
        if (frame == 0)
        {
            library.FreeContext(&context);
            return false;
        }

        log.Log(AirPlayLogLevel.Information, "AAC-ELD 解码器已打开（44.1 kHz 立体声）。AAC-ELD decoder is open (44.1 kHz stereo).");
        decoder = new FFmpegAacEldDecoder(library, context, frame, log);
        return true;
    }

    public bool TryDecode(ReadOnlySpan<byte> accessUnit, Span<short> interleaved, out int samples, out int channels, out int sampleRate)
    {
        samples = 0;
        channels = 0;
        sampleRate = 0;
        if (!IsAvailable || accessUnit.IsEmpty || accessUnit.Length > RaopAudioBuffer.MaxPacketBytes)
        {
            return false;
        }

        lock (_gate)
        {
            if (_context == 0)
            {
                return false;
            }

            if (!Send(accessUnit))
            {
                return false;
            }

            return Receive(interleaved, out samples, out channels, out sampleRate);
        }
    }

    public void Flush()
    {
        if (!IsAvailable)
        {
            return;
        }

        lock (_gate)
        {
            if (_context != 0)
            {
                _library.Flush(_context);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var frame = _frame;
            _frame = 0;
            if (frame != 0)
            {
                _library.FrameFree(&frame);
            }

            var context = _context;
            _context = 0;
            if (context != 0)
            {
                _library.FreeContext(&context);
            }
        }
    }

    private bool Send(ReadOnlySpan<byte> accessUnit)
    {
        var size = accessUnit.Length;
        var memory = _library.Malloc((nuint)(size + FFmpegLibrary.InputPadding));
        if (memory == 0)
        {
            return false;
        }

        accessUnit.CopyTo(new Span<byte>((void*)memory, size));
        new Span<byte>((void*)(memory + size), FFmpegLibrary.InputPadding).Clear();
        var packet = _library.PacketAlloc();
        if (packet == 0)
        {
            _library.Free(memory);
            return false;
        }

        if (_library.PacketFromData(packet, memory, size) < 0)
        {
            _library.Free(memory);
            _library.PacketFree(&packet);
            return false;
        }

        var result = _library.SendPacket(_context, packet);
        _library.PacketFree(&packet);
        if (result < 0)
        {
            NoteFailure("AAC-ELD send failed (" + _library.DescribeError(result) + ").");
            return false;
        }

        return true;
    }

    private bool Receive(Span<short> interleaved, out int samples, out int channels, out int sampleRate)
    {
        samples = 0;
        channels = 0;
        sampleRate = 0;
        _library.FrameUnref(_frame);
        var result = _library.ReceiveFrame(_context, _frame);
        if (result == FFmpegLibrary.ErrorAgain)
        {
            return false;
        }

        if (result < 0)
        {
            NoteFailure("AAC-ELD receive failed (" + _library.DescribeError(result) + ").");
            return false;
        }

        return PcmConvert.TryRead(_library, _frame, interleaved, out samples, out channels, out sampleRate);
    }

    private void NoteFailure(string message)
    {
        if (Interlocked.Exchange(ref _logged, 1) != 0)
        {
            return;
        }

        _log.Log(AirPlayLogLevel.Warning, message);
    }

    private static bool Configure(FFmpegLibrary library, nint context, AacCodecLayout layout, out nint extradata)
    {
        extradata = library.Malloc((nuint)(EldConfig.Length + FFmpegLibrary.InputPadding));
        if (extradata == 0)
        {
            return false;
        }

        EldConfig.CopyTo(new Span<byte>((void*)extradata, EldConfig.Length));
        new Span<byte>((void*)(extradata + EldConfig.Length), FFmpegLibrary.InputPadding).Clear();
        Marshal.WriteIntPtr(context, layout.Extradata, extradata);
        Marshal.WriteInt32(context, layout.ExtradataSize, EldConfig.Length);
        Marshal.WriteInt32(context, layout.SampleRate, 44100);
        Marshal.WriteInt32(context, layout.Strict, -2);
        library.TrySetInt(context, "strict", -2);
        if (layout.ChannelLayoutStruct >= 0)
        {
            var at = context + layout.ChannelLayoutStruct;
            Marshal.WriteInt32(at, 1);
            Marshal.WriteInt32(at, 4, 2);
            Marshal.WriteInt64(at, 8, 3);
            Marshal.WriteIntPtr(at, 16, 0);
        }
        else
        {
            Marshal.WriteInt32(context, layout.Channels, 2);
            Marshal.WriteInt64(context, layout.ChannelLayout, 3);
        }

        return true;
    }
}

internal readonly record struct AacCodecLayout(
    int Extradata,
    int ExtradataSize,
    int SampleRate,
    int Strict,
    int Channels,
    int ChannelLayout,
    int ChannelLayoutStruct,
    int FrameSampleRate)
{
    internal const int AacCodecId = 86018;

    internal static bool TryFor(int major, int minor, out AacCodecLayout layout)
    {
        layout = default;
        switch (major)
        {
            case 58:
                layout = new AacCodecLayout(88, 96, 400, 644, 404, 432, -1, 272);
                return true;
            case 59 when minor < 37:
                layout = new AacCodecLayout(88, 96, 352, 508, 356, 384, -1, 208);
                return true;
            case 59:
                layout = new AacCodecLayout(88, 96, 352, 508, -1, -1, 920, 208);
                return true;
            case 60:
                layout = new AacCodecLayout(88, 96, 352, 508, -1, -1, 912, 208);
                return true;
            case 61:
                layout = new AacCodecLayout(72, 80, 344, 516, -1, -1, 352, 192);
                return true;
            case >= 62 and <= 63:
                layout = new AacCodecLayout(72, 80, 344, 516, -1, -1, 352, 180);
                return true;
            default:
                return false;
        }
    }
}

internal static class PcmConvert
{
    internal static bool TryRead(FFmpegLibrary library, nint frame, Span<short> destination, out int samples, out int channels, out int sampleRate)
    {
        samples = 0;
        channels = 0;
        sampleRate = 44100;
        _ = library;
        var nb = Marshal.ReadInt32(frame, 112);
        var format = Marshal.ReadInt32(frame, 116);
        if (nb is <= 0 or > 2048)
        {
            return false;
        }

        if (!TrySample(format, out var bytesPer, out var planar))
        {
            return false;
        }

        var linesize = Marshal.ReadInt32(frame, 64);
        if (planar)
        {
            channels = 0;
            for (var i = 0; i < 2; i++)
            {
                if (Plane(frame, i) == 0)
                {
                    break;
                }

                channels++;
            }
        }
        else if (bytesPer > 0 && linesize >= bytesPer * nb)
        {
            channels = linesize / (bytesPer * nb);
        }

        if (channels is < 1 or > 2)
        {
            return false;
        }

        var rateOffset = 0;
        if (library.AvcodecMajor is 58)
        {
            rateOffset = 272;
        }
        else if (library.AvcodecMajor is 59 or 60)
        {
            rateOffset = 208;
        }
        else if (library.AvcodecMajor is 61)
        {
            rateOffset = 192;
        }
        else if (library.AvcodecMajor is >= 62 and <= 63)
        {
            rateOffset = 180;
        }

        if (rateOffset > 0)
        {
            var rate = Marshal.ReadInt32(frame, rateOffset);
            if (rate is >= 8000 and <= 192000)
            {
                sampleRate = rate;
            }
        }

        samples = nb * channels;
        if (destination.Length < samples)
        {
            return false;
        }

        for (var sample = 0; sample < nb; sample++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var plane = Plane(frame, planar ? channel : 0);
                if (plane == 0)
                {
                    return false;
                }

                var index = planar ? sample : (sample * channels) + channel;
                destination[(sample * channels) + channel] = ReadSample(plane, format, bytesPer, index);
            }
        }

        return true;
    }

    private static nint Plane(nint frame, int index)
    {
        var extended = Marshal.ReadIntPtr(frame, 96);
        if (extended != 0)
        {
            return Marshal.ReadIntPtr(extended, index * nint.Size);
        }

        return Marshal.ReadIntPtr(frame, index * nint.Size);
    }

    private static bool TrySample(int format, out int bytesPer, out bool planar)
    {
        planar = format is 5 or 6 or 7 or 8 or 9 or 11;
        bytesPer = format switch
        {
            0 or 5 => 1,
            1 or 6 => 2,
            2 or 3 or 7 or 8 => 4,
            4 or 9 or 10 or 11 => 8,
            _ => 0,
        };
        return bytesPer != 0;
    }

    private static short ReadSample(nint plane, int format, int bytesPer, int index)
    {
        var packed = format >= 5 && format != 10 ? format - 5 : format;
        var at = plane + (index * bytesPer);
        return packed switch
        {
            0 => (short)((Marshal.ReadByte(at) - 128) * 256),
            1 => (short)Marshal.ReadInt16(at),
            2 => (short)(Marshal.ReadInt32(at) / 65536),
            3 => FloatToS16(BitConverter.Int32BitsToSingle(Marshal.ReadInt32(at))),
            4 => FloatToS16((float)BitConverter.Int64BitsToDouble(Marshal.ReadInt64(at))),
            10 => (short)(Marshal.ReadInt64(at) / 4294967296L),
            _ => 0,
        };
    }

    private static short FloatToS16(float sample)
    {
        if (sample <= -1f)
        {
            return short.MinValue;
        }

        if (sample >= 1f)
        {
            return short.MaxValue;
        }

        var scaled = (int)Math.Round(sample * 32768.0);
        if (scaled > short.MaxValue)
        {
            return short.MaxValue;
        }

        if (scaled < short.MinValue)
        {
            return short.MinValue;
        }

        return (short)scaled;
    }
}
