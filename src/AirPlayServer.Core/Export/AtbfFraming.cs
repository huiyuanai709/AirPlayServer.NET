using System.Buffers.Binary;

namespace AirPlayServer.Core.Export;

/// <summary>
/// TapClock ATBF frame header, 12 bytes little-endian:
/// magic "ATBF" | version u8 = 1 | type u8 | flags u16 | length u32 | payload.
/// Type 3 is a full PNG or JPEG image. This sender writes JPEG with flags 0.
/// H.264 type 1 and type 2 are not produced.
/// </summary>
public static class AtbfFraming
{
    public const int HeaderLength = 12;
    public const byte Version = 1;
    public const byte TypeImage = 3;
    public const ushort FlagJpeg = 0;

    public static byte[] ImageFrame(ReadOnlySpan<byte> encodedImage, ushort flags = FlagJpeg)
        => Frame(TypeImage, flags, encodedImage);

    public static byte[] Frame(byte type, ushort flags, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[HeaderLength + payload.Length];
        WriteHeader(packet, type, flags, payload.Length);
        payload.CopyTo(packet.AsSpan(HeaderLength));
        return packet;
    }

    public static void WriteHeader(Span<byte> destination, byte type, ushort flags, int length)
    {
        if (destination.Length < HeaderLength)
        {
            throw new ArgumentException("ATBF header is 12 bytes.", nameof(destination));
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        "ATBF"u8.CopyTo(destination);
        destination[4] = Version;
        destination[5] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)length);
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> data, out byte type, out ushort flags, out int length)
    {
        type = 0;
        flags = 0;
        length = 0;
        if (data.Length < HeaderLength
            || data[0] != (byte)'A' || data[1] != (byte)'T' || data[2] != (byte)'B' || data[3] != (byte)'F'
            || data[4] != Version)
        {
            return false;
        }

        type = data[5];
        flags = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        var rawLength = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (rawLength > int.MaxValue)
        {
            return false;
        }

        length = (int)rawLength;
        return true;
    }
}
