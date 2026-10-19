namespace AirPlayServer.Core.Video;

/// <summary>
/// One decoded picture. <see cref="Bgra"/> is tightly packed or uses <see cref="Stride"/>
/// bytes per row, B, G, R, A. The buffer is not mutated after it is published.
/// </summary>
public readonly struct DecodedFrame
{
    private DecodedFrame(int width, int height, ReadOnlyMemory<byte> bgra, int stride, byte[]? owned)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (stride < width * 4 || bgra.Length < stride * height)
        {
            throw new ArgumentException("BGRA buffer is smaller than width, height, and stride.");
        }

        Width = width;
        Height = height;
        Bgra = bgra;
        Stride = stride;
        Owned = owned;
    }

    public DecodedFrame(int width, int height, ReadOnlyMemory<byte> bgra, int stride)
        : this(width, height, bgra, stride, null)
    {
    }

    /// <summary>
    /// A buffer the publisher may keep. The decoder must not write it again.
    /// <see cref="Bgra"/> is the live prefix; the array may be longer when it
    /// was reused from a larger frame.
    /// </summary>
    internal static DecodedFrame Own(int width, int height, byte[] bgra, int stride)
    {
        var bytes = checked(stride * height);
        return new DecodedFrame(width, height, bgra.AsMemory(0, bytes), stride, bgra);
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlyMemory<byte> Bgra { get; }

    public int Stride { get; }

    internal byte[]? Owned { get; }
}

/// <summary>
/// Latest-frame slot. Publishers overwrite; readers always observe the newest picture.
/// There is no queue. Same-size frames reuse pooled BGRA arrays. A leased picture
/// stays immutable until <see cref="Lease.Dispose"/>; the next publish writes a
/// different array, so the window and TapClock can still be reading the previous one.
/// </summary>
public sealed class FrameSlot
{
    private readonly object _gate = new();
    private readonly BgraBufferPool _pool = new();
    private Held? _current;
    private Held? _pin;
    private Held? _stateSpare;
    private int _pinStamp;
    private int _width;
    private int _height;
    private int _stride;
    private long _generation;

    /// <summary>Arrays allocated by <see cref="Buffers"/>. Stable once width and height stop changing.</summary>
    internal int BufferAllocations => _pool.Allocations;

    internal BgraBufferPool Buffers => _pool;

    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public long Publish(int width, int height, ReadOnlySpan<byte> bgra, int stride)
    {
        Validate(width, height, stride, bgra.Length);
        var tight = width * 4;
        var copy = _pool.Rent(tight * height);
        if (!BgraRows.TryPack(bgra, stride, copy, width, height))
        {
            _pool.Return(copy);
            throw new ArgumentException("BGRA buffer is smaller than width, height, and stride.");
        }

        return Install(width, height, copy, tight);
    }

    /// <summary>
    /// Publishes <paramref name="pixels"/> with no BGRA copy when the stride is
    /// already tight. The slot owns the array after this returns.
    /// </summary>
    internal long PublishOwned(int width, int height, byte[] pixels, int stride)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        try
        {
            Validate(width, height, stride, pixels.Length);
        }
        catch
        {
            _pool.Return(pixels);
            throw;
        }

        var tight = width * 4;
        if (stride == tight)
        {
            return Install(width, height, pixels, tight);
        }

        var packed = _pool.Rent(tight * height);
        if (!BgraRows.TryPack(pixels, stride, packed, width, height))
        {
            _pool.Return(packed);
            _pool.Return(pixels);
            throw new ArgumentException("BGRA buffer is smaller than width, height, and stride.");
        }

        _pool.Return(pixels);
        return Install(width, height, packed, tight);
    }

    public bool TryRead(out int width, out int height, out int stride, out byte[] pixels, out long generation)
    {
        lock (_gate)
        {
            if (_current is null)
            {
                width = 0;
                height = 0;
                stride = 0;
                pixels = [];
                generation = 0;
                return false;
            }

            // One pin keeps the last TryRead array stable until the next TryRead.
            // The window and TapClock use TryLease so they do not share this pin.
            if (!ReferenceEquals(_pin, _current))
            {
                ReleasePinLocked();
                _current.Leases++;
                _pin = _current;
                _pinStamp = _current.Stamp;
            }

            width = _width;
            height = _height;
            stride = _stride;
            pixels = _current.Data;
            generation = _generation;
            return true;
        }
    }

    public bool TryReadSize(out int width, out int height)
    {
        lock (_gate)
        {
            if (_current is null)
            {
                width = 0;
                height = 0;
                return false;
            }

            width = _width;
            height = _height;
            return true;
        }
    }

    public bool TryLease(out int width, out int height, out int stride, out byte[] pixels, out long generation, out Lease lease)
    {
        lock (_gate)
        {
            if (_current is null)
            {
                width = 0;
                height = 0;
                stride = 0;
                pixels = [];
                generation = 0;
                lease = default;
                return false;
            }

            _current.Leases++;
            width = _width;
            height = _height;
            stride = _stride;
            pixels = _current.Data;
            generation = _generation;
            lease = new Lease(this, _current, _current.Stamp);
            return true;
        }
    }

    private long Install(int width, int height, byte[] pixels, int stride)
    {
        lock (_gate)
        {
            if (_current is not null && _current.Leases == 0)
            {
                if (ReferenceEquals(_pin, _current))
                {
                    _pin = null;
                }

                var recycled = _current.Data;
                _current.Data = pixels;
                _current.Stamp++;
                if (!ReferenceEquals(recycled, pixels))
                {
                    _pool.Return(recycled);
                }
            }
            else
            {
                var state = _stateSpare ?? new Held();
                _stateSpare = null;
                state.Data = pixels;
                state.Leases = 0;
                state.Stamp++;
                _current = state;
            }

            _width = width;
            _height = height;
            _stride = stride;
            return ++_generation;
        }
    }

    private void Release(Held? buffer, int stamp)
    {
        if (buffer is null)
        {
            return;
        }

        lock (_gate)
        {
            if (buffer.Stamp != stamp || buffer.Leases <= 0)
            {
                return;
            }

            buffer.Leases--;
            if (buffer.Leases == 0 && !ReferenceEquals(buffer, _current))
            {
                Recycle(buffer);
            }
        }
    }

    private void ReleasePinLocked()
    {
        if (_pin is null)
        {
            return;
        }

        var pinned = _pin;
        _pin = null;
        if (pinned.Stamp != _pinStamp || pinned.Leases <= 0)
        {
            return;
        }

        pinned.Leases--;
        if (pinned.Leases == 0 && !ReferenceEquals(pinned, _current))
        {
            Recycle(pinned);
        }
    }

    private void Recycle(Held buffer)
    {
        if (ReferenceEquals(_pin, buffer))
        {
            _pin = null;
        }

        var data = buffer.Data;
        buffer.Data = [];
        buffer.Stamp++;
        if (data.Length != 0)
        {
            _pool.Return(data);
        }

        _stateSpare ??= buffer;
    }

    private static void Validate(int width, int height, int stride, int length)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        var tight = (long)width * 4;
        if (tight > int.MaxValue || stride < tight)
        {
            throw new ArgumentException("Stride is shorter than one BGRA row.", nameof(stride));
        }

        var rows = (long)stride * height;
        var bytes = tight * height;
        if (rows <= 0 || rows > length || bytes <= 0 || bytes > int.MaxValue)
        {
            throw new ArgumentException("BGRA buffer is smaller than width, height, and stride.");
        }
    }

    internal sealed class Held
    {
        public byte[] Data = [];
        public int Leases;
        public int Stamp;
    }

    /// <summary>
    /// Keeps one published array immutable. Dispose before the next await so a
    /// slow send does not hold the picture out of the pool.
    /// </summary>
    public readonly struct Lease : IDisposable
    {
        private readonly FrameSlot? _slot;
        private readonly Held? _held;
        private readonly int _stamp;

        internal Lease(FrameSlot slot, Held held, int stamp)
        {
            _slot = slot;
            _held = held;
            _stamp = stamp;
        }

        public void Dispose() => _slot?.Release(_held, _stamp);
    }
}

/// <summary>
/// H.264 access-unit decoder. The mirror pipeline converts AVCC length prefixes to
/// Annex-B before calling <see cref="TryDecode"/>. The first key frame after a new
/// SPS/PPS may be prefixed with those parameter sets. Windows prefers Media
/// Foundation. FFmpeg is used when its shared libraries load and Media Foundation
/// does not. Otherwise the decoder is <see cref="UnavailableH264Decoder"/>.
/// </summary>
public interface IH264Decoder
{
    bool IsAvailable { get; }

    string Name { get; }

    void Configure(ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet);

    /// <summary>
    /// Drop decoded reference pictures but keep the current parameter sets.
    /// Used when the phone locks, resumes, or replaces the mirror stream.
    /// </summary>
    void Reset();

    bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame);
}

/// <summary>
/// Fail-closed decoder. Access units are counted by the mirror pipeline and are not
/// presented as a phone picture.
/// </summary>
public sealed class UnavailableH264Decoder : IH264Decoder
{
    public bool IsAvailable => false;

    public string Name => "unavailable";

    public void Configure(ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet)
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

/// <summary>
/// HEVC access-unit decoder. The mirror pipeline converts AVCC length prefixes to
/// Annex-B and may prefix VPS, SPS, and PPS onto the next key frame. Windows
/// prefers Media Foundation. FFmpeg is used when those libraries load and Media
/// Foundation HEVC does not. Otherwise the decoder is <see cref="UnavailableHevcDecoder"/>.
/// </summary>
public interface IHevcDecoder
{
    bool IsAvailable { get; }

    string Name { get; }

    void Configure(ReadOnlySpan<byte> videoParameterSet, ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet);

    void Reset();

    bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame);
}

/// <summary>
/// Fail-closed HEVC decoder. Used when neither Media Foundation HEVC nor FFmpeg
/// can be opened. H.264 mirroring is unaffected.
/// </summary>
public sealed class UnavailableHevcDecoder : IHevcDecoder
{
    public bool IsAvailable => false;

    public string Name => "unavailable";

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

/// <summary>
/// Synthetic BGRA picture so the window and TapClock export can be exercised
/// without a paired phone. It is not a mirrored screen.
/// </summary>
public static class DemoFrame
{
    public static long Publish(FrameSlot slot, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(slot);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 245;
            pixels[i + 1] = 245;
            pixels[i + 2] = 245;
            pixels[i + 3] = 255;
        }

        Fill(pixels, width, height, width / 8, height / 10, (width * 3) / 4, Math.Max(8, height / 14), 20, 20, 20);
        Fill(pixels, width, height, width / 8, height / 10, Math.Max(8, width / 14), (height * 7) / 10, 20, 20, 20);
        Fill(pixels, width, height, 0, 0, Math.Min(48, width), Math.Min(48, height), 32, 32, 220);
        return slot.Publish(width, height, pixels, stride);
    }

    private static void Fill(byte[] pixels, int width, int height, int x, int y, int w, int h, byte b, byte g, byte r)
    {
        var x1 = Math.Clamp(x, 0, width);
        var y1 = Math.Clamp(y, 0, height);
        var x2 = Math.Clamp(x + w, 0, width);
        var y2 = Math.Clamp(y + h, 0, height);
        for (var row = y1; row < y2; row++)
        {
            var start = (row * width + x1) * 4;
            for (var col = x1; col < x2; col++)
            {
                pixels[start++] = b;
                pixels[start++] = g;
                pixels[start++] = r;
                pixels[start++] = 255;
            }
        }
    }
}
