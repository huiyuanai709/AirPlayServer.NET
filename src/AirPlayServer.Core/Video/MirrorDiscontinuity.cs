using AirPlayServer.Core.Media;

namespace AirPlayServer.Core.Video;

/// <summary>
/// What to do with the decoder when the phone locks and then resumes.
/// A 0x16/0x1e codec packet is the normal SPS/PPS (or VPS) packet. It means
/// resume only when the sender was paused. Pause and resume do not reset
/// the decoder: the phone keeps the same references and the next access
/// unit is often a P-frame. Reopening and then waiting for an IDR drops
/// those units, so the window stays on the last picture.
/// </summary>
internal static class MirrorDiscontinuity
{
    internal static bool LeavingPause(bool wasPaused, bool senderPaused, bool senderResumed)
        => wasPaused && (senderResumed || !senderPaused);

    /// <summary>
    /// HEVC only. Two coded slices with no picture, after a picture was already
    /// published, are treated as a broken reference picture set. FFmpeg then
    /// logs "Could not find ref with POC" on every later P-frame, so those
    /// slices wait for an IRAP.
    /// H.264 does not. Media Foundation returns no picture for a held sample
    /// and for a transient miss while the DPB is still valid. Resetting on
    /// that and discarding every later P-frame waits for an IDR the phone
    /// does not send until the next SETUP, which is the freeze after
    /// <c>DecodedFrames</c> has already moved. The open H.264 decoder keeps
    /// those slices.
    /// Misses before the first picture are startup latency for both codecs.
    /// </summary>
    internal static bool MissesRequireSync(bool hevc, bool picturePublished, int consecutiveMisses)
        => hevc && picturePublished && consecutiveMisses >= HardwareDecodeStall.MissesWithNoPicture;

    /// <summary>
    /// True when the access unit contains a coded slice. SEI, AUD, filler,
    /// and parameter sets are not evidence that a reference was dropped.
    /// </summary>
    internal static bool HasCodedSlice(bool hevc, ReadOnlySpan<byte> annexB)
    {
        if (annexB.IsEmpty)
        {
            return false;
        }

        if (hevc)
        {
            return HevcSequence.ContainsVcl(annexB);
        }

        for (var type = 1; type <= 5; type++)
        {
            if (AvccAnnexB.ContainsNalType(annexB, type))
            {
                return true;
            }
        }

        return false;
    }

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
