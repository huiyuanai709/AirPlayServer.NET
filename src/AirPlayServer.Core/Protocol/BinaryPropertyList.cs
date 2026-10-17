using System.Buffers.Binary;
using System.Text;

namespace AirPlayServer.Core.Protocol;

internal abstract class PlistNode;

internal sealed class PlistDictionary : PlistNode
{
    private readonly List<(string Key, PlistNode Value)> _items = [];

    public IReadOnlyList<(string Key, PlistNode Value)> Items => _items;

    public void Add(string key, PlistNode value) => _items.Add((key, value));

    public PlistNode Require(string key)
        => _items.First(item => item.Key == key).Value;
}

internal sealed class PlistArray : PlistNode
{
    private readonly List<PlistNode> _items = [];

    public IReadOnlyList<PlistNode> Items => _items;

    public void Add(PlistNode node) => _items.Add(node);
}

internal sealed class PlistString(string value) : PlistNode
{
    public string Value { get; } = value;
}

internal sealed class PlistInteger(long value) : PlistNode
{
    public long Value { get; } = value;
}

internal sealed class PlistReal(double value) : PlistNode
{
    public double Value { get; } = value;
}

internal sealed class PlistBool(bool value) : PlistNode
{
    public bool Value { get; } = value;
}

internal sealed class PlistData(byte[] value) : PlistNode
{
    public byte[] Value { get; } = value;
}

/// <summary>
/// Apple binary plist (bplist00) for the /info response. The reader accepts the
/// subset this writer emits: dict, array, string, integer, real, bool, and data.
/// </summary>
internal static class BinaryPropertyList
{
    private static ReadOnlySpan<byte> Magic => "bplist00"u8;

    public static byte[] Write(PlistNode root)
    {
        var encoder = new Encoder();
        encoder.Assign(root);
        return encoder.Emit(root);
    }

    public static PlistNode Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 40 || !data.StartsWith(Magic))
        {
            throw new FormatException("Binary plist magic is missing.");
        }

        if (data.Length > 256 * 1024)
        {
            throw new FormatException("Binary plist is larger than this slice accepts.");
        }

        var trailer = data[^32..];
        var offsetSize = trailer[6];
        var refSize = trailer[7];
        var count = checked((int)BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]));
        var top = checked((int)BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]));
        var table = checked((int)BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]));
        if (offsetSize is not (1 or 2 or 4) || refSize is not (1 or 2 or 4) || count is <= 0 or > 4096 || top >= count)
        {
            throw new FormatException("Binary plist trailer is not supported.");
        }

        var offsets = new int[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = checked((int)ReadUnsigned(data, table + (i * offsetSize), offsetSize));
        }

        return new Reader(data, offsets, refSize).ReadObject(top);
    }

    private static long ReadUnsigned(ReadOnlySpan<byte> data, int position, int size)
    {
        long value = 0;
        for (var i = 0; i < size; i++)
        {
            value = (value << 8) | data[position + i];
        }

        return value;
    }

    private sealed class Encoder
    {
        private readonly Dictionary<PlistNode, int> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, PlistString> _strings = new(StringComparer.Ordinal);
        private readonly List<PlistNode> _order = [];

        public void Assign(PlistNode node)
        {
            if (_ids.ContainsKey(node))
            {
                return;
            }

            _ids[node] = _order.Count;
            _order.Add(node);
            switch (node)
            {
                case PlistDictionary dictionary:
                    foreach (var (key, value) in dictionary.Items)
                    {
                        Assign(Intern(key));
                        Assign(value);
                    }

                    break;
                case PlistArray array:
                    foreach (var item in array.Items)
                    {
                        Assign(item);
                    }

                    break;
            }
        }

        public byte[] Emit(PlistNode root)
        {
            var refSize = _order.Count <= 256 ? 1 : _order.Count <= 65536 ? 2 : 4;
            var objects = new byte[_order.Count][];
            for (var i = 0; i < _order.Count; i++)
            {
                objects[i] = Encode(_order[i], refSize);
            }

            var bodyLength = Magic.Length + objects.Sum(static blob => blob.Length);
            var offsetSize = bodyLength <= 0xFF ? 1 : bodyLength <= 0xFFFF ? 2 : 4;
            var output = new byte[bodyLength + (_order.Count * offsetSize) + 32];
            Magic.CopyTo(output);
            var cursor = Magic.Length;
            var offsets = new int[_order.Count];
            for (var i = 0; i < objects.Length; i++)
            {
                offsets[i] = cursor;
                objects[i].CopyTo(output.AsSpan(cursor));
                cursor += objects[i].Length;
            }

            var table = cursor;
            foreach (var offset in offsets)
            {
                WriteUnsigned(output.AsSpan(cursor), offset, offsetSize);
                cursor += offsetSize;
            }

            var trailer = output.AsSpan(output.Length - 32);
            trailer[6] = (byte)offsetSize;
            trailer[7] = (byte)refSize;
            BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)_order.Count);
            BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], (ulong)_ids[root]);
            BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)table);
            return output;
        }

        private byte[] Encode(PlistNode node, int refSize) => node switch
        {
            PlistBool boolean => [(byte)(boolean.Value ? 0x09 : 0x08)],
            PlistInteger integer => EncodeInteger(integer.Value),
            PlistReal real => EncodeReal(real.Value),
            PlistData data => EncodeBytes(0x40, data.Value),
            PlistString text => EncodeString(text.Value),
            PlistArray array => EncodeRefs(0xA0, array.Items, refSize),
            PlistDictionary dictionary => EncodeDictionary(dictionary, refSize),
            _ => throw new NotSupportedException("Unsupported plist node."),
        };

        private byte[] EncodeDictionary(PlistDictionary dictionary, int refSize)
        {
            var refs = new List<PlistNode>(dictionary.Items.Count * 2);
            foreach (var (key, _) in dictionary.Items)
            {
                refs.Add(Intern(key));
            }

            foreach (var (_, value) in dictionary.Items)
            {
                refs.Add(value);
            }

            return EncodeRefs(0xD0, refs, refSize, dictionary.Items.Count);
        }

        private byte[] EncodeRefs(byte marker, IReadOnlyList<PlistNode> nodes, int refSize, int? countOverride = null)
        {
            var count = countOverride ?? nodes.Count;
            var output = new List<byte>(1 + (nodes.Count * refSize));
            WriteCount(output, marker, count);
            foreach (var node in nodes)
            {
                WriteUnsigned(output, _ids[node], refSize);
            }

            return [.. output];
        }

        private PlistString Intern(string value)
        {
            if (!_strings.TryGetValue(value, out var node))
            {
                node = new PlistString(value);
                _strings[value] = node;
            }

            return node;
        }
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly int[] _offsets;
        private readonly int _refSize;
        private readonly PlistNode?[] _cache;
        private int _depth;

        public Reader(ReadOnlySpan<byte> data, int[] offsets, int refSize)
        {
            _data = data;
            _offsets = offsets;
            _refSize = refSize;
            _cache = new PlistNode[offsets.Length];
        }

        public PlistNode ReadObject(int index)
        {
            if (_cache[index] is not null)
            {
                return _cache[index]!;
            }

            if (++_depth > 32)
            {
                throw new FormatException("Binary plist is nested too deeply.");
            }

            var position = _offsets[index];
            var marker = _data[position++];
            PlistNode node = (marker & 0xF0) switch
            {
                0x00 => marker switch
                {
                    0x08 => new PlistBool(false),
                    0x09 => new PlistBool(true),
                    _ => throw new FormatException($"Unsupported plist marker 0x{marker:X2}."),
                },
                0x10 => new PlistInteger(ReadSizedInteger(ref position, marker)),
                0x20 => new PlistReal(ReadReal(ref position, marker)),
                0x40 => new PlistData(ReadRaw(ref position, marker)),
                0x50 => new PlistString(Encoding.ASCII.GetString(ReadRaw(ref position, marker))),
                0x60 => new PlistString(Encoding.BigEndianUnicode.GetString(ReadUtf16(ref position, marker))),
                0xA0 => ReadArray(ref position, marker),
                0xD0 => ReadDictionary(ref position, marker),
                _ => throw new FormatException($"Unsupported plist marker 0x{marker:X2}."),
            };
            _cache[index] = node;
            _depth--;
            return node;
        }

        private PlistArray ReadArray(ref int position, byte marker)
        {
            var count = ReadCount(ref position, marker);
            var refs = ReadRefs(ref position, count);
            var array = new PlistArray();
            foreach (var reference in refs)
            {
                array.Add(ReadObject(reference));
            }

            return array;
        }

        private PlistDictionary ReadDictionary(ref int position, byte marker)
        {
            var count = ReadCount(ref position, marker);
            var keys = ReadRefs(ref position, count);
            var values = ReadRefs(ref position, count);
            var dictionary = new PlistDictionary();
            for (var i = 0; i < count; i++)
            {
                if (ReadObject(keys[i]) is not PlistString key)
                {
                    throw new FormatException("Binary plist dictionary key is not a string.");
                }

                dictionary.Add(key.Value, ReadObject(values[i]));
            }

            return dictionary;
        }

        private int[] ReadRefs(ref int position, int count)
        {
            var refs = new int[count];
            for (var i = 0; i < count; i++)
            {
                refs[i] = checked((int)ReadUnsigned(_data, position, _refSize));
                position += _refSize;
            }

            return refs;
        }

        private int ReadCount(ref int position, byte marker)
        {
            var low = marker & 0x0F;
            if (low != 0x0F)
            {
                return low;
            }

            var lengthMarker = _data[position++];
            return checked((int)ReadSizedInteger(ref position, lengthMarker));
        }

        private long ReadSizedInteger(ref int position, byte marker)
        {
            var size = 1 << (marker & 0x0F);
            if (size > 8)
            {
                throw new FormatException("Binary plist integer is wider than 8 bytes.");
            }

            Span<byte> wide = stackalloc byte[8];
            var raw = _data.Slice(position, size);
            position += size;
            if ((raw[0] & 0x80) != 0)
            {
                wide.Fill(0xFF);
            }

            raw.CopyTo(wide[(8 - size)..]);
            return BinaryPrimitives.ReadInt64BigEndian(wide);
        }

        private double ReadReal(ref int position, byte marker)
        {
            var size = 1 << (marker & 0x0F);
            if (size != 8)
            {
                throw new FormatException("Only 8-byte plist reals are supported.");
            }

            var value = BinaryPrimitives.ReadInt64BigEndian(_data.Slice(position, 8));
            position += 8;
            return BitConverter.Int64BitsToDouble(value);
        }

        private byte[] ReadRaw(ref int position, byte marker)
        {
            var count = ReadCount(ref position, marker);
            var bytes = _data.Slice(position, count).ToArray();
            position += count;
            return bytes;
        }

        private byte[] ReadUtf16(ref int position, byte marker)
        {
            var chars = ReadCount(ref position, marker);
            var bytes = chars * 2;
            var slice = _data.Slice(position, bytes).ToArray();
            position += bytes;
            return slice;
        }
    }

    private static byte[] EncodeInteger(long value)
    {
        if (value is >= 0 and <= 0x7F)
        {
            return [0x10, (byte)value];
        }

        if (value is >= 0 and <= 0x7FFF)
        {
            return PackInteger(0x11, value, 2);
        }

        if (value is >= 0 and <= 0x7FFFFFFF)
        {
            return PackInteger(0x12, value, 4);
        }

        return PackInteger(0x13, value, 8);
    }

    private static byte[] PackInteger(byte marker, long value, int size)
    {
        var output = new byte[1 + size];
        output[0] = marker;
        Span<byte> wide = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(wide, value);
        wide[^size..].CopyTo(output.AsSpan(1));
        return output;
    }

    private static byte[] EncodeReal(double value)
    {
        var output = new byte[9];
        output[0] = 0x23;
        BinaryPrimitives.WriteInt64BigEndian(output.AsSpan(1), BitConverter.DoubleToInt64Bits(value));
        return output;
    }

    private static byte[] EncodeString(string value)
    {
        var ascii = Encoding.UTF8.GetBytes(value);
        if (ascii.All(static item => item < 128))
        {
            return EncodeBytes(0x50, ascii);
        }

        var utf16 = Encoding.BigEndianUnicode.GetBytes(value);
        var output = new List<byte>(utf16.Length + 4);
        WriteCount(output, 0x60, value.Length);
        output.AddRange(utf16);
        return [.. output];
    }

    private static byte[] EncodeBytes(byte marker, byte[] value)
    {
        var output = new List<byte>(value.Length + 4);
        WriteCount(output, marker, value.Length);
        output.AddRange(value);
        return [.. output];
    }

    private static void WriteCount(List<byte> output, byte marker, int count)
    {
        if (count < 15)
        {
            output.Add((byte)(marker | count));
            return;
        }

        output.Add((byte)(marker | 0x0F));
        output.AddRange(EncodeInteger(count));
    }

    private static void WriteUnsigned(List<byte> output, int value, int size)
    {
        for (var shift = (size - 1) * 8; shift >= 0; shift -= 8)
        {
            output.Add((byte)(value >> shift));
        }
    }

    private static void WriteUnsigned(Span<byte> destination, int value, int size)
    {
        for (var i = 0; i < size; i++)
        {
            destination[i] = (byte)(value >> ((size - 1 - i) * 8));
        }
    }
}
