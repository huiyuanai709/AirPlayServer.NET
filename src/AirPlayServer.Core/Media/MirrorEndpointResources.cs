using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Media;

/// <summary>
/// Decoders, frame slot, and FairPlay state for one additional mirror.
/// The arrays and the FairPlay handshake are not shared with another device.
/// </summary>
internal sealed class MirrorEndpointResources
{
    public required FrameSlot Frames { get; init; }

    public required IH264Decoder Decoder { get; init; }

    public IHevcDecoder? Hevc { get; init; }

    public IMediaKeyUnwrapper? Unwrapper { get; init; }

    public IFairPlaySetup? FairPlay { get; init; }
}
