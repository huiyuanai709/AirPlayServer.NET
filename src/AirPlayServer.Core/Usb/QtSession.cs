namespace AirPlayServer.Core.Usb;

/// <summary>
/// One phone's QuickTime USB handshake. The device speaks first with PING.
/// SYNC messages are answered with RPLY. ASYN FEED is demuxed to Annex-B and
/// is not FairPlay-encrypted. ASYN EAT! is clear LPCM. A playable buffer is
/// returned on <see cref="QtStep.Pcm"/> for the USB playback queue.
/// </summary>
internal sealed class QtSession
{
    private readonly double _width;
    private readonly double _height;
    private readonly Func<ulong> _nanoseconds;
    private byte[]? _need;
    private ulong _deviceAudioClock;
    private byte[]? _sequence;
    private byte[]? _picture;
    private QtAudioFormat _audioFormat = QtAudioFormat.Default;
    private int _audioRejected;

    public QtSession(double width, double height, Func<ulong>? nanoseconds = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        _width = width;
        _height = height;
        _nanoseconds = nanoseconds ?? (() => (ulong)Math.Max(0, Environment.TickCount64) * 1_000_000UL);
    }

    public ReadOnlyMemory<byte> NeedPacket => _need ?? ReadOnlyMemory<byte>.Empty;

    public int AudioPackets { get; private set; }

    public QtAudioFormat AudioFormat => _audioFormat;

    /// <summary>
    /// Accepts one packet body with the 4-byte length already removed.
    /// Returned writes are complete packets, including their length prefix.
    /// </summary>
    public QtStep Accept(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
        {
            return QtStep.Empty;
        }

        var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (magic == QtMagic.Ping)
        {
            return new QtStep([QtPackets.Ping()], null);
        }

        if (magic == QtMagic.Sync)
        {
            return AcceptSync(body);
        }

        if (magic == QtMagic.Asyn)
        {
            return AcceptAsyn(body);
        }

        return QtStep.Empty;
    }

    public byte[][] Stop()
    {
        if (_deviceAudioClock == 0)
        {
            return [QtPackets.Hpd0()];
        }

        return [QtPackets.Hpa0(_deviceAudioClock), QtPackets.Hpd0()];
    }

    private QtStep AcceptSync(ReadOnlySpan<byte> body)
    {
        if (body.Length < 16)
        {
            return QtStep.Empty;
        }

        var subtype = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body[12..]);
        switch (subtype)
        {
            case QtMagic.Cwpa:
                return AcceptCwpa(body);
            case QtMagic.Cvrp:
                return AcceptCvrp(body);
            case QtMagic.Afmt:
                return AcceptAfmt(body);
            case QtMagic.Clok:
                return AcceptClock(body, QtMagic.Clok, 0x10000);
            case QtMagic.Time:
                return ClocklessReply(body, QtMagic.Time, correlation => QtPackets.TimeReply(correlation, _nanoseconds()));
            case QtMagic.Skew:
                return ClocklessReply(body, QtMagic.Skew, correlation => QtPackets.SkewReply(correlation, 48000d));
            case QtMagic.Og:
                return ClocklessReply(body, QtMagic.Og, correlation => QtPackets.ZeroReply(correlation, 24));
            case QtMagic.Stop:
                return ClocklessReply(body, QtMagic.Stop, correlation => QtPackets.ZeroReply(correlation, 24));
            default:
                return QtStep.Empty;
        }
    }

    private QtStep AcceptCwpa(ReadOnlySpan<byte> body)
    {
        if (!QtPackets.TrySync(body, QtMagic.Cwpa, out _, out var correlation, out var rest) || rest.Length < 8)
        {
            return QtStep.Empty;
        }

        _deviceAudioClock = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(rest);
        var local = _deviceAudioClock + 1000;
        var hpd = QtPackets.Hpd1(_width, _height);
        return new QtStep(
            [hpd, hpd, QtPackets.ClockReply(correlation, local), QtPackets.Hpa1(_deviceAudioClock)],
            null);
    }

    private QtStep AcceptCvrp(ReadOnlySpan<byte> body)
    {
        if (!QtPackets.TrySync(body, QtMagic.Cvrp, out _, out var correlation, out var rest) || rest.Length < 8)
        {
            return QtStep.Empty;
        }

        var deviceClock = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(rest);
        _need = QtPackets.Need(deviceClock);
        if (QtSampleBuffer.TryFindParameterSets(rest[8..], out var sequence, out var picture))
        {
            _sequence = sequence;
            _picture = picture;
        }

        var local = deviceClock + 0x1000AF;
        return new QtStep([_need, QtPackets.ClockReply(correlation, local)], null);
    }

    private QtStep AcceptAfmt(ReadOnlySpan<byte> body)
    {
        if (!QtPackets.TrySync(body, QtMagic.Afmt, out _, out var correlation, out var rest))
        {
            return QtStep.Empty;
        }

        if (QtAudioFormat.TryParse(rest, out var format))
        {
            _audioFormat = format;
        }

        return new QtStep([QtPackets.ErrorReply(correlation)], null);
    }

    private QtStep AcceptClock(ReadOnlySpan<byte> body, uint subtype, ulong delta)
    {
        if (!QtPackets.TrySync(body, subtype, out var clock, out var correlation, out _))
        {
            return QtStep.Empty;
        }

        return new QtStep([QtPackets.ClockReply(correlation, clock + delta)], null);
    }

    private static QtStep ClocklessReply(ReadOnlySpan<byte> body, uint subtype, Func<ulong, byte[]> reply)
    {
        if (!QtPackets.TrySync(body, subtype, out _, out var correlation, out _))
        {
            return QtStep.Empty;
        }

        return new QtStep([reply(correlation)], null);
    }

    private QtStep AcceptAsyn(ReadOnlySpan<byte> body)
    {
        if (!QtPackets.TryAsyn(body, out _, out var subtype, out var rest))
        {
            return QtStep.Empty;
        }

        if (subtype == QtMagic.Feed)
        {
            if (!QtSampleBuffer.TryReadVideo(rest, out var unit))
            {
                return _need is null ? QtStep.Empty : new QtStep([_need], null);
            }

            if (unit.Sequence is null && _sequence is not null)
            {
                unit = new QtVideoUnit(unit.AnnexB, unit.KeyFrame, _sequence, _picture);
            }
            else if (unit.Sequence is not null)
            {
                _sequence = unit.Sequence;
                _picture = unit.Picture;
            }

            return _need is null
                ? new QtStep([], unit)
                : new QtStep([_need], unit);
        }

        if (subtype == QtMagic.Eat)
        {
            AudioPackets++;
            if (QtPcm.TryRead(rest, _audioFormat, out var pcm))
            {
                return new QtStep([], null, pcm);
            }

            var firstReject = Interlocked.Exchange(ref _audioRejected, 1) == 0;
            return firstReject ? new QtStep([], null, unplayableAudio: true) : QtStep.Empty;
        }

        return QtStep.Empty;
    }
}

internal readonly struct QtStep
{
    public static readonly QtStep Empty = new([], null);

    public QtStep(byte[][] writes, QtVideoUnit? video, QtPcmChunk? pcm = null, bool unplayableAudio = false)
    {
        Writes = writes;
        Video = video;
        Pcm = pcm;
        UnplayableAudio = unplayableAudio;
    }

    public byte[][] Writes { get; }

    public QtVideoUnit? Video { get; }

    public QtPcmChunk? Pcm { get; }

    /// <summary>
    /// True once, for the first EAT! packet that is not playable LPCM.
    /// Later rejects stay quiet. Video is unaffected either way.
    /// </summary>
    public bool UnplayableAudio { get; }
}
