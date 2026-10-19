using System.Net;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Export;

/// <summary>
/// One mirror's latest frame and the data-socket peer that produced it.
/// A null peer is written as <see cref="MirrorExportNames.UnknownFileName"/>.
/// </summary>
internal readonly struct TapClockExportSource
{
    public TapClockExportSource(FrameSlot frames, IPAddress? peer)
    {
        ArgumentNullException.ThrowIfNull(frames);
        Frames = frames;
        Peer = peer;
    }

    public FrameSlot Frames { get; }

    public IPAddress? Peer { get; }
}
