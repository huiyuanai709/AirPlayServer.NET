using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Export;

public static class ExportSelfCheck
{
    public static void Run()
    {
        var slot = new FrameSlot();
        DemoFrame.Publish(slot, 16, 16);
        if (!slot.TryRead(out var width, out var height, out var stride, out var pixels, out _))
        {
            throw new InvalidOperationException("Demo frame was not published.");
        }

        var jpeg = BaselineJpegEncoder.EncodeBgra(pixels, width, height, stride, 75);
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || jpeg[^2] != 0xFF || jpeg[^1] != 0xD9
            || !BaselineJpegEncoder.TryReadSize(jpeg, out var jpegWidth, out var jpegHeight)
            || jpegWidth != width || jpegHeight != height)
        {
            throw new InvalidOperationException("JPEG self-check did not produce a baseline image.");
        }

        var oddWidth = 9;
        var oddHeight = 7;
        var oddStride = (oddWidth * 4) + 8;
        var odd = new byte[oddStride * oddHeight];
        odd[2] = 180;
        var oddJpeg = BaselineJpegEncoder.EncodeBgra(odd, oddWidth, oddHeight, oddStride, 100);
        if (!BaselineJpegEncoder.TryReadSize(oddJpeg, out jpegWidth, out jpegHeight) || jpegWidth != oddWidth || jpegHeight != oddHeight)
        {
            throw new InvalidOperationException("JPEG self-check rejected a padded non-multiple-of-8 frame.");
        }

        var packet = AtbfFraming.ImageFrame(jpeg);
        if (!AtbfFraming.TryReadHeader(packet, out var type, out var flags, out var length)
            || type != AtbfFraming.TypeImage
            || flags != AtbfFraming.FlagJpeg
            || length != jpeg.Length
            || packet.Length != AtbfFraming.HeaderLength + jpeg.Length)
        {
            throw new InvalidOperationException("ATBF self-check failed.");
        }
    }
}
