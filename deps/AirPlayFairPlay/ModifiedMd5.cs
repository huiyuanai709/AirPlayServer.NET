namespace AirPlayFairPlay;

/// <summary>
/// Faithful port of playfair/modified_md5.c. Sine constants are the values
/// produced by the original C expression, not a fresh libm call.
/// </summary>
internal static class ModifiedMd5
{
    private static readonly int[] Shift =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    internal static void Compute(ReadOnlySpan<byte> blockIn, ReadOnlySpan<byte> keyIn, Span<byte> keyOut)
    {
        Span<byte> block = stackalloc byte[64];
        blockIn[..64].CopyTo(block);

        uint a = Endian.ReadU32(keyIn, 0);
        uint b = Endian.ReadU32(keyIn, 4);
        uint c = Endian.ReadU32(keyIn, 8);
        uint d = Endian.ReadU32(keyIn, 12);

        for (int i = 0; i < 64; i++)
        {
            int j = i < 16 ? i
                : i < 32 ? ((5 * i) + 1) % 16
                : i < 48 ? ((3 * i) + 5) % 16
                : (7 * i) % 16;

            uint input = ((uint)block[4 * j] << 24)
                         | ((uint)block[(4 * j) + 1] << 16)
                         | ((uint)block[(4 * j) + 2] << 8)
                         | block[(4 * j) + 3];
            uint z = a + input + Constants.Md5Sine[i];
            uint f = i < 16 ? F(b, c, d)
                : i < 32 ? G(b, c, d)
                : i < 48 ? H(b, c, d)
                : I(b, c, d);
            z = Rol(z + f, Shift[i]);
            z += b;
            uint tmp = d;
            d = c;
            c = b;
            b = z;
            a = tmp;

            if (i == 31)
            {
                SwapWord(block, (int)(a & 15), (int)(b & 15));
                SwapWord(block, (int)(c & 15), (int)(d & 15));
                SwapWord(block, (int)((a & (15 << 4)) >> 4), (int)((b & (15 << 4)) >> 4));
                SwapWord(block, (int)((a & (15 << 8)) >> 8), (int)((b & (15 << 8)) >> 8));
                SwapWord(block, (int)((a & (15 << 12)) >> 12), (int)((b & (15 << 12)) >> 12));
            }
        }

        Endian.WriteU32(keyOut, 0, Endian.ReadU32(keyIn, 0) + a);
        Endian.WriteU32(keyOut, 4, Endian.ReadU32(keyIn, 4) + b);
        Endian.WriteU32(keyOut, 8, Endian.ReadU32(keyIn, 8) + c);
        Endian.WriteU32(keyOut, 12, Endian.ReadU32(keyIn, 12) + d);
    }

    private static uint F(uint b, uint c, uint d) => (b & c) | (~b & d);

    private static uint G(uint b, uint c, uint d) => (b & d) | (c & ~d);

    private static uint H(uint b, uint c, uint d) => b ^ c ^ d;

    private static uint I(uint b, uint c, uint d) => c ^ (b | ~d);

    private static uint Rol(uint input, int count)
        => (input << count) | (input >> (32 - count));

    private static void SwapWord(Span<byte> block, int left, int right)
    {
        int a = left * 4;
        int b = right * 4;
        for (int i = 0; i < 4; i++)
        {
            (block[a + i], block[b + i]) = (block[b + i], block[a + i]);
        }
    }
}
