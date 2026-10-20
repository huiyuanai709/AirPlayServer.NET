using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class MirrorDiscontinuityTests
{
    [Fact]
    public void Pause_resume_edge_resets_once()
    {
        Assert.False(MirrorDiscontinuity.LeavingPause(wasPaused: false, senderPaused: false, senderResumed: true));
        Assert.False(MirrorDiscontinuity.LeavingPause(wasPaused: false, senderPaused: true, senderResumed: false));
        Assert.False(MirrorDiscontinuity.LeavingPause(wasPaused: true, senderPaused: true, senderResumed: false));
        Assert.True(MirrorDiscontinuity.LeavingPause(wasPaused: true, senderPaused: false, senderResumed: true));
        Assert.True(MirrorDiscontinuity.LeavingPause(wasPaused: true, senderPaused: false, senderResumed: false));

        Assert.True(MirrorDiscontinuity.ShouldResetDecoder(leavingPause: true, alreadyWaitingForSync: false));
        Assert.False(MirrorDiscontinuity.ShouldResetDecoder(leavingPause: true, alreadyWaitingForSync: true));
        Assert.False(MirrorDiscontinuity.ShouldResetDecoder(leavingPause: false, alreadyWaitingForSync: false));
    }

    [Fact]
    public void Sync_point_is_the_header_flag_or_an_irap_nal()
    {
        Assert.True(MirrorDiscontinuity.IsSync(hevc: false, keyFrameFlag: true, [0, 0, 0, 1, 0x41]));
        Assert.False(MirrorDiscontinuity.IsSync(hevc: false, keyFrameFlag: false, [0, 0, 0, 1, 0x41]));
        Assert.True(MirrorDiscontinuity.IsSync(hevc: false, keyFrameFlag: false, [0, 0, 0, 1, 0x65]));
        Assert.False(MirrorDiscontinuity.IsSync(hevc: false, keyFrameFlag: false, ReadOnlySpan<byte>.Empty));

        Assert.True(MirrorDiscontinuity.IsSync(hevc: true, keyFrameFlag: false, [0, 0, 0, 1, 0x26, 0x01]));
        Assert.True(MirrorDiscontinuity.IsSync(hevc: true, keyFrameFlag: false, [0, 0, 0, 1, 0x2A, 0x01]));
        Assert.True(MirrorDiscontinuity.IsSync(hevc: true, keyFrameFlag: false, [0, 0, 0, 1, 0x20, 0x01]));
        Assert.False(MirrorDiscontinuity.IsSync(hevc: true, keyFrameFlag: false, [0, 0, 0, 1, 0x02, 0x01]));
        Assert.False(MirrorDiscontinuity.IsSync(hevc: false, keyFrameFlag: false, [0, 0, 0, 1, 0x26, 0x01]));
    }
}
