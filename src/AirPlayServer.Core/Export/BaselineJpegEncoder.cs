using System.Buffers.Binary;

namespace AirPlayServer.Core.Export;

/// <summary>
/// Baseline JPEG (SOF0), 4:4:4, standard Huffman tables, written from BGRA.
/// No external image library is linked, so Native AOT does not take an ImageSharp
/// or FFmpeg dependency for the TapClock stills.
/// </summary>
internal static class BaselineJpegEncoder
{
    private static readonly HuffmanTable DcLuminance = HuffmanTable.Build(DcLumBits, DcLumValues);
    private static readonly HuffmanTable DcChrominance = HuffmanTable.Build(DcChromBits, DcChromValues);
    private static readonly HuffmanTable AcLuminance = HuffmanTable.Build(AcLumBits, AcLumValues);
    private static readonly HuffmanTable AcChrominance = HuffmanTable.Build(AcChromBits, AcChromValues);

    private static readonly double[,] Cosine = BuildCosine();

    private static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static readonly byte[] LuminanceQuant =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly byte[] ChrominanceQuant =
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    public static byte[] EncodeBgra(ReadOnlySpan<byte> bgra, int width, int height, int stride, int quality)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (stride < width * 4 || bgra.Length < stride * height)
        {
            throw new ArgumentException("BGRA buffer is smaller than the image.");
        }

        quality = Math.Clamp(quality, 1, 100);
        Span<int> yQuant = stackalloc int[64];
        Span<int> cQuant = stackalloc int[64];
        ScaleQuant(LuminanceQuant, quality, yQuant);
        ScaleQuant(ChrominanceQuant, quality, cQuant);

        var output = new List<byte>(Math.Max(1024, width * height / 4));
        output.Add(0xFF);
        output.Add(0xD8);
        WriteApp0(output);
        WriteQuantization(output, yQuant, cQuant);
        WriteStartOfFrame(output, width, height);
        WriteHuffmanTables(output);
        WriteStartOfScan(output);

        var bits = new BitWriter(output);
        var yBlock = new double[64];
        var cbBlock = new double[64];
        var crBlock = new double[64];
        var coeff = new int[64];
        var previousY = 0;
        var previousCb = 0;
        var previousCr = 0;
        for (var blockY = 0; blockY < height; blockY += 8)
        {
            for (var blockX = 0; blockX < width; blockX += 8)
            {
                LoadBlocks(bgra, width, height, stride, blockX, blockY, yBlock, cbBlock, crBlock);
                EncodeBlock(yBlock, yQuant, coeff, ref previousY, DcLuminance, AcLuminance, bits);
                EncodeBlock(cbBlock, cQuant, coeff, ref previousCb, DcChrominance, AcChrominance, bits);
                EncodeBlock(crBlock, cQuant, coeff, ref previousCr, DcChrominance, AcChrominance, bits);
            }
        }

        bits.Flush();
        output.Add(0xFF);
        output.Add(0xD9);
        return output.ToArray();
    }

    private static void EncodeBlock(double[] samples, ReadOnlySpan<int> quant, int[] coefficients, ref int previousDc, HuffmanTable dc, HuffmanTable ac, BitWriter bits)
    {
        for (var v = 0; v < 8; v++)
        {
            for (var u = 0; u < 8; u++)
            {
                double sum = 0;
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        sum += samples[(y * 8) + x] * Cosine[u, x] * Cosine[v, y];
                    }
                }

                var cu = u == 0 ? 0.7071067811865476 : 1d;
                var cv = v == 0 ? 0.7071067811865476 : 1d;
                coefficients[(v * 8) + u] = (int)Math.Round((sum * 0.25 * cu * cv) / quant[(v * 8) + u]);
            }
        }

        Span<int> zigzag = stackalloc int[64];
        for (var i = 0; i < 64; i++)
        {
            zigzag[i] = coefficients[ZigZag[i]];
        }

        WriteDc(zigzag[0] - previousDc, dc, bits);
        previousDc = zigzag[0];
        var run = 0;
        var trailing = true;
        for (var i = 1; i < 64; i++)
        {
            if (zigzag[i] == 0)
            {
                run++;
                continue;
            }

            trailing = false;
            while (run > 15)
            {
                ac.Write(0xF0, bits);
                run -= 16;
            }

            WriteAc(run, zigzag[i], ac, bits);
            run = 0;
        }

        if (trailing || run > 0)
        {
            ac.Write(0x00, bits);
        }
    }

    private static void WriteDc(int diff, HuffmanTable table, BitWriter bits)
    {
        var category = Category(diff);
        table.Write(category, bits);
        WriteMagnitude(diff, category, bits);
    }

    private static void WriteAc(int run, int value, HuffmanTable table, BitWriter bits)
    {
        var category = Category(value);
        table.Write((run << 4) | category, bits);
        WriteMagnitude(value, category, bits);
    }

    private static void WriteMagnitude(int value, int category, BitWriter bits)
    {
        if (category == 0)
        {
            return;
        }

        var magnitude = value >= 0 ? value : value - 1;
        bits.Write(magnitude, category);
    }

    private static int Category(int value)
    {
        var magnitude = value < 0 ? -value : value;
        var category = 0;
        while (magnitude > 0)
        {
            magnitude >>= 1;
            category++;
        }

        return category;
    }

    private static void LoadBlocks(ReadOnlySpan<byte> bgra, int width, int height, int stride, int originX, int originY, double[] yBlock, double[] cbBlock, double[] crBlock)
    {
        for (var y = 0; y < 8; y++)
        {
            var sourceY = Math.Min(originY + y, height - 1);
            for (var x = 0; x < 8; x++)
            {
                var sourceX = Math.Min(originX + x, width - 1);
                var pixel = (sourceY * stride) + (sourceX * 4);
                var b = bgra[pixel];
                var g = bgra[pixel + 1];
                var r = bgra[pixel + 2];
                var index = (y * 8) + x;
                yBlock[index] = ((0.299 * r) + (0.587 * g) + (0.114 * b)) - 128;
                cbBlock[index] = ((-0.168736 * r) - (0.331264 * g)) + (0.5 * b);
                crBlock[index] = (0.5 * r) - (0.418688 * g) - (0.081312 * b);
            }
        }
    }

    private static void ScaleQuant(ReadOnlySpan<byte> baseline, int quality, Span<int> destination)
    {
        var scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        for (var i = 0; i < 64; i++)
        {
            var value = ((baseline[i] * scale) + 50) / 100;
            destination[i] = Math.Clamp(value, 1, 255);
        }
    }

    private static void WriteApp0(List<byte> output)
    {
        output.Add(0xFF);
        output.Add(0xE0);
        WriteBe16(output, 16);
        output.Add((byte)'J');
        output.Add((byte)'F');
        output.Add((byte)'I');
        output.Add((byte)'F');
        output.Add(0);
        output.Add(1);
        output.Add(1);
        output.Add(0);
        WriteBe16(output, 1);
        WriteBe16(output, 1);
        output.Add(0);
        output.Add(0);
    }

    private static void WriteQuantization(List<byte> output, ReadOnlySpan<int> yQuant, ReadOnlySpan<int> cQuant)
    {
        output.Add(0xFF);
        output.Add(0xDB);
        WriteBe16(output, 2 + (2 * 65));
        WriteQuantTable(output, 0, yQuant);
        WriteQuantTable(output, 1, cQuant);
    }

    private static void WriteQuantTable(List<byte> output, byte id, ReadOnlySpan<int> natural)
    {
        output.Add(id);
        for (var i = 0; i < 64; i++)
        {
            output.Add((byte)natural[ZigZag[i]]);
        }
    }

    private static void WriteStartOfFrame(List<byte> output, int width, int height)
    {
        output.Add(0xFF);
        output.Add(0xC0);
        WriteBe16(output, 17);
        output.Add(8);
        WriteBe16(output, height);
        WriteBe16(output, width);
        output.Add(3);
        output.Add(1);
        output.Add(0x11);
        output.Add(0);
        output.Add(2);
        output.Add(0x11);
        output.Add(1);
        output.Add(3);
        output.Add(0x11);
        output.Add(1);
    }

    private static void WriteHuffmanTables(List<byte> output)
    {
        var length = 2
            + TableSize(DcLumBits, DcLumValues)
            + TableSize(DcChromBits, DcChromValues)
            + TableSize(AcLumBits, AcLumValues)
            + TableSize(AcChromBits, AcChromValues);
        output.Add(0xFF);
        output.Add(0xC4);
        WriteBe16(output, length);
        WriteHuffman(output, 0x00, DcLumBits, DcLumValues);
        WriteHuffman(output, 0x01, DcChromBits, DcChromValues);
        WriteHuffman(output, 0x10, AcLumBits, AcLumValues);
        WriteHuffman(output, 0x11, AcChromBits, AcChromValues);
    }

    private static void WriteHuffman(List<byte> output, byte id, ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        output.Add(id);
        foreach (var count in bits)
        {
            output.Add(count);
        }

        foreach (var value in values)
        {
            output.Add(value);
        }
    }

    private static int TableSize(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values) => 1 + bits.Length + values.Length;

    private static void WriteStartOfScan(List<byte> output)
    {
        output.Add(0xFF);
        output.Add(0xDA);
        WriteBe16(output, 12);
        output.Add(3);
        output.Add(1);
        output.Add(0x00);
        output.Add(2);
        output.Add(0x11);
        output.Add(3);
        output.Add(0x11);
        output.Add(0);
        output.Add(63);
        output.Add(0);
    }

    private static void WriteBe16(List<byte> output, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        output.Add(bytes[0]);
        output.Add(bytes[1]);
    }

    private static double[,] BuildCosine()
    {
        var table = new double[8, 8];
        for (var frequency = 0; frequency < 8; frequency++)
        {
            for (var sample = 0; sample < 8; sample++)
            {
                table[frequency, sample] = Math.Cos(((2 * sample) + 1) * frequency * Math.PI / 16d);
            }
        }

        return table;
    }

    private static ReadOnlySpan<byte> DcLumBits => [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static ReadOnlySpan<byte> DcLumValues => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static ReadOnlySpan<byte> DcChromBits => [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static ReadOnlySpan<byte> DcChromValues => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static ReadOnlySpan<byte> AcLumBits => [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D];
    private static ReadOnlySpan<byte> AcLumValues =>
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    private static ReadOnlySpan<byte> AcChromBits => [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    private static ReadOnlySpan<byte> AcChromValues =>
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
        0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
        0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
        0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    private readonly struct HuffmanTable
    {
        private readonly ushort[] _code;
        private readonly byte[] _size;

        private HuffmanTable(ushort[] code, byte[] size)
        {
            _code = code;
            _size = size;
        }

        public static HuffmanTable Build(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
        {
            var expected = 0;
            foreach (var count in bits)
            {
                expected += count;
            }

            if (expected != values.Length)
            {
                throw new InvalidOperationException("JPEG Huffman bits and values disagree (" + values.Length + " vs " + expected + ").");
            }

            var code = new ushort[256];
            var size = new byte[256];
            var cursor = 0;
            var word = 0;
            for (var length = 1; length <= bits.Length; length++)
            {
                for (var count = 0; count < bits[length - 1]; count++)
                {
                    var symbol = values[cursor++];
                    code[symbol] = (ushort)word;
                    size[symbol] = (byte)length;
                    word++;
                }

                word <<= 1;
            }

            return new HuffmanTable(code, size);
        }

        public void Write(int symbol, BitWriter bits)
        {
            var length = _size[symbol];
            if (length == 0)
            {
                throw new InvalidOperationException("JPEG Huffman symbol " + symbol + " is missing.");
            }

            bits.Write(_code[symbol], length);
        }
    }

    private sealed class BitWriter(List<byte> output)
    {
        private uint _accumulator;
        private int _count;

        public void Write(int value, int size)
        {
            if (size <= 0)
            {
                return;
            }

            if (size > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }

            _accumulator = (_accumulator << size) | ((uint)value & ((1u << size) - 1));
            _count += size;
            while (_count >= 8)
            {
                _count -= 8;
                Emit((byte)((_accumulator >> _count) & 0xFF));
            }
        }

        public void Flush()
        {
            if (_count <= 0)
            {
                return;
            }

            var pad = 8 - _count;
            var value = (byte)(((_accumulator << pad) | ((1u << pad) - 1)) & 0xFF);
            _count = 0;
            Emit(value);
        }

        private void Emit(byte value)
        {
            output.Add(value);
            if (value == 0xFF)
            {
                output.Add(0x00);
            }
        }
    }
}
