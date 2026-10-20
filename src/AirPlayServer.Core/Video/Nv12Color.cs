namespace AirPlayServer.Core.Video;

/// <summary>
/// Y'CbCr matrix used to turn NV12 into BGRA. AirPlay screen mirroring is BT.709.
/// </summary>
internal enum YuvMatrix
{
    Bt709 = 0,
    Bt601 = 1,
}

/// <summary>
/// Studio swing (16–235) or full swing (0–255). iPhone mirroring is full range.
/// </summary>
internal enum YuvRange
{
    Limited = 0,
    Full = 1,
}

/// <summary>
/// Color space for one NV12 picture. <see cref="Resolve"/> prefers an explicit
/// SPS VUI value. An unspecified matrix coefficient is not a decision and does
/// not let a decoder's default BT.601 tag override the AirPlay default. Media
/// Foundation attributes fill in only what the bitstream left out. The AirPlay
/// screen default is BT.709 full range.
/// </summary>
internal readonly record struct YuvColorSpace(YuvMatrix Matrix, YuvRange Range)
{
    public static YuvColorSpace AirPlayMirror { get; } = new(YuvMatrix.Bt709, YuvRange.Full);

    public string Describe()
        => (Matrix == YuvMatrix.Bt709 ? "BT.709" : "BT.601")
            + (Range == YuvRange.Full ? " full range" : " limited range");

    public static YuvColorSpace Resolve(H264ColorSignals bitstream, int? mediaFoundationMatrix, int? mediaFoundationNominalRange)
    {
        var matrix = YuvMatrix.Bt709;
        var range = YuvRange.Full;
        if (bitstream.HasMatrix)
        {
            if (TryMapMatrixCoefficient(bitstream.MatrixCoefficients, out var fromBitstream))
            {
                matrix = fromBitstream;
            }
        }
        else if (mediaFoundationMatrix is int mfMatrix && TryMapMediaFoundationMatrix(mfMatrix, out var fromMediaType))
        {
            matrix = fromMediaType;
        }

        if (bitstream.HasRange)
        {
            range = bitstream.FullRange ? YuvRange.Full : YuvRange.Limited;
        }
        else if (mediaFoundationNominalRange is int mfRange && TryMapNominalRange(mfRange, out var fromRange))
        {
            range = fromRange;
        }

        return new YuvColorSpace(matrix, range);
    }

    /// <summary>
    /// One log line. Matrix and range can come from different places when the
    /// bitstream only carries one of them.
    /// </summary>
    public static string DescribeChoice(YuvColorSpace color, H264ColorSignals bitstream, int? mediaFoundationMatrix, int? mediaFoundationNominalRange)
    {
        var matrixSource = bitstream.HasMatrix && TryMapMatrixCoefficient(bitstream.MatrixCoefficients, out _)
            ? "sequence parameter set"
            : !bitstream.HasMatrix && mediaFoundationMatrix is int mfMatrix && TryMapMediaFoundationMatrix(mfMatrix, out _)
                ? "media type"
                : "AirPlay default";
        var rangeSource = bitstream.HasRange
            ? "sequence parameter set"
            : mediaFoundationNominalRange is int mfRange && TryMapNominalRange(mfRange, out _)
                ? "media type"
                : "AirPlay default";
        return color.Describe() + " (matrix: " + matrixSource + ", range: " + rangeSource + ")";
    }

    /// <summary>
    /// H.264 <c>matrix_coefficients</c> (Table E-5). Unspecified values (0 and 2)
    /// are not a decision, so the caller can fall through to the media type or the
    /// AirPlay default.
    /// </summary>
    internal static bool TryMapMatrixCoefficient(int value, out YuvMatrix matrix)
    {
        switch (value)
        {
            case 1: // BT.709
            case 7: // SMPTE 240M, nearer 709 than 601
                matrix = YuvMatrix.Bt709;
                return true;
            case 4: // FCC
            case 5: // BT.601 625
            case 6: // BT.601 525 / SMPTE 170M
                matrix = YuvMatrix.Bt601;
                return true;
            default:
                matrix = default;
                return false;
        }
    }

    /// <summary>
    /// <c>MFVideoTransferMatrix</c>. Unknown is not a decision.
    /// </summary>
    internal static bool TryMapMediaFoundationMatrix(int value, out YuvMatrix matrix)
    {
        switch (value)
        {
            case 1: // BT709
            case 3: // SMPTE240M
                matrix = YuvMatrix.Bt709;
                return true;
            case 2: // BT601
                matrix = YuvMatrix.Bt601;
                return true;
            default:
                matrix = default;
                return false;
        }
    }

    /// <summary>
    /// <c>MFNominalRange</c>. The header aliases are the opposite of the names:
    /// 1 is <c>MFNominalRange_0_255</c> (full) and 2 is <c>MFNominalRange_16_235</c>
    /// (limited). 48–208 is still studio swing. 64–127 is an xRGB range, not YUV.
    /// </summary>
    internal static bool TryMapNominalRange(int value, out YuvRange range)
    {
        switch (value)
        {
            case 1:
                range = YuvRange.Full;
                return true;
            case 2:
            case 3:
                range = YuvRange.Limited;
                return true;
            default:
                range = default;
                return false;
        }
    }
}

/// <summary>
/// NV12 (limited or full swing) to tightly packed BGRA. Chroma is left-sited:
/// one Cb/Cr pair covers the even pixel and the pixel to its right, and the
/// even row and the row below it.
/// </summary>
internal static class Nv12Color
{
    public static void Convert(byte y, byte cb, byte cr, YuvColorSpace color, out byte b, out byte g, out byte r)
    {
        // Same integer formula as before, looked up from tables built once per
        // matrix and range. A 1290×2796 frame was doing this division per pixel.
        var table = YuvConvertTable.For(color);
        b = table.Blue(y, cb);
        g = table.Green(y, cb, cr);
        r = table.Red(y, cr);
    }

    public static bool TryCopy(
        ReadOnlySpan<byte> source,
        int codedWidth,
        int codedHeight,
        int stride,
        int width,
        int height,
        YuvColorSpace color,
        Span<byte> destination)
    {
        if (codedWidth <= 0 || codedHeight <= 0 || width <= 0 || height <= 0
            || width > codedWidth || height > codedHeight
            || stride < codedWidth || (stride & 1) != 0
            || (codedWidth & 1) != 0 || (codedHeight & 1) != 0)
        {
            return false;
        }

        var chroma = (long)stride * codedHeight;
        var needed = chroma + (long)stride * (codedHeight / 2);
        if (chroma > int.MaxValue
            || (long)(height - 1) * stride > int.MaxValue
            || source.Length < needed
            || destination.Length < (long)width * height * 4)
        {
            return false;
        }

        unsafe
        {
            fixed (byte* sourcePointer = source)
            fixed (byte* destinationPointer = destination)
            {
                YuvConvert.CopyNv12(
                    sourcePointer,
                    stride,
                    sourcePointer + chroma,
                    stride,
                    width,
                    height,
                    color,
                    destinationPointer);
            }
        }

        return true;
    }

    internal static int NormalizeLuma(int value, YuvRange range)
        => range == YuvRange.Limited ? (value - 16) * 65536 / 219 : value * 65536 / 255;

    internal static int NormalizeChroma(int value, YuvRange range)
    {
        var centered = value - 128;
        return range == YuvRange.Limited ? centered * 65536 / 224 : centered * 65536 / 255;
    }

    internal static int Scale(int coefficient, int sample)
    {
        var product = (long)coefficient * sample;
        if (product >= 0)
        {
            return (int)((product + 32768) >> 16);
        }

        return -(int)(((-product) + 32768) >> 16);
    }

    internal static void Coefficients(YuvMatrix matrix, out int crToR, out int cbToG, out int crToG, out int cbToB)
    {
        var coefficients = MatrixCoefficients.For(matrix);
        crToR = coefficients.CrToR;
        cbToG = coefficients.CbToG;
        crToG = coefficients.CrToG;
        cbToB = coefficients.CbToB;
    }

    internal static byte ToByte(int q16)
    {
        var scaled = (long)q16 * 255;
        scaled += scaled >= 0 ? 32768 : -32768;
        return (byte)Math.Clamp((int)(scaled >> 16), 0, 255);
    }

    private readonly record struct MatrixCoefficients(int CrToR, int CbToG, int CrToG, int CbToB)
    {
        // Q16. 2*(1-Kr), 2*Kb*(1-Kb)/Kg, 2*Kr*(1-Kr)/Kg, 2*(1-Kb).
        private static readonly MatrixCoefficients Bt709 = new(103206, 12276, 30679, 121609);

        private static readonly MatrixCoefficients Bt601 = new(91881, 22553, 46802, 116130);

        internal static MatrixCoefficients For(YuvMatrix matrix)
            => matrix == YuvMatrix.Bt601 ? Bt601 : Bt709;
    }
}
