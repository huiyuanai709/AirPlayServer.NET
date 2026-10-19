using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AirPlayServer.Core;
using AirPlayServer.Core.Crypto;
using AirPlayServer.Core.Discovery;
using AirPlayServer.Core.Media;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class HevcMirrorTests
{
    [Fact]
    public void Feature_bit_42_is_off_unless_hevc_is_accepted()
    {
        Assert.Equal((long)AirPlayCompatibility.FeaturesLow, AirPlayCompatibility.FeaturesInteger(false));
        Assert.Equal("0x5A7FFEE6,0x0", AirPlayCompatibility.FeaturesText);
        Assert.Equal("0x5A7FFEE6,0x0", AirPlayCompatibility.FormatFeatures(false));

        var enabled = AirPlayCompatibility.FeaturesInteger(true);
        Assert.Equal(AirPlayCompatibility.FeaturesLow, (uint)enabled);
        Assert.Equal(0x400L, enabled >> 32);
        Assert.Equal(4399564848870L, enabled);
        Assert.Equal("0x5A7FFEE6,0x400", AirPlayCompatibility.FormatFeatures(true));
        Assert.True((enabled & (1L << AirPlayCompatibility.SupportsScreenMultiCodecBit)) != 0);
    }

    [Fact]
    public void Info_plist_uses_the_full_feature_integer_only_when_hevc_is_on()
    {
        var off = Advertisement(false);
        var on = Advertisement(true);
        var offXml = ServerInfo.Xml(off);
        var onXml = ServerInfo.Xml(on);
        Assert.Contains("<integer>" + AirPlayCompatibility.FeaturesLow.ToString(CultureInfo.InvariantCulture) + "</integer>", offXml, StringComparison.Ordinal);
        Assert.Contains("<integer>4399564848870</integer>", onXml, StringComparison.Ordinal);

        var offRoot = Assert.IsType<PlistDictionary>(BinaryPropertyList.Read(ServerInfo.Binary(off, new byte[32])));
        var onRoot = Assert.IsType<PlistDictionary>(BinaryPropertyList.Read(ServerInfo.Binary(on, new byte[32])));
        Assert.Equal((long)AirPlayCompatibility.FeaturesLow, Assert.IsType<PlistInteger>(offRoot.Require("features")).Value);
        Assert.Equal(4399564848870L, Assert.IsType<PlistInteger>(onRoot.Require("features")).Value);
        Assert.Contains("SupportsScreenMultiCodec", ServerInfo.PlainStatus(on, true, false), StringComparison.Ordinal);
    }

    [Fact]
    public void Hevc_pause_and_resume_use_the_second_option_bytes()
    {
        var paused = Header(1, 0x5E);
        var resumed = Header(1, 0x1E);
        var h264Paused = Header(1, 0x56);
        var h264Resumed = Header(1, 0x16);
        Assert.True(paused.SenderPaused);
        Assert.False(paused.SenderResumed);
        Assert.True(resumed.SenderResumed);
        Assert.False(resumed.SenderPaused);
        Assert.True(h264Paused.SenderPaused);
        Assert.True(h264Resumed.SenderResumed);
    }

    [Fact]
    public void H264_codec_payload_is_not_treated_as_hevc()
    {
        byte[] sps = [0x67, 0x42, 0x00, 0x0A];
        byte[] pps = [0x68, 0xCE];
        var payload = new byte[8 + sps.Length + 3 + pps.Length];
        payload[6] = (byte)(sps.Length >> 8);
        payload[7] = (byte)sps.Length;
        sps.CopyTo(payload, 8);
        payload[8 + sps.Length] = 1;
        payload[9 + sps.Length] = (byte)(pps.Length >> 8);
        payload[10 + sps.Length] = (byte)pps.Length;
        pps.CopyTo(payload, 11 + sps.Length);

        Assert.False(CodecConfig.LooksLikeHevc(payload));
        Assert.True(CodecConfig.TryParse(payload, out var sequence, out var picture));
        Assert.Equal(sps, sequence);
        Assert.Equal(pps, picture);
        Assert.False(CodecConfig.TryParseHevc(payload, out _, out _, out _));
    }

    [Fact]
    public void Hevc_parameter_sets_parse_from_hvcc_and_from_the_fixed_offset()
    {
        var vps = new byte[300];
        vps[0] = 0x40;
        vps[1] = 0x01;
        vps[299] = 0x7E;
        byte[] sps = [0x42, 0x01, 0x02];
        byte[] pps = [0x44, 0x01, 0x03, 0x04];
        var arrays = HvccArrays(vps, sps, pps);
        var fallback = new byte[0x75 + arrays.Length];
        WriteHvc1(fallback);
        arrays.CopyTo(fallback, 0x75);

        Assert.True(CodecConfig.TryParseHevc(fallback, out var parsedVps, out var parsedSps, out var parsedPps));
        Assert.Equal(vps, parsedVps);
        Assert.Equal(sps, parsedSps);
        Assert.Equal(pps, parsedPps);

        byte[] boxedVps = [0x40, 0x01, 0xAA];
        byte[] boxedSps = [0x42, 0x01, 0xBB];
        byte[] boxedPps = [0x44, 0x01, 0xCC];
        var boxedArrays = HvccArrays(boxedVps, boxedSps, boxedPps);
        var boxed = new byte[Math.Max(47 + boxedArrays.Length, 0x75 + arrays.Length)];
        WriteHvc1(boxed);
        boxed[20] = (byte)'h';
        boxed[21] = (byte)'v';
        boxed[22] = (byte)'c';
        boxed[23] = (byte)'C';
        boxed[24] = 1;
        boxed[46] = 3;
        boxedArrays.CopyTo(boxed, 47);
        arrays.CopyTo(boxed, 0x75);

        Assert.True(CodecConfig.TryParseHevc(boxed, out parsedVps, out parsedSps, out parsedPps));
        Assert.Equal(boxedVps, parsedVps);
        Assert.Equal(boxedSps, parsedSps);
        Assert.Equal(boxedPps, parsedPps);
    }

    [Fact]
    public void Hevc_sps_reports_visible_size_and_the_configuration_record_has_no_start_code()
    {
        var profile = new byte[] { 0x01, 0x60, 0x00, 0x00, 0x00, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x5A };
        var plain = BuildSps(profile, 320, 180, cropRight: 0);
        Assert.True(HevcSequence.TryGetPictureInfo(plain, out var info));
        Assert.Equal(320, info.VisibleWidth);
        Assert.Equal(180, info.VisibleHeight);
        Assert.Equal(320, info.CodedWidth);
        Assert.Equal(180, info.CodedHeight);
        Assert.Equal(1, info.ChromaFormat);
        Assert.Equal(0, info.BitDepthLumaMinus8);
        Assert.Equal(0, info.BitDepthChromaMinus8);

        var cropped = BuildSps(profile, 320, 180, cropRight: 4);
        Assert.True(HevcSequence.TryGetPictureInfo(cropped, out var croppedInfo));
        Assert.Equal(312, croppedInfo.VisibleWidth);
        Assert.Equal(180, croppedInfo.VisibleHeight);
        Assert.Equal(320, croppedInfo.CodedWidth);
        Assert.False(HevcSequence.TryGetPictureInfo([0x42, 0x01], out _));

        byte[] vps = [0x40, 0x01, 0x11];
        byte[] pps = [0x44, 0x01, 0x22];
        var record = new byte[128];
        var written = HevcSequence.WriteDecoderConfigurationRecord(vps, plain, pps, record);
        Assert.True(written > 23);
        record = record[..written];
        Assert.Equal(1, record[0]);
        Assert.Equal(profile, record[1..13]);
        Assert.Equal(0xFD, record[16]);
        Assert.Equal(0xF8, record[17]);
        Assert.Equal(0xF8, record[18]);
        Assert.Equal(0x0F, record[21]);
        Assert.Equal(3, record[22]);
        Assert.True(record.AsSpan().IndexOf(vps) >= 0);
        Assert.True(record.AsSpan().IndexOf(new byte[] { 0, 0, 0, 1 }) < 0);
        Assert.Equal(0xA0, record[23]);

        var annex = new byte[] { 0, 0, 0, 1, 0x40, 0x01, 0x11 };
        Assert.True(HevcSequence.ContainsNalType(annex, 32));
        Assert.False(AvccAnnexB.ContainsNalType(annex, 7));
        Assert.Equal(32, HevcSequence.NalType(0x40));
    }

    [Fact]
    public void Platform_hevc_decoder_is_unavailable_off_windows()
    {
        var decoder = HevcDecoders.CreatePlatformDefault();
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.True(decoder.Name is "unavailable" or "ffmpeg-hevc");
                Assert.Equal(decoder.Name == "ffmpeg-hevc", decoder.IsAvailable);
                return;
            }

            _ = decoder.IsAvailable;
            Assert.True(decoder.Name == "unavailable"
                || decoder.Name.StartsWith("media-foundation-hevc", StringComparison.Ordinal)
                || decoder.Name == "ffmpeg-hevc");
        }
        finally
        {
            (decoder as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task Hevc_keyframes_are_prefixed_once_and_skip_an_access_unit_that_already_has_vps()
    {
        var lab = new byte[16];
        lab[0] = 4;
        var secret = new byte[32];
        secret[1] = 9;
        const ulong streamId = 15;
        var h264 = new SpyH264();
        var hevc = new SpyHevc();
        byte[] vps = [0x40, 0x01, 0x10];
        byte[] sps = [0x42, 0x01, 0x20];
        byte[] pps = [0x44, 0x01, 0x30];
        await using var mirror = MirrorRuntime.Start(
            IPAddress.Loopback,
            9,
            new FrameSlot(),
            h264,
            null,
            lab,
            NullAirPlayLog.Instance,
            acceptHevc: true,
            hevcDecoder: hevc);
        mirror.ArmForStream(lab, secret, streamId);

        var first = LengthPrefixed(0x02, 0x01);
        var second = LengthPrefixed(0x02, 0x02);
        await SendAsync(
            mirror,
            () => hevc.Snapshot().Length >= 2,
            HevcCodecPacket(vps, sps, pps),
            Packet(0, 0x10, 0, Encrypt(lab, secret, streamId, first, 0)),
            Packet(0, 0x10, 0, Encrypt(lab, secret, streamId, second, first.Length)));

        Assert.Equal(0, h264.Configures);
        Assert.Equal(0, h264.Decodes);
        Assert.Equal(vps, hevc.Video);
        Assert.Equal(sps, hevc.Sequence);
        Assert.Equal(pps, hevc.Picture);
        Assert.Equal(Prefixed(vps, sps, pps, Annex(0x02, 0x01)), hevc.Snapshot()[0]);
        Assert.Equal(Annex(0x02, 0x02), hevc.Snapshot()[1]);

        var embedded = ConcatAvcc([0x40, 0x01, 0x10], [0x02, 0x03]);
        await SendAsync(
            mirror,
            () => hevc.Snapshot().Length >= 3,
            HevcCodecPacket(vps, sps, pps),
            Packet(0, 0x10, 0, Encrypt(lab, secret, streamId, embedded, first.Length + second.Length)));
        Assert.Equal(1, CountHevcNal(hevc.Snapshot()[2], 32));
        Assert.Equal(ConcatAnnex([0x40, 0x01, 0x10], [0x02, 0x03]), hevc.Snapshot()[2]);
    }

    [Fact]
    public async Task H264_still_decodes_when_hevc_is_enabled()
    {
        var lab = new byte[16];
        lab[2] = 6;
        var secret = new byte[32];
        secret[3] = 7;
        const ulong streamId = 21;
        var h264 = new SpyH264();
        var hevc = new SpyHevc();
        await using var mirror = MirrorRuntime.Start(
            IPAddress.Loopback,
            9,
            new FrameSlot(),
            h264,
            null,
            lab,
            NullAirPlayLog.Instance,
            acceptHevc: true,
            hevcDecoder: hevc);
        mirror.ArmForStream(lab, secret, streamId);
        var access = new byte[] { 0, 0, 0, 1, 0x65 };
        await SendAsync(
            mirror,
            () => h264.Decodes == 1,
            H264CodecPacket(),
            Packet(0, 0x10, 0, Encrypt(lab, secret, streamId, access, 0)));
        Assert.Equal(0, hevc.Configures);
        Assert.Equal(1, h264.Configures);
        Assert.Equal(0x67, h264.Snapshot()[0][4]);
        Assert.Equal(0x65, h264.Snapshot()[0][^1]);
    }

    [Fact]
    public async Task Hevc_offer_is_rejected_when_the_flag_is_off()
    {
        var lab = new byte[16];
        var secret = new byte[32];
        var h264 = new SpyH264();
        var hevc = new SpyHevc();
        var log = new CountingLog();
        await using var mirror = MirrorRuntime.Start(IPAddress.Loopback, 9, new FrameSlot(), h264, null, lab, log, acceptHevc: false, hevcDecoder: hevc);
        mirror.ArmForStream(lab, secret, 3);
        await SendAsync(
            mirror,
            () => mirror.Activity.RejectedPayloads >= 1,
            HevcCodecPacket([0x40, 0x01], [0x42, 0x01], [0x44, 0x01]));
        Assert.Equal(0, h264.Configures);
        Assert.Equal(0, hevc.Configures);
        Assert.True(log.Contains("--h265"));
        Assert.True(log.Contains("bit 42"));
    }

    [Fact]
    public async Task Missing_hevc_decoder_is_logged_and_does_not_publish_a_frame()
    {
        var lab = new byte[16];
        var secret = new byte[32];
        var h264 = new SpyH264();
        var frames = new FrameSlot();
        var log = new CountingLog();
        await using var mirror = MirrorRuntime.Start(
            IPAddress.Loopback,
            9,
            frames,
            h264,
            null,
            lab,
            log,
            acceptHevc: true,
            hevcDecoder: new UnavailableHevcDecoder());
        mirror.ArmForStream(lab, secret, 4);
        await SendAsync(
            mirror,
            () => mirror.Activity.RejectedPayloads >= 1,
            HevcCodecPacket([0x40, 0x01], [0x42, 0x01], [0x44, 0x01]),
            Packet(0, 0x10, 0, Encrypt(lab, secret, 4, LengthPrefixed(0x02, 0x01), 0)));
        Assert.False(frames.TryRead(out _, out _, out _, out _, out _));
        Assert.Equal(0, h264.Configures);
        Assert.True(log.Contains("not available") || log.Contains("CLSID_CMSH265DecoderMFT") || log.Contains("Media Foundation"));
    }

    private static ReceiverAdvertisement Advertisement(bool acceptHevc) => new()
    {
        InstanceName = "room",
        HostLabel = "room",
        DeviceId = "aa:bb:cc:dd:ee:ff",
        RaopInstance = "AABBCCDDEEFF@room",
        AirPlayPort = 1,
        RaopPort = 2,
        PublicKeyBase64 = "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqo=",
        AcceptHevc = acceptHevc,
    };

    private static MirrorHeader Header(byte type, byte option)
    {
        var bytes = new byte[128];
        bytes[4] = type;
        bytes[6] = option;
        Assert.True(MirrorHeader.TryParse(bytes, out var header));
        return header;
    }

    private static void WriteHvc1(byte[] payload)
    {
        payload[4] = (byte)'h';
        payload[5] = (byte)'v';
        payload[6] = (byte)'c';
        payload[7] = (byte)'1';
    }

    private static byte[] HvccArrays(byte[] vps, byte[] sps, byte[] pps)
    {
        var output = new byte[15 + vps.Length + sps.Length + pps.Length];
        WriteArray(output, 0, 32, vps);
        WriteArray(output, 5 + vps.Length, 33, sps);
        WriteArray(output, 10 + vps.Length + sps.Length, 34, pps);
        return output;
    }

    private static void WriteArray(byte[] destination, int offset, int nalType, byte[] nal)
    {
        destination[offset] = (byte)(0x80 | nalType);
        destination[offset + 1] = 0;
        destination[offset + 2] = 1;
        destination[offset + 3] = (byte)(nal.Length >> 8);
        destination[offset + 4] = (byte)nal.Length;
        nal.CopyTo(destination, offset + 5);
    }

    private static byte[] BuildSps(byte[] profile, int width, int height, int cropRight)
    {
        var packer = new BitPacker();
        packer.WriteUe(0);
        packer.WriteUe(1);
        packer.WriteUe((uint)width);
        packer.WriteUe((uint)height);
        if (cropRight == 0)
        {
            packer.WriteBit(0);
        }
        else
        {
            packer.WriteBit(1);
            packer.WriteUe(0);
            packer.WriteUe((uint)cropRight);
            packer.WriteUe(0);
            packer.WriteUe(0);
        }

        packer.WriteUe(0);
        packer.WriteUe(0);
        var tail = packer.Finish();
        var sps = new byte[3 + profile.Length + tail.Length];
        sps[0] = 0x42;
        sps[1] = 0x01;
        sps[2] = 0x01;
        profile.CopyTo(sps, 3);
        tail.CopyTo(sps, 3 + profile.Length);
        return sps;
    }

    private static async Task SendAsync(MirrorRuntime mirror, Func<bool> until, params byte[][] packets)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, mirror.Activity.DataPort);
        await using var stream = client.GetStream();
        foreach (var packet in packets)
        {
            await stream.WriteAsync(packet);
        }

        await stream.FlushAsync();
        var deadline = Environment.TickCount64 + 2000;
        while (!until())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Mirror stream did not reach the expected state.");
            }

            await Task.Delay(1);
        }
    }

    private static byte[] Encrypt(byte[] content, byte[] secret, ulong streamId, byte[] plain, int skip)
    {
        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[16];
        StreamCipher.Derive(content, secret, streamId, key, iv);
        var encrypted = new byte[plain.Length];
        using var ctr = new AesCtr(key, iv);
        if (skip > 0)
        {
            ctr.Skip(skip);
        }

        ctr.Transform(plain, encrypted);
        return encrypted;
    }

    private static byte[] LengthPrefixed(params byte[] nal)
    {
        var output = new byte[4 + nal.Length];
        output[2] = (byte)(nal.Length >> 8);
        output[3] = (byte)nal.Length;
        nal.CopyTo(output, 4);
        return output;
    }

    private static byte[] ConcatAvcc(params byte[][] nals)
    {
        var length = 0;
        foreach (var nal in nals)
        {
            length += 4 + nal.Length;
        }

        var output = new byte[length];
        var offset = 0;
        foreach (var nal in nals)
        {
            output[offset + 2] = (byte)(nal.Length >> 8);
            output[offset + 3] = (byte)nal.Length;
            nal.CopyTo(output, offset + 4);
            offset += 4 + nal.Length;
        }

        return output;
    }

    private static byte[] ConcatAnnex(params byte[][] nals)
    {
        var length = 0;
        foreach (var nal in nals)
        {
            length += 4 + nal.Length;
        }

        var output = new byte[length];
        var offset = 0;
        foreach (var nal in nals)
        {
            output[offset + 3] = 1;
            nal.CopyTo(output, offset + 4);
            offset += 4 + nal.Length;
        }

        return output;
    }

    private static byte[] Annex(params byte[] nal)
    {
        var output = new byte[4 + nal.Length];
        output[3] = 1;
        nal.CopyTo(output, 4);
        return output;
    }

    private static byte[] Prefixed(byte[] vps, byte[] sps, byte[] pps, byte[] annex)
    {
        var output = new byte[12 + vps.Length + sps.Length + pps.Length + annex.Length];
        var offset = 0;
        offset = CopyNal(output, offset, vps);
        offset = CopyNal(output, offset, sps);
        offset = CopyNal(output, offset, pps);
        annex.CopyTo(output, offset);
        return output;
    }

    private static int CopyNal(byte[] destination, int offset, byte[] nal)
    {
        destination[offset + 3] = 1;
        nal.CopyTo(destination, offset + 4);
        return offset + 4 + nal.Length;
    }

    private static int CountHevcNal(byte[] annex, int nalType)
    {
        var count = 0;
        for (var i = 0; i + 4 < annex.Length; i++)
        {
            if (annex[i] == 0 && annex[i + 1] == 0 && annex[i + 2] == 0 && annex[i + 3] == 1 && HevcSequence.NalType(annex[i + 4]) == nalType)
            {
                count++;
            }
        }

        return count;
    }

    private static byte[] Packet(byte type, byte flags, byte option, byte[] payload)
    {
        var packet = new byte[128 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(packet, 0);
        packet[4] = type;
        packet[5] = flags;
        packet[6] = option;
        payload.CopyTo(packet, 128);
        return packet;
    }

    private static byte[] HevcCodecPacket(byte[] vps, byte[] sps, byte[] pps)
    {
        var arrays = HvccArrays(vps, sps, pps);
        var payload = new byte[0x75 + arrays.Length];
        WriteHvc1(payload);
        arrays.CopyTo(payload, 0x75);
        return Packet(1, 0, 0, payload);
    }

    private static byte[] H264CodecPacket()
    {
        byte[] sps = [0x67, 0x42, 0x00, 0x0A];
        byte[] pps = [0x68, 0xCE];
        var payload = new byte[8 + sps.Length + 3 + pps.Length];
        payload[6] = (byte)(sps.Length >> 8);
        payload[7] = (byte)sps.Length;
        sps.CopyTo(payload, 8);
        payload[8 + sps.Length] = 1;
        payload[9 + sps.Length] = (byte)(pps.Length >> 8);
        payload[10 + sps.Length] = (byte)pps.Length;
        pps.CopyTo(payload, 11 + sps.Length);
        return Packet(1, 0, 0, payload);
    }

    private sealed class BitPacker
    {
        private readonly List<byte> _bytes = [];
        private int _bits;
        private byte _current;

        public void WriteBit(int bit)
        {
            _current = (byte)((_current << 1) | (bit & 1));
            _bits++;
            if (_bits == 8)
            {
                _bytes.Add(_current);
                _bits = 0;
                _current = 0;
            }
        }

        public void WriteUe(uint value)
        {
            var code = value + 1;
            var width = 0;
            var probe = code;
            while (probe > 1)
            {
                width++;
                probe >>= 1;
            }

            for (var i = 0; i < width; i++)
            {
                WriteBit(0);
            }

            for (var i = width; i >= 0; i--)
            {
                WriteBit((int)((code >> i) & 1));
            }
        }

        public byte[] Finish()
        {
            if (_bits != 0)
            {
                _current <<= 8 - _bits;
                _bytes.Add(_current);
            }

            return _bytes.ToArray();
        }
    }

    private sealed class SpyH264 : IH264Decoder
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _annexes = [];

        public int Configures { get; private set; }

        public int Decodes { get; private set; }

        public bool IsAvailable => true;

        public string Name => "h264-spy";

        public byte[][] Snapshot()
        {
            lock (_gate)
            {
                return _annexes.ToArray();
            }
        }

        public void Configure(ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet)
            => Configures++;

        public void Reset()
        {
        }

        public bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame)
        {
            if (Configures == 0)
            {
                frame = default;
                return false;
            }

            lock (_gate)
            {
                _annexes.Add(annexB.ToArray());
                Decodes = _annexes.Count;
            }

            frame = new DecodedFrame(2, 2, new byte[16], 8);
            return true;
        }
    }

    private sealed class SpyHevc : IHevcDecoder
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _annexes = [];

        public int Configures { get; private set; }

        public byte[] Video { get; private set; } = [];

        public byte[] Sequence { get; private set; } = [];

        public byte[] Picture { get; private set; } = [];

        public bool IsAvailable => true;

        public string Name => "hevc-spy";

        public byte[][] Snapshot()
        {
            lock (_gate)
            {
                return _annexes.ToArray();
            }
        }

        public void Configure(ReadOnlySpan<byte> videoParameterSet, ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet)
        {
            Video = videoParameterSet.ToArray();
            Sequence = sequenceParameterSet.ToArray();
            Picture = pictureParameterSet.ToArray();
            Configures++;
        }

        public void Reset()
        {
        }

        public bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame)
        {
            if (Configures == 0)
            {
                frame = default;
                return false;
            }

            lock (_gate)
            {
                _annexes.Add(annexB.ToArray());
            }

            frame = new DecodedFrame(2, 2, new byte[16], 8);
            return true;
        }
    }

    private sealed class CountingLog : IAirPlayLog
    {
        private readonly List<string> _lines = [];

        public void Log(AirPlayLogLevel level, string message)
        {
            lock (_lines)
            {
                _lines.Add(message);
            }
        }

        public bool Contains(string text)
        {
            lock (_lines)
            {
                return _lines.Exists(line => line.Contains(text, StringComparison.Ordinal));
            }
        }
    }
}
