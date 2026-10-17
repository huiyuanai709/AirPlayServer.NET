using System.Buffers.Binary;
using System.Text;

namespace AirPlayServer.Core.Discovery;

internal sealed class DnsWriter
{
    private byte[] _buffer = new byte[512];
    private int _length;
    private readonly Dictionary<string, ushort> _names = new(StringComparer.Ordinal);

    public void WriteHeader(ushort id, ushort flags, ushort questions, ushort answers, ushort authority, ushort additional)
    {
        WriteU16(id);
        WriteU16(flags);
        WriteU16(questions);
        WriteU16(answers);
        WriteU16(authority);
        WriteU16(additional);
    }

    public void WriteServiceType(string service)
        => WriteLabels(service, "_tcp", "local");

    public void WriteServiceInstance(string instance, string service)
        => WriteLabels(instance, service, "_tcp", "local");

    public void WriteHost(string hostLabel)
        => WriteLabels(hostLabel, "local");

    public void WriteQuestion(string service)
    {
        WriteServiceType(service);
        WriteU16(DnsType.Ptr);
        WriteU16(DnsClass.In);
    }

    public void WritePtr(string ownerService, string instance, uint ttl)
    {
        var start = BeginRecord(ownerService, DnsType.Ptr, cacheFlush: false, ttl);
        WriteServiceInstance(instance, ownerService);
        FinishRecord(start);
    }

    public void WriteSrv(string instance, string service, ushort port, string hostLabel, uint ttl)
    {
        var start = BeginInstanceRecord(instance, service, DnsType.Srv, ttl);
        WriteU16(0);
        WriteU16(0);
        WriteU16(port);
        WriteHost(hostLabel);
        FinishRecord(start);
    }

    public void WriteTxt(string instance, string service, IReadOnlyList<string> entries, uint ttl)
    {
        var start = BeginInstanceRecord(instance, service, DnsType.Txt, ttl);
        foreach (var entry in entries)
        {
            var raw = Encoding.UTF8.GetBytes(entry);
            if (raw.Length is 0 or > 255)
            {
                throw new ArgumentException($"TXT entry '{entry}' must be 1..255 bytes.", nameof(entries));
            }

            WriteByte((byte)raw.Length);
            WriteBytes(raw);
        }

        FinishRecord(start);
    }

    public void WriteAddress(string hostLabel, System.Net.IPAddress address, uint ttl)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            throw new ArgumentException("This slice advertises IPv4 addresses only.", nameof(address));
        }

        var start = BeginHostRecord(hostLabel, DnsType.A, ttl);
        WriteBytes(bytes);
        FinishRecord(start);
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    private int BeginInstanceRecord(string instance, string service, ushort type, uint ttl)
    {
        WriteServiceInstance(instance, service);
        return BeginAfterName(type, ttl);
    }

    private int BeginHostRecord(string hostLabel, ushort type, uint ttl)
    {
        WriteHost(hostLabel);
        return BeginAfterName(type, ttl);
    }

    private int BeginRecord(string service, ushort type, bool cacheFlush, uint ttl)
    {
        WriteServiceType(service);
        WriteU16(type);
        WriteU16((ushort)(cacheFlush ? DnsClass.FlushIn : DnsClass.In));
        WriteU32(ttl);
        var lengthPosition = _length;
        WriteU16(0);
        return lengthPosition;
    }

    private int BeginAfterName(ushort type, uint ttl)
    {
        WriteU16(type);
        WriteU16(DnsClass.FlushIn);
        WriteU32(ttl);
        var lengthPosition = _length;
        WriteU16(0);
        return lengthPosition;
    }

    private void FinishRecord(int lengthPosition)
    {
        var dataLength = _length - (lengthPosition + 2);
        if (dataLength is < 0 or > ushort.MaxValue)
        {
            throw new InvalidOperationException("DNS rdata does not fit in one record.");
        }

        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(lengthPosition), (ushort)dataLength);
    }

    private void WriteLabels(params string[] labels)
    {
        for (var i = 0; i < labels.Length; i++)
        {
            var key = string.Join('\u0001', labels.Skip(i));
            if (_names.TryGetValue(key, out var offset))
            {
                WriteU16((ushort)(0xC000 | offset));
                return;
            }

            if (_length > 0x3FFF)
            {
                throw new InvalidOperationException("DNS compression offset does not fit in 14 bits.");
            }

            _names[key] = (ushort)_length;
            var raw = Encoding.UTF8.GetBytes(labels[i]);
            if (raw.Length is 0 or > 63)
            {
                throw new ArgumentException($"DNS label '{labels[i]}' must be 1..63 bytes.");
            }

            WriteByte((byte)raw.Length);
            WriteBytes(raw);
        }

        WriteByte(0);
    }

    private void WriteU16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(_length), value);
        _length += 2;
    }

    private void WriteU32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    private void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    private void WriteBytes(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    private void Ensure(int count)
    {
        if (_length + count <= _buffer.Length)
        {
            return;
        }

        var grown = new byte[Math.Max(_buffer.Length * 2, _length + count)];
        _buffer.AsSpan(0, _length).CopyTo(grown);
        _buffer = grown;
    }
}

internal static class DnsType
{
    public const ushort A = 1;
    public const ushort Ptr = 12;
    public const ushort Txt = 16;
    public const ushort Srv = 33;
    public const ushort Any = 255;
}

internal static class DnsClass
{
    public const ushort In = 1;
    public const ushort FlushIn = 0x8001;
}
