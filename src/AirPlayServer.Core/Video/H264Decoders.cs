namespace AirPlayServer.Core.Video;

/// <summary>
/// Picks the H.264 decoder for this process. Windows uses Media Foundation when
/// it opens. Linux, and Windows if Media Foundation cannot open, use FFmpeg
/// when libavcodec and libavutil load. Otherwise the decoder is
/// <see cref="UnavailableH264Decoder"/>.
/// </summary>
public static class H264Decoders
{
    public static IH264Decoder CreatePlatformDefault(IAirPlayLog? log = null)
        => Create(log, load: null);

    internal static IH264Decoder Create(IAirPlayLog? log, Func<IAirPlayLog?, FFmpegLibrary?>? load, BgraBufferPool? buffers = null)
    {
#if WINDOWS_HOST
        if (OperatingSystem.IsWindows())
        {
            var mediaFoundation = new MediaFoundationH264Decoder(log, buffers);
            if (mediaFoundation.IsAvailable)
            {
                return mediaFoundation;
            }

            mediaFoundation.Dispose();
            log?.Log(
                AirPlayLogLevel.Information,
                "Media Foundation H.264 打不开，改试 FFmpeg（libavcodec）。Media Foundation H.264 is not available. Trying FFmpeg (libavcodec).");
        }
#endif
        var library = load is not null ? load(log) : FFmpegLibrary.TryLoad(log);
        if (library is not null && FFmpegH264Decoder.TryCreate(library, log, buffers, out var ffmpeg) && ffmpeg is not null)
        {
            return ffmpeg;
        }

        return new UnavailableH264Decoder();
    }

    internal static bool NegotiatedOutput(IH264Decoder decoder)
    {
#if WINDOWS_HOST
        if (decoder is MediaFoundationH264Decoder mediaFoundation)
        {
            return mediaFoundation.HasNegotiatedOutput;
        }
#else
        _ = decoder;
#endif
        return false;
    }
}
