using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class MirrorDiscontinuityTests
{
    [Fact]
    public void Pause_resume_edge_is_only_the_flag()
    {
        Assert.False(MirrorDiscontinuity.LeavingPause(wasPaused: false, senderPaused: false, senderResumed: true));
        Assert.False(MirrorDiscontinuity.LeavingPause(wasPaused: false, senderPaused: true, senderResumed: false));
        Assert.False(MirrorDiscontinuity.LeavingPause(wasPaused: true, senderPaused: true, senderResumed: false));
        Assert.True(MirrorDiscontinuity.LeavingPause(wasPaused: true, senderPaused: false, senderResumed: true));
        Assert.True(MirrorDiscontinuity.LeavingPause(wasPaused: true, senderPaused: false, senderResumed: false));
    }

    [Fact]
    public void Startup_misses_are_not_a_pause()
    {
        Assert.False(MirrorDiscontinuity.MissesRequireSync(hevc: true, picturePublished: false, consecutiveMisses: 1));
        Assert.False(MirrorDiscontinuity.MissesRequireSync(hevc: true, picturePublished: false, consecutiveMisses: 2));
        Assert.False(MirrorDiscontinuity.MissesRequireSync(hevc: true, picturePublished: false, consecutiveMisses: 8));
        Assert.False(MirrorDiscontinuity.MissesRequireSync(hevc: false, picturePublished: false, consecutiveMisses: 8));
        Assert.False(MirrorDiscontinuity.MissesRequireSync(hevc: true, picturePublished: true, consecutiveMisses: 1));
        Assert.True(MirrorDiscontinuity.MissesRequireSync(hevc: true, picturePublished: true, consecutiveMisses: 2));
    }

    [Fact]
    public void H264_misses_after_a_picture_do_not_wait_for_an_idr()
    {
        Assert.False(MirrorDiscontinuity.MissesRequireSync(hevc: false, picturePublished: true, consecutiveMisses: 1));
        Assert.False(MirrorDiscontinuity.MissesRequireSync(hevc: false, picturePublished: true, consecutiveMisses: 2));
        Assert.False(MirrorDiscontinuity.MissesRequireSync(hevc: false, picturePublished: true, consecutiveMisses: 8));
    }

    [Fact]
    public void Coded_slice_is_not_sei_aud_or_parameter_sets()
    {
        Assert.True(MirrorDiscontinuity.HasCodedSlice(hevc: false, [0, 0, 0, 1, 0x41]));
        Assert.True(MirrorDiscontinuity.HasCodedSlice(hevc: false, [0, 0, 0, 1, 0x65]));
        Assert.True(MirrorDiscontinuity.HasCodedSlice(hevc: false, [0, 0, 0, 1, 0x06, 0x01, 0, 0, 0, 1, 0x41]));
        Assert.False(MirrorDiscontinuity.HasCodedSlice(hevc: false, [0, 0, 0, 1, 0x06]));
        Assert.False(MirrorDiscontinuity.HasCodedSlice(hevc: false, [0, 0, 0, 1, 0x09, 0x10]));
        Assert.False(MirrorDiscontinuity.HasCodedSlice(hevc: false, [0, 0, 0, 1, 0x67, 0x42]));
        Assert.False(MirrorDiscontinuity.HasCodedSlice(hevc: false, [0, 0, 0, 1, 0x68, 0xCE]));
        Assert.False(MirrorDiscontinuity.HasCodedSlice(hevc: false, ReadOnlySpan<byte>.Empty));

        Assert.True(MirrorDiscontinuity.HasCodedSlice(hevc: true, [0, 0, 0, 1, 0x02, 0x01]));
        Assert.True(MirrorDiscontinuity.HasCodedSlice(hevc: true, [0, 0, 0, 1, 0x26, 0x01]));
        Assert.False(MirrorDiscontinuity.HasCodedSlice(hevc: true, [0, 0, 0, 1, 0x40, 0x01]));
        Assert.False(MirrorDiscontinuity.HasCodedSlice(hevc: true, [0, 0, 0, 1, 0x4E, 0x01]));
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
