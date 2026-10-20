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
/// 720×1560 is the phone's frame size. The session stays up. v0.1.12 still
/// freezes on that one frame.
/// </para>
/// <para>
/// <c>MFT_MESSAGE_COMMAND_DRAIN</c> forces the held IDR out and empties the
/// decoded picture buffer. v0.1.12 then sent flush, begin-streaming, and
/// start-of-stream to <b>that same object</b> and submitted the keyframe
/// again. Flush on the same H.264 transform already produced no further
/// pictures after an iPhone lock (the resume fix that started releasing the
/// transform). The resubmit therefore lands on an object that will not emit.
/// <c>ProcessMessage</c> failures were ignored, and a resubmit that returns
/// no picture still published the drained frame. Later P-frames hit
/// <c>MF_E_NOTACCEPTING</c> or are accepted and never come back out. The
/// window stays on the drained frame. The TCP session does not drop.
/// </para>
/// <para>
/// Drain is only how the first picture is forced out. The drained object is
/// released. The same keyframe is submitted to a new software MFT with
/// <c>MFSampleExtension_Discontinuity</c>, and that new MFT is not drained.
/// The following P-frame is what makes it paint. Hardware still never drains.
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
    /// <c>MFSampleExtension_Discontinuity</c>. The sample that follows a drain
    /// is a random-access point; CleanPoint alone does not say that.
    /// </summary>
    internal static readonly Guid DiscontinuityAttribute = new("9cdf01d9-a0f0-43ba-b077-eaa06cbd728a");

    /// <summary>
    /// Silent access units in a row after a picture has already been published.
    /// One miss is the software decoder holding a sample. Three means it
    /// stopped emitting while still accepting input.
    /// </summary>
    internal const int SoftwareStallMisses = 3;

    internal readonly struct Plan
    {
        public bool Drain { get; init; }

        /// <summary>
        /// Release the drained transform and submit the keyframe to a new one.
        /// Flushing the drained object and continuing is what v0.1.12 did.
        /// </summary>
        public bool ReplaceTransform { get; init; }

        public bool MarkDiscontinuity { get; init; }
    }

    internal readonly struct SubmitResult
    {
        public bool Accepted { get; init; }

        public bool Picture { get; init; }
    }

    internal readonly struct ResumeResult
    {
        public int DrainHresult { get; init; }

        public bool DrainedPicture { get; init; }

        public bool ReplacementPicture { get; init; }

        public bool ReplacementAccepted { get; init; }

        public bool UseReplacementPicture { get; init; }

        /// <summary>
        /// The drained picture may be published only when the new transform
        /// accepted the keyframe. Publishing it after a rejected resubmit is
        /// the frozen frame: the window has a picture and the decoder does not
        /// have a reference.
        /// </summary>
        public bool PublishDrainedPicture { get; init; }
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
            ReplaceTransform = drain,
            MarkDiscontinuity = drain,
        };
    }

    /// <summary>
    /// Drain, pull the forced picture, release that transform, then submit the
    /// keyframe to whatever <paramref name="submitReplacement"/> creates.
    /// Flush, begin-streaming, and start-of-stream are not sent here. Those
    /// messages do not make the drained object emit again.
    /// </summary>
    internal static ResumeResult ContinueAfterSoftwareDrain(
        Func<int, int> processMessage,
        Func<bool> pullPicture,
        Action releaseTransform,
        Func<SubmitResult> submitReplacement)
    {
        ArgumentNullException.ThrowIfNull(processMessage);
        ArgumentNullException.ThrowIfNull(pullPicture);
        ArgumentNullException.ThrowIfNull(releaseTransform);
        ArgumentNullException.ThrowIfNull(submitReplacement);

        var drainHr = processMessage(MessageDrain);
        var drained = pullPicture();
        releaseTransform();
        var submitted = submitReplacement();
        return new ResumeResult
        {
            DrainHresult = drainHr,
            DrainedPicture = drained,
            ReplacementPicture = submitted.Picture,
            ReplacementAccepted = submitted.Accepted,
            UseReplacementPicture = submitted.Picture,
            PublishDrainedPicture = !submitted.Picture && submitted.Accepted && drained,
        };
    }

    internal static bool ShouldRecreateAfterReject(bool hardware, bool allowRecreate, int hresult)
        => !hardware && allowRecreate && hresult == NotAccepting;

    internal static bool ShouldRecreateAfterSoftwareStall(bool hardware, bool allowRecreate, int picturesProduced, int consecutiveMisses)
        => !hardware && allowRecreate && picturesProduced > 0 && consecutiveMisses >= SoftwareStallMisses;
}
