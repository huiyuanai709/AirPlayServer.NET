namespace AirPlayFairPlay;

/// <summary>
/// Faithful port of playfair/sap_hash.c and playfair/hand_garble.c.
/// Unsigned char stores truncate to 8 bits. Unsigned int temporaries wrap at 2^32.
/// </summary>
internal static class SapHash
{
    internal static void Compute(ReadOnlySpan<byte> blockIn, Span<byte> keyOut)
    {
        byte[] buffer0 =
        [
            0x96, 0x5F, 0xC6, 0x53, 0xF8, 0x46, 0xCC, 0x18, 0xDF, 0xBE, 0xB2, 0xF8, 0x38, 0xD7, 0xEC, 0x22, 0x03, 0xD1, 0x20, 0x8F,
        ];
        byte[] buffer1 = new byte[210];
        byte[] buffer2 =
        [
            0x43, 0x54, 0x62, 0x7A, 0x18, 0xC3, 0xD6, 0xB3, 0x9A, 0x56, 0xF6, 0x1C, 0x14, 0x3F, 0x0C, 0x1D, 0x3B, 0x36, 0x83, 0xB1, 0x39, 0x51, 0x4A, 0xAA, 0x09, 0x3E, 0xFE, 0x44, 0xAF, 0xDE, 0xC3, 0x20, 0x9D, 0x42, 0x3A,
        ];
        byte[] buffer3 = new byte[132];
        byte[] buffer4 =
        [
            0xED, 0x25, 0xD1, 0xBB, 0xBC, 0x27, 0x9F, 0x02, 0xA2, 0xA9, 0x11, 0x00, 0x0C, 0xB3, 0x52, 0xC0, 0xBD, 0xE3, 0x1B, 0x49, 0xC7,
        ];
        int[] i0Index = [18, 22, 23, 0, 5, 19, 32, 31, 10, 21, 30];

        for (int i = 0; i < 210; i++)
        {
            uint word = Endian.ReadU32(blockIn, ((i % 64) >> 2) * 4);
            buffer1[i] = (byte)((word >> ((3 - (i % 4)) << 3)) & 0xff);
        }

        for (int i = 0; i < 840; i++)
        {
            byte x = buffer1[Endian.UMod(i - 155, 210)];
            byte y = buffer1[Endian.UMod(i - 57, 210)];
            byte z = buffer1[Endian.UMod(i - 13, 210)];
            byte w = buffer1[i % 210];
            buffer1[i % 210] = (byte)(Endian.Rol8(y, 5) + (Endian.Rol8(z, 3) ^ w) - Endian.Rol8(x, 7));
        }

        Garble(buffer0, buffer1, buffer2, buffer3, buffer4);

        for (int i = 0; i < 16; i++)
        {
            keyOut[i] = 0xE1;
        }

        for (int i = 0; i < 11; i++)
        {
            keyOut[i] = i == 3 ? (byte)0x3d : (byte)(keyOut[i] + buffer3[i0Index[i] * 4]);
        }

        for (int i = 0; i < 20; i++)
        {
            keyOut[i % 16] ^= buffer0[i];
        }

        for (int i = 0; i < 35; i++)
        {
            keyOut[i % 16] ^= buffer2[i];
        }

        for (int i = 0; i < 210; i++)
        {
            keyOut[i % 16] ^= buffer1[i];
        }

        for (int j = 0; j < 16; j++)
        {
            for (int i = 0; i < 16; i++)
            {
                byte x = keyOut[Endian.UMod(i - 7, 16)];
                byte y = keyOut[i % 16];
                byte z = keyOut[Endian.UMod(i - 37, 16)];
                byte w = keyOut[Endian.UMod(i - 177, 16)];
                keyOut[i] = (byte)(Endian.Rol8(x, 1) ^ y ^ Endian.Rol8(z, 6) ^ Endian.Rol8(w, 5));
            }
        }
    }

    private static void Garble(byte[] buffer0, byte[] buffer1, byte[] buffer2, byte[] buffer3, byte[] buffer4)
    {
        uint tmp;
        uint tmp2;
        uint tmp3;
        uint a;
        uint b;
        uint c;
        uint d;
        uint e;
        uint m;
        uint j;
        uint g;
        uint f;
        uint h;
        uint k;
        uint r;
        uint s;
        uint t;
        uint u;
        uint v;
        uint w;
        uint x;
        uint y;
        uint z;

        buffer2[12] = (byte)(0x14 + (((buffer1[64] & 92) | ((buffer1[99] / 3) & 35)) & buffer4[Endian.Rol8x(buffer4[buffer1[206] % 21], 4) % 21]));
        buffer1[4] = (byte)((buffer1[99] / 5) * (buffer1[99] / 5) * 2);
        buffer2[34] = 0xb8;
        buffer1[153] ^= (byte)(buffer2[buffer1[203] % 35] * buffer2[buffer1[203] % 35] * buffer1[190]);
        buffer0[3] = unchecked((byte)(buffer0[3] - (((buffer4[buffer1[205] % 21] >> 1) & 80) | 0xe6440)));
        buffer0[16] = 0x93;
        buffer0[13] = 0x62;
        buffer1[33] = (byte)(buffer1[33] - (buffer4[buffer1[36] % 21] & 0xf6));
        tmp2 = buffer2[buffer1[67] % 35];
        buffer2[12] = 0x07;
        tmp = buffer0[buffer1[181] % 20];
        buffer1[2] = unchecked((byte)(buffer1[2] - 3136));
        buffer0[19] = buffer4[buffer1[58] % 21];
        buffer3[0] = unchecked((byte)(92 - buffer2[buffer1[32] % 35]));
        buffer3[4] = (byte)(buffer2[buffer1[15] % 35] + 0x9e);
        buffer1[34] = (byte)(buffer1[34] + (buffer4[((buffer2[buffer1[15] % 35] + 0x9e) & 0xff) % 21] / 5));
        buffer0[19] = (byte)(buffer0[19] + (0xfffffee6u - (uint)((buffer0[buffer3[4] % 20] >> 1) & 102)));

        int shiftSource = buffer4[buffer1[190] % 21];
        int shifted = ((buffer1[72] >> (shiftSource & 7)) ^ (buffer1[72] << ((7 - (shiftSource - 1)) & 7))) - (3 * buffer4[buffer1[126] % 21]);
        buffer1[15] = (byte)((3 * shifted) ^ buffer1[15]);

        buffer0[15] ^= (byte)(buffer2[buffer1[181] % 35] * buffer2[buffer1[181] % 35] * buffer2[buffer1[181] % 35]);
        buffer2[4] ^= (byte)(buffer1[202] / 3);

        a = (uint)(92 - buffer0[buffer3[0] % 20]);
        uint not105 = unchecked((uint)~buffer1[105]);
        e = (a & 0xc6) | (not105 & 0xc6) | (a & not105);
        buffer2[1] = (byte)(buffer2[1] + (e * e * e));
        buffer0[19] ^= (byte)(((224 | (buffer4[buffer1[92] % 21] & 27)) * buffer2[buffer1[41] % 35]) / 3);
        buffer1[140] = (byte)(buffer1[140] + Endian.WeirdRor8(92, buffer1[5] & 7));

        int not4 = ~buffer1[4];
        int mixed = not4 ^ buffer2[buffer1[12] % 35];
        buffer2[12] = (byte)(buffer2[12] + ((((mixed | buffer1[182]) & 192) | (mixed & buffer1[182]))));
        buffer1[36] = (byte)(buffer1[36] + 125);
        int rollInput = (((74 & buffer1[138]) | ((74 | buffer1[138]) & buffer0[15])) & buffer0[buffer1[43] % 20])
                         | (((74 & buffer1[138]) | ((74 | buffer1[138]) & buffer0[15]) | buffer0[buffer1[43] % 20]) & 95);
        buffer1[124] = (byte)Endian.Rol8x((byte)rollInput, 4);
        buffer3[8] = (byte)(((((buffer0[buffer3[4] % 20] & 95) & ((buffer4[buffer1[68] % 21] & 46) << 1)) | 16) ^ 92));

        a = (uint)(buffer1[177] + buffer4[buffer1[79] % 21]);
        d = (((a >> 1) | (uint)((3 * buffer1[148]) / 5)) & buffer2[1]) | ((a >> 1) & (uint)((3 * buffer1[148]) / 5));
        buffer3[12] = (byte)(unchecked((uint)(-34) - d));

        a = (uint)(8 - (buffer2[22] & 7));
        b = (uint)(buffer1[33] >> (int)(a & 7));
        c = (uint)(buffer1[33] << (buffer2[22] & 7));
        uint left16 = (uint)((buffer2[buffer3[0] % 35] & 159) | buffer0[buffer3[4] % 20] | 8);
        buffer2[16] = (byte)(buffer2[16] + (left16 - ((b ^ c) | 128)));
        buffer0[14] ^= buffer2[buffer3[12] % 35];

        a = Endian.WeirdRol8(buffer4[buffer0[buffer1[201] % 20] % 21], (buffer2[buffer1[112] % 35] << 1) & 7);
        d = (uint)((buffer0[buffer1[208] % 20] & 131) | (buffer0[buffer1[164] % 20] & 124));
        buffer1[19] = (byte)(buffer1[19] + ((a & (d / 5)) | ((a | (d / 5)) & 37)));
        buffer2[8] = (byte)Endian.WeirdRor8(140, ((buffer4[buffer1[45] % 21] + 92) * (buffer4[buffer1[45] % 21] + 92)) & 7);
        buffer1[190] = 56;
        buffer2[8] ^= buffer3[0];
        buffer1[53] = (byte)(~((buffer0[buffer1[83] % 20] | 204) / 5));
        buffer0[13] = (byte)(buffer0[13] + buffer0[buffer1[41] % 20]);
        buffer0[10] = (byte)(((buffer2[buffer3[0] % 35] & buffer1[2]) | ((buffer2[buffer3[0] % 35] | buffer1[2]) & buffer3[12])) / 15);

        a = (uint)((((56 | (buffer4[buffer1[2] % 21] & 68)) | buffer2[buffer3[8] % 35]) & 42) | (((buffer4[buffer1[2] % 21] & 68) | 56) & buffer2[buffer3[8] % 35]));
        buffer3[16] = (byte)((a * a) + 110);
        buffer3[20] = (byte)(202 - buffer3[16]);
        buffer3[24] = buffer1[151];
        buffer2[13] ^= buffer4[buffer3[0] % 21];

        b = ((uint)(buffer2[buffer1[179] % 35] - 38) & 177) | (buffer3[12] & 177u);
        c = (uint)(buffer2[buffer1[179] % 35] - 38) & buffer3[12];
        buffer3[28] = (byte)(30 + ((b | c) * (b | c)));
        buffer3[32] = (byte)(buffer3[28] + 62);

        uint sum20 = (uint)(buffer3[20] + (buffer3[0] & 74));
        uint notB4 = unchecked((uint)~buffer4[buffer3[0] % 21]);
        a = (sum20 | notB4) & 121;
        b = sum20 & notB4;
        tmp3 = a | b;
        c = (((tmp3 ^ 0xffffffa6) | buffer3[0]) & 4) | ((tmp3 ^ 0xffffffa6) & buffer3[0]);
        buffer1[47] = (byte)((buffer2[buffer1[89] % 35] + c) ^ buffer1[47]);
        uint rolled36 = (uint)(Endian.Rol8((byte)((tmp & 179) + 68), 2) & buffer0[3]);
        buffer3[36] = (byte)((rolled36 | (tmp2 & unchecked((uint)~buffer0[3]))) - 15u);
        buffer1[123] ^= 221;

        a = unchecked((uint)((buffer4[buffer3[0] % 21] / 3) - buffer2[buffer3[4] % 35]));
        c = (uint)((((buffer3[0] & 163) + 92) & 246) | (buffer3[0] & 92));
        e = ((c | buffer3[24]) & 54) | (c & buffer3[24]);
        buffer3[40] = (byte)(a - e);
        buffer3[44] = (byte)(tmp3 ^ 81 ^ (uint)(((buffer3[0] >> 1) & 101) + 26));
        buffer3[48] = (byte)(buffer2[buffer3[4] % 35] & 27);
        buffer3[52] = 27;
        buffer3[56] = 199;
        buffer3[64] = (byte)(buffer3[4] + (((((((buffer3[40] | buffer3[24]) & 177) | (buffer3[40] & buffer3[24])) & (((buffer4[buffer3[0] % 20] & 177) | 176) | (buffer4[buffer3[0] % 21] & ~3))) | ((((buffer3[40] & buffer3[24]) | ((buffer3[40] | buffer3[24]) & 177)) & 199) | ((((buffer4[buffer3[0] % 21] & 1) + 176) | (buffer4[buffer3[0] % 21] & ~3)) & buffer3[56]))) & ~buffer3[52]) | buffer3[48]));
        buffer2[33] ^= buffer1[26];
        buffer1[106] ^= (byte)(buffer3[20] ^ 133);
        buffer2[30] = unchecked((byte)(((buffer3[64] / 3) - (275 | (buffer3[0] & 247))) ^ buffer0[buffer1[122] % 20]));
        buffer1[22] = (byte)((buffer2[buffer1[90] % 35] & 95) | 68);

        a = (uint)((buffer4[buffer3[36] % 21] & 184) | (buffer2[buffer3[44] % 35] & ~184));
        buffer2[18] = (byte)(buffer2[18] + ((a * a * a) >> 1));
        buffer2[5] = (byte)(buffer2[5] - buffer4[buffer1[92] % 21]);
        a = (uint)((((buffer1[41] & ~24) | (buffer2[buffer1[183] % 35] & 24)) & (buffer3[16] + 53)) | (buffer3[20] & buffer2[buffer3[20] % 35]));
        b = (uint)((buffer1[17] & ~buffer3[44]) | (buffer0[buffer1[59] % 20] & buffer3[44]));
        buffer2[18] ^= (byte)(a * b);

        a = Endian.WeirdRor8(buffer1[11], buffer2[buffer1[28] % 35] & 7) & 7;
        b = (uint)((((buffer0[buffer1[93] % 20] & ~buffer0[14]) | (buffer0[14] & 150)) & ~28) | (buffer1[7] & 28));
        uint rolled = Endian.WeirdRol8(buffer2[buffer3[0] % 35], (int)a);
        buffer2[22] = (byte)(((((b | rolled) & buffer2[33]) | (b & rolled)) + 74) & 0xff);
        a = buffer4[(buffer0[buffer1[39] % 20] ^ 217) % 21];
        uint mask15 = (uint)((((buffer3[20] | buffer3[0]) & 214) | (buffer3[20] & buffer3[0])));
        buffer0[15] = (byte)(buffer0[15] - ((mask15 & a) | ((mask15 | a) & buffer3[32])));

        b = (uint)((((buffer2[buffer1[57] % 35] & buffer0[buffer3[64] % 20]) | ((buffer0[buffer3[64] % 20] | buffer2[buffer1[57] % 35]) & 95) | (buffer3[64] & 45) | 82) & 32));
        c = (uint)(((buffer2[buffer1[57] % 35] & buffer0[buffer3[64] % 20]) | ((buffer2[buffer1[57] % 35] | buffer0[buffer3[64] % 20]) & 95)) & ((buffer3[64] & 45) | 82));
        int dSigned = ((buffer3[0] / 3) - (buffer3[64] | buffer1[22])) ^ (buffer3[28] + 62);
        d = unchecked((uint)dSigned) ^ (b | c);
        t = buffer0[(int)((d & 0xff) % 20)];
        int quad = buffer0[buffer1[99] % 20];
        quad = unchecked(quad * buffer0[buffer1[99] % 20]);
        quad = unchecked(quad * buffer0[buffer1[99] % 20]);
        quad = unchecked(quad * buffer0[buffer1[99] % 20]);
        buffer3[68] = (byte)(quad | buffer2[buffer3[64] % 35]);

        u = buffer0[buffer1[50] % 20];
        w = buffer2[buffer1[138] % 35];
        x = buffer4[buffer1[39] % 21];
        y = buffer0[buffer1[4] % 20];
        z = buffer4[buffer1[202] % 21];
        v = buffer0[buffer1[151] % 20];
        s = buffer2[buffer1[14] % 35];
        r = buffer0[buffer1[145] % 20];

        a = (uint)((buffer2[buffer3[68] % 35] & buffer0[buffer1[209] % 20]) | ((buffer2[buffer3[68] % 35] | buffer0[buffer1[209] % 20]) & 24));
        b = Endian.WeirdRol8(buffer4[buffer1[127] % 21], buffer2[buffer3[68] % 35] & 7);
        c = (a & buffer0[10]) | (b & unchecked((uint)~buffer0[10]));
        d = (uint)(7 ^ (buffer4[buffer2[buffer3[36] % 35] % 21] << 1));
        buffer3[72] = (byte)((c & 71) | (d & unchecked((uint)~71)));
        buffer2[2] = (byte)(buffer2[2] + ((((buffer0[buffer3[20] % 20] << 1) & 159) | (buffer4[buffer1[190] % 21] & ~159)) & ((((buffer4[buffer3[64] % 21] & 110) | (buffer0[buffer1[25] % 20] & ~110)) & ~150) | (buffer1[25] & 150))));
        buffer2[14] = (byte)(buffer2[14] - (((buffer2[buffer3[20] % 35] & (buffer3[72] ^ buffer2[buffer1[100] % 35])) & ~34) | (buffer1[97] & 34)));
        buffer0[17] = 115;
        buffer1[23] ^= (byte)(((((((buffer4[buffer1[17] % 21] | buffer0[buffer3[20] % 20]) & buffer3[72]) | (buffer4[buffer1[17] % 21] & buffer0[buffer3[20] % 20])) & (buffer1[50] / 3))
                                | ((((buffer4[buffer1[17] % 21] | buffer0[buffer3[20] % 20]) & buffer3[72]) | (buffer4[buffer1[17] % 21] & buffer0[buffer3[20] % 20]) | (buffer1[50] / 3)) & 246)) << 1));
        buffer0[13] = (byte)(((((((buffer0[buffer3[40] % 20] | buffer1[10]) & 82) | (buffer0[buffer3[40] % 20] & buffer1[10])) & 209)
                              | ((buffer0[buffer1[39] % 20] << 1) & 46)) >> 1));
        buffer2[33] = (byte)(buffer2[33] - (buffer1[113] & 9));
        buffer2[28] = (byte)(buffer2[28] - ((((2 | (buffer1[110] & 222)) >> 1) & ~223) | (buffer3[20] & 223)));

        j = Endian.WeirdRol8((byte)(v | z), (int)(u & 7));
        a = (buffer2[16] & t) | (w & unchecked((uint)~buffer2[16]));
        b = (buffer1[33] & 17u) | (x & unchecked((uint)~17));
        e = ((y | ((a + b) / 5)) & 147) | (y & ((a + b) / 5));
        int mixIndex = (int)((buffer3[8] + j + e) & 0xff) % 21;
        m = (buffer3[40] & (uint)buffer4[mixIndex]) | ((buffer3[40] | (uint)buffer4[mixIndex]) & buffer2[23]);
        int cubeMask = ((buffer4[buffer3[20] % 21] - 48) & ~buffer1[184]) | ((buffer4[buffer3[20] % 21] - 48) & 189) | (189 & ~buffer1[184]);
        buffer0[15] = (byte)(unchecked((uint)cubeMask) & (m * m * m));
        buffer2[22] = (byte)(buffer2[22] + buffer1[183]);
        buffer3[76] = (byte)((3 * buffer4[buffer1[1] % 21]) ^ buffer3[0]);

        a = buffer2[(int)((buffer3[8] + (j + e)) & 0xff) % 35];
        f = (((buffer4[buffer1[178] % 21] & a) | ((buffer4[buffer1[178] % 21] | a) & 209)) * buffer0[buffer1[13] % 20]) * (uint)(buffer4[buffer1[26] % 21] >> 1);
        g = ((f + 0x733ffff9) * 198) - ((((f + 0x733ffff9) * 396) + 212) & 212) + 85;
        buffer3[80] = (byte)(buffer3[36] + (g ^ 148) + ((g ^ 107) << 1) - 127);
        buffer3[84] = (byte)((buffer2[buffer3[64] % 35] & 245) | (buffer2[buffer3[20] % 35] & 10));
        a = (uint)(buffer0[buffer3[68] % 20] | 81);
        buffer2[18] = (byte)(buffer2[18] - (((a * a * a) & unchecked((uint)~buffer0[15])) | ((uint)(buffer3[80] / 15) & buffer0[15])));
        int jeIndex = (int)((buffer3[8] + j + e) & 255);
        buffer3[88] = (byte)(buffer3[8] + j + e - buffer0[buffer1[160] % 20] + (uint)(buffer4[buffer0[jeIndex % 20] % 21] / 3));

        b = ((r ^ buffer3[72]) & unchecked((uint)~198)) | ((s * s) & 198);
        f = (uint)(buffer4[buffer1[69] % 21] & buffer1[172]) | ((uint)(buffer4[buffer1[69] % 21] | buffer1[172]) & ((uint)(buffer3[12] - b) + 77));
        buffer0[16] = (byte)(147u - ((buffer3[72] & ((f & 251) | 1)) | (((f & 250) | buffer3[72]) & 198)));
        c = (uint)((buffer4[buffer1[168] % 21] & buffer0[buffer1[29] % 20] & 7) | ((buffer4[buffer1[168] % 21] | buffer0[buffer1[29] % 20]) & 6));
        f = (uint)((buffer4[buffer1[155] % 21] & buffer1[105]) | ((buffer4[buffer1[155] % 21] | buffer1[105]) & 141));
        buffer0[3] = (byte)(buffer0[3] - buffer4[(int)(Endian.WeirdRol32((byte)f, (int)c) % 21)]);
        buffer1[5] = (byte)(Endian.WeirdRor8(buffer0[12], (buffer0[buffer1[61] % 20] / 5) & 7) ^ (unchecked((uint)~buffer2[buffer3[84] % 35]) / 5));
        buffer1[198] = (byte)(buffer1[198] + buffer1[3]);
        a = 162u | buffer2[buffer3[64] % 35];
        buffer1[164] = (byte)(buffer1[164] + ((a * a) / 5));

        g = Endian.WeirdRor8(139, buffer3[80] & 7);
        c = (uint)(((buffer4[buffer3[64] % 21] * buffer4[buffer3[64] % 21] * buffer4[buffer3[64] % 21]) & 95) | (buffer0[buffer3[40] % 20] & ~95));
        buffer3[92] = (byte)((g & 12) | (uint)(buffer0[buffer3[20] % 20] & 12) | (g & buffer0[buffer3[20] % 20]) | c);
        buffer2[12] = (byte)(buffer2[12] + (((buffer1[103] & 32) | (buffer3[92] & (buffer1[103] | 60)) | 16) / 3));
        buffer3[96] = buffer1[143];
        buffer3[100] = 27;
        buffer3[104] = (byte)((((buffer3[40] & ~buffer2[8]) | (buffer1[35] & buffer2[8])) & buffer3[64]) ^ 119);
        buffer3[108] = (byte)(238 & ((((buffer3[40] & ~buffer2[8]) | (buffer1[35] & buffer2[8])) & buffer3[64]) << 1));
        buffer3[112] = (byte)((~buffer3[64] & (buffer3[84] / 3)) ^ 49);
        buffer3[116] = (byte)(98 & ((~buffer3[64] & (buffer3[84] / 3)) << 1));

        a = (uint)((buffer1[35] & buffer2[8]) | (buffer3[40] & ~buffer2[8]));
        b = (a & buffer3[64]) | (uint)((buffer3[84] / 3) & ~buffer3[64]);
        uint part1 = b & (uint)(86 + ((buffer1[172] & 64) >> 1));
        uint part2 = (uint)((((buffer1[172] & 65) >> 1) ^ 86) | ((~buffer3[64] & (buffer3[84] / 3)) | (((buffer3[40] & ~buffer2[8]) | (buffer1[35] & buffer2[8])) & buffer3[64]))) & buffer3[100];
        buffer1[143] = (byte)(buffer3[96] - (part1 | part2));
        buffer2[29] = 162;

        a = (uint)((((buffer4[buffer3[88] % 21] & 160) | (buffer0[buffer1[125] % 20] & 95)) >> 1));
        b = (uint)(buffer2[buffer1[149] % 35] ^ (buffer1[43] * buffer1[43]));
        buffer0[15] = (byte)(buffer0[15] + ((b & a) | ((a | b) & 115)));
        buffer3[120] = (byte)(buffer3[64] - buffer0[buffer3[40] % 20]);
        buffer1[95] = buffer4[buffer3[20] % 21];
        a = Endian.WeirdRor8(buffer2[buffer3[80] % 35], (buffer2[buffer1[17] % 35] * buffer2[buffer1[17] % 35] * buffer2[buffer1[17] % 35]) & 7);
        buffer0[7] = (byte)(buffer0[7] - (a * a));
        buffer2[8] = unchecked((byte)(buffer2[8] - buffer1[184] + (buffer4[buffer1[202] % 21] * buffer4[buffer1[202] % 21] * buffer4[buffer1[202] % 21])));
        buffer0[16] = (byte)((buffer2[buffer1[102] % 35] << 1) & 132);
        buffer3[124] = (byte)((buffer4[buffer3[40] % 21] >> 1) ^ buffer3[68]);
        buffer0[7] = unchecked((byte)(buffer0[7] - (buffer0[buffer1[191] % 20] - (((buffer4[buffer1[80] % 21] << 1) & ~177) | (buffer4[buffer4[buffer3[88] % 21] % 21] & 177)))));
        buffer0[6] = buffer0[buffer1[119] % 20];

        a = (uint)((buffer4[buffer1[190] % 21] & ~209) | (buffer1[118] & 209));
        b = (uint)(buffer0[buffer3[120] % 20] * buffer0[buffer3[120] % 20]);
        buffer0[12] = (byte)((buffer0[buffer3[84] % 20] ^ (buffer2[buffer1[71] % 35] + buffer2[buffer1[15] % 35])) & (int)((a & b) | ((a | b) & 27)));
        b = (uint)((buffer1[32] & buffer2[buffer3[88] % 35]) | ((buffer1[32] | buffer2[buffer3[88] % 35]) & 23));
        d = ((uint)((buffer4[buffer1[57] % 21] * 231) & 169)) | (b & 86);
        f = (uint)((((buffer0[buffer1[82] % 20] & ~29) | (buffer4[buffer3[124] % 21] & 29)) & 190) | (buffer4[(int)(d / 5) % 21] & ~190));
        h = (uint)(buffer0[buffer3[40] % 20] * buffer0[buffer3[40] % 20] * buffer0[buffer3[40] % 20]);
        k = (h & buffer1[82]) | (h & 92) | (buffer1[82] & 92u);
        buffer3[128] = (byte)(((f & k) | ((f | k) & 192)) ^ (d / 5));
        uint rolled76 = Endian.WeirdRol8(buffer3[76], buffer4[buffer3[124] % 21] & 7) & (uint)(buffer3[20] + 110);
        uint spread = unchecked((uint)((buffer0[buffer3[120] % 20] << 1) * buffer1[5])) - rolled76;
        buffer2[25] ^= (byte)spread;
    }
}
