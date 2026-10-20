namespace AirPlayServer.Core.Video;

/// <summary>
/// Maps FFmpeg color metadata onto the AirPlay default. An unspecified matrix or
/// range is not a decision, so a decoder tag of "unspecified" stays BT.709 full
/// range. An H.264 SPS VUI wins when it is present.
/// </summary>
internal static class FFmpegColor
{
    internal static YuvColorSpace Resolve(H264ColorSignals bitstream, int pixelFormat, int colorSpace, int colorRange)
    {
        var matrix = YuvMatrix.Bt709;
        if (bitstream.HasMatrix && YuvColorSpace.TryMapMatrixCoefficient(bitstream.MatrixCoefficients, out var fromBitstream))
        {
            matrix = fromBitstream;
        }
        else if (YuvColorSpace.TryMapMatrixCoefficient(colorSpace, out var fromFrame))
        {
            matrix = fromFrame;
        }

        var range = YuvRange.Full;
        if (bitstream.HasRange)
        {
            range = bitstream.FullRange ? YuvRange.Full : YuvRange.Limited;
        }
        else if (pixelFormat == FFmpegLibrary.PixelYuvJ420P || colorRange == 2)
        {
            range = YuvRange.Full;
        }
        else if (colorRange == 1)
        {
            range = YuvRange.Limited;
        }

        return new YuvColorSpace(matrix, range);
    }
}

/// <summary>
/// One libavcodec decode context. Annex-B access units go in; BGRA comes out
/// through <see cref="Nv12Color"/> when the picture is NV12, NV21, or YUV 4:2:0.
/// </summary>
internal sealed unsafe class FFmpegDecodeSession : IDisposable
{
    private readonly FFmpegLibrary _library;
    private readonly IAirPlayLog _log;
    private readonly BgraBufferPool _buffers;
    private readonly object _gate = new();
    private readonly nint _codec;
    private readonly nint _frame;
    private nint _context;
    private H264ColorSignals _bitstream;
    private int _disposed;
    private int _loggedColor;
    private int _loggedFormat;
    private int _loggedSend;
    private bool _dropUntilKey;
    private BgraReuseSlot _reuse;

    private FFmpegDecodeSession(FFmpegLibrary library, nint codec, nint context, nint frame, IAirPlayLog? log, string name, BgraBufferPool buffers)
    {
        _library = library;
        _codec = codec;
        _context = context;
        _frame = frame;
        _log = log ?? NullAirPlayLog.Instance;
        _buffers = buffers;
        Name = name;
    }

    internal bool IsOpen => Volatile.Read(ref _disposed) == 0 && _context != 0;

    internal string Name { get; }

    internal static bool TryOpen(FFmpegLibrary library, string codecName, int fallbackCodecId, string name, IAirPlayLog? log, BgraBufferPool? buffers, out FFmpegDecodeSession? session)
    {
        session = null;
        var codec = library.FindNamedDecoder(codecName, fallbackCodecId);
        if (codec == 0)
        {
            var shifted = codecName == "hevc"
                ? " libavcodec 63 (FFmpeg 9) 上数字 id 173 是 FIC 不是 HEVC，所以这里按名字 \"hevc\" 找解码器。"
                    + " On libavcodec 63 (FFmpeg 9) numeric id 173 is FIC, not HEVC, so the decoder is selected by the name \"hevc\"."
                : "";
            log?.Log(
                AirPlayLogLevel.Warning,
                "已加载 FFmpeg，但这份 libavcodec 没有 " + codecName + " 解码器。" + shifted
                + " This libavcodec build has no " + codecName + " decoder." + shifted);
            return false;
        }

        log?.Log(
            AirPlayLogLevel.Information,
            name + " opened decoder \"" + codecName + "\" (AVCodecID "
            + FFmpegLibrary.ReadCodecId(codec).ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");

        var context = library.AllocContext(codec);
        if (context == 0)
        {
            log?.Log(AirPlayLogLevel.Warning, "avcodec_alloc_context3 failed for " + name + ".");
            return false;
        }

        var opened = library.OpenCapped(context, codec);
        if (opened < 0)
        {
            log?.Log(
                AirPlayLogLevel.Warning,
                "avcodec_open2 failed for " + name + " (" + library.DescribeError(opened) + ").");
            library.FreeContext(&context);
            return false;
        }

        if (library.TryReadThreads(context, out var threads))
        {
            log?.Log(
                AirPlayLogLevel.Information,
                name + " 解码线程 " + threads.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "。Decode threads: " + threads.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
            if (threads != 1)
            {
                log?.Log(
                    AirPlayLogLevel.Warning,
                    name + " 没能把解码线程限制为 1，高分辨率镜像可能占满 CPU。Could not keep decode threads at 1; a large mirror may use every core.");
            }
        }

        var frame = library.FrameAlloc();
        if (frame == 0)
        {
            library.FreeContext(&context);
            log?.Log(AirPlayLogLevel.Warning, "av_frame_alloc failed for " + name + ".");
            return false;
        }

        session = new FFmpegDecodeSession(library, codec, context, frame, log, name, buffers ?? new BgraBufferPool());
        return true;
    }

    internal void OfferReuse(byte[] buffer, int width, int height, int stride)
        => _reuse.Offer(buffer, width, height, stride);

    internal void ClearReuse() => _reuse.Clear();

    internal void SetBitstreamColor(H264ColorSignals color)
    {
        lock (_gate)
        {
            _bitstream = color;
        }
    }

    internal void Reset()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }

            // avcodec_flush_buffers clears the DPB but leaves HEVC POC tracking.
            // The next P-frame then logs "Could not find ref with POC" and
            // "Error constructing the frame RPS" forever. A new context does not.
            ReopenLocked();
            _dropUntilKey = true;
        }
    }

    internal bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame)
    {
        frame = default;
        if (!IsOpen || annexB.IsEmpty)
        {
            return false;
        }

        lock (_gate)
        {
            if (_context == 0)
            {
                return false;
            }

            if (_dropUntilKey && !keyFrame)
            {
                return false;
            }

            var status = DecodeOnce(annexB, out frame);
            if (status == DecodeStatus.Picture)
            {
                _dropUntilKey = false;
                return true;
            }

            if (status == DecodeStatus.Accepted)
            {
                // The keyframe is in the new context. The picture can come
                // out on the next access unit. Dropping that unit leaves the
                // IDR stuck and the window stays on the last frame.
                _dropUntilKey = false;
                return false;
            }

            if (status != DecodeStatus.Hard)
            {
                return false;
            }

            // Missing refs or a broken RPS. Replacing the context stops the
            // per-frame av_log flood. Only the next keyframe is fed to it.
            ReopenLocked();
            _dropUntilKey = true;
            if (!keyFrame || _context == 0)
            {
                frame = default;
                return false;
            }

            status = DecodeOnce(annexB, out frame);
            if (status == DecodeStatus.Picture)
            {
                _dropUntilKey = false;
                return true;
            }

            if (status == DecodeStatus.Accepted)
            {
                _dropUntilKey = false;
                frame = default;
                return false;
            }

            _dropUntilKey = true;
            frame = default;
            return false;
        }
    }

    private enum DecodeStatus
    {
        NeedMore = 0,
        Picture = 1,
        Hard = 2,

        /// <summary>The packet was accepted and no picture is ready yet.</summary>
        Accepted = 3,
    }

    private DecodeStatus DecodeOnce(ReadOnlySpan<byte> annexB, out DecodedFrame frame)
    {
        frame = default;
        if (!Send(annexB, out var drained, out var hard))
        {
            ReturnFrame(drained);
            return hard ? DecodeStatus.Hard : DecodeStatus.NeedMore;
        }

        if (Receive(out frame, out hard))
        {
            ReturnFrame(drained);
            return DecodeStatus.Picture;
        }

        if (hard)
        {
            ReturnFrame(drained);
            frame = default;
            return DecodeStatus.Hard;
        }

        // The decoder was full, so one picture was pulled out to make room.
        // If the new packet does not produce its own picture yet, that
        // drained picture is still the latest frame.
        frame = drained;
        return drained.Width > 0 ? DecodeStatus.Picture : DecodeStatus.Accepted;
    }

    private void ReopenLocked()
    {
        if (_context != 0)
        {
            var dying = _context;
            _context = 0;
            _library.FreeContext(&dying);
        }

        if (_codec == 0 || _disposed != 0)
        {
            return;
        }

        var context = _library.AllocContext(_codec);
        if (context == 0)
        {
            _log.Log(AirPlayLogLevel.Warning, Name + " could not allocate a new decode context.");
            return;
        }

        var opened = _library.OpenCapped(context, _codec);
        if (opened < 0)
        {
            _library.FreeContext(&context);
            _log.Log(AirPlayLogLevel.Warning, Name + " could not reopen (" + _library.DescribeError(opened) + ").");
            return;
        }

        _context = context;
        _loggedSend = 0;
        _log.Log(
            AirPlayLogLevel.Information,
            Name + " 已重建解码器，旧的参考帧和 POC 已丢掉。Reopened the decoder. Previous reference frames and POC state were dropped.");
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

    private bool Send(ReadOnlySpan<byte> annexB, out DecodedFrame drained, out bool hard)
    {
        drained = default;
        hard = false;
        if (annexB.Length <= 0 || annexB.Length > 8 * 1024 * 1024)
        {
            return false;
        }

        var size = annexB.Length;
        var memory = _library.Malloc((nuint)(size + FFmpegLibrary.InputPadding));
        if (memory == 0)
        {
            return false;
        }

        annexB.CopyTo(new Span<byte>((void*)memory, size));
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
        if (result == FFmpegLibrary.ErrorAgain)
        {
            Receive(out drained, out _);
            result = _library.SendPacket(_context, packet);
        }

        _library.PacketFree(&packet);
        if (result < 0)
        {
            // Includes a second EAGAIN. The context is not making progress.
            hard = true;
            if (Interlocked.Exchange(ref _loggedSend, 1) == 0)
            {
                _log.Log(AirPlayLogLevel.Warning, Name + " send failed (" + _library.DescribeError(result) + ").");
            }

            return false;
        }

        return true;
    }

    private bool Receive(out DecodedFrame frame, out bool hard)
    {
        frame = default;
        hard = false;
        var produced = false;
        while (_context != 0)
        {
            _library.FrameUnref(_frame);
            var result = _library.ReceiveFrame(_context, _frame);
            if (result == FFmpegLibrary.ErrorAgain)
            {
                break;
            }

            if (result < 0)
            {
                if (!produced)
                {
                    hard = true;
                }

                if (Interlocked.Exchange(ref _loggedSend, 1) == 0)
                {
                    _log.Log(AirPlayLogLevel.Warning, Name + " receive failed (" + _library.DescribeError(result) + ").");
                }

                break;
            }

            _library.ApplyCropping(_frame);
            if (TryConvert(_frame, out var decoded))
            {
                ReturnFrame(frame);
                frame = decoded;
                produced = true;
            }
        }

        return produced;
    }

    private bool TryConvert(nint picture, out DecodedFrame frame)
    {
        frame = default;
        if (!_library.TryReadPicture(picture, out var width, out var height, out var format, out var y, out var u, out var v, out var yStride, out var uStride, out var vStride))
        {
            return false;
        }

        // av_frame_apply_cropping can leave a one-pixel edge. The planar copy
        // requires even 4:2:0 dimensions; dropping the frame paints nothing.
        if ((width & 1) != 0)
        {
            width--;
        }

        if ((height & 1) != 0)
        {
            height--;
        }

        if (width < 2 || height < 2)
        {
            return false;
        }

        // colorspace and color_range sit past the stable AVFrame prefix and move
        // between FFmpeg majors. Unspecified (2, 0) keeps the AirPlay default
        // unless the SPS or a JPEG pixel format says otherwise.
        var color = FFmpegColor.Resolve(_bitstream, format, colorSpace: 2, colorRange: 0);
        if (Interlocked.Exchange(ref _loggedColor, 1) == 0)
        {
            _log.Log(AirPlayLogLevel.Information, Name + " YUV to BGRA is " + color.Describe() + ".");
        }

        var byteCount = width * height * 4;
        var reused = _reuse.TryTake(width, height, byteCount, out var pixels);
        if (!reused)
        {
            pixels = _buffers.Rent(byteCount);
        }

        var destination = pixels.AsSpan(0, byteCount);
        var copied = format switch
        {
            FFmpegLibrary.PixelYuv420P or FFmpegLibrary.PixelYuvJ420P => YuvPlanarColor.TryCopyPlanes(
                y, yStride, u, uStride, v, vStride, width, height, color, 1, 0, false, false, destination),
            FFmpegLibrary.PixelYuv420P10Le => YuvPlanarColor.TryCopyPlanes(
                y, yStride, u, uStride, v, vStride, width, height, color, 2, 2, false, false, destination),
            FFmpegLibrary.PixelNv12 => YuvPlanarColor.TryCopyPlanes(
                y, yStride, u, uStride, 0, 0, width, height, color, 1, 0, true, false, destination),
            FFmpegLibrary.PixelNv21 => YuvPlanarColor.TryCopyPlanes(
                y, yStride, u, uStride, 0, 0, width, height, color, 1, 0, true, true, destination),
            _ => false,
        };
        if (!copied)
        {
            if (!reused)
            {
                _buffers.Return(pixels);
            }

            if (Interlocked.Exchange(ref _loggedFormat, 1) == 0)
            {
                _log.Log(
                    AirPlayLogLevel.Warning,
                    Name + " picture format " + format.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " is not NV12 or YUV420P, so the frame was not shown.");
            }

            return false;
        }

        frame = reused
            ? new DecodedFrame(width, height, pixels.AsMemory(0, byteCount), width * 4)
            : DecodedFrame.Own(width, height, pixels, width * 4);
        return true;
    }

    private void ReturnFrame(DecodedFrame frame)
    {
        if (frame.Owned is { } owned)
        {
            _buffers.Return(owned);
        }
    }
}

internal sealed class FFmpegH264Decoder : IH264Decoder, IBgraReuseTarget, IDisposable
{
    private readonly FFmpegDecodeSession _session;

    private FFmpegH264Decoder(FFmpegDecodeSession session)
    {
        _session = session;
    }

    public bool IsAvailable => _session.IsOpen;

    public string Name => _session.Name;

    public void Configure(ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet)
    {
        _ = pictureParameterSet;
        if (H264Sequence.TryGetColorSignals(sequenceParameterSet, out var color))
        {
            _session.SetBitstreamColor(color);
        }
    }

    public void Reset() => _session.Reset();

    public void OfferReuse(byte[] buffer, int width, int height, int stride)
        => _session.OfferReuse(buffer, width, height, stride);

    public void ClearReuse() => _session.ClearReuse();

    public bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame)
        => _session.TryDecode(annexB, keyFrame, out frame);

    public void Dispose() => _session.Dispose();

    internal static bool TryCreate(FFmpegLibrary library, IAirPlayLog? log, out FFmpegH264Decoder? decoder)
        => TryCreate(library, log, null, out decoder);

    internal static bool TryCreate(FFmpegLibrary library, IAirPlayLog? log, BgraBufferPool? buffers, out FFmpegH264Decoder? decoder)
    {
        decoder = null;
        if (!FFmpegDecodeSession.TryOpen(library, "h264", FFmpegLibrary.CodecH264, "ffmpeg-h264", log, buffers, out var session) || session is null)
        {
            return false;
        }

        decoder = new FFmpegH264Decoder(session);
        return true;
    }
}

internal sealed class FFmpegHevcDecoder : IHevcDecoder, IBgraReuseTarget, IDisposable
{
    private readonly FFmpegDecodeSession _session;

    private FFmpegHevcDecoder(FFmpegDecodeSession session)
    {
        _session = session;
    }

    public bool IsAvailable => _session.IsOpen;

    public string Name => _session.Name;

    public void Configure(ReadOnlySpan<byte> videoParameterSet, ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet)
    {
        // The mirror pipeline prefixes VPS/SPS/PPS onto the next key frame.
        // libavcodec reads those NAL units from the Annex-B access unit.
        _ = videoParameterSet;
        _ = sequenceParameterSet;
        _ = pictureParameterSet;
    }

    public void Reset() => _session.Reset();

    public void OfferReuse(byte[] buffer, int width, int height, int stride)
        => _session.OfferReuse(buffer, width, height, stride);

    public void ClearReuse() => _session.ClearReuse();

    public bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame)
    {
        return _session.TryDecode(annexB, keyFrame, out frame);
    }

    public void Dispose() => _session.Dispose();

    internal static bool TryCreate(FFmpegLibrary library, IAirPlayLog? log, out FFmpegHevcDecoder? decoder)
        => TryCreate(library, log, null, out decoder);

    internal static bool TryCreate(FFmpegLibrary library, IAirPlayLog? log, BgraBufferPool? buffers, out FFmpegHevcDecoder? decoder)
    {
        decoder = null;
        if (!FFmpegDecodeSession.TryOpen(library, "hevc", FFmpegLibrary.HevcCodecId(library.AvcodecMajor), "ffmpeg-hevc", log, buffers, out var session) || session is null)
        {
            return false;
        }

        decoder = new FFmpegHevcDecoder(session);
        return true;
    }
}
