namespace AirPlayServer.Core.Video;

/// <summary>
/// Reads the visible width and height from an H.264 sequence parameter set.
/// The buffer is one SPS NAL, including its one-byte header. Emulation-prevention
/// bytes are removed before the exp-Golomb parse. High profile scaling lists are skipped.
/// </summary>
internal static class H264Sequence
{
    public static bool TryGetDimensions(ReadOnlySpan<byte> sequenceParameterSet, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (sequenceParameterSet.Length < 4)
        {
            return false;
        }

        Span<byte> rbsp = sequenceParameterSet.Length <= 512
            ? stackalloc byte[sequenceParameterSet.Length]
            : new byte[sequenceParameterSet.Length];
        var written = RemoveEmulationPrevention(sequenceParameterSet, rbsp);
        if (written < 4 || (rbsp[0] & 0x1F) != 7)
        {
            return false;
        }

        var reader = new BitReader(rbsp[..written]);
        if (!reader.TryReadBits(8, out _))
        {
            return false;
        }

        if (!reader.TryReadBits(8, out var profile) || !reader.TryReadBits(8, out _) || !reader.TryReadBits(8, out _))
        {
            return false;
        }

        if (!reader.TryReadUe(out _))
        {
            return false;
        }

        var chroma = 1u;
        var separateColourPlane = false;
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            if (!reader.TryReadUe(out chroma))
            {
                return false;
            }

            if (chroma == 3)
            {
                if (!reader.TryReadBit(out var separate))
                {
                    return false;
                }

                separateColourPlane = separate == 1;
            }

            if (!reader.TryReadUe(out _) || !reader.TryReadUe(out _) || !reader.TryReadBit(out _))
            {
                return false;
            }

            if (!reader.TryReadBit(out var scalingPresent))
            {
                return false;
            }

            if (scalingPresent == 1)
            {
                var count = chroma == 3 ? 12 : 8;
                for (var i = 0; i < count; i++)
                {
                    if (!reader.TryReadBit(out var present))
                    {
                        return false;
                    }

                    if (present == 1 && !SkipScalingList(ref reader, i < 6 ? 16 : 64))
                    {
                        return false;
                    }
                }
            }
        }

        if (!reader.TryReadUe(out _))
        {
            return false;
        }

        if (!reader.TryReadUe(out var order))
        {
            return false;
        }

        if (order == 0)
        {
            if (!reader.TryReadUe(out _))
            {
                return false;
            }
        }
        else if (order == 1)
        {
            if (!reader.TryReadBit(out _) || !reader.TryReadSe(out _) || !reader.TryReadSe(out _) || !reader.TryReadUe(out var cycle))
            {
                return false;
            }

            if (cycle > 255)
            {
                return false;
            }

            for (var i = 0; i < cycle; i++)
            {
                if (!reader.TryReadSe(out _))
                {
                    return false;
                }
            }
        }
        else if (order != 2)
        {
            return false;
        }

        if (!reader.TryReadUe(out _) || !reader.TryReadBit(out _) || !reader.TryReadUe(out var widthMbs) || !reader.TryReadUe(out var heightUnits))
        {
            return false;
        }

        if (!reader.TryReadBit(out var frameMbsOnly))
        {
            return false;
        }

        if (frameMbsOnly == 0 && !reader.TryReadBit(out _))
        {
            return false;
        }

        if (!reader.TryReadBit(out _) || !reader.TryReadBit(out var cropping))
        {
            return false;
        }

        var cropLeft = 0u;
        var cropRight = 0u;
        var cropTop = 0u;
        var cropBottom = 0u;
        if (cropping == 1
            && (!reader.TryReadUe(out cropLeft) || !reader.TryReadUe(out cropRight) || !reader.TryReadUe(out cropTop) || !reader.TryReadUe(out cropBottom)))
        {
            return false;
        }

        var codedWidth = (int)(widthMbs + 1) * 16;
        var codedHeight = (int)(heightUnits + 1) * 16 * (frameMbsOnly == 0 ? 2 : 1);
        var chromaArray = separateColourPlane ? 0 : (int)chroma;
        int cropX;
        int cropY;
        if (chromaArray == 0)
        {
            cropX = 1;
            cropY = 2 - frameMbsOnly;
        }
        else
        {
            var subWidth = chromaArray == 3 ? 1 : 2;
            var subHeight = chromaArray == 1 ? 2 : 1;
            cropX = subWidth;
            cropY = subHeight * (2 - frameMbsOnly);
        }

        var visibleWidth = codedWidth - (int)(cropLeft + cropRight) * cropX;
        var visibleHeight = codedHeight - (int)(cropTop + cropBottom) * cropY;
        if (visibleWidth is < 2 or > 8192 || visibleHeight is < 2 or > 8192)
        {
            return false;
        }

        width = visibleWidth;
        height = visibleHeight;
        return true;
    }

    internal static int RemoveEmulationPrevention(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
        {
            return -1;
        }

        var written = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (i + 2 < source.Length && source[i] == 0 && source[i + 1] == 0 && source[i + 2] == 3)
            {
                destination[written++] = 0;
                destination[written++] = 0;
                i += 2;
                continue;
            }

            destination[written++] = source[i];
        }

        return written;
    }

    /// <summary>
    /// Writes SPS and PPS as Annex-B (each NAL prefixed by a 4-byte start code).
    /// Returns the byte count, or -1 when <paramref name="destination"/> is shorter than that.
    /// </summary>
    internal static int WriteAnnexBParameterSets(ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet, Span<byte> destination)
    {
        var needed = checked(8 + sequenceParameterSet.Length + pictureParameterSet.Length);
        if (destination.Length < needed)
        {
            return -1;
        }

        WriteStartCode(destination);
        sequenceParameterSet.CopyTo(destination[4..]);
        var picture = 4 + sequenceParameterSet.Length;
        WriteStartCode(destination[picture..]);
        pictureParameterSet.CopyTo(destination[(picture + 4)..]);
        return needed;
    }

    private static void WriteStartCode(Span<byte> destination)
    {
        destination[0] = 0;
        destination[1] = 0;
        destination[2] = 0;
        destination[3] = 1;
    }

    private static bool SkipScalingList(ref BitReader reader, int size)
    {
        var lastScale = 8;
        var nextScale = 8;
        for (var j = 0; j < size; j++)
        {
            if (nextScale != 0)
            {
                if (!reader.TryReadSe(out var delta))
                {
                    return false;
                }

                nextScale = (lastScale + delta + 256) % 256;
            }

            lastScale = nextScale == 0 ? lastScale : nextScale;
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

        public bool TryReadSe(out int value)
        {
            value = 0;
            if (!TryReadUe(out var code))
            {
                return false;
            }

            value = (code & 1) == 0 ? -(int)(code >> 1) : (int)((code + 1) >> 1);
            return true;
        }
    }
}
