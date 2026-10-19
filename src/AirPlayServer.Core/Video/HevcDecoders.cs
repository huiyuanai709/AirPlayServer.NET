namespace AirPlayServer.Core.Video;

/// <summary>
/// Picks the HEVC decoder for this process. Windows tries Media Foundation
/// first. If that transform is not installed, and on every other OS, FFmpeg is
/// used when libavcodec and libavutil load. Features bit 42 is advertised only
/// when <see cref="ShouldAdvertise"/> sees an open decoder.
/// </summary>
public static class HevcDecoders
{
    internal const string UnavailableAdvertisementMessage =
        "没有可用的 HEVC 解码器，已忽略 --h265 / --hevc，不会宣告 Features bit 42，手机继续发送 H.264。"
        + " HEVC was requested (--h265 / --hevc) but neither Media Foundation HEVC (CLSID_CMSH265DecoderMFT) nor FFmpeg (libavcodec) is available."
        + " Features bit 42 is not advertised, so the phone stays on H.264.";

    public static IHevcDecoder CreatePlatformDefault(IAirPlayLog? log = null)
        => Create(log, load: null);

    internal static IHevcDecoder Create(IAirPlayLog? log, Func<IAirPlayLog?, FFmpegLibrary?>? load, BgraBufferPool? buffers = null)
    {
#if WINDOWS_HOST
        if (OperatingSystem.IsWindows())
        {
            var mediaFoundation = new MediaFoundationHevcDecoder(log, buffers);
            if (mediaFoundation.IsAvailable)
            {
                return mediaFoundation;
            }

            mediaFoundation.Dispose();
            log?.Log(
                AirPlayLogLevel.Information,
                "本机没有 Media Foundation HEVC（CLSID_CMSH265DecoderMFT），改试 FFmpeg（libavcodec）。"
                + " Media Foundation HEVC is not available. Trying FFmpeg (libavcodec) when the shared libraries are present.");
        }
#endif
        var library = load is not null ? load(log) : FFmpegLibrary.TryLoad(log);
        if (library is not null && FFmpegHevcDecoder.TryCreate(library, log, buffers, out var ffmpeg) && ffmpeg is not null)
        {
            return ffmpeg;
        }

        return new UnavailableHevcDecoder();
    }

    /// <summary>
    /// Features bit 42 is set only when HEVC was requested and the decoder opened.
    /// A missing decoder does not advertise, so the phone stays on H.264.
    /// </summary>
    internal static bool ShouldAdvertise(bool requested, IHevcDecoder? decoder, IAirPlayLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!requested)
        {
            return false;
        }

        if (decoder is { IsAvailable: true })
        {
            log.Log(
                AirPlayLogLevel.Information,
                "HEVC mirror is enabled. Features bit 42 (SupportsScreenMultiCodec) is advertised. Decoder: " + decoder.Name + ".");
            return true;
        }

        log.Log(AirPlayLogLevel.Warning, UnavailableAdvertisementMessage);
        return false;
    }
}
