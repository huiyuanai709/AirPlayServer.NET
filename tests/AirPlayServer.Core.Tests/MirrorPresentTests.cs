using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class MirrorPresentTests
{
    [Fact]
    public void Hardware_stall_keeps_one_silent_startup_unit_then_releases()
    {
        Assert.False(HardwareDecodeStall.ShouldAbandon(picturesProduced: 0, consecutiveMisses: 0));
        Assert.False(HardwareDecodeStall.ShouldAbandon(picturesProduced: 0, consecutiveMisses: 1));
        Assert.True(HardwareDecodeStall.ShouldAbandon(picturesProduced: 0, consecutiveMisses: 2));
        Assert.False(HardwareDecodeStall.ShouldAbandon(picturesProduced: 1, consecutiveMisses: 0));
        Assert.True(HardwareDecodeStall.ShouldAbandon(picturesProduced: 1, consecutiveMisses: 1));
        Assert.True(HardwareDecodeStall.ShouldAbandon(picturesProduced: 8, consecutiveMisses: 3));
        Assert.False(HardwareDecodeStall.ShouldAbandon(picturesProduced: -1, consecutiveMisses: 4));
    }

    [Fact]
    public void Resume_does_not_drain_the_software_transform_that_is_holding_the_idr()
    {
        // Mirror sender resumed, geometry 720x1560, then a new software MFT
        // (pictures == 0). The first keyframe has not produced a picture yet.
        // v0.1.12 flushed that object. v0.1.14 released it and resubmitted the
        // keyframe. Both left the window on one frame. Drain stays off.
        Assert.False(MediaFoundationDrainRestart.ShouldDrain(true, false, false, true, picturesProduced: 0, hardware: false));
        Assert.True(MediaFoundationDrainRestart.HoldsFirstKeyframe(false, false, true, picturesProduced: 0, hardware: false));
        Assert.False(MediaFoundationDrainRestart.ShouldDrain(true, false, false, true, picturesProduced: 1, hardware: false));
        Assert.False(MediaFoundationDrainRestart.HoldsFirstKeyframe(false, false, true, picturesProduced: 1, hardware: false));
        Assert.False(MediaFoundationDrainRestart.ShouldDrain(true, false, false, true, picturesProduced: 0, hardware: true));
        Assert.False(MediaFoundationDrainRestart.HoldsFirstKeyframe(false, false, true, picturesProduced: 0, hardware: true));
        Assert.False(MediaFoundationDrainRestart.ShouldDrain(true, false, producedPicture: true, true, 0, false));
        Assert.False(MediaFoundationDrainRestart.HoldsFirstKeyframe(false, producedPicture: true, true, 0, false));
        Assert.False(MediaFoundationDrainRestart.ShouldDrain(false, false, false, true, 0, false));
        Assert.False(MediaFoundationDrainRestart.ShouldDrain(true, outputUnsupported: true, false, true, 0, false));
        Assert.False(MediaFoundationDrainRestart.HoldsFirstKeyframe(outputUnsupported: true, false, true, 0, false));
        Assert.False(MediaFoundationDrainRestart.ShouldDrain(true, false, false, keyFrame: false, 0, false));
        Assert.False(MediaFoundationDrainRestart.HoldsFirstKeyframe(false, false, keyFrame: false, 0, false));
        Assert.False(MediaFoundationDrainRestart.ShouldDrain(true, false, false, true, picturesProduced: -1, hardware: false));
    }

    [Fact]
    public void Resume_v0112_flush_on_the_drained_object_publishes_one_frame()
    {
        // Stand-in for CLSID_CMSH264DecoderMFT. Linux CI cannot load it.
        // After drain, flush + begin + start on the same object does not emit
        // again. That is the v0.1.12 resume freeze: one picture, session stays up.
        var mft = new SoftwareDecoderStandIn();
        Assert.Equal(0, mft.ProcessInput());
        Assert.False(mft.Pull());
        Assert.Equal(0, mft.ProcessMessage(MediaFoundationDrainRestart.MessageDrain));
        Assert.True(mft.Pull());
        Assert.Equal(0, mft.ProcessMessage(MediaFoundationDrainRestart.MessageFlush));
        Assert.Equal(0, mft.ProcessMessage(MediaFoundationDrainRestart.MessageBeginStreaming));
        Assert.Equal(0, mft.ProcessMessage(MediaFoundationDrainRestart.MessageStartOfStream));
        Assert.Equal(MediaFoundationDrainRestart.NotAccepting, mft.ProcessInput());
        Assert.False(mft.Pull());
        Assert.False(mft.SubmitFollowingFrame());
        Assert.Equal(1, mft.Pictures);
        Assert.Equal(0, mft.Releases);
    }

    [Fact]
    public void Resume_leaves_the_idr_in_the_open_transform_until_the_next_access_unit()
    {
        // The Microsoft software decoder holds the first sample and emits when
        // the next access unit arrives. Drain would end that object. Linux CI
        // cannot load CLSID_CMSH264DecoderMFT, so this stand-in is the policy.
        var mft = new SoftwareDecoderStandIn();
        Assert.Equal(0, mft.ProcessInput());
        Assert.False(mft.Pull());
        Assert.Empty(mft.Messages);
        Assert.Equal(0, mft.Releases);
        Assert.True(mft.SubmitFollowingFrame());
        Assert.Equal(1, mft.Pictures);
        Assert.False(mft.Pull());
    }

    [Fact]
    public void Software_reject_and_stall_recreate_the_transform_hardware_does_not()
    {
        Assert.True(MediaFoundationDrainRestart.ShouldRecreateAfterReject(hardware: false, allowRecreate: true, MediaFoundationDrainRestart.NotAccepting));
        Assert.False(MediaFoundationDrainRestart.ShouldRecreateAfterReject(hardware: true, allowRecreate: true, MediaFoundationDrainRestart.NotAccepting));
        Assert.False(MediaFoundationDrainRestart.ShouldRecreateAfterReject(hardware: false, allowRecreate: false, MediaFoundationDrainRestart.NotAccepting));
        Assert.False(MediaFoundationDrainRestart.ShouldRecreateAfterReject(hardware: false, allowRecreate: true, hresult: -1));

        Assert.False(MediaFoundationDrainRestart.ShouldRecreateAfterSoftwareStall(false, true, picturesProduced: 1, consecutiveMisses: 2));
        Assert.True(MediaFoundationDrainRestart.ShouldRecreateAfterSoftwareStall(false, true, picturesProduced: 1, consecutiveMisses: MediaFoundationDrainRestart.SoftwareStallMisses));
        Assert.False(MediaFoundationDrainRestart.ShouldRecreateAfterSoftwareStall(false, true, picturesProduced: 0, consecutiveMisses: 4));
        Assert.False(MediaFoundationDrainRestart.ShouldRecreateAfterSoftwareStall(true, true, picturesProduced: 3, consecutiveMisses: 4));
        Assert.False(MediaFoundationDrainRestart.ShouldRecreateAfterSoftwareStall(false, allowRecreate: false, picturesProduced: 2, consecutiveMisses: 4));
    }

    /// <summary>
    /// Behavior measured on the Microsoft software H.264 decoder: the first
    /// sample is held until the next access unit. Drain forces that picture
    /// out and the same object then refuses input. Flush, begin-streaming,
    /// and start-of-stream do not revive it. Media Foundation cannot run on
    /// Linux CI.
    /// </summary>
    private sealed class SoftwareDecoderStandIn
    {
        private bool _alive = true;
        private bool _holding;
        private bool _picturePending;

        public List<int> Messages { get; } = [];

        public int Pictures { get; private set; }

        public int Releases { get; private set; }

        public int ProcessInput()
        {
            if (!_alive)
            {
                return MediaFoundationDrainRestart.NotAccepting;
            }

            _holding = true;
            return 0;
        }

        public int ProcessMessage(int message)
        {
            Messages.Add(message);
            if (message == MediaFoundationDrainRestart.MessageDrain && _holding)
            {
                _holding = false;
                _picturePending = true;
                _alive = false;
            }

            if (message == MediaFoundationDrainRestart.MessageFlush
                || message == MediaFoundationDrainRestart.MessageBeginStreaming
                || message == MediaFoundationDrainRestart.MessageStartOfStream)
            {
                _alive = false;
                _holding = false;
            }

            return 0;
        }

        public bool Pull()
        {
            if (!_picturePending)
            {
                return false;
            }

            _picturePending = false;
            Pictures++;
            return true;
        }

        public void Release()
        {
            Releases++;
            _alive = true;
            _holding = false;
            _picturePending = false;
        }

        public bool SubmitFollowingFrame()
        {
            if (!_alive || !_holding)
            {
                return false;
            }

            _holding = false;
            Pictures++;
            return true;
        }
    }

    [Fact]
    public void Letterboxed_bgra_lands_upright_and_leaves_the_bars_black()
    {
        var source = new byte[4 * 2 * 4];
        Paint(source, 0, 0, 4, 10, 20, 30);
        Paint(source, 3, 0, 4, 40, 50, 60);
        Paint(source, 0, 1, 4, 70, 80, 90);
        Paint(source, 3, 1, 4, 100, 110, 120);
        var destination = new byte[6 * 4 * 4];
        destination.AsSpan().Fill(7);

        Assert.True(BgraScale.PaintLetterbox(
            source,
            sourceWidth: 4,
            sourceHeight: 2,
            sourceStride: 16,
            destination,
            destinationWidth: 6,
            destinationHeight: 4,
            destinationStride: 24,
            originX: 1,
            originY: 1,
            viewWidth: 4,
            viewHeight: 2,
            bottomUp: false));

        AssertUnpainted(destination, 0, 0, 6);
        AssertPixel(destination, 1, 1, 6, 10, 20, 30);
        AssertPixel(destination, 4, 1, 6, 40, 50, 60);
        AssertPixel(destination, 1, 2, 6, 70, 80, 90);
        AssertPixel(destination, 4, 2, 6, 100, 110, 120);
        AssertUnpainted(destination, 5, 3, 6);
        Assert.Equal(255, destination[(1 * 6 + 1) * 4 + 3]);
    }

    [Fact]
    public void Bottom_up_window_buffer_puts_the_top_row_at_the_end()
    {
        var source = new byte[2 * 2 * 4];
        Paint(source, 0, 0, 2, 1, 2, 3);
        Paint(source, 1, 1, 2, 4, 5, 6);
        var destination = new byte[2 * 2 * 4];

        Assert.True(BgraScale.PaintLetterbox(
            source,
            2,
            2,
            8,
            destination,
            2,
            2,
            8,
            0,
            0,
            2,
            2,
            bottomUp: true));

        AssertPixel(destination, 0, 1, 2, 1, 2, 3);
        AssertPixel(destination, 1, 0, 2, 4, 5, 6);
    }

    [Fact]
    public void Tall_phone_frame_samples_the_top_and_bottom()
    {
        var source = new byte[2 * 8 * 4];
        Paint(source, 0, 0, 2, 11, 0, 0);
        Paint(source, 0, 7, 2, 0, 22, 0);
        var destination = new byte[2 * 4 * 4];

        Assert.True(BgraScale.PaintLetterbox(source, 2, 8, 8, destination, 2, 4, 8, 0, 0, 2, 4, bottomUp: false));
        AssertPixel(destination, 0, 0, 2, 11, 0, 0);
        AssertPixel(destination, 0, 3, 2, 0, 22, 0);
        Assert.False(BgraScale.PaintLetterbox(source, 2, 8, 8, destination, 2, 4, 8, 0, 0, 0, 4, bottomUp: false));
    }

    [Fact]
    public void Scaled_letterbox_matches_nearest_neighbor_and_forces_alpha()
    {
        var sizes = new (int sw, int sh, int dw, int dh, int vw, int vh, int ox, int oy, bool bottomUp, int pad)[]
        {
            (4, 2, 4, 2, 4, 2, 0, 0, false, 0),
            (4, 2, 6, 4, 4, 2, 1, 1, false, 4),
            (5, 3, 3, 2, 3, 2, 0, 0, false, 0),
            (2, 2, 5, 4, 5, 4, 0, 0, true, 0),
            (3, 5, 7, 6, 4, 3, 2, 1, true, 8),
            (1, 8, 1, 3, 1, 3, 0, 0, false, 0),
            (8, 1, 3, 1, 3, 1, 0, 0, true, 0),
        };

        foreach (var size in sizes)
        {
            var sourceStride = (size.sw * 4) + size.pad;
            var source = new byte[sourceStride * size.sh];
            for (var y = 0; y < size.sh; y++)
            {
                for (var x = 0; x < size.sw; x++)
                {
                    var index = (y * sourceStride) + (x * 4);
                    source[index] = (byte)(10 + x);
                    source[index + 1] = (byte)(20 + y);
                    source[index + 2] = (byte)(30 + x + y);
                    source[index + 3] = 1;
                }
            }

            var destinationStride = size.dw * 4;
            var actual = new byte[destinationStride * size.dh];
            var expected = new byte[actual.Length];
            actual.AsSpan().Fill(7);
            expected.AsSpan().Fill(7);
            Assert.True(BgraScale.PaintLetterbox(
                source,
                size.sw,
                size.sh,
                sourceStride,
                actual,
                size.dw,
                size.dh,
                destinationStride,
                size.ox,
                size.oy,
                size.vw,
                size.vh,
                size.bottomUp));
            PaintReference(source, size.sw, size.sh, sourceStride, expected, size.dw, size.dh, destinationStride, size.ox, size.oy, size.vw, size.vh, size.bottomUp);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Rgb32_rows_are_copied_upright_or_bottom_up_with_opaque_alpha()
    {
        var source = new byte[8 * 2];
        Paint(source, 0, 0, 2, 1, 2, 3);
        Paint(source, 1, 0, 2, 4, 5, 6);
        Paint(source, 0, 1, 2, 7, 8, 9);
        Paint(source, 1, 1, 2, 10, 11, 12);
        var topDown = new byte[16];
        var bottomUp = new byte[16];
        Assert.True(BgraRows.TryCopyRgb32(source, 2, 2, 8, false, 2, 2, topDown));
        Assert.True(BgraRows.TryCopyRgb32(source, 2, 2, 8, true, 2, 2, bottomUp));
        Assert.Equal(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255 }, topDown);
        Assert.Equal(new byte[] { 7, 8, 9, 255, 10, 11, 12, 255, 1, 2, 3, 255, 4, 5, 6, 255 }, bottomUp);
    }

    private static void PaintReference(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        byte[] destination,
        int destinationWidth,
        int destinationHeight,
        int destinationStride,
        int originX,
        int originY,
        int viewWidth,
        int viewHeight,
        bool bottomUp)
    {
        if (originX > destinationWidth - viewWidth)
        {
            viewWidth = destinationWidth - originX;
        }

        if (originY > destinationHeight - viewHeight)
        {
            viewHeight = destinationHeight - originY;
        }

        for (var y = 0; y < viewHeight; y++)
        {
            var sourceY = Sample(y, viewHeight, sourceHeight);
            var destY = originY + y;
            if (bottomUp)
            {
                destY = destinationHeight - 1 - destY;
            }

            for (var x = 0; x < viewWidth; x++)
            {
                var sourceX = Sample(x, viewWidth, sourceWidth);
                var from = (sourceY * sourceStride) + (sourceX * 4);
                var to = (destY * destinationStride) + ((originX + x) * 4);
                destination[to] = source[from];
                destination[to + 1] = source[from + 1];
                destination[to + 2] = source[from + 2];
                destination[to + 3] = 255;
            }
        }
    }

    private static int Sample(int position, int view, int source)
    {
        if (view <= 1)
        {
            return source / 2;
        }

        var index = (int)((long)position * (source - 1) / (view - 1));
        return index >= source ? source - 1 : index;
    }

    private static void Paint(byte[] pixels, int x, int y, int width, byte b, byte g, byte r)
    {
        var index = ((y * width) + x) * 4;
        pixels[index] = b;
        pixels[index + 1] = g;
        pixels[index + 2] = r;
        pixels[index + 3] = 1;
    }

    private static void AssertUnpainted(byte[] pixels, int x, int y, int width)
    {
        var index = ((y * width) + x) * 4;
        Assert.Equal(7, pixels[index]);
        Assert.Equal(7, pixels[index + 1]);
        Assert.Equal(7, pixels[index + 2]);
        Assert.Equal(7, pixels[index + 3]);
    }

    private static void AssertPixel(byte[] pixels, int x, int y, int width, byte b, byte g, byte r)
    {
        var index = ((y * width) + x) * 4;
        Assert.Equal(b, pixels[index]);
        Assert.Equal(g, pixels[index + 1]);
        Assert.Equal(r, pixels[index + 2]);
        Assert.Equal(255, pixels[index + 3]);
    }
}
