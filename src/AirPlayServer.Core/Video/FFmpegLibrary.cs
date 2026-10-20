using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AirPlayServer.Core.Video;

/// <summary>
/// Process-lifetime load of libavcodec and libavutil. The Native AOT host does
/// not link FFmpeg. Callers place the shared libraries next to the executable
/// or on the system search path. A missing library leaves the decoders unavailable.
/// </summary>
internal sealed unsafe class FFmpegLibrary
{
    internal const int CodecH264 = 27;

    /// <summary>
    /// <c>AV_CODEC_ID_HEVC</c> on libavcodec 58–62 (FFmpeg 4.4–8).
    /// </summary>
    internal const int CodecHevcThroughAvcodec62 = 173;

    /// <summary>
    /// <c>AV_CODEC_ID_HEVC</c> on libavcodec 63 (FFmpeg 9). That release removed
    /// <c>AV_CODEC_ID_V410</c>, so every video id after it moved down by one.
    /// 173 is <c>AV_CODEC_ID_FIC</c> there. Opening 173 feeds Annex-B HEVC to the
    /// FIC decoder, which logs "Zero slices found" and returns invalid data.
    /// </summary>
    internal const int CodecHevcFromAvcodec63 = 172;
    internal const int PixelYuv420P = 0;
    internal const int PixelYuvJ420P = 12;
    internal const int PixelNv12 = 23;
    internal const int PixelNv21 = 24;
    internal const int PixelYuv420P10Le = 62;
    internal const int ErrorAgain = -11;
    internal const int InputPadding = 64;

    private const int LinesizeOffset = 64;
    private const int WidthOffset = 104;
    private const int HeightOffset = 108;
    private const int FormatOffset = 116;

    private static readonly object Gate = new();
    private static FFmpegLibrary? _cached;
    private static bool _cachedMiss;
    private static bool _loggedMiss;

    private readonly delegate* unmanaged[Cdecl]<nuint, nint> _malloc;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _free;
    private readonly delegate* unmanaged[Cdecl]<nint> _frameAlloc;
    private readonly delegate* unmanaged[Cdecl]<nint*, void> _frameFree;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _frameUnref;
    private readonly delegate* unmanaged[Cdecl]<nint, int, int> _frameGetBuffer;
    private readonly delegate* unmanaged[Cdecl]<nint, int, int> _applyCropping;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, nint, int, int> _optSet;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, long, int, int> _optSetInt;
    private readonly delegate* unmanaged[Cdecl]<int, nint> _findDecoder;
    private readonly delegate* unmanaged[Cdecl]<nint, nint> _findDecoderByName;
    private readonly delegate* unmanaged[Cdecl]<nint, nint> _allocContext;
    private readonly delegate* unmanaged[Cdecl]<nint*, void> _freeContext;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, nint, int> _open;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, int> _sendPacket;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, int> _receiveFrame;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _flush;
    private readonly delegate* unmanaged[Cdecl]<nint> _packetAlloc;
    private readonly delegate* unmanaged[Cdecl]<nint*, void> _packetFree;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, int, int> _packetFromData;
    private readonly delegate* unmanaged[Cdecl]<nint> _versionInfo;
    private readonly delegate* unmanaged[Cdecl]<int, nint, nuint, int> _strerror;
    private readonly delegate* unmanaged[Cdecl]<nint*, nint, nint, int, int> _dictSet;
    private readonly delegate* unmanaged[Cdecl]<nint*, void> _dictFree;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, int, long*, int> _optGetInt;

    private FFmpegLibrary(
        string description,
        delegate* unmanaged[Cdecl]<nuint, nint> malloc,
        delegate* unmanaged[Cdecl]<nint, void> free,
        delegate* unmanaged[Cdecl]<nint> frameAlloc,
        delegate* unmanaged[Cdecl]<nint*, void> frameFree,
        delegate* unmanaged[Cdecl]<nint, void> frameUnref,
        delegate* unmanaged[Cdecl]<nint, int, int> frameGetBuffer,
        delegate* unmanaged[Cdecl]<nint, int, int> applyCropping,
        delegate* unmanaged[Cdecl]<nint, nint, nint, int, int> optSet,
        delegate* unmanaged[Cdecl]<nint, nint, long, int, int> optSetInt,
        delegate* unmanaged[Cdecl]<int, nint> findDecoder,
        delegate* unmanaged[Cdecl]<nint, nint> findDecoderByName,
        int avcodecMajor,
        delegate* unmanaged[Cdecl]<nint, nint> allocContext,
        delegate* unmanaged[Cdecl]<nint*, void> freeContext,
        delegate* unmanaged[Cdecl]<nint, nint, nint, int> open,
        delegate* unmanaged[Cdecl]<nint, nint, int> sendPacket,
        delegate* unmanaged[Cdecl]<nint, nint, int> receiveFrame,
        delegate* unmanaged[Cdecl]<nint, void> flush,
        delegate* unmanaged[Cdecl]<nint> packetAlloc,
        delegate* unmanaged[Cdecl]<nint*, void> packetFree,
        delegate* unmanaged[Cdecl]<nint, nint, int, int> packetFromData,
        delegate* unmanaged[Cdecl]<nint> versionInfo,
        delegate* unmanaged[Cdecl]<int, nint, nuint, int> strerror,
        delegate* unmanaged[Cdecl]<nint*, nint, nint, int, int> dictSet,
        delegate* unmanaged[Cdecl]<nint*, void> dictFree,
        delegate* unmanaged[Cdecl]<nint, nint, int, long*, int> optGetInt)
    {
        Description = description;
        _malloc = malloc;
        _free = free;
        _frameAlloc = frameAlloc;
        _frameFree = frameFree;
        _frameUnref = frameUnref;
        _frameGetBuffer = frameGetBuffer;
        _applyCropping = applyCropping;
        _optSet = optSet;
        _optSetInt = optSetInt;
        _findDecoder = findDecoder;
        _findDecoderByName = findDecoderByName;
        AvcodecMajor = avcodecMajor;
        _allocContext = allocContext;
        _freeContext = freeContext;
        _open = open;
        _sendPacket = sendPacket;
        _receiveFrame = receiveFrame;
        _flush = flush;
        _packetAlloc = packetAlloc;
        _packetFree = packetFree;
        _packetFromData = packetFromData;
        _versionInfo = versionInfo;
        _strerror = strerror;
        _dictSet = dictSet;
        _dictFree = dictFree;
        _optGetInt = optGetInt;
    }

    internal string Description { get; }

    /// <summary>libavcodec SONAME major, or 0 when the file name has none.</summary>
    internal int AvcodecMajor { get; }

    internal static int HevcCodecId(int avcodecMajor)
        => avcodecMajor >= 63 ? CodecHevcFromAvcodec63 : CodecHevcThroughAvcodec62;

    internal static int TryParseAvcodecMajor(string fileName)
    {
        var name = Path.GetFileName(fileName);
        const string windows = "avcodec-";
        if (name.StartsWith(windows, StringComparison.Ordinal)
            && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return ParseLeadingInt(name[windows.Length..^4]);
        }

        const string linux = "libavcodec.so.";
        if (name.StartsWith(linux, StringComparison.Ordinal))
        {
            return ParseLeadingInt(name[linux.Length..]);
        }

        return 0;
    }

    internal static FFmpegLibrary? TryLoad(IAirPlayLog? log)
    {
        lock (Gate)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            if (_cachedMiss)
            {
                return null;
            }

            var loaded = Load(log);
            if (loaded is null)
            {
                _cachedMiss = true;
                if (!_loggedMiss)
                {
                    _loggedMiss = true;
                    log?.Log(
                        AirPlayLogLevel.Information,
                        "未找到 FFmpeg 动态库（libavcodec / libavutil）。Linux 上的 H.264，以及没有 Media Foundation HEVC 时的 HEVC，需要把这些库放在程序旁边或系统路径里。见 packaging/RUN.txt。"
                        + " FFmpeg (libavcodec/libavutil) was not found next to the host or on the system library path.");
                }

                return null;
            }

            _cached = loaded;
            log?.Log(AirPlayLogLevel.Information, "FFmpeg " + loaded.Description + " is available.");
            return loaded;
        }
    }

    internal nint FindDecoder(int codecId) => _findDecoder(codecId);

    /// <summary>
    /// Opens the decoder named <paramref name="codecName"/> (<c>hevc</c> or
    /// <c>h264</c>). The name lookup is stable across the FFmpeg 9 codec-id
    /// shift. A numeric id is used only when its <c>AVCodec.name</c> matches,
    /// so id 173 cannot come back as FIC.
    /// </summary>
    internal unsafe nint FindNamedDecoder(string codecName, int fallbackCodecId)
    {
        if (_findDecoderByName != null)
        {
            var bytes = Utf8(codecName);
            fixed (byte* text = bytes)
            {
                var found = _findDecoderByName((nint)text);
                if (found != 0 && CodecNameIs(found, codecName))
                {
                    return found;
                }
            }
        }

        if (TryDecoderId(fallbackCodecId, codecName, out var byId))
        {
            return byId;
        }

        if (codecName == "hevc")
        {
            var other = fallbackCodecId == CodecHevcFromAvcodec63
                ? CodecHevcThroughAvcodec62
                : CodecHevcFromAvcodec63;
            if (other != fallbackCodecId && TryDecoderId(other, codecName, out var alternate))
            {
                return alternate;
            }
        }

        return 0;
    }

    internal static bool CodecNameIs(nint codec, string expected)
    {
        var actual = ReadCodecName(codec);
        return actual is not null && actual.Equals(expected, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>AVCodec.name</c> is the first field on FFmpeg 4.4 through 9.
    /// </summary>
    internal static string? ReadCodecName(nint codec)
    {
        if (codec == 0)
        {
            return null;
        }

        var pointer = Marshal.ReadIntPtr(codec);
        return pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);
    }

    /// <summary>
    /// <c>AVCodec.id</c> follows two pointers and <c>AVMediaType</c> on 64-bit
    /// FFmpeg 4.4 through 9.
    /// </summary>
    internal static int ReadCodecId(nint codec)
        => Marshal.ReadInt32(codec, (nint.Size * 2) + sizeof(int));

    private bool TryDecoderId(int codecId, string codecName, out nint codec)
    {
        codec = _findDecoder(codecId);
        return codec != 0 && CodecNameIs(codec, codecName);
    }

    internal nint AllocContext(nint codec) => _allocContext(codec);

    internal void FreeContext(nint* context) => _freeContext(context);

    internal int Open(nint context, nint codec) => _open(context, codec, 0);

    /// <summary>
    /// Opens the codec with one decode thread. FFmpeg treats a thread count of 0
    /// as "one thread per core", which pegs a 1290x2794 HEVC mirror. The count is
    /// also passed in the <c>avcodec_open2</c> dictionary so a private default
    /// cannot put it back.
    /// </summary>
    internal unsafe int OpenCapped(nint context, nint codec)
    {
        TrySetInt(context, "threads", 1);
        TrySet(context, "thread_type", "slice");
        TrySet(context, "flags", "+low_delay");
        if (_dictSet == null)
        {
            return _open(context, codec, 0);
        }

        var key = Utf8("threads");
        var value = Utf8("1");
        nint dict = 0;
        fixed (byte* keyPointer = key)
        fixed (byte* valuePointer = value)
        {
            if (_dictSet(&dict, (nint)keyPointer, (nint)valuePointer, 0) < 0)
            {
                dict = 0;
            }

            var opened = _open(context, codec, (nint)(&dict));
            if (_dictFree != null)
            {
                _dictFree(&dict);
            }

            return opened;
        }
    }

    internal unsafe bool TryReadThreads(nint context, out long threads)
    {
        threads = 0;
        if (_optGetInt == null)
        {
            return false;
        }

        var name = Utf8("threads");
        fixed (byte* text = name)
        {
            long value;
            if (_optGetInt(context, (nint)text, 0, &value) < 0)
            {
                return false;
            }

            threads = value;
            return true;
        }
    }

    internal int SendPacket(nint context, nint packet) => _sendPacket(context, packet);

    internal int ReceiveFrame(nint context, nint frame) => _receiveFrame(context, frame);

    internal void Flush(nint context) => _flush(context);

    internal nint FrameAlloc() => _frameAlloc();

    internal void FrameFree(nint* frame) => _frameFree(frame);

    internal void FrameUnref(nint frame) => _frameUnref(frame);

    internal int ApplyCropping(nint frame) => _applyCropping(frame, 1);

    internal nint Malloc(nuint size) => _malloc(size);

    internal void Free(nint pointer) => _free(pointer);

    internal nint PacketAlloc() => _packetAlloc();

    internal void PacketFree(nint* packet) => _packetFree(packet);

    internal int PacketFromData(nint packet, nint data, int size) => _packetFromData(packet, data, size);

    internal bool TrySetInt(nint obj, string name, long value)
    {
        var bytes = Utf8(name);
        fixed (byte* text = bytes)
        {
            return _optSetInt(obj, (nint)text, value, 0) >= 0;
        }
    }

    internal bool TrySet(nint obj, string name, string value)
    {
        var nameBytes = Utf8(name);
        var valueBytes = Utf8(value);
        fixed (byte* nameText = nameBytes)
        fixed (byte* valueText = valueBytes)
        {
            return _optSet(obj, (nint)nameText, (nint)valueText, 0) >= 0;
        }
    }

    internal bool TryReadPicture(nint frame, out int width, out int height, out int format, out nint y, out nint u, out nint v, out int yStride, out int uStride, out int vStride)
    {
        width = Marshal.ReadInt32(frame, WidthOffset);
        height = Marshal.ReadInt32(frame, HeightOffset);
        format = Marshal.ReadInt32(frame, FormatOffset);
        y = Marshal.ReadIntPtr(frame, 0);
        u = Marshal.ReadIntPtr(frame, nint.Size);
        v = Marshal.ReadIntPtr(frame, nint.Size * 2);
        yStride = Marshal.ReadInt32(frame, LinesizeOffset);
        uStride = Marshal.ReadInt32(frame, LinesizeOffset + 4);
        vStride = Marshal.ReadInt32(frame, LinesizeOffset + 8);
        return width > 0 && height > 0 && y != 0;
    }

    internal string DescribeError(int error)
    {
        if (_strerror == null)
        {
            return error.ToString(CultureInfo.InvariantCulture);
        }

        var buffer = stackalloc byte[128];
        if (_strerror(error, (nint)buffer, 128) < 0)
        {
            return error.ToString(CultureInfo.InvariantCulture);
        }

        return Marshal.PtrToStringUTF8((nint)buffer) ?? error.ToString(CultureInfo.InvariantCulture);
    }

    private static FFmpegLibrary? Load(IAirPlayLog? log)
    {
        if (nint.Size != 8)
        {
            log?.Log(AirPlayLogLevel.Information, "FFmpeg decode is only wired for 64-bit processes.");
            return null;
        }

        // Newest compatible SONAME first. Each triple is one FFmpeg major
        // (avcodec, avutil, swresample): 9 / current BtbN master (63/61/7),
        // 8 (62/60/6), 7 (61/59/5), 6 (60/58/4), 5 (59/57/4), 4.4 (58/56/3).
        // Windows names are avcodec-63.dll, avutil-61.dll, swresample-7.dll.
        // Linux names are libavcodec.so.63, libavutil.so.61, libswresample.so.7.
        foreach (var triple in SonameTriples)
        {
            if (TryLoadTriple(triple.Avutil, triple.Swresample, triple.Avcodec, out var avutil, out var avcodec, out var avutilName, out var avcodecName)
                && TryBind(avutil, avcodec, avutilName, avcodecName, out var library))
            {
                return library;
            }
        }

        if (TryLoadTriple(Unversioned("avutil"), Unversioned("swresample"), Unversioned("avcodec"), out var plainUtil, out var plainCodec, out var plainUtilName, out var plainCodecName)
            && TryBind(plainUtil, plainCodec, plainUtilName, plainCodecName, out var plain))
        {
            return plain;
        }

        return null;
    }

    private static bool TryBind(nint avutil, nint avcodec, string avutilName, string avcodecName, out FFmpegLibrary? library)
    {
        library = null;
        if (!TryExport(avutil, "av_malloc", out var malloc)
            || !TryExport(avutil, "av_free", out var free)
            || !TryExport(avutil, "av_frame_alloc", out var frameAlloc)
            || !TryExport(avutil, "av_frame_free", out var frameFree)
            || !TryExport(avutil, "av_frame_unref", out var frameUnref)
            || !TryExport(avutil, "av_frame_get_buffer", out var frameGetBuffer)
            || !TryExport(avutil, "av_frame_apply_cropping", out var applyCropping)
            || !TryExport(avutil, "av_opt_set", out var optSet)
            || !TryExport(avutil, "av_opt_set_int", out var optSetInt)
            || !TryExport(avcodec, "avcodec_find_decoder", out var findDecoder)
            || !TryExport(avcodec, "avcodec_alloc_context3", out var allocContext)
            || !TryExport(avcodec, "avcodec_free_context", out var freeContext)
            || !TryExport(avcodec, "avcodec_open2", out var open)
            || !TryExport(avcodec, "avcodec_send_packet", out var sendPacket)
            || !TryExport(avcodec, "avcodec_receive_frame", out var receiveFrame)
            || !TryExport(avcodec, "avcodec_flush_buffers", out var flush)
            || !TryExport(avcodec, "av_packet_alloc", out var packetAlloc)
            || !TryExport(avcodec, "av_packet_free", out var packetFree)
            || !TryExport(avcodec, "av_packet_from_data", out var packetFromData))
        {
            return false;
        }

        TryExport(avcodec, "avcodec_find_decoder_by_name", out var findDecoderByName);
        TryExport(avutil, "av_version_info", out var versionInfo);
        TryExport(avutil, "av_strerror", out var strerror);
        TryExport(avutil, "av_dict_set", out var dictSet);
        TryExport(avutil, "av_dict_free", out var dictFree);
        TryExport(avutil, "av_opt_get_int", out var optGetInt);
        TryExport(avutil, "av_log_set_level", out var setLevel);
        if (setLevel != 0)
        {
            // AV_LOG_ERROR. Decode failures are reported by this process.
            ((delegate* unmanaged[Cdecl]<int, void>)setLevel)(16);
        }

        var bound = new FFmpegLibrary(
            Describe(versionInfo, avcodecName, avutilName),
            (delegate* unmanaged[Cdecl]<nuint, nint>)malloc,
            (delegate* unmanaged[Cdecl]<nint, void>)free,
            (delegate* unmanaged[Cdecl]<nint>)frameAlloc,
            (delegate* unmanaged[Cdecl]<nint*, void>)frameFree,
            (delegate* unmanaged[Cdecl]<nint, void>)frameUnref,
            (delegate* unmanaged[Cdecl]<nint, int, int>)frameGetBuffer,
            (delegate* unmanaged[Cdecl]<nint, int, int>)applyCropping,
            (delegate* unmanaged[Cdecl]<nint, nint, nint, int, int>)optSet,
            (delegate* unmanaged[Cdecl]<nint, nint, long, int, int>)optSetInt,
            (delegate* unmanaged[Cdecl]<int, nint>)findDecoder,
            findDecoderByName == 0 ? null : (delegate* unmanaged[Cdecl]<nint, nint>)findDecoderByName,
            TryParseAvcodecMajor(avcodecName),
            (delegate* unmanaged[Cdecl]<nint, nint>)allocContext,
            (delegate* unmanaged[Cdecl]<nint*, void>)freeContext,
            (delegate* unmanaged[Cdecl]<nint, nint, nint, int>)open,
            (delegate* unmanaged[Cdecl]<nint, nint, int>)sendPacket,
            (delegate* unmanaged[Cdecl]<nint, nint, int>)receiveFrame,
            (delegate* unmanaged[Cdecl]<nint, void>)flush,
            (delegate* unmanaged[Cdecl]<nint>)packetAlloc,
            (delegate* unmanaged[Cdecl]<nint*, void>)packetFree,
            (delegate* unmanaged[Cdecl]<nint, nint, int, int>)packetFromData,
            (delegate* unmanaged[Cdecl]<nint>)versionInfo,
            (delegate* unmanaged[Cdecl]<int, nint, nuint, int>)strerror,
            (delegate* unmanaged[Cdecl]<nint*, nint, nint, int, int>)dictSet,
            (delegate* unmanaged[Cdecl]<nint*, void>)dictFree,
            (delegate* unmanaged[Cdecl]<nint, nint, int, long*, int>)optGetInt);
        if (!bound.ProbeFrameLayout())
        {
            return false;
        }

        library = bound;
        return true;
    }

    private bool ProbeFrameLayout()
    {
        var frame = _frameAlloc();
        if (frame == 0)
        {
            return false;
        }

        try
        {
            // AVFrame has no leading AVClass, so av_opt_set does not write its fields.
            // The prefix (data, linesize, extended_data, width, height, nb_samples, format)
            // is stable across FFmpeg 4.4 through 9; the buffer allocation checks it.
            Marshal.WriteInt32(frame, WidthOffset, 32);
            Marshal.WriteInt32(frame, HeightOffset, 32);
            Marshal.WriteInt32(frame, FormatOffset, PixelYuv420P);
            if (_frameGetBuffer(frame, 32) < 0)
            {
                return false;
            }

            if (!TryReadPicture(frame, out var width, out var height, out var format, out var y, out var u, out var v, out var yStride, out var uStride, out _))
            {
                return false;
            }

            // data[3] stays null for yuv420p. A shifted prefix would fail this closed.
            var data3 = Marshal.ReadIntPtr(frame, nint.Size * 3);
            return width == 32 && height == 32 && format == PixelYuv420P
                && y != 0 && u != 0 && v != 0 && data3 == 0
                && yStride >= 32 && uStride >= 16;
        }
        finally
        {
            _frameFree(&frame);
        }
    }

    private static bool TryLoadTriple(string avutilFile, string swresampleFile, string avcodecFile, out nint avutil, out nint avcodec, out string avutilName, out string avcodecName)
    {
        avutil = 0;
        avcodec = 0;
        avutilName = avutilFile;
        avcodecName = avcodecFile;
        if (!TryLoadOne(avutilFile, out avutil))
        {
            return false;
        }

        // libavcodec's DT_NEEDED / import library. Optional so a build that
        // folded swresample still proceeds to avcodec.
        TryLoadOne(swresampleFile, out _);
        return TryLoadOne(avcodecFile, out avcodec);
    }

    private static bool TryLoadOne(string fileName, out nint handle)
    {
        var local = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(local) && NativeLibrary.TryLoad(local, out handle))
        {
            return true;
        }

        return NativeLibrary.TryLoad(fileName, out handle);
    }

    private static bool TryExport(nint handle, string name, out nint address)
    {
        address = 0;
        if (handle == 0)
        {
            return false;
        }

        try
        {
            address = NativeLibrary.GetExport(handle, name);
            return address != 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static string Unversioned(string library)
        => OperatingSystem.IsWindows() ? library + ".dll" : "lib" + library + ".so";

    // Newest first. Windows: avcodec-N.dll. Linux: libavcodec.so.N.
    // 9 / current BtbN master and n9.0 ship avcodec 63, avutil 61, swresample 7.
    internal static readonly (int Avcodec, int Avutil, int Swresample)[] SonameTriples =
    [
        (63, 61, 7),
        (62, 60, 6),
        (61, 59, 5),
        (60, 58, 4),
        (59, 57, 4),
        (58, 56, 3),
    ];

    internal static string LibraryFileName(string library, int major, bool windows)
        => windows
            ? library + "-" + major.ToString(CultureInfo.InvariantCulture) + ".dll"
            : "lib" + library + ".so." + major.ToString(CultureInfo.InvariantCulture);

    private static string FileName(string library, int major)
        => LibraryFileName(library, major, OperatingSystem.IsWindows());

    private static bool TryLoadTriple(int avutilMajor, int swresampleMajor, int avcodecMajor, out nint avutil, out nint avcodec, out string avutilName, out string avcodecName)
        => TryLoadTriple(FileName("avutil", avutilMajor), FileName("swresample", swresampleMajor), FileName("avcodec", avcodecMajor), out avutil, out avcodec, out avutilName, out avcodecName);

    private static string Describe(nint versionInfo, string avcodecName, string avutilName)
    {
        var version = "libavcodec";
        if (versionInfo != 0)
        {
            version = Marshal.PtrToStringUTF8(((delegate* unmanaged[Cdecl]<nint>)versionInfo)()) ?? version;
        }

        return version + " (" + avcodecName + ", " + avutilName + ")";
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

    private static int ParseLeadingInt(string text)
    {
        var end = 0;
        while (end < text.Length && text[end] is >= '0' and <= '9')
        {
            end++;
        }

        return end > 0 && int.TryParse(text[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }
}
