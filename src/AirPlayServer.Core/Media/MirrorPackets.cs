using System.Buffers.Binary;

namespace AirPlayServer.Core.Media;

/// <summary>
/// 128-byte mirror data header. Upstream reads the length, type, and geometry with
/// little-endian loads (and native little-endian floats). Payload type is the first
/// type byte; 0x10 in the following byte marks a key frame. NAL lengths inside an
/// encrypted payload are big-endian AVCC.
/// </summary>
internal readonly record struct MirrorHeader(
    int PayloadSize,
    byte PayloadType,
    byte Flags,
    ushort Option,
    ulong Timestamp,
    float SourceWidth,
    float SourceHeight,
    float Width,
    float Height)
{
    public bool IsKeyframe => Flags == 0x10;

    public bool SenderPaused => PayloadType == 1 && (Option & 0xFF) is 0x56 or 0x5E;

    public bool SenderResumed => PayloadType == 1 && (Option & 0xFF) is 0x16 or 0x1E;

    public static bool TryParse(ReadOnlySpan<byte> header, out MirrorHeader value)
    {
        if (header.Length < 128)
        {
            value = default;
            return false;
        }

        value = new MirrorHeader(
            BinaryPrimitives.ReadInt32LittleEndian(header),
            header[4],
            header[5],
            BinaryPrimitives.ReadUInt16LittleEndian(header[6..]),
            BinaryPrimitives.ReadUInt64LittleEndian(header[8..]),
            BinaryPrimitives.ReadSingleLittleEndian(header[40..]),
            BinaryPrimitives.ReadSingleLittleEndian(header[44..]),
            BinaryPrimitives.ReadSingleLittleEndian(header[56..]),
            BinaryPrimitives.ReadSingleLittleEndian(header[60..]));
        return true;
    }
}

internal static class AvccAnnexB
{
    public static bool TryConvertInPlace(Span<byte> payload)
    {
        var offset = 0;
        var units = 0;
        while (offset + 4 <= payload.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(payload[offset..]);
            if (length <= 0 || length > payload.Length - offset - 4)
            {
                return false;
            }

            // forbidden_zero_bit. A desynced CTR keystream often still has a
            // plausible length; the NAL header bit catches it before it is decoded.
            if ((payload[offset + 4] & 0x80) != 0)
            {
                return false;
            }

            payload[offset] = 0;
            payload[offset + 1] = 0;
            payload[offset + 2] = 0;
            payload[offset + 3] = 1;
            offset += 4 + length;
            units++;
        }

        return units > 0 && offset == payload.Length;
    }

    public static bool ContainsNalType(ReadOnlySpan<byte> annexB, int nalType)
    {
        var offset = 0;
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

            if (header < annexB.Length && (annexB[header] & 0x1F) == nalType)
            {
                return true;
            }

            offset = header + 1;
        }

        return false;
    }
}

internal enum MirrorVideoCodec
{
    Unknown = 0,
    H264 = 1,
    Hevc = 2,
}

internal static class CodecConfig
{
    public static bool TryParse(ReadOnlySpan<byte> payload, out byte[] sequence, out byte[] picture)
    {
        sequence = [];
        picture = [];
        if (payload.Length < 11)
        {
            return false;
        }

        var spsLength = (payload[6] << 8) | payload[7];
        var ppsCountOffset = 8 + spsLength;
        if (spsLength <= 0 || ppsCountOffset > payload.Length || payload.Length - ppsCountOffset < 3)
        {
            return false;
        }

        var ppsCount = payload[ppsCountOffset];
        var ppsLength = (payload[ppsCountOffset + 1] << 8) | payload[ppsCountOffset + 2];
        var ppsOffset = ppsCountOffset + 3;
        if (ppsCount == 0 || ppsLength <= 0 || ppsLength > payload.Length - ppsOffset)
        {
            return false;
        }

        sequence = payload.Slice(8, spsLength).ToArray();
        picture = payload.Slice(ppsOffset, ppsLength).ToArray();
        return true;
    }

    public static bool LooksLikeHevc(ReadOnlySpan<byte> payload)
        => payload.Length >= 8
            && payload[4] == (byte)'h'
            && payload[5] == (byte)'v'
            && payload[6] == (byte)'c'
            && payload[7] == (byte)'1';

    public static bool TryParseHevc(ReadOnlySpan<byte> payload, out byte[] video, out byte[] sequence, out byte[] picture)
    {
        video = [];
        sequence = [];
        picture = [];
        if (!LooksLikeHevc(payload))
        {
            return false;
        }

        if (TryFindHvccArrays(payload, out var arrays, out var arrayCount)
            && TryReadArrays(payload[arrays..], arrayCount, out video, out sequence, out picture))
        {
            return true;
        }

        return payload.Length > 0x75
            && TryReadArrays(payload[0x75..], 8, out video, out sequence, out picture);
    }

    private static bool TryFindHvccArrays(ReadOnlySpan<byte> payload, out int arrays, out int arrayCount)
    {
        arrays = 0;
        arrayCount = 0;
        for (var i = 0; i + 26 < payload.Length; i++)
        {
            if (payload[i] != (byte)'h'
                || payload[i + 1] != (byte)'v'
                || payload[i + 2] != (byte)'c'
                || payload[i + 3] != (byte)'C'
                || payload[i + 4] != 1)
            {
                continue;
            }

            var count = payload[i + 4 + 22];
            if (count is < 1 or > 8)
            {
                continue;
            }

            arrays = i + 4 + 23;
            arrayCount = count;
            return arrays < payload.Length;
        }

        return false;
    }

    private static bool TryReadArrays(ReadOnlySpan<byte> data, int maxArrays, out byte[] video, out byte[] sequence, out byte[] picture)
    {
        video = [];
        sequence = [];
        picture = [];
        var offset = 0;
        for (var array = 0; array < maxArrays && offset + 3 <= data.Length; array++)
        {
            var nalType = data[offset] & 0x3F;
            var count = (data[offset + 1] << 8) | data[offset + 2];
            offset += 3;
            if (count is < 1 or > 16)
            {
                return false;
            }

            for (var nal = 0; nal < count; nal++)
            {
                if (offset + 2 > data.Length)
                {
                    return false;
                }

                var length = (data[offset] << 8) | data[offset + 1];
                offset += 2;
                if (length <= 0 || length > data.Length - offset)
                {
                    return false;
                }

                if (nal == 0)
                {
                    var bytes = data.Slice(offset, length).ToArray();
                    if (nalType == 32 && video.Length == 0)
                    {
                        video = bytes;
                    }
                    else if (nalType == 33 && sequence.Length == 0)
                    {
                        sequence = bytes;
                    }
                    else if (nalType == 34 && picture.Length == 0)
                    {
                        picture = bytes;
                    }
                }

                offset += length;
            }

            if (video.Length > 0 && sequence.Length > 0 && picture.Length > 0)
            {
                return true;
            }
        }

        return video.Length > 0 && sequence.Length > 0 && picture.Length > 0;
    }
}
