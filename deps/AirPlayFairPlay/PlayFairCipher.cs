namespace AirPlayFairPlay;

/// <summary>
/// Faithful port of playfair/playfair.c and the FairPlay half of playfair/omg_hax.c.
/// Word stores follow the little-endian layout of the original x64 build.
/// </summary>
internal static class PlayFairCipher
{
    internal static void Decrypt(ReadOnlySpan<byte> message3, ReadOnlySpan<byte> cipherText, Span<byte> keyOut)
    {
        Span<byte> sapKey = stackalloc byte[16];
        Span<uint> schedule = stackalloc uint[44];
        GenerateSessionKey(Constants.DefaultSap, message3, sapKey);
        GenerateKeySchedule(sapKey, schedule);

        Span<byte> block = stackalloc byte[16];
        ZXor(cipherText[56..], block);
        Cycle(block, schedule);
        for (int i = 0; i < 16; i++)
        {
            keyOut[i] = (byte)(block[i] ^ cipherText[16 + i]);
        }

        XXor(keyOut);
        ZXor(keyOut, keyOut);
    }

    private static void GenerateSessionKey(ReadOnlySpan<byte> oldSap, ReadOnlySpan<byte> messageIn, Span<byte> sessionKey)
    {
        Span<byte> decrypted = stackalloc byte[128];
        Span<byte> newSap = stackalloc byte[320];
        DecryptMessage(messageIn, decrypted);
        Constants.StaticSource1.AsSpan().CopyTo(newSap);
        decrypted.CopyTo(newSap[0x11..]);
        oldSap.Slice(0x80, 0x80).CopyTo(newSap[0x91..]);
        Constants.StaticSource2.AsSpan().CopyTo(newSap[0x111..]);
        Constants.InitialSessionKey.AsSpan().CopyTo(sessionKey);

        Span<byte> md5 = stackalloc byte[16];
        for (int round = 0; round < 5; round++)
        {
            ReadOnlySpan<byte> basis = newSap.Slice(round * 64, 64);
            ModifiedMd5.Compute(basis, sessionKey, md5);
            SapHash.Compute(basis, sessionKey);
            for (int i = 0; i < 4; i++)
            {
                Endian.WriteU32(sessionKey, i * 4, Endian.ReadU32(sessionKey, i * 4) + Endian.ReadU32(md5, i * 4));
            }
        }

        for (int i = 0; i < 16; i += 4)
        {
            (sessionKey[i], sessionKey[i + 3]) = (sessionKey[i + 3], sessionKey[i]);
            (sessionKey[i + 1], sessionKey[i + 2]) = (sessionKey[i + 2], sessionKey[i + 1]);
        }

        for (int i = 0; i < 16; i++)
        {
            sessionKey[i] ^= 121;
        }
    }

    private static void DecryptMessage(ReadOnlySpan<byte> messageIn, Span<byte> decryptedMessage)
    {
        int mode = messageIn[12];
        Span<uint> schedule = stackalloc uint[44];
        GenerateKeySchedule(Constants.InitialSessionKey, schedule);
        Span<byte> buffer = stackalloc byte[16];

        Span<byte> snapshot = stackalloc byte[16];
        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 16; j++)
            {
                int source = mode == 3 ? (0x80 - (0x10 * i)) + j : (0x10 * (i + 1)) + j;
                buffer[j] = messageIn[source];
            }

            for (int round = 0; round < 9; round++)
            {
                int basis = 0x80 - (0x10 * round);
                PermuteMessage(buffer, mode, basis);
                buffer.CopyTo(snapshot);
                for (int word = 0; word < 4; word++)
                {
                    int o = word * 4;
                    uint mixed = Tables.TableS9[snapshot[o]]
                                 ^ Tables.TableS9[0x100 + snapshot[o + 1]]
                                 ^ Tables.TableS9[0x200 + snapshot[o + 2]]
                                 ^ Tables.TableS9[0x300 + snapshot[o + 3]];
                    Endian.WriteU32(buffer, o, mixed);
                }
            }

            buffer[0x0] = Tables.TableS10[(0x0 << 8) + buffer[0x0]];
            buffer[0x4] = Tables.TableS10[(0x4 << 8) + buffer[0x4]];
            buffer[0x8] = Tables.TableS10[(0x8 << 8) + buffer[0x8]];
            buffer[0xc] = Tables.TableS10[(0xc << 8) + buffer[0xc]];
            byte swap = buffer[0x0d];
            buffer[0xd] = Tables.TableS10[(0xd << 8) + buffer[0x9]];
            buffer[0x9] = Tables.TableS10[(0x9 << 8) + buffer[0x5]];
            buffer[0x5] = Tables.TableS10[(0x5 << 8) + buffer[0x1]];
            buffer[0x1] = Tables.TableS10[(0x1 << 8) + swap];
            swap = buffer[0x02];
            buffer[0x2] = Tables.TableS10[(0x2 << 8) + buffer[0xa]];
            buffer[0xa] = Tables.TableS10[(0xa << 8) + swap];
            swap = buffer[0x06];
            buffer[0x6] = Tables.TableS10[(0x6 << 8) + buffer[0xe]];
            buffer[0xe] = Tables.TableS10[(0xe << 8) + swap];
            swap = buffer[0x3];
            buffer[0x3] = Tables.TableS10[(0x3 << 8) + buffer[0x7]];
            buffer[0x7] = Tables.TableS10[(0x7 << 8) + buffer[0xb]];
            buffer[0xb] = Tables.TableS10[(0xb << 8) + buffer[0xf]];
            buffer[0xf] = Tables.TableS10[(0xf << 8) + swap];

            if (mode is 0 or 1 or 2)
            {
                int at = 0x10 * i;
                XorBlock(buffer, i > 0 ? messageIn.Slice(at, 16) : MessageIv(mode), decryptedMessage.Slice(at, 16));
            }
            else
            {
                int at = 0x70 - (0x10 * i);
                XorBlock(buffer, i < 7 ? messageIn.Slice(at, 16) : MessageIv(mode), decryptedMessage.Slice(at, 16));
            }
        }
    }

    private static void PermuteMessage(Span<byte> buffer, int mode, int basis)
    {
        buffer[0x0] = (byte)(MessageTable(basis + 0x0, buffer[0x0]) ^ MessageKey(mode, basis + 0x0));
        buffer[0x4] = (byte)(MessageTable(basis + 0x4, buffer[0x4]) ^ MessageKey(mode, basis + 0x4));
        buffer[0x8] = (byte)(MessageTable(basis + 0x8, buffer[0x8]) ^ MessageKey(mode, basis + 0x8));
        buffer[0xc] = (byte)(MessageTable(basis + 0xc, buffer[0xc]) ^ MessageKey(mode, basis + 0xc));

        byte swap = buffer[0x0d];
        buffer[0xd] = (byte)(MessageTable(basis + 0xd, buffer[0x9]) ^ MessageKey(mode, basis + 0xd));
        buffer[0x9] = (byte)(MessageTable(basis + 0x9, buffer[0x5]) ^ MessageKey(mode, basis + 0x9));
        buffer[0x5] = (byte)(MessageTable(basis + 0x5, buffer[0x1]) ^ MessageKey(mode, basis + 0x5));
        buffer[0x1] = (byte)(MessageTable(basis + 0x1, swap) ^ MessageKey(mode, basis + 0x1));

        swap = buffer[0x02];
        buffer[0x2] = (byte)(MessageTable(basis + 0x2, buffer[0xa]) ^ MessageKey(mode, basis + 0x2));
        buffer[0xa] = (byte)(MessageTable(basis + 0xa, swap) ^ MessageKey(mode, basis + 0xa));
        swap = buffer[0x06];
        buffer[0x6] = (byte)(MessageTable(basis + 0x6, buffer[0xe]) ^ MessageKey(mode, basis + 0x6));
        buffer[0xe] = (byte)(MessageTable(basis + 0xe, swap) ^ MessageKey(mode, basis + 0xe));

        swap = buffer[0x3];
        buffer[0x3] = (byte)(MessageTable(basis + 0x3, buffer[0x7]) ^ MessageKey(mode, basis + 0x3));
        buffer[0x7] = (byte)(MessageTable(basis + 0x7, buffer[0xb]) ^ MessageKey(mode, basis + 0x7));
        buffer[0xb] = (byte)(MessageTable(basis + 0xb, buffer[0xf]) ^ MessageKey(mode, basis + 0xb));
        buffer[0xf] = (byte)(MessageTable(basis + 0xf, swap) ^ MessageKey(mode, basis + 0xf));
    }

    private static void GenerateKeySchedule(ReadOnlySpan<byte> keyMaterial, Span<uint> schedule)
    {
        Span<byte> buffer = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            buffer[i] = (byte)(keyMaterial[i] ^ Tables.TKey[i]);
        }

        int ti = 0;
        for (int round = 0; round < 11; round++)
        {
            schedule[(round * 4) + 0] = Endian.ReadU32(buffer, 0);
            buffer[0] = (byte)(buffer[0] ^ TableS1(ti, buffer[0x0d]) ^ Constants.IndexMangle[round]);
            buffer[1] = (byte)(buffer[1] ^ TableS1(ti + 1, buffer[0x0e]));
            buffer[2] = (byte)(buffer[2] ^ TableS1(ti + 2, buffer[0x0f]));
            buffer[3] = (byte)(buffer[3] ^ TableS1(ti + 3, buffer[0x0c]));
            ti += 4;

            schedule[(round * 4) + 1] = Endian.ReadU32(buffer, 4);
            Endian.WriteU32(buffer, 4, Endian.ReadU32(buffer, 4) ^ Endian.ReadU32(buffer, 0));
            schedule[(round * 4) + 2] = Endian.ReadU32(buffer, 8);
            Endian.WriteU32(buffer, 8, Endian.ReadU32(buffer, 8) ^ Endian.ReadU32(buffer, 4));
            schedule[(round * 4) + 3] = Endian.ReadU32(buffer, 12);
            Endian.WriteU32(buffer, 12, Endian.ReadU32(buffer, 12) ^ Endian.ReadU32(buffer, 8));
        }
    }

    private static void Cycle(Span<byte> block, ReadOnlySpan<uint> schedule)
    {
        XorSchedule(block, schedule, 10);
        PermuteBlock1(block);
        Span<byte> key0 = stackalloc byte[4];
        Span<byte> key1 = stackalloc byte[4];
        Span<byte> key2 = stackalloc byte[4];
        Span<byte> key3 = stackalloc byte[4];
        for (int round = 0; round < 9; round++)
        {
            int row = 9 - round;
            Endian.WriteU32(key0, 0, schedule[(row * 4) + 0]);
            Endian.WriteU32(key1, 0, schedule[(row * 4) + 1]);
            Endian.WriteU32(key2, 0, schedule[(row * 4) + 2]);
            Endian.WriteU32(key3, 0, schedule[(row * 4) + 3]);

            uint word0 = Tables.TableS5[block[3] ^ key0[3]]
                         ^ Tables.TableS6[block[2] ^ key0[2]]
                         ^ Tables.TableS8[block[0] ^ key0[0]]
                         ^ Tables.TableS7[block[1] ^ key0[1]];
            uint word1 = Tables.TableS6[block[6] ^ key1[2]]
                         ^ Tables.TableS5[block[7] ^ key1[3]]
                         ^ Tables.TableS8[block[4] ^ key1[0]]
                         ^ Tables.TableS7[block[5] ^ key1[1]];
            uint word2 = Tables.TableS5[block[11] ^ key2[3]]
                         ^ Tables.TableS6[block[10] ^ key2[2]]
                         ^ Tables.TableS7[block[9] ^ key2[1]]
                         ^ Tables.TableS8[block[8] ^ key2[0]];
            uint word3 = Tables.TableS5[block[15] ^ key3[3]]
                         ^ Tables.TableS6[block[14] ^ key3[2]]
                         ^ Tables.TableS7[block[13] ^ key3[1]]
                         ^ Tables.TableS8[block[12] ^ key3[0]];
            Endian.WriteU32(block, 0, word0);
            Endian.WriteU32(block, 4, word1);
            Endian.WriteU32(block, 8, word2);
            Endian.WriteU32(block, 12, word3);
            PermuteBlock2(block, 8 - round);
        }

        XorSchedule(block, schedule, 0);
    }

    private static void PermuteBlock1(Span<byte> block)
    {
        block[0] = Tables.TableS3[block[0]];
        block[4] = Tables.TableS3[0x400 + block[4]];
        block[8] = Tables.TableS3[0x800 + block[8]];
        block[12] = Tables.TableS3[0xc00 + block[12]];

        byte swap = block[13];
        block[13] = Tables.TableS3[0x100 + block[9]];
        block[9] = Tables.TableS3[0xd00 + block[5]];
        block[5] = Tables.TableS3[0x900 + block[1]];
        block[1] = Tables.TableS3[0x500 + swap];

        swap = block[2];
        block[2] = Tables.TableS3[0xa00 + block[10]];
        block[10] = Tables.TableS3[0x200 + swap];
        swap = block[6];
        block[6] = Tables.TableS3[0xe00 + block[14]];
        block[14] = Tables.TableS3[0x600 + swap];

        swap = block[3];
        block[3] = Tables.TableS3[0xf00 + block[7]];
        block[7] = Tables.TableS3[0x300 + block[11]];
        block[11] = Tables.TableS3[0x700 + block[15]];
        block[15] = Tables.TableS3[0xb00 + swap];
    }

    private static void PermuteBlock2(Span<byte> block, int round)
    {
        int basis = round * 16;
        block[0] = Permute2(basis + 0, block[0]);
        block[4] = Permute2(basis + 4, block[4]);
        block[8] = Permute2(basis + 8, block[8]);
        block[12] = Permute2(basis + 12, block[12]);

        byte swap = block[13];
        block[13] = Permute2(basis + 13, block[9]);
        block[9] = Permute2(basis + 9, block[5]);
        block[5] = Permute2(basis + 5, block[1]);
        block[1] = Permute2(basis + 1, swap);

        swap = block[2];
        block[2] = Permute2(basis + 2, block[10]);
        block[10] = Permute2(basis + 10, swap);
        swap = block[6];
        block[6] = Permute2(basis + 6, block[14]);
        block[14] = Permute2(basis + 14, swap);

        swap = block[3];
        block[3] = Permute2(basis + 3, block[7]);
        block[7] = Permute2(basis + 7, block[11]);
        block[11] = Permute2(basis + 11, block[15]);
        block[15] = Permute2(basis + 15, swap);
    }

    private static void XorSchedule(Span<byte> block, ReadOnlySpan<uint> schedule, int round)
    {
        for (int i = 0; i < 4; i++)
        {
            Endian.XorU32(block, i * 4, schedule[(round * 4) + i]);
        }
    }

    private static void ZXor(ReadOnlySpan<byte> input, Span<byte> output)
    {
        for (int i = 0; i < 16; i++)
        {
            output[i] = (byte)(input[i] ^ Tables.ZKey[i]);
        }
    }

    private static void XXor(Span<byte> block)
    {
        for (int i = 0; i < 16; i++)
        {
            block[i] = (byte)(block[i] ^ Tables.XKey[i]);
        }
    }

    private static void XorBlock(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, Span<byte> output)
    {
        for (int i = 0; i < 16; i++)
        {
            output[i] = (byte)(left[i] ^ right[i]);
        }
    }

    private static byte TableS1(int i, int value)
        => Tables.TableS1[(((31 * i) % 0x28) << 8) + value];

    private static byte MessageTable(int i, int value)
        => Tables.TableS2[(((97 * i) % 144) << 8) + value];

    private static byte MessageKey(int mode, int index)
        => Tables.MessageKey[(mode * 144) + index];

    private static ReadOnlySpan<byte> MessageIv(int mode)
        => Tables.MessageIv.AsSpan(mode * 16, 16);

    private static byte Permute2(int i, int value)
        => Tables.TableS4[(((71 * i) % 144) << 8) + value];
}
