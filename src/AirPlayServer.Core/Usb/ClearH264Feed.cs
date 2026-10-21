using AirPlayServer.Core.Media;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Usb;

/// <summary>
/// Publishes clear H.264 from the USB FEED path. There is no FairPlay unwrap
/// and no AES-CTR. SPS/PPS are configured on the existing decoder and prefixed
/// onto the next key frame when the access unit does not already carry them.
/// </summary>
internal sealed class ClearH264Feed
{
    private readonly FrameSlot _frames;
    private readonly IH264Decoder _decoder;
    private readonly IAirPlayLog _log;
    private byte[]? _sequence;
    private byte[]? _picture;
    private bool _prepend = true;
    private int _logged;

    public ClearH264Feed(FrameSlot frames, IH264Decoder decoder, IAirPlayLog log)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(log);
        _frames = frames;
        _decoder = decoder;
        _log = log;
    }

    public int Published { get; private set; }

    public bool Submit(QtVideoUnit unit)
    {
        if (unit.Sequence is { Length: > 0 } && unit.Picture is { Length: > 0 })
        {
            if (_sequence is null || !unit.Sequence.AsSpan().SequenceEqual(_sequence))
            {
                _sequence = unit.Sequence;
                _picture = unit.Picture;
                _prepend = true;
                _decoder.Configure(_sequence, _picture);
            }
        }

        var presented = unit.AnnexB;
        if (_prepend && unit.KeyFrame && _sequence is not null && _picture is not null && !AvccAnnexB.ContainsNalType(presented, 7))
        {
            _prepend = false;
            presented = Prefix(_sequence, _picture, presented);
        }

        if (!_decoder.TryDecode(presented, unit.KeyFrame, out var frame) || frame.Width == 0)
        {
            return false;
        }

        if (frame.Owned is { } owned)
        {
            _frames.PublishOwned(frame.Width, frame.Height, owned, frame.Stride);
        }
        else
        {
            _frames.Publish(frame.Width, frame.Height, frame.Bgra.Span, frame.Stride);
        }

        Published++;
        if (Interlocked.Exchange(ref _logged, 1) == 0)
        {
            _log.Log(
                AirPlayLogLevel.Information,
                "USB 画面已解码 " + frame.Width + "x" + frame.Height + "。这一路没有 FairPlay。 USB picture decoded "
                + frame.Width + "x" + frame.Height + ". This path has no FairPlay.");
        }

        return true;
    }

    private static byte[] Prefix(byte[] sequence, byte[] picture, ReadOnlySpan<byte> accessUnit)
    {
        var output = new byte[8 + sequence.Length + picture.Length + accessUnit.Length];
        Write(output.AsSpan(0), sequence);
        Write(output.AsSpan(4 + sequence.Length), picture);
        accessUnit.CopyTo(output.AsSpan(8 + sequence.Length + picture.Length));
        return output;
    }

    private static void Write(Span<byte> destination, ReadOnlySpan<byte> nal)
    {
        destination[0] = 0;
        destination[1] = 0;
        destination[2] = 0;
        destination[3] = 1;
        nal.CopyTo(destination[4..]);
    }
}
