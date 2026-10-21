using System.Buffers.Binary;

namespace AirPlayServer.Core.Usb;

/// <summary>
/// One H.264 access unit taken from an ASYN FEED CMSampleBuffer.
/// <see cref="AnnexB"/> uses 4-byte start codes. Parameter sets come from the
/// avcC record inside the format description (CVRP or the sample itself).
/// </summary>
internal readonly struct QtVideoUnit
{
    public QtVideoUnit(byte[] annexB, bool keyFrame, byte[]? sequence, byte[]? picture)
    {
        AnnexB = annexB;
        KeyFrame = keyFrame;
        Sequence = sequence;
        Picture = picture;
    }

    public byte[] AnnexB { get; }

    public bool KeyFrame { get; }

    public byte[]? Sequence { get; }

    public byte[]? Picture { get; }
}

/// <summary>
/// Walks CMSampleBuffer and CMFormatDescription atoms far enough to recover
/// AVCC sample bytes and the SPS/PPS stored in the extension dictionary.
/// </summary>
internal static class QtSampleBuffer
{
    public static bool TryReadVideo(ReadOnlySpan<byte> sampleAtom, out QtVideoUnit unit)
    {
        unit = default;
        if (!TryAtom(sampleAtom, QtMagic.SampleBuffer, out var body))
        {
            return false;
        }

        byte[]? sample = null;
        byte[]? sequence = null;
        byte[]? picture = null;
        while (body.Length >= 8)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(body);
            if (length < 8 || length > body.Length)
            {
                return false;
            }

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
            var atom = body[..length];
            if (magic == QtMagic.SampleData)
            {
                sample = atom[8..].ToArray();
            }
            else if (magic == QtMagic.FormatDescriptor && TryReadParameterSets(atom, out var sps, out var pps))
            {
                sequence = sps;
                picture = pps;
            }

            body = body[length..];
        }

        return TryFinish(sample, sequence, picture, out unit);
    }

    public static bool TryReadParameterSets(ReadOnlySpan<byte> descriptorAtom, out byte[] sequence, out byte[] picture)
    {
        sequence = [];
        picture = [];
        if (!TryAtom(descriptorAtom, QtMagic.FormatDescriptor, out var body))
        {
            return false;
        }

        ReadOnlySpan<byte> extensions = default;
        var sawVideo = false;
        while (body.Length >= 8)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(body);
            if (length < 8 || length > body.Length)
            {
                break;
            }

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
            if (magic == QtMagic.MediaType && length >= 12)
            {
                sawVideo = BinaryPrimitives.ReadUInt32LittleEndian(body[8..]) == QtMagic.Video;
            }
            else if (magic == QtMagic.Extension)
            {
                extensions = body[..length];
            }

            body = body[length..];
        }

        if (!sawVideo || extensions.IsEmpty || !TryAvcC(extensions, out var avcC))
        {
            return false;
        }

        return TryParseAvcC(avcC, out sequence, out picture);
    }

    /// <summary>
    /// Searches a SYNC payload for a nested format description. CVRP carries
    /// the first SPS/PPS before any FEED sample.
    /// </summary>
    public static bool TryFindParameterSets(ReadOnlySpan<byte> data, out byte[] sequence, out byte[] picture)
    {
        sequence = [];
        picture = [];
        for (var i = 0; i + 8 <= data.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]) != QtMagic.FormatDescriptor)
            {
                continue;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(data[i..]);
            if (length < 8 || i + length > data.Length)
            {
                continue;
            }

            if (TryReadParameterSets(data.Slice(i, length), out sequence, out picture))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFinish(byte[]? sample, byte[]? sequence, byte[]? picture, out QtVideoUnit unit)
    {
        unit = default;
        if (sample is null || sample.Length < 5 || !TryAnnexB(sample, out var annexB, out var keyFrame))
        {
            return false;
        }

        if (sequence is not null && picture is not null && (sequence.Length == 0 || picture.Length == 0))
        {
            sequence = null;
            picture = null;
        }

        if (!keyFrame && ContainsNal(annexB, 5))
        {
            keyFrame = true;
        }

        unit = new QtVideoUnit(annexB, keyFrame, sequence, picture);
        return true;
    }

    public static bool TryAnnexB(ReadOnlySpan<byte> avcc, out byte[] annexB, out bool keyFrame)
    {
        annexB = [];
        keyFrame = false;
        var offset = 0;
        var units = 0;
        var size = 0;
        while (offset + 4 <= avcc.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(avcc[offset..]);
            if (length <= 0 || offset + 4 + length > avcc.Length)
            {
                return false;
            }

            var nal = avcc[offset + 4];
            if ((nal & 0x80) != 0)
            {
                return false;
            }

            if ((nal & 0x1F) is 5 or 7)
            {
                keyFrame = true;
            }

            size += 4 + length;
            offset += 4 + length;
            units++;
        }

        if (units == 0 || offset != avcc.Length)
        {
            return false;
        }

        annexB = new byte[size];
        offset = 0;
        var written = 0;
        while (offset + 4 <= avcc.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(avcc[offset..]);
            annexB[written] = 0;
            annexB[written + 1] = 0;
            annexB[written + 2] = 0;
            annexB[written + 3] = 1;
            avcc.Slice(offset + 4, length).CopyTo(annexB.AsSpan(written + 4));
            written += 4 + length;
            offset += 4 + length;
        }

        return true;
    }

    private static bool ContainsNal(ReadOnlySpan<byte> annexB, int nalType)
    {
        for (var i = 0; i + 4 < annexB.Length; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1
                && (annexB[i + 4] & 0x1F) == nalType)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAvcC(ReadOnlySpan<byte> extensionAtom, out ReadOnlySpan<byte> avcC)
    {
        avcC = default;
        if (!TryAtom(extensionAtom, QtMagic.Extension, out var body))
        {
            return false;
        }

        while (body.Length >= 8)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(body);
            if (length < 8 || length > body.Length || BinaryPrimitives.ReadUInt32LittleEndian(body[4..]) != QtMagic.KeyValue)
            {
                return false;
            }

            if (TryIndex(body[..length], 49, out var value)
                && TryIndex(value, 105, out var inner)
                && TryAtom(inner, QtMagic.DataValue, out var bytes))
            {
                avcC = bytes;
                return true;
            }

            body = body[length..];
        }

        return false;
    }

    private static bool TryIndex(ReadOnlySpan<byte> keyValueOrDict, int key, out ReadOnlySpan<byte> valueAtom)
    {
        valueAtom = default;
        if (keyValueOrDict.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(keyValueOrDict[4..]) == QtMagic.KeyValue)
        {
            if (!TryAtom(keyValueOrDict, QtMagic.KeyValue, out var pair))
            {
                return false;
            }

            if (!TryAtom(pair, QtMagic.IndexKey, out var keyBody) || keyBody.Length < 2)
            {
                return false;
            }

            if (BinaryPrimitives.ReadUInt16LittleEndian(keyBody) != key)
            {
                return false;
            }

            var keyLength = BinaryPrimitives.ReadInt32LittleEndian(pair);
            if (keyLength < 8 || keyLength > pair.Length)
            {
                return false;
            }

            valueAtom = pair[keyLength..];
            return valueAtom.Length >= 8;
        }

        if (!TryAtom(keyValueOrDict, QtMagic.Dict, out var entries) && keyValueOrDict.Length >= 8)
        {
            entries = keyValueOrDict;
        }

        while (entries.Length >= 8)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(entries);
            if (length < 8 || length > entries.Length)
            {
                return false;
            }

            if (TryIndex(entries[..length], key, out valueAtom))
            {
                return true;
            }

            entries = entries[length..];
        }

        return false;
    }

    public static bool TryParseAvcC(ReadOnlySpan<byte> data, out byte[] sequence, out byte[] picture)
    {
        sequence = [];
        picture = [];
        if (data.Length < 8 || data[0] != 1)
        {
            return false;
        }

        var offset = 6;
        var sequenceCount = data[5] & 0x1F;
        byte[]? sps = null;
        for (var i = 0; i < sequenceCount; i++)
        {
            if (!TryTakeNal(data, ref offset, out var nal))
            {
                return false;
            }

            if ((nal[0] & 0x1F) == 7)
            {
                sps = nal;
            }
        }

        if (offset >= data.Length)
        {
            return false;
        }

        var pictureCount = data[offset++];
        byte[]? pps = null;
        for (var i = 0; i < pictureCount; i++)
        {
            if (!TryTakeNal(data, ref offset, out var nal))
            {
                return false;
            }

            if ((nal[0] & 0x1F) == 8)
            {
                pps = nal;
            }
        }

        if (sps is null || pps is null)
        {
            return false;
        }

        sequence = sps;
        picture = pps;
        return true;
    }

    private static bool TryTakeNal(ReadOnlySpan<byte> data, ref int offset, out byte[] nal)
    {
        nal = [];
        if (offset + 2 > data.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        if (length == 0 || offset + length > data.Length)
        {
            return false;
        }

        nal = data.Slice(offset, length).ToArray();
        offset += length;
        return true;
    }

    private static bool TryAtom(ReadOnlySpan<byte> data, uint magic, out ReadOnlySpan<byte> body)
    {
        body = default;
        if (data.Length < 8)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (length < 8 || length > data.Length || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != magic)
        {
            return false;
        }

        body = data.Slice(8, length - 8);
        return true;
    }
}
