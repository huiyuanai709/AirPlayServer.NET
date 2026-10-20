namespace AirPlayServer.Core.Video;

/// <summary>
/// When a Media Foundation decoder may be rebuilt. A live transform is never
/// drained. Hardware and software both keep the access unit that was accepted.
/// </summary>
/// <remarks>
/// <para>
/// Resume with unchanged SPS/PPS releases the transform. The next keyframe
/// builds a new software MFT whose picture count is zero. The live Windows
/// log is <c>Mirror sender resumed</c>, then <c>Mirror geometry 720x1560</c>,
/// then <c>NV12 to BGRA is BT.709 full range</c>, then
/// <c>H.264 decoder is the Microsoft Media Foundation software decoder</c>.
/// Those lines are written when the transform is created, not when a picture
/// is copied out. 720×1560 is the phone's frame size. The TCP session stays up.
/// </para>
/// <para>
/// <c>MFT_MESSAGE_COMMAND_DRAIN</c> forces the held IDR out and ends the
/// stream. v0.1.12 then sent flush, begin-streaming, and start-of-stream to
/// that same object and submitted the keyframe again. The resubmit does not
/// emit. v0.1.14 released the drained object and submitted the keyframe to a
/// new MFT with <c>MFSampleExtension_Discontinuity</c>, and published the
/// drained picture when the new MFT accepted input but returned nothing.
/// The window still stayed on that one frame. Drain is not used on a live
/// transform. The IDR stays in the open MFT. The next access unit pulls the
/// picture. Begin-streaming and start-of-stream are still sent once, when the
/// output type is first applied.
/// </para>
/// </remarks>
internal static class MediaFoundationDrainRestart
{
    internal const int MessageFlush = 0;

    internal const int MessageDrain = 1;

    internal const int MessageBeginStreaming = 0x10000000;

    internal const int MessageStartOfStream = 0x10000003;

    internal const int NotAccepting = unchecked((int)0xC00D36B5);

    /// <summary>
    /// Silent access units in a row after a picture has already been published.
    /// One miss is the software decoder holding a sample. Three means it
    /// stopped emitting while still accepting input.
    /// </summary>
    internal const int SoftwareStallMisses = 3;

    /// <summary>
    /// Drain always returns false. The arguments are the case that used to
    /// drain (a new software MFT, first keyframe, no picture yet) so a caller
    /// cannot turn it back on for that case alone.
    /// </summary>
    internal static bool ShouldDrain(
        bool allowDrain,
        bool outputUnsupported,
        bool producedPicture,
        bool keyFrame,
        int picturesProduced,
        bool hardware)
    {
        _ = allowDrain;
        _ = outputUnsupported;
        _ = producedPicture;
        _ = keyFrame;
        _ = picturesProduced;
        _ = hardware;
        return false;
    }

    /// <summary>
    /// The first keyframe on a new software transform is expected to sit
    /// inside the MFT until the next access unit. That is not a stall and it
    /// is not a reason to drain.
    /// </summary>
    internal static bool HoldsFirstKeyframe(
        bool outputUnsupported,
        bool producedPicture,
        bool keyFrame,
        int picturesProduced,
        bool hardware)
        => !outputUnsupported
            && !producedPicture
            && keyFrame
            && picturesProduced == 0
            && !hardware;

    internal static bool ShouldRecreateAfterReject(bool hardware, bool allowRecreate, int hresult)
        => !hardware && allowRecreate && hresult == NotAccepting;

    internal static bool ShouldRecreateAfterSoftwareStall(bool hardware, bool allowRecreate, int picturesProduced, int consecutiveMisses)
        => !hardware && allowRecreate && picturesProduced > 0 && consecutiveMisses >= SoftwareStallMisses;
}
