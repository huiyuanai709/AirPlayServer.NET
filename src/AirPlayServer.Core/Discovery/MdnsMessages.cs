using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace AirPlayServer.Core.Discovery;

internal static class MdnsMessages
{
    public const ushort AuthoritativeResponse = 0x8400;
    public const uint TtlSeconds = MdnsCadence.TtlSeconds;

    public static byte[] BuildAnnouncement(ReceiverAdvertisement advertisement, IPAddress? address = null)
        => BuildResponse(advertisement, id: 0, includeAirPlay: true, includeRaop: true, AddressList(address));

    public static byte[] BuildAnnouncement(ReceiverAdvertisement advertisement, IReadOnlyList<IPAddress> addresses)
        => BuildResponse(advertisement, id: 0, includeAirPlay: true, includeRaop: true, addresses);

    public static bool TryBuildQueryResponse(
        ReadOnlySpan<byte> query,
        ReceiverAdvertisement advertisement,
        Span<byte> destination,
        out int written,
        out bool unicast,
        IPAddress? address = null)
        => TryBuildQueryResponse(query, advertisement, destination, out written, out unicast, AddressList(address));

    public static bool TryBuildQueryResponse(
        ReadOnlySpan<byte> query,
        ReceiverAdvertisement advertisement,
        Span<byte> destination,
        out int written,
        out bool unicast,
        IReadOnlyList<IPAddress>? addresses)
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

        var packet = BuildResponse(advertisement, id, airPlay, raop, addresses);
        if (packet.Length > destination.Length)
        {
            return false;
        }

        packet.CopyTo(destination);
        written = packet.Length;
        return true;
    }

    private static IReadOnlyList<IPAddress>? AddressList(IPAddress? address)
        => address is null ? null : [address];

    /// <summary>
    /// Multicast mDNS responses use query id 0. Receivers must ignore the id.
    /// Unicast responses keep the id copied from the query.
    /// </summary>
    public static void StampMulticastId(Span<byte> message)
    {
        if (message.Length >= 2)
        {
            message[0] = 0;
            message[1] = 0;
        }
    }

    public static byte[] BuildQuery(ushort id, string service)
    {
        var writer = new DnsWriter();
        writer.WriteHeader(id, flags: 0, questions: 1, answers: 0, authority: 0, additional: 0);
        writer.WriteQuestion(service);
        return writer.ToArray();
    }

    private static byte[] BuildResponse(
        ReceiverAdvertisement advertisement,
        ushort id,
        bool includeAirPlay,
        bool includeRaop,
        IReadOnlyList<IPAddress>? addresses)
    {
        var published = NormalizeAddresses(addresses, advertisement);
        var services = (includeAirPlay ? 1 : 0) + (includeRaop ? 1 : 0);
        // PTR + SRV + TXT per service. Every A record is shared by the host name, so it is counted once.
        var answerCount = (services * 3) + (services > 0 ? published.Count : 0);

        var writer = new DnsWriter();
        writer.WriteHeader(id, AuthoritativeResponse, questions: 0, (ushort)answerCount, authority: 0, additional: 0);
        var wroteAddress = false;
        if (includeAirPlay)
        {
            WriteService(writer, advertisement, published, "_airplay", advertisement.InstanceName, advertisement.AirPlayPort, ServiceText.AirPlay(advertisement), ref wroteAddress);
        }

        if (includeRaop)
        {
            WriteService(writer, advertisement, published, "_raop", advertisement.RaopInstance, advertisement.RaopPort, ServiceText.Raop(advertisement), ref wroteAddress);
        }

        return writer.ToArray();
    }

    private static IReadOnlyList<IPAddress> NormalizeAddresses(IReadOnlyList<IPAddress>? addresses, ReceiverAdvertisement advertisement)
    {
        if (addresses is { Count: > 0 })
        {
            return addresses;
        }

        return advertisement.Address is null ? [] : [advertisement.Address];
    }

    private static void WriteService(
        DnsWriter writer,
        ReceiverAdvertisement advertisement,
        IReadOnlyList<IPAddress> addresses,
        string service,
        string instance,
        ushort port,
        IReadOnlyList<string> text,
        ref bool wroteAddress)
    {
        writer.WritePtr(service, instance, TtlSeconds);
        writer.WriteSrv(instance, service, port, advertisement.HostLabel, TtlSeconds);
        writer.WriteTxt(instance, service, text, TtlSeconds);
        if (wroteAddress || addresses.Count == 0)
        {
            return;
        }

        // One cache-flush set. Separate packets each flushing a different A record
        // are what replaced the LAN address with the hotspot and stalled the picture.
        foreach (var address in addresses)
        {
            writer.WriteAddress(advertisement.HostLabel, address, TtlSeconds);
        }

        wroteAddress = true;
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

    internal static uint[] AnswerTtls(ReadOnlySpan<byte> message)
    {
        if (message.Length < 12)
        {
            throw new FormatException("DNS message is shorter than a header.");
        }

        var answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        var ttls = new uint[answers];
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

            ttls[i] = BinaryPrimitives.ReadUInt32BigEndian(message[(position + 4)..]);
            position += 8;
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(message[position..]);
            position += 2;
            if (position + dataLength > message.Length)
            {
                throw new FormatException("DNS rdata is truncated.");
            }

            position += dataLength;
        }

        return ttls;
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
