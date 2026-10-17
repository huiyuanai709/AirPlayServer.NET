namespace AirPlayFairPlay;

internal static class Endian
{
    internal static uint ReadU32(ReadOnlySpan<byte> data, int offset)
        => (uint)data[offset]
           | ((uint)data[offset + 1] << 8)
           | ((uint)data[offset + 2] << 16)
           | ((uint)data[offset + 3] << 24);

    internal static void WriteU32(Span<byte> data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    internal static void XorU32(Span<byte> data, int offset, uint value)
        => WriteU32(data, offset, ReadU32(data, offset) ^ value);

    internal static byte Rol8(byte input, int count)
        => (byte)(((input << count) & 0xff) | ((input & 0xff) >> (8 - count)));

    internal static uint Rol8x(byte input, int count)
        => (uint)((input << count) | (input >> (8 - count)));

    internal static uint WeirdRol8(byte input, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        return (uint)(((input << count) & 0xff) | ((input & 0xff) >> (8 - count)));
    }

    internal static uint WeirdRor8(byte input, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        return (uint)(((input >> count) & 0xff) | ((input & 0xff) << (8 - count)));
    }

    internal static uint WeirdRol32(byte input, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        return (uint)((input << count) ^ (input >> (8 - count)));
    }

    /// <summary>
    /// C unsigned wrap of a possibly negative index, then modulo.
    /// Matches <c>(value &amp; 0xffffffff) % modulus</c>.
    /// </summary>
    internal static int UMod(int value, int modulus)
        => (int)(unchecked((uint)value) % (uint)modulus);
}
