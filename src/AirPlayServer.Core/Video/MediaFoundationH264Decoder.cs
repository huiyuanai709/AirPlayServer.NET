#if WINDOWS_HOST
using System.Runtime.InteropServices;

namespace AirPlayServer.Core.Video;

/// <summary>
/// Windows H.264 decoder. Synchronous hardware Media Foundation transforms are
/// preferred when they still expose a lockable NV12, RGB32, or ARGB32 buffer.
/// A hardware transform that accepts input and then stops producing pictures is
/// released once and replaced by the Microsoft software decoder
/// (CLSID_CMSH264DecoderMFT). Draining the hardware transform to force the
/// first picture is not used: that flush drops reference frames and the DXGI
/// worker threads keep spinning while the window stays on the last picture.
/// The software decoder's documented outputs are NV12, YUY2, and planar 4:2:0, not RGB.
/// RGB32/ARGB32 are taken when a transform lists them, and the output type is
/// stamped with the resolved matrix and range so Media Foundation converts.
/// NV12 is converted here: SPS VUI, then the media-type attributes, then
/// full-range BT.709. Output is BGRA for the window and TapClock path. This
/// file is compiled only into a Windows host; it does not reference FFmpeg.
/// </summary>
internal sealed unsafe class MediaFoundationH264Decoder : IH264Decoder, IBgraReuseTarget, IDisposable
{
    private const uint MfVersion = 0x00020070;
    private const uint MfStartupLite = 1;
    private const uint ClsctxInprocServer = 1;
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private const int NotAccepting = unchecked((int)0xC00D36B5);
    private const int NoMoreTypes = unchecked((int)0xC00D36B9);
    private const int RpcChangedMode = unchecked((int)0x80010106);
    private const uint OutputProvidesSamples = 0x100;
    private const uint OutputCanProvideSamples = 0x200;
    private const int MessageDrain = 1;
    private const int MessageBeginStreaming = 0x10000000;
    private const int MessageStartOfStream = 0x10000003;

    private static readonly Guid Video = Guid.Parse("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid H264 = Guid.Parse("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid Nv12 = Guid.Parse("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid Rgb32 = Guid.Parse("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid Argb32 = Guid.Parse("00000015-0000-0010-8000-00aa00389b71");
    private static readonly Guid YuvMatrixAttribute = Guid.Parse("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid NominalRangeAttribute = Guid.Parse("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid MajorType = Guid.Parse("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = Guid.Parse("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSize = Guid.Parse("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = Guid.Parse("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid PixelAspect = Guid.Parse("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid InterlaceMode = Guid.Parse("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid SequenceHeader = Guid.Parse("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    private static readonly Guid DefaultStride = Guid.Parse("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid CleanPoint = Guid.Parse("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    private static readonly Guid TransformAsync = Guid.Parse("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    private static readonly Guid LowLatency = Guid.Parse("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid D3DAware = Guid.Parse("eaa35c29-775e-488e-9b61-b3283e49583b");
    private static readonly Guid VideoDecoderCategory = Guid.Parse("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private static readonly Guid SoftwareDecoder = Guid.Parse("62ce7e72-4c71-4d20-b15d-452831a87d9d");
    private static readonly Guid TransformInterface = Guid.Parse("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid Buffer2D = Guid.Parse("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");

    private static readonly object PlatformGate = new();
    private static int _mfState;
    [ThreadStatic]
    private static int _comReady;

    private readonly IAirPlayLog _log;
    private readonly BgraBufferPool _buffers;
    private readonly object _gate = new();
    private BgraReuseSlot _reuse;
    private byte[]? _sps;
    private byte[]? _pps;
    private nint _transform;
    private volatile bool _hardware;
    private bool _rejectHardware;
    private bool _typesReady;
    private bool _rgb32;
    private int _codedWidth;
    private int _codedHeight;
    private int _visibleWidth;
    private int _visibleHeight;
    private int _spsCodedWidth;
    private int _spsCodedHeight;
    private int _stride;
    private bool _bottomUp;
    private H264ColorSignals _spsColor;
    private YuvColorSpace _color = YuvColorSpace.AirPlayMirror;
    private long _sampleTime;
    private int _probed;
    private int _disposed;
    private int _pictures;
    private int _consecutiveMisses;
    private byte[]? _lastKeyframe;
    private byte[]? _contiguous;
    private const int MaxKeyframeBytes = 4 * 1024 * 1024;

    public MediaFoundationH264Decoder(IAirPlayLog? log, BgraBufferPool? buffers = null)
    {
        _log = log ?? NullAirPlayLog.Instance;
        _buffers = buffers ?? new BgraBufferPool();
    }

    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            lock (_gate)
            {
                if (_disposed != 0)
                {
                    return false;
                }

                if (_probed == 0)
                {
                    try
                    {
                        _probed = ProbeSoftwareDecoder() ? 1 : 2;
                    }
                    catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                    {
                        _probed = 2;
                    }
                }

                return _probed == 1;
            }
        }
    }

    public string Name => _hardware ? "media-foundation-hardware" : "media-foundation";

    internal bool HasNegotiatedOutput
    {
        get
        {
            lock (_gate)
            {
                return _typesReady;
            }
        }
    }

    public void Configure(ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet)
    {
        if (!OperatingSystem.IsWindows() || sequenceParameterSet.IsEmpty || pictureParameterSet.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }

            if (_sps is not null
                && _pps is not null
                && _transform != 0
                && _sps.AsSpan().SequenceEqual(sequenceParameterSet)
                && _pps.AsSpan().SequenceEqual(pictureParameterSet))
            {
                // A periodic SPS/PPS with the same bytes must not rebuild a
                // transform that is already producing pictures.
                return;
            }

            _sps = sequenceParameterSet.ToArray();
            _pps = pictureParameterSet.ToArray();
            _lastKeyframe = null;
            _visibleWidth = 0;
            _visibleHeight = 0;
            _spsCodedWidth = 0;
            _spsCodedHeight = 0;
            _spsColor = default;
            if (H264Sequence.TryGetPictureSize(_sps, out var width, out var height, out var codedWidth, out var codedHeight, out var color))
            {
                _visibleWidth = width;
                _visibleHeight = height;
                _spsCodedWidth = codedWidth;
                _spsCodedHeight = codedHeight;
                _spsColor = color;
            }

            if (_transform != 0)
            {
                // SetInputType again on the locked transform stays silent.
                // Drop it; the next access unit builds one with these parameter sets.
                ReleaseTransform();
            }
        }
    }

    public void OfferReuse(byte[] buffer, int width, int height, int stride)
        => _reuse.Offer(buffer, width, height, stride);

    public void ClearReuse() => _reuse.Clear();

    public void Reset()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }

            // Flush plus BeginStreaming on the same Media Foundation object
            // produced no pictures after an iPhone lock, so the window kept
            // the last frame. Releasing the transform keeps the stored SPS/PPS.
            // The next access unit creates a new decoder and can show the new IDR.
            ReleaseTransform();
        }
    }

    public bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame)
    {
        frame = default;
        if (!OperatingSystem.IsWindows() || annexB.Length < 4 || annexB.Length > 8 * 1024 * 1024)
        {
            return false;
        }

        lock (_gate)
        {
            if (_disposed != 0 || _sps is null || _pps is null)
            {
                return false;
            }

            try
            {
                if (keyFrame && annexB.Length <= MaxKeyframeBytes)
                {
                    _lastKeyframe = annexB.ToArray();
                }

                return DecodeLocked(annexB, keyFrame, out frame, allowHardwareFallback: true);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _log.Log(AirPlayLogLevel.Warning, "Media Foundation is not available (" + ex.GetType().Name + ").");
                _probed = 2;
                frame = default;
                return false;
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

            ReleaseTransform();
        }
    }

    private bool DecodeLocked(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame, bool allowHardwareFallback, bool allowDrain = true)
    {
        frame = default;
        if (!EnsureTransform() || !_typesReady && !ApplyTypes())
        {
            return false;
        }

        if (!CreateInputSample(annexB, keyFrame, out var sample))
        {
            return false;
        }

        try
        {
            var hr = Slots.ProcessInput(_transform, sample);
            if (hr == NotAccepting)
            {
                Pull(ref frame);
                hr = Slots.ProcessInput(_transform, sample);
            }

            if (hr < 0)
            {
                if (frame.Width != 0)
                {
                    NotePicture();
                    return true;
                }

                if (_hardware && allowHardwareFallback)
                {
                    return AbandonHardware(
                        annexB,
                        keyFrame,
                        "硬件 H.264 拒绝了输入（0x" + hr.ToString("X8") + "），已释放该变换并改用软件解码器，避免驱动线程空转。Hardware H.264 rejected input (0x" + hr.ToString("X8") + "). Released it and switched to the software decoder.",
                        out frame);
                }

                return false;
            }
        }
        finally
        {
            Slots.Release(sample);
        }

        var produced = Pull(ref frame);
        if (allowDrain && produced != PullResult.Unsupported && frame.Width == 0 && keyFrame && _pictures == 0 && !_hardware)
        {
            // A single IDR can sit inside the software transform until it is told
            // the stream paused. Hardware transforms are not drained: the flush
            // drops reference pictures and later access units never paint.
            Slots.ProcessMessage(_transform, MessageDrain, 0);
            produced = Pull(ref frame);
            Slots.ProcessMessage(_transform, MessageStartOfStream, 0);
        }

        if (produced == PullResult.Unsupported && _hardware && allowHardwareFallback)
        {
            return AbandonHardware(
                annexB,
                keyFrame,
                "硬件 H.264 的输出缓冲无法锁定，已改用软件解码器。Hardware H.264 output could not be locked. Switched to the software decoder.",
                out frame);
        }

        if (frame.Width != 0)
        {
            NotePicture();
            return true;
        }

        if (_hardware && allowHardwareFallback)
        {
            _consecutiveMisses++;
            if (HardwareDecodeStall.ShouldAbandon(_pictures, _consecutiveMisses))
            {
                return AbandonHardware(
                    annexB,
                    keyFrame,
                    "硬件 H.264 连续没有画面（已出画 " + _pictures + "，连续空帧 " + _consecutiveMisses + "），已释放硬件变换并改用软件解码器，避免驱动线程空转。Hardware H.264 produced no picture (pictures=" + _pictures + ", consecutive misses=" + _consecutiveMisses + "). Released the hardware transform and switched to the software decoder.",
                    out frame);
            }
        }

        return false;
    }

    private bool AbandonHardware(ReadOnlySpan<byte> annexB, bool keyFrame, string reason, out DecodedFrame frame)
    {
        frame = default;
        var replay = _lastKeyframe;
        var replayDifferent = replay is not null && (replay.Length != annexB.Length || !annexB.SequenceEqual(replay));
        _rejectHardware = true;
        ReleaseTransform();
        _log.Log(AirPlayLogLevel.Warning, reason);
        DecodedFrame latest = default;
        if (replayDifferent && replay is not null)
        {
            // Keep the IDR in the new decoder. Draining between the two units
            // would flush the reference the following slice still needs.
            if (DecodeLocked(replay, true, out var keyed, allowHardwareFallback: false, allowDrain: false) && keyed.Width != 0)
            {
                latest = keyed;
            }
        }

        var drainSingleKeyframe = !replayDifferent;
        if (DecodeLocked(annexB, keyFrame, out var current, allowHardwareFallback: false, allowDrain: drainSingleKeyframe) && current.Width != 0)
        {
            latest = current;
        }

        frame = latest;
        return latest.Width != 0;
    }

    private void NotePicture()
    {
        _pictures++;
        _consecutiveMisses = 0;
    }

    private PullResult Pull(ref DecodedFrame frame)
    {
        for (var i = 0; i < 4; i++)
        {
            if (!PrepareOutput(out var output, out var owned))
            {
                return frame.Width != 0 ? PullResult.Frame : PullResult.None;
            }

            uint status;
            var hr = Slots.ProcessOutput(_transform, &output, out status);
            if (hr == NeedMoreInput)
            {
                if (owned != 0)
                {
                    Slots.Release(owned);
                }

                return frame.Width != 0 ? PullResult.Frame : PullResult.None;
            }

            if (hr == StreamChange || (status & 0x100) != 0)
            {
                ReleaseOutput(ref output, owned);
                if (!SelectOutput())
                {
                    return PullResult.None;
                }

                continue;
            }

            if (hr < 0 || output.Sample == 0)
            {
                ReleaseOutput(ref output, owned);
                return frame.Width != 0 ? PullResult.Frame : PullResult.None;
            }

            var copied = TryCopySample(output.Sample, out var decoded);
            ReleaseOutput(ref output, owned);
            if (copied == PullResult.Unsupported)
            {
                return PullResult.Unsupported;
            }

            if (copied == PullResult.Frame)
            {
                frame = decoded;
            }
        }

        return frame.Width != 0 ? PullResult.Frame : PullResult.None;
    }

    private bool PrepareOutput(out Slots.OutputBuffer output, out nint ownedSample)
    {
        output = default;
        ownedSample = 0;
        if (Slots.GetOutputStreamInfo(_transform, out var info) < 0)
        {
            return false;
        }

        var provides = (info.Flags & (OutputProvidesSamples | OutputCanProvideSamples)) != 0;
        if (provides)
        {
            return true;
        }

        var size = info.Size;
        if (size == 0)
        {
            var width = Math.Max(_codedWidth, 16);
            var height = Math.Max(_codedHeight, 16);
            size = (uint)(width * height * 4);
        }

        if (Native.CreateBuffer(size, info.Alignment, out var buffer) < 0)
        {
            return false;
        }

        if (Native.MFCreateSample(out var sample) < 0)
        {
            Slots.Release(buffer);
            return false;
        }

        if (Slots.AddBuffer(sample, buffer) < 0)
        {
            Slots.Release(buffer);
            Slots.Release(sample);
            return false;
        }

        Slots.Release(buffer);
        output.Sample = sample;
        ownedSample = sample;
        return true;
    }

    private static void ReleaseOutput(ref Slots.OutputBuffer output, nint ownedSample)
    {
        if (output.Sample != 0)
        {
            Slots.Release(output.Sample);
        }

        if (ownedSample != 0 && ownedSample != output.Sample)
        {
            Slots.Release(ownedSample);
        }

        output.Sample = 0;

        if (output.Events != 0)
        {
            Slots.Release(output.Events);
            output.Events = 0;
        }
    }

    private PullResult TryCopySample(nint sample, out DecodedFrame frame)
    {
        frame = default;
        if (Slots.ConvertToContiguous(sample, out var buffer) < 0 || buffer == 0)
        {
            return PullResult.None;
        }

        try
        {
            if (Slots.Lock(buffer, out var data, out var length) >= 0 && data != null && length > 0)
            {
                try
                {
                    return CopyPlanes(new ReadOnlySpan<byte>(data, checked((int)length)), out frame)
                        ? PullResult.Frame
                        : PullResult.None;
                }
                finally
                {
                    Slots.Unlock(buffer);
                }
            }

            if (Slots.QueryInterface(buffer, Buffer2D, out var twoD) >= 0 && twoD != 0)
            {
                try
                {
                    if (Slots.GetContiguousLength(twoD, out var bytes) < 0 || bytes == 0 || bytes > 64 * 1024 * 1024)
                    {
                        return PullResult.Unsupported;
                    }

                    if (_contiguous is null || _contiguous.Length < (int)bytes)
                    {
                        _contiguous = new byte[bytes];
                    }

                    var copy = _contiguous;
                    fixed (byte* destination = copy)
                    {
                        if (Slots.ContiguousCopyTo(twoD, destination, bytes) < 0)
                        {
                            return PullResult.Unsupported;
                        }
                    }

                    return CopyPlanes(copy, out frame) ? PullResult.Frame : PullResult.None;
                }
                finally
                {
                    Slots.Release(twoD);
                }
            }

            return _hardware ? PullResult.Unsupported : PullResult.None;
        }
        finally
        {
            Slots.Release(buffer);
        }
    }

    private bool CopyPlanes(ReadOnlySpan<byte> pixels, out DecodedFrame frame)
    {
        frame = default;
        var codedWidth = _codedWidth;
        var codedHeight = _codedHeight;
        if (codedWidth <= 0 || codedHeight <= 0)
        {
            return false;
        }

        var visibleWidth = _visibleWidth > 0 && codedWidth >= _visibleWidth && codedWidth - _visibleWidth <= 16
            ? _visibleWidth
            : codedWidth;
        var visibleHeight = _visibleHeight > 0 && codedHeight >= _visibleHeight && codedHeight - _visibleHeight <= 16
            ? _visibleHeight
            : codedHeight;
        var byteCount = visibleWidth * visibleHeight * 4;
        var reused = _reuse.TryTake(visibleWidth, visibleHeight, byteCount, out var tight);
        if (!reused)
        {
            tight = _buffers.Rent(byteCount);
        }

        var wrote = false;
        if (_rgb32)
        {
            var stride = _stride == 0 ? codedWidth * 4 : _stride;
            wrote = BgraRows.TryCopyRgb32(pixels, codedWidth, codedHeight, stride, _bottomUp, visibleWidth, visibleHeight, tight.AsSpan(0, byteCount));
        }
        else
        {
            var stride = _stride == 0 ? InferNv12Stride(pixels.Length, codedWidth, codedHeight) : _stride;
            wrote = Nv12Color.TryCopy(pixels, codedWidth, codedHeight, stride, visibleWidth, visibleHeight, _color, tight.AsSpan(0, byteCount));
        }

        if (!wrote)
        {
            if (!reused)
            {
                _buffers.Return(tight);
            }

            return false;
        }

        frame = reused
            ? new DecodedFrame(visibleWidth, visibleHeight, tight.AsMemory(0, byteCount), visibleWidth * 4)
            : DecodedFrame.Own(visibleWidth, visibleHeight, tight, visibleWidth * 4);
        return true;
    }

    private static int InferNv12Stride(int bufferLength, int codedWidth, int codedHeight)
    {
        if (codedWidth <= 0 || codedHeight <= 0)
        {
            return Math.Max(codedWidth, 0);
        }

        var rows = codedHeight + (codedHeight / 2);
        if (rows <= 0 || bufferLength < codedWidth * rows)
        {
            return codedWidth;
        }

        var stride = bufferLength / rows;
        if ((stride & 1) != 0)
        {
            stride--;
        }

        return stride >= codedWidth ? stride : codedWidth;
    }

    private bool CreateInputSample(ReadOnlySpan<byte> annexB, bool keyFrame, out nint sample)
    {
        sample = 0;
        if (Native.MFCreateMemoryBuffer((uint)annexB.Length, out var buffer) < 0)
        {
            return false;
        }

        try
        {
            if (Slots.Lock(buffer, out var data, out _) < 0 || data == null)
            {
                return false;
            }

            try
            {
                annexB.CopyTo(new Span<byte>(data, annexB.Length));
            }
            finally
            {
                Slots.Unlock(buffer);
            }

            if (Slots.SetCurrentLength(buffer, (uint)annexB.Length) < 0 || Native.MFCreateSample(out sample) < 0)
            {
                return false;
            }

            if (Slots.AddBuffer(sample, buffer) < 0)
            {
                Slots.Release(sample);
                sample = 0;
                return false;
            }

            _sampleTime += 333333;
            Slots.SetSampleTime(sample, _sampleTime);
            Slots.SetSampleDuration(sample, 333333);
            if (keyFrame)
            {
                Slots.SetUInt32(sample, CleanPoint, 1);
            }

            return true;
        }
        finally
        {
            Slots.Release(buffer);
        }
    }

    private bool EnsureTransform()
    {
        if (_transform != 0)
        {
            return true;
        }

        if (!EnsurePlatform())
        {
            return false;
        }

        if (!_rejectHardware && TryCreateHardware(out var hardware))
        {
            _transform = hardware;
            _hardware = true;
            if (ApplyTypes())
            {
                _log.Log(AirPlayLogLevel.Information, "H.264 decoder is a synchronous Media Foundation hardware transform.");
                return true;
            }

            _log.Log(AirPlayLogLevel.Information, "Hardware H.264 transform did not accept a system-memory output. Using the software decoder.");
            ReleaseTransform();
            _hardware = false;
        }

        if (Native.CoCreate(SoftwareDecoder, TransformInterface, out var software) < 0)
        {
            _log.Log(AirPlayLogLevel.Warning, "Media Foundation H.264 decoder could not be created.");
            _probed = 2;
            return false;
        }

        _transform = software;
        _hardware = false;
        IgnoreLowLatency(software);
        if (!ApplyTypes())
        {
            _log.Log(AirPlayLogLevel.Warning, "Media Foundation rejected the H.264 media type.");
            ReleaseTransform();
            return false;
        }

        _log.Log(AirPlayLogLevel.Information, "H.264 decoder is the Microsoft Media Foundation software decoder.");
        return true;
    }

    private bool TryCreateHardware(out nint transform)
    {
        transform = 0;
        var info = new MediaFoundationNative.RegisterInfo { Major = Video, Subtype = H264 };
        var category = VideoDecoderCategory;
        var hr = Native.MFTEnumEx(category, 0x1 | 0x4 | 0x10 | 0x40, &info, null, out var list, out var count);
        if (hr < 0 || list == 0 || count == 0)
        {
            if (list != 0)
            {
                Native.CoTaskMemFree(list);
            }

            return false;
        }

        nint chosen = 0;
        try
        {
            var available = (int)Math.Min(count, 8u);
            for (var i = 0; i < available && chosen == 0; i++)
            {
                var activate = Marshal.ReadIntPtr(list, i * nint.Size);
                if (activate == 0 || Slots.Activate(activate, TransformInterface, out var created) < 0 || created == 0)
                {
                    continue;
                }

                if (IsAsync(created))
                {
                    Slots.Release(created);
                    continue;
                }

                IgnoreLowLatency(created);
                PreferSystemMemory(created);
                chosen = created;
            }
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                var activate = Marshal.ReadIntPtr(list, (int)i * nint.Size);
                if (activate != 0)
                {
                    Slots.Release(activate);
                }
            }

            Native.CoTaskMemFree(list);
        }

        transform = chosen;
        return chosen != 0;
    }

    private static bool IsAsync(nint transform)
    {
        if (Slots.GetAttributes(transform, out var attributes) < 0 || attributes == 0)
        {
            return false;
        }

        try
        {
            return Slots.GetUInt32(attributes, TransformAsync, out var value) >= 0 && value != 0;
        }
        finally
        {
            Slots.Release(attributes);
        }
    }

    private static void IgnoreLowLatency(nint transform)
    {
        if (Slots.GetAttributes(transform, out var attributes) < 0 || attributes == 0)
        {
            return;
        }

        try
        {
            Slots.SetUInt32(attributes, LowLatency, 1);
        }
        finally
        {
            Slots.Release(attributes);
        }
    }

    private static void PreferSystemMemory(nint transform)
    {
        // MF_SA_D3D_AWARE = 0 asks for a lockable system-memory buffer. A DXGI
        // surface path is what stops returning pictures after the first frame.
        if (Slots.GetAttributes(transform, out var attributes) < 0 || attributes == 0)
        {
            return;
        }

        try
        {
            Slots.SetUInt32(attributes, D3DAware, 0);
        }
        finally
        {
            Slots.Release(attributes);
        }
    }

    private bool ApplyTypes()
    {
        _typesReady = false;
        if (_sps is null || _pps is null || _transform == 0)
        {
            return false;
        }

        if (!CreateInputType(out var input))
        {
            return false;
        }

        try
        {
            var hr = Slots.SetInputType(_transform, input);
            if (hr < 0)
            {
                _log.Log(AirPlayLogLevel.Warning, "Media Foundation SetInputType failed: 0x" + hr.ToString("X8") + ".");
                return false;
            }
        }
        finally
        {
            Slots.Release(input);
        }

        if (!SelectOutput())
        {
            return false;
        }

        Slots.ProcessMessage(_transform, MessageBeginStreaming, 0);
        Slots.ProcessMessage(_transform, MessageStartOfStream, 0);
        _typesReady = true;
        return true;
    }

    private bool CreateInputType(out nint type)
    {
        type = 0;
        if (Native.MFCreateMediaType(out type) < 0)
        {
            return false;
        }

        var width = _spsCodedWidth > 0 ? _spsCodedWidth : _visibleWidth > 0 ? _visibleWidth : 1280;
        var height = _spsCodedHeight > 0 ? _spsCodedHeight : _visibleHeight > 0 ? _visibleHeight : 720;
        if (Slots.SetGuid(type, MajorType, Video) < 0
            || Slots.SetGuid(type, Subtype, H264) < 0
            || Slots.SetUInt64(type, FrameSize, ((ulong)(uint)width << 32) | (uint)height) < 0
            || Slots.SetUInt64(type, FrameRate, ((ulong)30 << 32) | 1) < 0
            || Slots.SetUInt64(type, PixelAspect, ((ulong)1 << 32) | 1) < 0
            || Slots.SetUInt32(type, InterlaceMode, 2) < 0)
        {
            Slots.Release(type);
            type = 0;
            return false;
        }

        var header = new byte[8 + _sps!.Length + _pps!.Length];
        if (H264Sequence.WriteAnnexBParameterSets(_sps, _pps, header) < 0
            || Slots.SetBlob(type, SequenceHeader, header) < 0)
        {
            Slots.Release(type);
            type = 0;
            return false;
        }

        return true;
    }

    private bool SelectOutput()
    {
        nint chosen = 0;
        var chosenRgb = false;
        for (uint index = 0; index < 16; index++)
        {
            if (Slots.GetOutputAvailableType(_transform, index, out var candidate) == NoMoreTypes || candidate == 0)
            {
                break;
            }

            if (Slots.GetGuid(candidate, Subtype, out var subtype) < 0)
            {
                Slots.Release(candidate);
                continue;
            }

            var rgb = subtype == Rgb32 || subtype == Argb32;
            var nv12 = subtype == Nv12;
            if (!rgb && !nv12)
            {
                Slots.Release(candidate);
                continue;
            }

            if (chosen == 0 || (rgb && !chosenRgb))
            {
                if (chosen != 0)
                {
                    Slots.Release(chosen);
                }

                chosen = candidate;
                chosenRgb = rgb;
                if (rgb)
                {
                    break;
                }

                continue;
            }

            Slots.Release(candidate);
        }

        if (chosen == 0 && Native.MFCreateMediaType(out chosen) >= 0)
        {
            chosenRgb = false;
            if (Slots.SetGuid(chosen, MajorType, Video) < 0 || Slots.SetGuid(chosen, Subtype, Nv12) < 0)
            {
                Slots.Release(chosen);
                return false;
            }
        }

        if (chosen == 0)
        {
            return false;
        }

        try
        {
            var width = _spsCodedWidth > 0 ? _spsCodedWidth : _visibleWidth > 0 ? _visibleWidth : _codedWidth > 0 ? _codedWidth : 1280;
            var height = _spsCodedHeight > 0 ? _spsCodedHeight : _visibleHeight > 0 ? _visibleHeight : _codedHeight > 0 ? _codedHeight : 720;
            Slots.SetGuid(chosen, MajorType, Video);
            Slots.SetUInt64(chosen, FrameSize, ((ulong)(uint)width << 32) | (uint)height);
            Slots.SetUInt64(chosen, FrameRate, ((ulong)30 << 32) | 1);
            Slots.SetUInt64(chosen, PixelAspect, ((ulong)1 << 32) | 1);
            Slots.SetUInt32(chosen, InterlaceMode, 2);
            ReadColorAttributes(chosen, out var mfMatrix, out var mfRange);
            var color = YuvColorSpace.Resolve(_spsColor, mfMatrix, mfRange);
            var stamped = false;
            if (chosenRgb)
            {
                // The transform does the YUV conversion. Stamp the matrix and range
                // it should use. A type that rejects the attributes is retried bare.
                StampColorAttributes(chosen, color);
                stamped = true;
            }

            var hr = Slots.SetOutputType(_transform, chosen);
            if (hr < 0 && stamped)
            {
                Slots.DeleteItem(chosen, YuvMatrixAttribute);
                Slots.DeleteItem(chosen, NominalRangeAttribute);
                hr = Slots.SetOutputType(_transform, chosen);
            }

            if (hr < 0)
            {
                _log.Log(AirPlayLogLevel.Warning, "Media Foundation SetOutputType failed: 0x" + hr.ToString("X8") + ".");
                return false;
            }

            _rgb32 = chosenRgb;
            _codedWidth = width;
            _codedHeight = height;
            _stride = 0;
            _bottomUp = false;
            _color = color;
            if (Slots.GetOutputCurrentType(_transform, out var current) >= 0 && current != 0)
            {
                ReadOutputFormat(current);
                Slots.Release(current);
            }
            else if (!chosenRgb)
            {
                LogNv12Color(mfMatrix, mfRange);
            }

            return true;
        }
        finally
        {
            Slots.Release(chosen);
        }
    }

    private void ReadOutputFormat(nint type)
    {
        if (Slots.GetGuid(type, Subtype, out var subtype) >= 0)
        {
            _rgb32 = subtype == Rgb32 || subtype == Argb32;
        }

        if (Slots.GetUInt64(type, FrameSize, out var size) >= 0 && size != 0)
        {
            _codedWidth = (int)(size >> 32);
            _codedHeight = (int)(size & 0xFFFFFFFF);
        }

        if (Slots.GetUInt32(type, DefaultStride, out var raw) >= 0 && raw != 0)
        {
            var stride = unchecked((int)raw);
            _bottomUp = stride < 0;
            _stride = Math.Abs(stride);
        }

        ReadColorAttributes(type, out var mfMatrix, out var mfRange);
        _color = YuvColorSpace.Resolve(_spsColor, mfMatrix, mfRange);
        if (!_rgb32)
        {
            LogNv12Color(mfMatrix, mfRange);
        }
    }

    private void LogNv12Color(int? mediaFoundationMatrix, int? mediaFoundationNominalRange)
    {
        _log.Log(
            AirPlayLogLevel.Information,
            "NV12 to BGRA is " + YuvColorSpace.DescribeChoice(_color, _spsColor, mediaFoundationMatrix, mediaFoundationNominalRange) + ".");
    }

    private static void ReadColorAttributes(nint type, out int? matrix, out int? range)
    {
        matrix = null;
        range = null;
        if (Slots.GetUInt32(type, YuvMatrixAttribute, out var matrixValue) >= 0)
        {
            matrix = (int)matrixValue;
        }

        if (Slots.GetUInt32(type, NominalRangeAttribute, out var rangeValue) >= 0)
        {
            range = (int)rangeValue;
        }
    }

    private static void StampColorAttributes(nint type, YuvColorSpace color)
    {
        Slots.SetUInt32(type, YuvMatrixAttribute, color.Matrix == YuvMatrix.Bt709 ? 1u : 2u);
        // MFNominalRange_0_255 is 1 (full). MFNominalRange_16_235 is 2 (limited).
        Slots.SetUInt32(type, NominalRangeAttribute, color.Range == YuvRange.Full ? 1u : 2u);
    }

    private bool ProbeSoftwareDecoder()
    {
        if (!EnsurePlatform())
        {
            return false;
        }

        if (Native.CoCreate(SoftwareDecoder, TransformInterface, out var created) < 0 || created == 0)
        {
            return false;
        }

        Slots.Release(created);
        return true;
    }

    private bool EnsurePlatform()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        EnsureCom();
        lock (PlatformGate)
        {
            if (_mfState == 1)
            {
                return true;
            }

            if (_mfState == 2)
            {
                return false;
            }

            var hr = Native.MFStartup(MfVersion, MfStartupLite);
            if (hr < 0)
            {
                _mfState = 2;
                _log.Log(AirPlayLogLevel.Warning, "MFStartup failed: 0x" + hr.ToString("X8") + ".");
                return false;
            }

            _mfState = 1;
            return true;
        }
    }

    private static void EnsureCom()
    {
        if (_comReady != 0)
        {
            return;
        }

        var hr = Native.CoInitializeEx(0, 0);
        if (hr >= 0 || hr == RpcChangedMode)
        {
            _comReady = 1;
        }
    }

    private void ReleaseTransform()
    {
        if (_transform != 0)
        {
            Slots.Release(_transform);
            _transform = 0;
        }

        _typesReady = false;
        _hardware = false;
        _pictures = 0;
        _consecutiveMisses = 0;
    }

    private enum PullResult
    {
        None = 0,
        Frame = 1,
        Unsupported = 2,
    }

    private static class Native
    {
        public static int CoCreate(Guid clsid, Guid iid, out nint instance)
            => MediaFoundationNative.CoCreate(clsid, iid, out instance);

        public static int CreateBuffer(uint size, uint alignment, out nint buffer)
            => MediaFoundationNative.CreateBuffer(size, alignment, out buffer);

        public static int CoInitializeEx(nint reserved, uint coinit)
            => MediaFoundationNative.CoInitializeEx(reserved, coinit);

        public static void CoTaskMemFree(nint pointer) => MediaFoundationNative.CoTaskMemFree(pointer);

        public static int MFStartup(uint version, uint flags) => MediaFoundationNative.MFStartup(version, flags);

        public static int MFCreateMediaType(out nint type) => MediaFoundationNative.MFCreateMediaType(out type);

        public static int MFCreateSample(out nint sample) => MediaFoundationNative.MFCreateSample(out sample);

        public static int MFCreateMemoryBuffer(uint maxLength, out nint buffer)
            => MediaFoundationNative.MFCreateMemoryBuffer(maxLength, out buffer);

        public static unsafe int MFTEnumEx(in Guid category, uint flags, MediaFoundationNative.RegisterInfo* inputType, MediaFoundationNative.RegisterInfo* outputType, out nint activates, out uint count)
            => MediaFoundationNative.MFTEnumEx(category, flags, inputType, outputType, out activates, out count);
    }

    private static unsafe class Slots
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct StreamInfo
        {
            public uint Flags;
            public uint Size;
            public uint Alignment;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OutputBuffer
        {
            public uint StreamId;
            public nint Sample;
            public uint Status;
            public nint Events;
        }

        public static int Release(nint self)
        {
            if (self == 0)
            {
                return 0;
            }

            return ((delegate* unmanaged[Stdcall]<nint, int>)Function(self, 2))(self);
        }

        public static int QueryInterface(nint self, Guid iid, out nint result)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Function(self, 0);
            var local = iid;
            nint value;
            var hr = fn(self, &local, &value);
            result = hr >= 0 ? value : 0;
            return hr;
        }

        public static int GetUInt32(nint self, Guid key, out uint value)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Function(self, 7);
            var local = key;
            uint read;
            var hr = fn(self, &local, &read);
            value = read;
            return hr;
        }

        public static int GetUInt64(nint self, Guid key, out ulong value)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Function(self, 8);
            var local = key;
            ulong read;
            var hr = fn(self, &local, &read);
            value = read;
            return hr;
        }

        public static int GetGuid(nint self, Guid key, out Guid value)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Function(self, 10);
            var local = key;
            Guid read;
            var hr = fn(self, &local, &read);
            value = read;
            return hr;
        }

        public static int DeleteItem(nint self, Guid key)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, int>)Function(self, 19);
            var local = key;
            return fn(self, &local);
        }

        public static int SetUInt32(nint self, Guid key, uint value)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Function(self, 21);
            var local = key;
            return fn(self, &local, value);
        }

        public static int SetUInt64(nint self, Guid key, ulong value)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Function(self, 22);
            var local = key;
            return fn(self, &local, value);
        }

        public static int SetGuid(nint self, Guid key, Guid value)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Function(self, 24);
            var localKey = key;
            var localValue = value;
            return fn(self, &localKey, &localValue);
        }

        public static int SetBlob(nint self, Guid key, ReadOnlySpan<byte> blob)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, byte*, uint, int>)Function(self, 26);
            var local = key;
            fixed (byte* pointer = blob)
            {
                return fn(self, &local, pointer, (uint)blob.Length);
            }
        }

        public static int Lock(nint buffer, out byte* data, out uint current)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Function(buffer, 3);
            byte* pointer;
            uint max;
            uint length;
            var hr = fn(buffer, &pointer, &max, &length);
            data = pointer;
            current = length == 0 ? max : length;
            return hr;
        }

        public static int Unlock(nint buffer) => ((delegate* unmanaged[Stdcall]<nint, int>)Function(buffer, 4))(buffer);

        public static int SetCurrentLength(nint buffer, uint length)
            => ((delegate* unmanaged[Stdcall]<nint, uint, int>)Function(buffer, 6))(buffer, length);

        public static int GetContiguousLength(nint buffer, out uint length)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, uint*, int>)Function(buffer, 7);
            uint value;
            var hr = fn(buffer, &value);
            length = value;
            return hr;
        }

        public static int ContiguousCopyTo(nint buffer, byte* destination, uint size)
            => ((delegate* unmanaged[Stdcall]<nint, byte*, uint, int>)Function(buffer, 8))(buffer, destination, size);

        public static int GetOutputStreamInfo(nint transform, out StreamInfo info)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, uint, StreamInfo*, int>)Function(transform, 7);
            StreamInfo value;
            var hr = fn(transform, 0, &value);
            info = value;
            return hr;
        }

        public static int GetAttributes(nint transform, out nint attributes)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Function(transform, 8);
            nint value;
            var hr = fn(transform, &value);
            attributes = hr >= 0 ? value : 0;
            return hr;
        }

        public static int GetOutputAvailableType(nint transform, uint index, out nint type)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Function(transform, 14);
            nint value;
            var hr = fn(transform, 0, index, &value);
            type = hr >= 0 ? value : 0;
            return hr;
        }

        public static int SetInputType(nint transform, nint type)
            => ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Function(transform, 15))(transform, 0, type, 0);

        public static int SetOutputType(nint transform, nint type)
            => ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Function(transform, 16))(transform, 0, type, 0);

        public static int GetOutputCurrentType(nint transform, out nint type)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Function(transform, 18);
            nint value;
            var hr = fn(transform, 0, &value);
            type = hr >= 0 ? value : 0;
            return hr;
        }

        public static int ProcessMessage(nint transform, int message, nuint parameter)
            => ((delegate* unmanaged[Stdcall]<nint, int, nuint, int>)Function(transform, 23))(transform, message, parameter);

        public static int ProcessInput(nint transform, nint sample)
            => ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Function(transform, 24))(transform, 0, sample, 0);

        public static int ProcessOutput(nint transform, OutputBuffer* output, out uint status)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, uint, uint, OutputBuffer*, uint*, int>)Function(transform, 25);
            uint value;
            var hr = fn(transform, 0, 1, output, &value);
            status = value;
            return hr;
        }

        public static int SetSampleTime(nint sample, long time)
            => ((delegate* unmanaged[Stdcall]<nint, long, int>)Function(sample, 36))(sample, time);

        public static int SetSampleDuration(nint sample, long duration)
            => ((delegate* unmanaged[Stdcall]<nint, long, int>)Function(sample, 38))(sample, duration);

        public static int ConvertToContiguous(nint sample, out nint buffer)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Function(sample, 41);
            nint value;
            var hr = fn(sample, &value);
            buffer = hr >= 0 ? value : 0;
            return hr;
        }

        public static int AddBuffer(nint sample, nint buffer)
            => ((delegate* unmanaged[Stdcall]<nint, nint, int>)Function(sample, 42))(sample, buffer);

        public static int Activate(nint activate, Guid iid, out nint instance)
        {
            var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Function(activate, 33);
            var local = iid;
            nint value;
            var hr = fn(activate, &local, &value);
            instance = hr >= 0 ? value : 0;
            return hr;
        }

        private static nint Function(nint self, int slot)
            => Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * nint.Size);
    }
}

internal static unsafe partial class MediaFoundationNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RegisterInfo
    {
        public Guid Major;
        public Guid Subtype;
    }

    public static int CoCreate(Guid clsid, Guid iid, out nint instance)
    {
        var localClass = clsid;
        var localInterface = iid;
        return CoCreateInstance(localClass, 0, 1, localInterface, out instance);
    }

    public static int CreateBuffer(uint size, uint alignment, out nint buffer)
    {
        if (alignment != 0 && (alignment & (alignment - 1)) == 0)
        {
            var hr = MFCreateAlignedMemoryBuffer(size, alignment, out buffer);
            if (hr >= 0)
            {
                return hr;
            }
        }

        return MFCreateMemoryBuffer(size, out buffer);
    }

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coinit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint pointer);

    [LibraryImport("mfplat.dll")]
    public static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMediaType(out nint type);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateSample(out nint sample);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMemoryBuffer(uint maxLength, out nint buffer);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateAlignedMemoryBuffer(uint maxLength, uint alignment, out nint buffer);

    [LibraryImport("mfplat.dll")]
    public static partial int MFTEnumEx(in Guid category, uint flags, RegisterInfo* inputType, RegisterInfo* outputType, out nint activates, out uint count);
}
#endif
