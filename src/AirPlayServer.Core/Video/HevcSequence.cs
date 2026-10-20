namespace AirPlayServer.Core.Video;

/// <summary>
/// HEVC parameter-set helpers. NAL buffers include the two-byte header and no
/// start code. Picture size comes from the SPS RBSP. The decoder-configuration
/// record is the hvcC body used as a Media Foundation sequence header.
/// </summary>
internal static class HevcSequence
{
    internal readonly record struct PictureInfo(
        int VisibleWidth,
        int VisibleHeight,
        int CodedWidth,
        int CodedHeight,
        int ChromaFormat,
        int BitDepthLumaMinus8,
        int BitDepthChromaMinus8);

    public static int NalType(byte header) => (header >> 1) & 0x3F;

    public static bool ContainsNalType(ReadOnlySpan<byte> annexB, int nalType)
    {
        var offset = 0;
        while (offset + 3 < annexB.Length)
        {
            if (!TryReadNalHeader(annexB, ref offset, out var type))
            {
                break;
            }

            if (type == nalType)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>HEVC VCL is nal_unit_type 0–31. Parameter sets, SEI, and AUD are 32 and above.</summary>
    public static bool ContainsVcl(ReadOnlySpan<byte> annexB)
    {
        var offset = 0;
        while (offset + 3 < annexB.Length)
        {
            if (!TryReadNalHeader(annexB, ref offset, out var type))
            {
                break;
            }

            if (type <= 31)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadNalHeader(ReadOnlySpan<byte> annexB, ref int offset, out int nalType)
    {
        nalType = 0;
        while (offset + 3 < annexB.Length)
        {
            int header;
            if (annexB[offset] == 0 && annexB[offset + 1] == 0 && annexB[offset + 2] == 1)
            {
                header = offset + 3;
            }
            else if (offset + 4 < annexB.Length
                && annexB[offset] == 0
                && annexB[offset + 1] == 0
                && annexB[offset + 2] == 0
                && annexB[offset + 3] == 1)
            {
                header = offset + 4;
            }
            else
            {
                offset++;
                continue;
            }

            offset = header + 1;
            if (header >= annexB.Length)
            {
                return false;
            }

            nalType = NalType(annexB[header]);
            return true;
        }

        return false;
    }

    internal static bool TryGetPictureInfo(ReadOnlySpan<byte> sequenceParameterSet, out PictureInfo info)
    {
        info = default;
        if (sequenceParameterSet.Length < 4)
        {
            return false;
        }

        Span<byte> rbsp = sequenceParameterSet.Length <= 1024
            ? stackalloc byte[sequenceParameterSet.Length]
            : new byte[sequenceParameterSet.Length];
        var written = H264Sequence.RemoveEmulationPrevention(sequenceParameterSet, rbsp);
        if (written < 6 || NalType(rbsp[0]) != 33)
        {
            return false;
        }

        var reader = new BitReader(rbsp[..written]);
        if (!reader.TrySkip(16)
            || !reader.TryReadBits(4, out _)
            || !reader.TryReadBits(3, out var maxSubLayers)
            || !reader.TryReadBit(out _)
            || !SkipProfileTierLevel(ref reader, profilePresent: true, (int)maxSubLayers)
            || !reader.TryReadUe(out _)
            || !reader.TryReadUe(out var chroma))
        {
            return false;
        }

        if (chroma > 3)
        {
            return false;
        }

        if (chroma == 3 && !reader.TryReadBit(out _))
        {
            return false;
        }

        if (!reader.TryReadUe(out var width) || !reader.TryReadUe(out var height) || !reader.TryReadBit(out var conformance))
        {
            return false;
        }

        var cropLeft = 0u;
        var cropRight = 0u;
        var cropTop = 0u;
        var cropBottom = 0u;
        if (conformance == 1
            && (!reader.TryReadUe(out cropLeft)
                || !reader.TryReadUe(out cropRight)
                || !reader.TryReadUe(out cropTop)
                || !reader.TryReadUe(out cropBottom)))
        {
            return false;
        }

        if (!reader.TryReadUe(out var lumaDepth) || !reader.TryReadUe(out var chromaDepth))
        {
            return false;
        }

        if (width is < 2 or > 8192 || height is < 2 or > 8192 || lumaDepth > 8 || chromaDepth > 8)
        {
            return false;
        }

        var subWidth = chroma is 1 or 2 ? 2 : 1;
        var subHeight = chroma == 1 ? 2 : 1;
        var visibleWidth = (int)width - (int)(cropLeft + cropRight) * subWidth;
        var visibleHeight = (int)height - (int)(cropTop + cropBottom) * subHeight;
        if (visibleWidth is < 2 or > 8192 || visibleHeight is < 2 or > 8192)
        {
            return false;
        }

        info = new PictureInfo(visibleWidth, visibleHeight, (int)width, (int)height, (int)chroma, (int)lumaDepth, (int)chromaDepth);
        return true;
    }

    /// <summary>
    /// Writes VPS, SPS, and PPS as Annex-B. Returns the byte count, or -1 when
    /// <paramref name="destination"/> is too short.
    /// </summary>
    internal static int WriteAnnexBParameterSets(
        ReadOnlySpan<byte> videoParameterSet,
        ReadOnlySpan<byte> sequenceParameterSet,
        ReadOnlySpan<byte> pictureParameterSet,
        Span<byte> destination)
    {
        var needed = checked(12 + videoParameterSet.Length + sequenceParameterSet.Length + pictureParameterSet.Length);
        if (destination.Length < needed)
        {
            return -1;
        }

        var offset = WriteNal(destination, 0, videoParameterSet);
        offset = WriteNal(destination, offset, sequenceParameterSet);
        WriteNal(destination, offset, pictureParameterSet);
        return needed;
    }

    /// <summary>
    /// HEVCDecoderConfigurationRecord (the hvcC payload, without the box header).
    /// Returns -1 when the SPS does not carry a general profile.
    /// </summary>
    internal static int WriteDecoderConfigurationRecord(
        ReadOnlySpan<byte> videoParameterSet,
        ReadOnlySpan<byte> sequenceParameterSet,
        ReadOnlySpan<byte> pictureParameterSet,
        Span<byte> destination)
    {
        if (videoParameterSet.Length is 0 or > ushort.MaxValue
            || sequenceParameterSet.Length is 0 or > ushort.MaxValue
            || pictureParameterSet.Length is 0 or > ushort.MaxValue
            || !TryCopyProfile(sequenceParameterSet, out var profile, out var maxSubLayers, out var nested))
        {
            return -1;
        }

        var chroma = 1;
        var lumaDepth = 0;
        var chromaDepth = 0;
        if (TryGetPictureInfo(sequenceParameterSet, out var picture))
        {
            chroma = picture.ChromaFormat;
            lumaDepth = picture.BitDepthLumaMinus8;
            chromaDepth = picture.BitDepthChromaMinus8;
        }

        var needed = checked(23 + 15 + videoParameterSet.Length + sequenceParameterSet.Length + pictureParameterSet.Length);
        if (destination.Length < needed)
        {
            return -1;
        }

        destination[0] = 1;
        profile.CopyTo(destination[1..]);
        destination[13] = 0xF0;
        destination[14] = 0x00;
        destination[15] = 0xFC;
        destination[16] = (byte)(0xFC | (chroma & 0x3));
        destination[17] = (byte)(0xF8 | (lumaDepth & 0x7));
        destination[18] = (byte)(0xF8 | (chromaDepth & 0x7));
        destination[19] = 0;
        destination[20] = 0;
        var layers = Math.Min(7, maxSubLayers + 1);
        destination[21] = (byte)(((layers & 7) << 3) | ((nested & 1) << 2) | 3);
        destination[22] = 3;
        var offset = 23;
        offset = WriteArray(destination, offset, 32, videoParameterSet);
        offset = WriteArray(destination, offset, 33, sequenceParameterSet);
        offset = WriteArray(destination, offset, 34, pictureParameterSet);
        return offset;
    }

    private static bool TryCopyProfile(ReadOnlySpan<byte> sequenceParameterSet, out byte[] profile, out int maxSubLayers, out int nested)
    {
        profile = [];
        maxSubLayers = 0;
        nested = 0;
        Span<byte> rbsp = sequenceParameterSet.Length <= 1024
            ? stackalloc byte[sequenceParameterSet.Length]
            : new byte[sequenceParameterSet.Length];
        var written = H264Sequence.RemoveEmulationPrevention(sequenceParameterSet, rbsp);
        if (written < 15 || NalType(rbsp[0]) != 33)
        {
            return false;
        }

        profile = rbsp.Slice(3, 12).ToArray();
        maxSubLayers = (rbsp[2] >> 1) & 7;
        nested = rbsp[2] & 1;
        return true;
    }

    private static int WriteNal(Span<byte> destination, int offset, ReadOnlySpan<byte> nal)
    {
        destination[offset] = 0;
        destination[offset + 1] = 0;
        destination[offset + 2] = 0;
        destination[offset + 3] = 1;
        nal.CopyTo(destination[(offset + 4)..]);
        return offset + 4 + nal.Length;
    }

    private static int WriteArray(Span<byte> destination, int offset, int nalType, ReadOnlySpan<byte> nal)
    {
        destination[offset] = (byte)(0x80 | nalType);
        destination[offset + 1] = 0;
        destination[offset + 2] = 1;
        destination[offset + 3] = (byte)(nal.Length >> 8);
        destination[offset + 4] = (byte)nal.Length;
        nal.CopyTo(destination[(offset + 5)..]);
        return offset + 5 + nal.Length;
    }

    private static bool SkipProfileTierLevel(ref BitReader reader, bool profilePresent, int maxSubLayersMinus1)
    {
        if (profilePresent && !reader.TrySkip(88))
        {
            return false;
        }

        if (!reader.TrySkip(8))
        {
            return false;
        }

        if (maxSubLayersMinus1 <= 0)
        {
            return true;
        }

        if (maxSubLayersMinus1 > 7)
        {
            return false;
        }

        Span<byte> profileFlags = stackalloc byte[7];
        Span<byte> levelFlags = stackalloc byte[7];
        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            if (!reader.TryReadBit(out var profileFlag) || !reader.TryReadBit(out var levelFlag))
            {
                return false;
            }

            profileFlags[i] = (byte)profileFlag;
            levelFlags[i] = (byte)levelFlag;
        }

        for (var i = maxSubLayersMinus1; i < 8; i++)
        {
            if (!reader.TrySkip(2))
            {
                return false;
            }
        }

        for (var i = 0; i < maxSubLayersMinus1; i++)
        {
            if (profileFlags[i] == 1 && !reader.TrySkip(88))
            {
                return false;
            }

            if (levelFlags[i] == 1 && !reader.TrySkip(8))
            {
                return false;
            }
        }

        return true;
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public bool TrySkip(int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (!TryReadBit(out _))
                {
                    return false;
                }
            }

            return true;
        }

        public bool TryReadBit(out int bit)
        {
            bit = 0;
            if ((uint)_position >= (uint)(_data.Length * 8))
            {
                return false;
            }

            var shift = 7 - (_position & 7);
            bit = (_data[_position >> 3] >> shift) & 1;
            _position++;
            return true;
        }

        public bool TryReadBits(int count, out uint value)
        {
            value = 0;
            for (var i = 0; i < count; i++)
            {
                if (!TryReadBit(out var bit))
                {
                    return false;
                }

                value = (value << 1) | (uint)bit;
            }

            return true;
        }

        public bool TryReadUe(out uint value)
        {
            value = 0;
            var zeros = 0;
            while (true)
            {
                if (!TryReadBit(out var bit))
                {
                    return false;
                }

                if (bit == 1)
                {
                    break;
                }

                if (++zeros > 31)
                {
                    return false;
                }
            }

            if (zeros == 0)
            {
                return true;
            }

            if (!TryReadBits(zeros, out var rest))
            {
                return false;
            }

            value = ((1u << zeros) - 1) + rest;
            return true;
        }
    }
}
