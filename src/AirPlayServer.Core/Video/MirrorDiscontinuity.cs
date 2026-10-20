using AirPlayServer.Core.Media;

namespace AirPlayServer.Core.Video;

/// <summary>
/// What to do with the decoder when the phone locks and then resumes.
/// A 0x16/0x1e codec packet is the normal SPS/PPS (or VPS) packet, not by
/// itself a stream break. It means resume only when the sender was paused.
/// Resetting on every one of those packets releases a working decoder, and
/// the next P-frame then has no reference: Media Foundation stays on the
/// last picture, and FFmpeg HEVC logs "Could not find ref with POC".
/// </summary>
internal static class MirrorDiscontinuity
{
    internal static bool LeavingPause(bool wasPaused, bool senderPaused, bool senderResumed)
        => wasPaused && (senderResumed || !senderPaused);

    /// <summary>
    /// The pause→resume edge resets the decoder once. A later codec packet
    /// while that wait is still open must not reset again: that drops the
    /// IDR the fresh decoder just accepted, and the following P-frames paint
    /// nothing.
    /// </summary>
    internal static bool ShouldResetDecoder(bool leavingPause, bool alreadyWaitingForSync)
        => leavingPause && !alreadyWaitingForSync;

    /// <summary>
    /// Two access units with no picture mean the references are gone only after
    /// this stream has already published a frame. Before that, the software
    /// decoder is still holding the opening IDR, or the phone sent SEI. Treating
    /// those misses as a pause drops that GOP and the phone tears the session down.
    /// </summary>
    internal static bool MissesRequireSync(bool picturePublished, int consecutiveMisses)
        => picturePublished && consecutiveMisses >= HardwareDecodeStall.MissesWithNoPicture;

    internal static bool IsSync(bool hevc, bool keyFrameFlag, ReadOnlySpan<byte> annexB)
    {
        if (keyFrameFlag)
        {
            return true;
        }

        if (annexB.IsEmpty)
        {
            return false;
        }

        if (!hevc)
        {
            // H.264 IDR, even when the mirror header did not set 0x10.
            return AvccAnnexB.ContainsNalType(annexB, 5);
        }

        // HEVC IRAP: BLA 16–18, IDR 19–20, CRA 21. A P-frame after a flushed
        // DPB is what prints "Could not find ref with POC".
        for (var type = 16; type <= 21; type++)
        {
            if (HevcSequence.ContainsNalType(annexB, type))
            {
                return true;
            }
        }

        return false;
    }
}
