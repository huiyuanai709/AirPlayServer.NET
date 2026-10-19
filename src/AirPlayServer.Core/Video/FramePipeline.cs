namespace AirPlayServer.Core.Video;

/// <summary>
/// One decoded picture. <see cref="Bgra"/> is tightly packed or uses <see cref="Stride"/>
/// bytes per row, B, G, R, A. The buffer is not mutated after it is published.
/// </summary>
public readonly struct DecodedFrame
{
    public DecodedFrame(int width, int height, ReadOnlyMemory<byte> bgra, int stride)
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
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlyMemory<byte> Bgra { get; }

    public int Stride { get; }
}

/// <summary>
/// Latest-frame slot. Publishers overwrite; readers always observe the newest picture.
/// </summary>
public sealed class FrameSlot
{
    private readonly object _gate = new();
    private byte[]? _pixels;
    private int _width;
    private int _height;
    private int _stride;
    private long _generation;

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
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (stride < width * 4)
        {
            throw new ArgumentException("Stride is shorter than one BGRA row.", nameof(stride));
        }

        var tight = width * 4;
        var copy = new byte[tight * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            if (row + tight > bgra.Length)
            {
                throw new ArgumentException("BGRA buffer is smaller than width, height, and stride.");
            }

            bgra.Slice(row, tight).CopyTo(copy.AsSpan(y * tight));
        }

        lock (_gate)
        {
            _pixels = copy;
            _width = width;
            _height = height;
            _stride = tight;
            return ++_generation;
        }
    }

    public bool TryRead(out int width, out int height, out int stride, out byte[] pixels, out long generation)
    {
        lock (_gate)
        {
            if (_pixels is null)
            {
                width = 0;
                height = 0;
                stride = 0;
                pixels = [];
                generation = 0;
                return false;
            }

            width = _width;
            height = _height;
            stride = _stride;
            pixels = _pixels;
            generation = _generation;
            return true;
        }
    }
}

/// <summary>
/// H.264 access-unit decoder. The mirror pipeline converts AVCC length prefixes to
/// Annex-B before calling <see cref="TryDecode"/>. The first key frame after a new
/// SPS/PPS may be prefixed with those parameter sets. Windows uses Media Foundation.
/// Other platforms keep <see cref="UnavailableH264Decoder"/> unless the process
/// supplies its own decoder.
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
/// Annex-B and may prefix VPS, SPS, and PPS onto the next key frame. Windows uses
/// Media Foundation. Other platforms keep <see cref="UnavailableHevcDecoder"/>.
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
/// Fail-closed HEVC decoder. Used off Windows and when the HEVC Media Foundation
/// transform is not installed. H.264 mirroring is unaffected.
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
