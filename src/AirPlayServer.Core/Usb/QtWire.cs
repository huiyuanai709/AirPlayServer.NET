using System.Buffers.Binary;
using System.Text;

namespace AirPlayServer.Core.Usb;

/// <summary>
/// Apple QuickTime-over-USB (Valeria) packet codec. This is a pure C# reading
/// of the public length-prefixed PING / SYNC / ASYN framing. It does not link
/// the Qt widget toolkit, QuickTime.app, or QTKit.
/// Multi-byte integers on this path are little-endian. Four-character codes are
/// stored little-endian, so the bytes read as the code reversed.
/// </summary>
internal static class QtMagic
{
    public const uint Ping = 0x70696E67;
    public const uint Sync = 0x73796E63;
    public const uint Reply = 0x72706C79;
    public const uint Asyn = 0x6173796E;

    public const uint Cwpa = 0x63777061;
    public const uint Afmt = 0x61666D74;
    public const uint Cvrp = 0x63767270;
    public const uint Clok = 0x636C6F6B;
    public const uint Time = 0x74696D65;
    public const uint Skew = 0x736B6577;
    public const uint Og = 0x676F2120;
    public const uint Stop = 0x73746F70;

    public const uint Feed = 0x66656564;
    public const uint Eat = 0x65617421;
    public const uint Need = 0x6E656564;
    public const uint Hpd1 = 0x68706431;
    public const uint Hpa1 = 0x68706131;
    public const uint Hpd0 = 0x68706430;
    public const uint Hpa0 = 0x68706130;
    public const uint Sprp = 0x73707270;
    public const uint Srat = 0x73726174;
    public const uint Tbas = 0x74626173;
    public const uint Tjmp = 0x746A6D70;
    public const uint Rels = 0x72656C73;

    public const uint Dict = 0x64696374;
    public const uint KeyValue = 0x6B657976;
    public const uint StringKey = 0x7374726B;
    public const uint IndexKey = 0x6964786B;
    public const uint BoolValue = 0x62756C76;
    public const uint StringValue = 0x73747276;
    public const uint DataValue = 0x64617476;
    public const uint NumberValue = 0x6E6D6276;

    public const uint SampleBuffer = 0x73627566;
    public const uint SampleData = 0x73646174;
    public const uint FormatDescriptor = 0x66647363;
    public const uint MediaType = 0x6D646961;
    public const uint VideoDimension = 0x7664696D;
    public const uint Codec = 0x636F6463;
    public const uint Extension = 0x6578746E;
    public const uint Video = 0x76696465;
    public const uint Avc1 = 0x61766331;

    public const ulong EmptyClock = 1;

    public const int MaxPacketBytes = 8 * 1024 * 1024;
}

/// <summary>
/// Reassembles USB bulk chunks into length-prefixed packets. The 4-byte length
/// counts itself. The callback receives the packet without that length field.
/// </summary>
internal sealed class QtReassembler
{
    private byte[] _data = new byte[4096];
    private int _count;

    public void Push(ReadOnlySpan<byte> chunk, Action<ReadOnlySpan<byte>> onBody)
    {
        ArgumentNullException.ThrowIfNull(onBody);
        if (chunk.IsEmpty)
        {
            return;
        }

        Ensure(_count + chunk.Length);
        chunk.CopyTo(_data.AsSpan(_count));
        _count += chunk.Length;
        while (_count >= 4)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(_data);
            if (length < 16 || length > QtMagic.MaxPacketBytes)
            {
                Buffer.BlockCopy(_data, 1, _data, 0, _count - 1);
                _count--;
                continue;
            }

            if (_count < length)
            {
                return;
            }

            onBody(_data.AsSpan(4, length - 4));
            var rest = _count - length;
            if (rest > 0)
            {
                Buffer.BlockCopy(_data, length, _data, 0, rest);
            }

            _count = rest;
        }
    }

    private void Ensure(int needed)
    {
        if (_data.Length >= needed)
        {
            return;
        }

        var grown = _data.Length;
        while (grown < needed)
        {
            grown = Math.Min(grown * 2, QtMagic.MaxPacketBytes + 4);
        }

        var next = new byte[grown];
        _data.AsSpan(0, _count).CopyTo(next);
        _data = next;
    }
}

internal static class QtPackets
{
    private static readonly byte[] AudioFormat =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x70, 0xE7, 0x40,
        0x6D, 0x63, 0x70, 0x6C, 0x0C, 0x00, 0x00, 0x00,
        0x04, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x04, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
        0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x70, 0xE7, 0x40,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x70, 0xE7, 0x40,
    ];

    public static byte[] Ping()
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), QtMagic.Ping);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), 0x0000000100000000);
        return packet;
    }

    public static byte[] ClockReply(ulong correlationId, ulong clock)
    {
        var packet = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 28);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), QtMagic.Reply);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), correlationId);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(20), clock);
        return packet;
    }

    public static byte[] TimeReply(ulong correlationId, ulong nanoseconds)
    {
        var packet = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 44);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), QtMagic.Reply);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), correlationId);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(20), nanoseconds);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(28), 1_000_000_000);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(32), 1);
        return packet;
    }

    public static byte[] SkewReply(ulong correlationId, double skew)
    {
        var packet = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 28);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), QtMagic.Reply);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), correlationId);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(20), BitConverter.DoubleToUInt64Bits(skew));
        return packet;
    }

    public static byte[] ZeroReply(ulong correlationId, int length)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), QtMagic.Reply);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), correlationId);
        return packet;
    }

    public static byte[] Need(ulong deviceClock)
        => AsynHeader(20, deviceClock, QtMagic.Need);

    public static byte[] Hpd0()
        => AsynHeader(20, QtMagic.EmptyClock, QtMagic.Hpd0);

    public static byte[] Hpa0(ulong deviceAudioClock)
        => AsynHeader(20, deviceAudioClock, QtMagic.Hpa0);

    public static byte[] Hpd1(double width, double height)
    {
        var dict = new QtDictWriter();
        dict.Bool("Valeria", true);
        dict.Bool("HEVCDecoderSupports444", true);
        var display = new QtDictWriter();
        display.Float("Width", width);
        display.Float("Height", height);
        dict.Dict("DisplaySize", display);
        return AsynDict(QtMagic.EmptyClock, QtMagic.Hpd1, dict.ToArray());
    }

    public static byte[] Hpa1(ulong deviceAudioClock)
    {
        var dict = new QtDictWriter();
        dict.Float("BufferAheadInterval", BitConverter.UInt64BitsToDouble(0x3FB2B020C49BA5E4));
        dict.String("deviceUID", "Valeria");
        dict.Float("ScreenLatency", 0.04d);
        dict.Data("formats", AudioFormat);
        dict.UInt32("EDIDAC3Support", 0);
        dict.String("deviceName", "Valeria");
        return AsynDict(deviceAudioClock, QtMagic.Hpa1, dict.ToArray());
    }

    public static byte[] ErrorReply(ulong correlationId)
    {
        var dict = new QtDictWriter();
        dict.UInt32("Error", 0);
        var body = dict.ToArray();
        var packet = new byte[20 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), QtMagic.Reply);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), correlationId);
        body.CopyTo(packet.AsSpan(20));
        return packet;
    }

    public static bool TrySync(ReadOnlySpan<byte> body, uint subtype, out ulong clock, out ulong correlation, out ReadOnlySpan<byte> rest)
    {
        clock = 0;
        correlation = 0;
        rest = default;
        if (body.Length < 24 || ReadU32(body) != QtMagic.Sync || ReadU32(body[12..]) != subtype)
        {
            return false;
        }

        clock = BinaryPrimitives.ReadUInt64LittleEndian(body[4..]);
        correlation = BinaryPrimitives.ReadUInt64LittleEndian(body[16..]);
        rest = body[24..];
        return true;
    }

    public static bool TryAsyn(ReadOnlySpan<byte> body, out ulong clock, out uint subtype, out ReadOnlySpan<byte> rest)
    {
        clock = 0;
        subtype = 0;
        rest = default;
        if (body.Length < 16 || ReadU32(body) != QtMagic.Asyn)
        {
            return false;
        }

        clock = BinaryPrimitives.ReadUInt64LittleEndian(body[4..]);
        subtype = ReadU32(body[12..]);
        rest = body[16..];
        return true;
    }

    private static byte[] AsynHeader(int length, ulong clock, uint subtype)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), QtMagic.Asyn);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), clock);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), subtype);
        return packet;
    }

    private static byte[] AsynDict(ulong clock, uint subtype, byte[] dict)
    {
        var packet = new byte[20 + dict.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), QtMagic.Asyn);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), clock);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), subtype);
        dict.CopyTo(packet.AsSpan(20));
        return packet;
    }

    private static uint ReadU32(ReadOnlySpan<byte> data)
        => BinaryPrimitives.ReadUInt32LittleEndian(data);
}

/// <summary>
/// String-key dictionary writer for the handful of host-originated messages.
/// Boolean values are one byte. NSNumber type is one byte followed by the
/// integer or IEEE-754 payload. Length fields include themselves.
/// </summary>
internal sealed class QtDictWriter
{
    private readonly List<byte[]> _entries = [];

    public void Bool(string key, bool value)
    {
        var payload = new byte[9];
        WriteAtom(payload, QtMagic.BoolValue);
        payload[8] = value ? (byte)1 : (byte)0;
        _entries.Add(Entry(key, payload));
    }

    public void String(string key, string value)
    {
        var text = Encoding.UTF8.GetBytes(value);
        var payload = new byte[8 + text.Length];
        WriteAtom(payload, QtMagic.StringValue);
        text.CopyTo(payload.AsSpan(8));
        _entries.Add(Entry(key, payload));
    }

    public void Data(string key, ReadOnlySpan<byte> value)
    {
        var payload = new byte[8 + value.Length];
        WriteAtom(payload, QtMagic.DataValue);
        value.CopyTo(payload.AsSpan(8));
        _entries.Add(Entry(key, payload));
    }

    public void UInt32(string key, uint value)
    {
        var payload = new byte[13];
        WriteAtom(payload, QtMagic.NumberValue);
        payload[8] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(9), value);
        _entries.Add(Entry(key, payload));
    }

    public void Float(string key, double value)
    {
        var payload = new byte[17];
        WriteAtom(payload, QtMagic.NumberValue);
        payload[8] = 6;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(9), BitConverter.DoubleToUInt64Bits(value));
        _entries.Add(Entry(key, payload));
    }

    public void Dict(string key, QtDictWriter nested)
        => _entries.Add(Entry(key, nested.ToArray()));

    public byte[] ToArray()
    {
        var size = 8;
        foreach (var entry in _entries)
        {
            size += entry.Length;
        }

        var dict = new byte[size];
        WriteAtom(dict, QtMagic.Dict);
        var offset = 8;
        foreach (var entry in _entries)
        {
            entry.CopyTo(dict.AsSpan(offset));
            offset += entry.Length;
        }

        return dict;
    }

    private static byte[] Entry(string key, byte[] value)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var keyAtom = new byte[8 + keyBytes.Length];
        WriteAtom(keyAtom, QtMagic.StringKey);
        keyBytes.CopyTo(keyAtom.AsSpan(8));
        var entry = new byte[8 + keyAtom.Length + value.Length];
        WriteAtom(entry, QtMagic.KeyValue);
        keyAtom.CopyTo(entry.AsSpan(8));
        value.CopyTo(entry.AsSpan(8 + keyAtom.Length));
        return entry;
    }

    private static void WriteAtom(Span<byte> destination, uint magic)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)destination.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], magic);
    }
}
