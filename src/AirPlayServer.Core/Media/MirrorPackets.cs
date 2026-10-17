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

    public bool SenderPaused => PayloadType == 1 && (Option & 0xFF) == 0x56;

    public bool SenderResumed => PayloadType == 1 && (Option & 0xFF) == 0x16;

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

            payload[offset] = 0;
            payload[offset + 1] = 0;
            payload[offset + 2] = 0;
            payload[offset + 3] = 1;
            offset += 4 + length;
            units++;
        }

        return units > 0 && offset == payload.Length;
    }
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
}
