using System.Buffers.Binary;
using System.Text;

namespace AirPlayServer.Core.Discovery;

internal static class MdnsMessages
{
    public const ushort AuthoritativeResponse = 0x8400;
    public const uint TtlSeconds = 4500;

    public static byte[] BuildAnnouncement(ReceiverAdvertisement advertisement)
        => BuildResponse(advertisement, id: 0, includeAirPlay: true, includeRaop: true);

    public static bool TryBuildQueryResponse(
        ReadOnlySpan<byte> query,
        ReceiverAdvertisement advertisement,
        Span<byte> destination,
        out int written,
        out bool unicast)
    {
        written = 0;
        unicast = false;
        if (!TryReadQuestions(query, out var id, out var questions))
        {
            return false;
        }

        var airPlay = false;
        var raop = false;
        var host = false;
        foreach (var question in questions)
        {
            if (!Matches(question, advertisement, ref airPlay, ref raop, ref host))
            {
                continue;
            }

            if (question.UnicastResponse)
            {
                unicast = true;
            }
        }

        if (!airPlay && !raop && !host)
        {
            return false;
        }

        if (host)
        {
            airPlay = true;
            raop = true;
        }

        var packet = BuildResponse(advertisement, id, airPlay, raop);
        if (packet.Length > destination.Length)
        {
            return false;
        }

        packet.CopyTo(destination);
        written = packet.Length;
        return true;
    }

    public static byte[] BuildQuery(ushort id, string service)
    {
        var writer = new DnsWriter();
        writer.WriteHeader(id, flags: 0, questions: 1, answers: 0, authority: 0, additional: 0);
        writer.WriteQuestion(service);
        return writer.ToArray();
    }

    private static byte[] BuildResponse(ReceiverAdvertisement advertisement, ushort id, bool includeAirPlay, bool includeRaop)
    {
        var answerCount = 0;
        if (includeAirPlay)
        {
            answerCount += advertisement.Address is null ? 3 : 4;
        }

        if (includeRaop)
        {
            answerCount += advertisement.Address is null ? 3 : 4;
        }

        if (includeAirPlay && includeRaop && advertisement.Address is not null)
        {
            // The host A record is shared. Count it once.
            answerCount--;
        }

        var writer = new DnsWriter();
        writer.WriteHeader(id, AuthoritativeResponse, questions: 0, (ushort)answerCount, authority: 0, additional: 0);
        var wroteAddress = false;
        if (includeAirPlay)
        {
            WriteService(writer, advertisement, "_airplay", advertisement.InstanceName, advertisement.AirPlayPort, ServiceText.AirPlay(advertisement), ref wroteAddress);
        }

        if (includeRaop)
        {
            WriteService(writer, advertisement, "_raop", advertisement.RaopInstance, advertisement.RaopPort, ServiceText.Raop(advertisement), ref wroteAddress);
        }

        return writer.ToArray();
    }

    private static void WriteService(
        DnsWriter writer,
        ReceiverAdvertisement advertisement,
        string service,
        string instance,
        ushort port,
        IReadOnlyList<string> text,
        ref bool wroteAddress)
    {
        writer.WritePtr(service, instance, TtlSeconds);
        writer.WriteSrv(instance, service, port, advertisement.HostLabel, TtlSeconds);
        writer.WriteTxt(instance, service, text, TtlSeconds);
        if (!wroteAddress && advertisement.Address is not null)
        {
            writer.WriteAddress(advertisement.HostLabel, advertisement.Address, TtlSeconds);
            wroteAddress = true;
        }
    }

    private static bool Matches(DnsQuestion question, ReceiverAdvertisement advertisement, ref bool airPlay, ref bool raop, ref bool host)
    {
        var name = question.Name;
        var type = question.Type;
        if (type is not (DnsType.Ptr or DnsType.Srv or DnsType.Txt or DnsType.A or DnsType.Any))
        {
            return false;
        }

        if (EqualsName(name, "_airplay._tcp.local") && type is DnsType.Ptr or DnsType.Any)
        {
            airPlay = true;
            return true;
        }

        if (EqualsName(name, "_raop._tcp.local") && type is DnsType.Ptr or DnsType.Any)
        {
            raop = true;
            return true;
        }

        if (EqualsName(name, advertisement.AirPlayInstanceFqdn) && type is DnsType.Srv or DnsType.Txt or DnsType.Any)
        {
            airPlay = true;
            return true;
        }

        if (EqualsName(name, advertisement.RaopInstanceFqdn) && type is DnsType.Srv or DnsType.Txt or DnsType.Any)
        {
            raop = true;
            return true;
        }

        if (EqualsName(name, advertisement.HostFqdn) && type is DnsType.A or DnsType.Any)
        {
            host = true;
            return true;
        }

        return false;
    }

    private static bool EqualsName(string left, string right)
        => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static bool TryReadQuestions(ReadOnlySpan<byte> message, out ushort id, out List<DnsQuestion> questions)
    {
        id = 0;
        questions = [];
        if (message.Length < 12)
        {
            return false;
        }

        id = BinaryPrimitives.ReadUInt16BigEndian(message);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if ((flags & 0x8000) != 0)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var position = 12;
        for (var i = 0; i < count; i++)
        {
            if (!TryReadName(message, ref position, out var name))
            {
                return false;
            }

            if (position + 4 > message.Length)
            {
                return false;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(message[position..]);
            var klass = BinaryPrimitives.ReadUInt16BigEndian(message[(position + 2)..]);
            position += 4;
            questions.Add(new DnsQuestion(name, type, (ushort)(klass & 0x7FFF), (klass & 0x8000) != 0));
        }

        return true;
    }

    internal static int CountWalkableRecords(ReadOnlySpan<byte> message)
    {
        if (message.Length < 12)
        {
            throw new FormatException("DNS message is shorter than a header.");
        }

        var answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        var position = 12;
        for (var i = 0; i < answers; i++)
        {
            if (!TryReadName(message, ref position, out _))
            {
                throw new FormatException("DNS record name is truncated.");
            }

            if (position + 10 > message.Length)
            {
                throw new FormatException("DNS record header is truncated.");
            }

            position += 8;
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(message[position..]);
            position += 2;
            if (position + dataLength > message.Length)
            {
                throw new FormatException("DNS rdata is truncated.");
            }

            position += dataLength;
        }

        return answers;
    }

    private static bool TryReadName(ReadOnlySpan<byte> message, ref int position, out string name)
    {
        name = string.Empty;
        var labels = new List<string>(6);
        var cursor = position;
        var advanced = false;
        var hops = 0;
        while (cursor < message.Length)
        {
            var length = message[cursor];
            if (length == 0)
            {
                cursor++;
                if (!advanced)
                {
                    position = cursor;
                }

                name = string.Join('.', labels);
                return true;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= message.Length)
                {
                    return false;
                }

                if (!advanced)
                {
                    position = cursor + 2;
                    advanced = true;
                }

                cursor = ((length & 0x3F) << 8) | message[cursor + 1];
                if (++hops > 16)
                {
                    return false;
                }

                continue;
            }

            if ((length & 0xC0) != 0 || cursor + 1 + length > message.Length)
            {
                return false;
            }

            cursor++;
            labels.Add(Encoding.UTF8.GetString(message.Slice(cursor, length)));
            cursor += length;
            if (!advanced)
            {
                position = cursor;
            }
        }

        return false;
    }

    private readonly record struct DnsQuestion(string Name, ushort Type, ushort Class, bool UnicastResponse);
}
