using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Media;

/// <summary>
/// Decoders, frame slot, and FairPlay state for one additional mirror.
/// FairPlay, the decryptor, the sockets, and the decoder instance stay on this
/// device. The BGRA pool may be the receiver-wide pool; a checked-out array is
/// still owned by one session until it is returned.
/// </summary>
internal sealed class MirrorEndpointResources
{
    public required FrameSlot Frames { get; init; }

    public required IH264Decoder Decoder { get; init; }

    public IHevcDecoder? Hevc { get; init; }

    public IMediaKeyUnwrapper? Unwrapper { get; init; }

    public IFairPlaySetup? FairPlay { get; init; }
}
