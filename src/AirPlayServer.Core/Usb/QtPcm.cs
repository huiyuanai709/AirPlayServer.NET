using System.Buffers.Binary;

namespace AirPlayServer.Core.Usb;

/// <summary>
/// One block of interleaved 16-bit PCM taken from an ASYN EAT! sample.
/// The wired path is clear LPCM. It is not AAC-ELD and it is not FairPlay.
/// </summary>
internal readonly struct QtPcmChunk
{
    public QtPcmChunk(short[] samples, int channels, int sampleRate)
    {
        Samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
    }

    public short[] Samples { get; }

    public int Channels { get; }

    public int SampleRate { get; }
}

/// <summary>
/// AudioStreamBasicDescription carried by SYNC AFMT. QuickTime-over-USB
/// advertises and sends little-endian LPCM. The host's HPA1 blob asks for
/// 48 kHz stereo 16-bit, which is <see cref="Default"/>.
/// </summary>
internal readonly struct QtAudioFormat
{
    public const uint Lpcm = 0x6C70636D;

    public double SampleRate { get; init; }

    public uint FormatId { get; init; }

    public uint FormatFlags { get; init; }

    public int BytesPerPacket { get; init; }

    public int FramesPerPacket { get; init; }

    public int BytesPerFrame { get; init; }

    public int Channels { get; init; }

    public int BitsPerChannel { get; init; }

    public int SampleRateHertz => (int)Math.Round(SampleRate);

    public bool IsLpcm => FormatId == Lpcm;

    public bool IsFloat => (FormatFlags & 1) != 0;

    public bool IsBigEndian => (FormatFlags & 2) != 0;

    public bool IsSigned => (FormatFlags & 4) != 0;

    public bool IsPacked => (FormatFlags & 8) != 0;

    public bool IsNonInterleaved => (FormatFlags & 32) != 0;

    public static QtAudioFormat Default { get; } = new()
    {
        SampleRate = 48000,
        FormatId = Lpcm,
        FormatFlags = 0x0C,
        BytesPerPacket = 4,
        FramesPerPacket = 1,
        BytesPerFrame = 4,
        Channels = 2,
        BitsPerChannel = 16,
    };

    public bool IsPlayable
    {
        get
        {
            if (!IsLpcm || Channels is < 1 or > 2 || !IsPacked)
            {
                return false;
            }

            var rate = SampleRateHertz;
            if (rate is < 8000 or > 192000)
            {
                return false;
            }

            if (IsFloat)
            {
                return BitsPerChannel == 32 && !IsBigEndian;
            }

            return IsSigned && BitsPerChannel == 16;
        }
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out QtAudioFormat format)
    {
        format = default;
        if (data.Length < 40)
        {
            return false;
        }

        var rate = BitConverter.ToDouble(data[..8]);
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0)
        {
            return false;
        }

        format = new QtAudioFormat
        {
            SampleRate = rate,
            FormatId = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
            FormatFlags = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
            BytesPerPacket = BinaryPrimitives.ReadInt32LittleEndian(data[16..]),
            FramesPerPacket = BinaryPrimitives.ReadInt32LittleEndian(data[20..]),
            BytesPerFrame = BinaryPrimitives.ReadInt32LittleEndian(data[24..]),
            Channels = BinaryPrimitives.ReadInt32LittleEndian(data[28..]),
            BitsPerChannel = BinaryPrimitives.ReadInt32LittleEndian(data[32..]),
        };
        return true;
    }
}

/// <summary>
/// Turns an EAT! CMSampleBuffer into interleaved signed 16-bit PCM.
/// Integer LPCM is copied. Float32 LPCM is scaled into that range.
/// Anything else is left unplayed so the video session can continue.
/// </summary>
internal static class QtPcm
{
    public static bool TryRead(ReadOnlySpan<byte> sampleAtom, QtAudioFormat format, out QtPcmChunk chunk)
    {
        chunk = default;
        if (!format.IsPlayable || !QtSampleBuffer.TryCopySampleData(sampleAtom, out var data))
        {
            return false;
        }

        if (!TryDecode(data, format, out var samples))
        {
            return false;
        }

        chunk = new QtPcmChunk(samples, format.Channels, format.SampleRateHertz);
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, QtAudioFormat format, out short[] interleaved)
    {
        interleaved = [];
        if (!format.IsPlayable || data.IsEmpty)
        {
            return false;
        }

        var sampleBytes = format.IsFloat ? 4 : 2;
        var channels = format.Channels;
        int frames;
        var planar = format.IsNonInterleaved;
        var stride = 0;
        if (planar)
        {
            if (data.Length % (channels * sampleBytes) != 0)
            {
                return false;
            }

            frames = data.Length / (channels * sampleBytes);
        }
        else
        {
            stride = format.BytesPerFrame > 0 ? format.BytesPerFrame : sampleBytes * channels;
            if (stride < sampleBytes * channels)
            {
                return false;
            }

            frames = data.Length / stride;
        }

        if (frames <= 0)
        {
            return false;
        }

        interleaved = new short[frames * channels];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = planar
                    ? (channel * frames * sampleBytes) + (frame * sampleBytes)
                    : (frame * stride) + (channel * sampleBytes);
                interleaved[(frame * channels) + channel] = ReadSample(data, offset, format);
            }
        }

        return true;
    }

    private static short ReadSample(ReadOnlySpan<byte> data, int offset, QtAudioFormat format)
    {
        if (format.IsFloat)
        {
            var value = BitConverter.ToSingle(data.Slice(offset, 4));
            if (float.IsNaN(value))
            {
                return 0;
            }

            if (value >= 1f)
            {
                return short.MaxValue;
            }

            if (value <= -1f)
            {
                return short.MinValue;
            }

            return (short)Math.Round(value * 32767f);
        }

        return format.IsBigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2));
    }
}
