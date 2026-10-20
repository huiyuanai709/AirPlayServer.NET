namespace AirPlayServer.Core.Video;

/// <summary>
/// What a software Media Foundation decoder must do after draining the first
/// picture. Hardware transforms are never drained: that flush drops reference
/// frames and the window stays on the last picture.
/// </summary>
/// <remarks>
/// <para>
/// Resume with unchanged SPS/PPS releases the transform. The next keyframe
/// builds a new software MFT whose picture count is zero, so it takes this
/// path. The live Windows log is <c>Mirror sender resumed</c>, then
/// <c>Mirror geometry 720x1560</c>, then
/// <c>NV12 to BGRA is BT.709 full range</c>, then
/// <c>H.264 decoder is the Microsoft Media Foundation software decoder</c>.
/// 720×1560 is the phone's frame size. The session stays up.
/// </para>
/// <para>
/// <c>MFT_MESSAGE_COMMAND_DRAIN</c> forces that first IDR out, and it also
/// drops the decoded picture buffer. <c>MFT_MESSAGE_NOTIFY_START_OF_STREAM</c>
/// alone leaves <c>CLSID_CMSH264DecoderMFT</c> returning
/// <c>MF_E_NOTACCEPTING</c> (0xC00D36B5). Later access units never publish, so
/// the window freezes on the drained frame. The restart is flush, then begin
/// streaming, then start of stream, then the same keyframe submitted again
/// with drain disabled so the following P-frames still have a reference.
/// </para>
/// </remarks>
internal static class MediaFoundationDrainRestart
{
    internal const int MessageFlush = 0;

    internal const int MessageDrain = 1;

    internal const int MessageBeginStreaming = 0x10000000;

    internal const int MessageStartOfStream = 0x10000003;

    /// <summary>
    /// Sent after <see cref="MessageDrain"/> and before the keyframe is submitted again.
    /// Flush is first. Start-of-stream alone does not accept new input.
    /// </summary>
    internal static readonly int[] MessagesAfterDrain =
    [
        MessageFlush,
        MessageBeginStreaming,
        MessageStartOfStream,
    ];

    internal readonly struct Plan
    {
        public bool Drain { get; init; }

        public bool ResubmitKeyframe { get; init; }
    }

    internal static Plan For(
        bool allowDrain,
        bool outputUnsupported,
        bool producedPicture,
        bool keyFrame,
        int picturesProduced,
        bool hardware)
    {
        var drain = allowDrain
            && !outputUnsupported
            && !producedPicture
            && keyFrame
            && picturesProduced == 0
            && !hardware;
        return new Plan
        {
            Drain = drain,
            ResubmitKeyframe = drain,
        };
    }
}
