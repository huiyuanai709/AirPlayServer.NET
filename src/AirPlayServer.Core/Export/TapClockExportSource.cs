using System.Net;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Export;

/// <summary>
/// One mirror's latest frame and the data-socket peer that produced it.
/// A null peer is written as <see cref="MirrorExportNames.UnknownFileName"/>.
/// </summary>
internal readonly struct TapClockExportSource
{
    public TapClockExportSource(FrameSlot frames, IPAddress? peer, string? fileKey = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        Frames = frames;
        Peer = peer;
        FileKey = string.IsNullOrWhiteSpace(fileKey) ? null : fileKey;
    }

    public FrameSlot Frames { get; }

    public IPAddress? Peer { get; }

    /// <summary>Stable file label when there is no IP, for example <c>usb-&lt;serial&gt;</c>.</summary>
    public string? FileKey { get; }
}
