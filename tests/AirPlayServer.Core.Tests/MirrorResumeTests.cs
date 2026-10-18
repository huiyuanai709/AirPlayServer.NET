using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlayServer.Core.Crypto;
using AirPlayServer.Core.Media;
using AirPlayServer.Core.Pairing;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class MirrorResumeTests
{
    [Fact]
    public async Task High_bit_stream_connection_id_still_decrypts()
    {
        const ulong streamId = 1UL << 63;
        var lab = new byte[16];
        lab[0] = 9;
        var decoder = new SpyDecoder();
        var log = new CountingLog();
        await using var mirror = MirrorRuntime.Start(IPAddress.Loopback, 9, new FrameSlot(), decoder, null, lab, log);
        using var session = FinishPairing(out var secret);
        Assert.Equal(200, mirror.HandleMedia(Request("SETUP", SessionBody()), session, null).Status);
        Assert.Equal(200, mirror.HandleMedia(Request("SETUP", StreamBody(streamId)), session, null).Status);
        Assert.True(mirror.Activity.MediaKeysReady);

        await SendAsync(
            mirror,
            () => mirror.Activity.DecodedFrames == 1,
            CodecPacket(0),
            Packet(0, 0x10, 0, Encrypt(lab, secret, streamId, AccessUnit(0x65), skip: 0)));
        Assert.Equal(0, mirror.Activity.RejectedPayloads);
        Assert.Equal(0, log.Count("not AVCC"));
        Assert.Equal(Prefixed(AccessUnit(0x65)), decoder.Snapshot()[0]);
    }

    [Fact]
    public async Task Video_teardown_keeps_the_session_key_and_a_new_setup_resumes()
    {
        var lab = new byte[16];
        lab[1] = 3;
        var secret = new byte[32];
        secret[2] = 8;
        const ulong firstId = 41;
        const ulong secondId = 99;
        var decoder = new SpyDecoder();
        await using var mirror = MirrorRuntime.Start(IPAddress.Loopback, 9, new FrameSlot(), decoder, null, lab, NullAirPlayLog.Instance);
        mirror.ArmForStream(lab, secret, firstId);
        using var session = new PairingSession(PairingIdentity.Generate());

        await SendAsync(
            mirror,
            () => mirror.Activity.DecodedFrames == 1,
            CodecPacket(0),
            Packet(0, 0x10, 0, Encrypt(lab, secret, firstId, AccessUnit(0x65), skip: 0)));
        var resetsAfterPicture = decoder.Resets;

        Assert.Equal(200, mirror.HandleMedia(Request("TEARDOWN", StreamBody(firstId)), session, null).Status);
        Assert.False(mirror.Activity.MediaKeysReady);
        Assert.True(decoder.Resets > resetsAfterPicture);

        Assert.Equal(200, mirror.HandleMedia(Request("SETUP", StreamBody(secondId)), session, null).Status);
        Assert.True(mirror.Activity.MediaKeysReady);
        await SendAsync(
            mirror,
            () => mirror.Activity.DecodedFrames == 2,
            CodecPacket(0),
            Packet(0, 0x10, 0, Encrypt(lab, secret, secondId, AccessUnit(0x65), skip: 0)));
        Assert.Equal(0, mirror.Activity.RejectedPayloads);

        Assert.Equal(200, mirror.HandleMedia(Request("TEARDOWN", []), session, null).Status);
        Assert.False(mirror.Activity.MediaKeysReady);
        Assert.Equal(200, mirror.HandleMedia(Request("SETUP", StreamBody(secondId)), session, null).Status);
        Assert.False(mirror.Activity.MediaKeysReady);
    }

    [Fact]
    public async Task Repeated_setup_restarts_ctr_and_audio_teardown_does_not()
    {
        var lab = new byte[16];
        lab[3] = 11;
        var secret = new byte[32];
        secret[4] = 6;
        const ulong streamId = 77;
        var decoder = new SpyDecoder();
        await using var mirror = MirrorRuntime.Start(IPAddress.Loopback, 9, new FrameSlot(), decoder, null, lab, NullAirPlayLog.Instance);
        mirror.ArmForStream(lab, secret, streamId);
        using var session = new PairingSession(PairingIdentity.Generate());

        var first = AccessUnit(0x65);
        var second = AccessUnit(0x41);
        await SendAsync(
            mirror,
            () => decoder.Snapshot().Length >= 2,
            CodecPacket(0),
            Packet(0, 0x10, 0, Encrypt(lab, secret, streamId, first, skip: 0)),
            Packet(0, 0, 0, Encrypt(lab, secret, streamId, second, skip: first.Length)));
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x41 }, decoder.Snapshot()[1]);

        Assert.Equal(200, mirror.HandleMedia(Request("SETUP", StreamBody(streamId)), session, null).Status);
        var restarted = AccessUnit(0x45);
        await SendAsync(
            mirror,
            () => decoder.Snapshot().Length >= 3,
            Packet(0, 0x10, 0, Encrypt(lab, secret, streamId, restarted, skip: 0)));
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x45 }, decoder.Snapshot()[2]);
        Assert.True(decoder.Resets > 0);

        var beforeAudio = mirror.Activity.AcceptedAccessUnits;
        Assert.Equal(200, mirror.HandleMedia(Request("TEARDOWN", AudioBody()), session, null).Status);
        Assert.True(mirror.Activity.MediaKeysReady);
        var continued = AccessUnit(0x21);
        await SendAsync(
            mirror,
            () => mirror.Activity.AcceptedAccessUnits == beforeAudio + 1,
            Packet(0, 0, 0, Encrypt(lab, secret, streamId, continued, skip: restarted.Length)));
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x21 }, decoder.Snapshot()[^1]);
    }

    [Fact]
    public async Task Parameter_sets_are_prefixed_once_and_not_onto_an_access_unit_that_already_has_them()
    {
        var lab = new byte[16];
        lab[5] = 2;
        var secret = new byte[32];
        secret[6] = 7;
        const ulong streamId = 15;
        var decoder = new SpyDecoder();
        await using var mirror = MirrorRuntime.Start(IPAddress.Loopback, 9, new FrameSlot(), decoder, null, lab, NullAirPlayLog.Instance);
        mirror.ArmForStream(lab, secret, streamId);

        var idr = AccessUnit(0x65);
        var later = AccessUnit(0x45);
        await SendAsync(
            mirror,
            () => decoder.Snapshot().Length >= 1,
            Packet(0, 0x10, 0, Encrypt(lab, secret, streamId, idr, skip: 0)),
            CodecPacket(0));
        Assert.Equal(Prefixed(idr), decoder.Snapshot()[0]);
        Assert.Equal(1, decoder.Configures);

        await SendAsync(
            mirror,
            () => decoder.Snapshot().Length >= 2,
            Packet(0, 0x10, 0, Encrypt(lab, secret, streamId, later, skip: idr.Length)));
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x45 }, decoder.Snapshot()[1]);

        var embedded = EmbeddedParameterSets();
        await SendAsync(
            mirror,
            () => decoder.Snapshot().Length >= 3 && decoder.Resets >= 1,
            CodecPacket(0x16),
            Packet(0, 0x10, 0, Encrypt(lab, secret, streamId, embedded, skip: idr.Length + later.Length)));
        var third = decoder.Snapshot()[2];
        Assert.Equal(1, CountNal(third, 7));
        Assert.Equal(0x67, third[4]);
    }

    [Fact]
    public async Task Invalid_access_units_are_logged_once_and_recover_on_the_next_setup()
    {
        var lab = new byte[16];
        lab[7] = 13;
        var secret = new byte[32];
        secret[8] = 4;
        const ulong streamId = 21;
        var decoder = new SpyDecoder();
        var log = new CountingLog();
        await using var mirror = MirrorRuntime.Start(IPAddress.Loopback, 9, new FrameSlot(), decoder, null, lab, log);
        mirror.ArmForStream(lab, secret, streamId);
        using var session = new PairingSession(PairingIdentity.Generate());

        var garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var burst = new byte[6][];
        for (var i = 0; i < burst.Length; i++)
        {
            burst[i] = Packet(0, 0, 0, Encrypt(lab, secret, streamId, garbage, skip: i * garbage.Length));
        }

        await SendAsync(mirror, () => mirror.Activity.RejectedPayloads >= 4, burst);
        Assert.Equal(1, log.Count("Dropped a mirror payload that was not AVCC."));
        Assert.Equal(1, log.Count("This stream stays dropped until the next SETUP."));
        Assert.False(mirror.Activity.MediaKeysReady);
        var rejected = mirror.Activity.RejectedPayloads;
        var dropped = mirror.Activity.DroppedEncryptedPayloads;
        await SendAsync(
            mirror,
            () => mirror.Activity.DroppedEncryptedPayloads > dropped,
            Packet(0, 0, 0, Encrypt(lab, secret, streamId, garbage, skip: 0)));
        Assert.Equal(rejected, mirror.Activity.RejectedPayloads);
        Assert.Equal(1, log.Count("Dropped a mirror payload that was not AVCC."));

        Assert.Equal(200, mirror.HandleMedia(Request("SETUP", StreamBody(streamId)), session, null).Status);
        Assert.True(mirror.Activity.MediaKeysReady);
        await SendAsync(
            mirror,
            () => mirror.Activity.DecodedFrames == 1,
            CodecPacket(0),
            Packet(0, 0x10, 0, Encrypt(lab, secret, streamId, AccessUnit(0x65), skip: 0)));
        Assert.Equal(1, log.Count("Dropped a mirror payload that was not AVCC."));
    }

    [Fact]
    public async Task Resume_after_pause_keeps_the_ctr_counter()
    {
        var lab = new byte[16];
        lab[8] = 15;
        var secret = new byte[32];
        secret[9] = 1;
        const ulong streamId = 33;
        var decoder = new SpyDecoder();
        await using var mirror = MirrorRuntime.Start(IPAddress.Loopback, 9, new FrameSlot(), decoder, null, lab, NullAirPlayLog.Instance);
        mirror.ArmForStream(lab, secret, streamId);

        var idr = AccessUnit(0x65);
        var delta = AccessUnit(0x41);
        var after = AccessUnit(0x21);
        await SendAsync(
            mirror,
            () => decoder.Snapshot().Length >= 2,
            CodecPacket(0),
            Packet(0, 0x10, 0, Encrypt(lab, secret, streamId, idr, skip: 0)),
            Packet(0, 0, 0, Encrypt(lab, secret, streamId, delta, skip: idr.Length)));
        var resets = decoder.Resets;

        await SendAsync(
            mirror,
            () => decoder.Resets > resets && decoder.Snapshot().Length >= 3,
            CodecPacket(0x56),
            CodecPacket(0x16),
            Packet(0, 0, 0, Encrypt(lab, secret, streamId, after, skip: idr.Length + delta.Length)));
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x21 }, decoder.Snapshot()[^1]);
        Assert.Equal(0, mirror.Activity.RejectedPayloads);
    }

    private static PairingSession FinishPairing(out byte[] secret)
    {
        using var senderKey = PairingIdentity.Generate();
        using var sender = new PairingSender(senderKey);
        var session = new PairingSession(PairingIdentity.Generate());
        sender.AcceptSetup(session.PairSetup(sender.CreateSetupRequest()));
        var start = sender.CreateVerifyStart();
        session.PairVerifyFinish(sender.AcceptVerifyStart(session.PairVerifyStart(start)));
        secret = sender.SharedSecret.ToArray();
        return session;
    }

    private static Http1Request Request(string method, byte[] body)
    {
        var header = Encoding.ASCII.GetBytes(method + " * RTSP/1.0\r\nContent-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n\r\n");
        var buffer = new byte[header.Length + body.Length];
        header.CopyTo(buffer, 0);
        body.CopyTo(buffer, header.Length);
        Assert.Equal(HttpParseStatus.Ok, Http1Request.TryParse(buffer, out var request, out var consumed));
        Assert.Equal(buffer.Length, consumed);
        return request!;
    }

    private static byte[] SessionBody()
    {
        var session = new PlistDictionary();
        session.Add("eiv", new PlistData(new byte[16]));
        session.Add("ekey", new PlistData(new byte[16]));
        session.Add("timingPort", new PlistInteger(46000));
        return BinaryPropertyList.Write(session);
    }

    private static byte[] StreamBody(ulong streamId)
    {
        var stream = new PlistDictionary();
        stream.Add("type", new PlistInteger(110));
        stream.Add("streamConnectionID", new PlistInteger(unchecked((long)streamId)));
        var streams = new PlistArray();
        streams.Add(stream);
        var body = new PlistDictionary();
        body.Add("streams", streams);
        return BinaryPropertyList.Write(body);
    }

    private static byte[] AudioBody()
    {
        var stream = new PlistDictionary();
        stream.Add("type", new PlistInteger(96));
        var streams = new PlistArray();
        streams.Add(stream);
        var body = new PlistDictionary();
        body.Add("streams", streams);
        return BinaryPropertyList.Write(body);
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
        await WaitUntilAsync(until);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 2000;
        while (!condition())
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

    private static byte[] AccessUnit(byte nal)
        => [0, 0, 0, 1, nal];

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

    private static byte[] CodecPacket(byte option)
    {
        var codec = CodecPayload();
        var packet = Packet(1, 0, option, codec);
        BitConverter.GetBytes(480f).CopyTo(packet, 56);
        BitConverter.GetBytes(1040f).CopyTo(packet, 60);
        return packet;
    }

    private static byte[] CodecPayload()
    {
        byte[] sps = [0x67, 0x42, 0x00, 0x0A];
        byte[] pps = [0x68, 0xCE];
        var payload = new byte[8 + sps.Length + 3 + pps.Length];
        payload[0] = 1;
        payload[6] = (byte)(sps.Length >> 8);
        payload[7] = (byte)sps.Length;
        sps.CopyTo(payload, 8);
        var ppsCount = 8 + sps.Length;
        payload[ppsCount] = 1;
        payload[ppsCount + 1] = (byte)(pps.Length >> 8);
        payload[ppsCount + 2] = (byte)pps.Length;
        pps.CopyTo(payload, ppsCount + 3);
        return payload;
    }

    private static byte[] EmbeddedParameterSets()
    {
        byte[] sps = [0x67, 0x42, 0x00, 0x0A];
        byte[] pps = [0x68, 0xCE];
        var payload = new byte[4 + sps.Length + 4 + pps.Length + 5];
        WriteLength(payload, 0, sps);
        WriteLength(payload, 4 + sps.Length, pps);
        payload[4 + sps.Length + 4 + pps.Length] = 0;
        payload[4 + sps.Length + 4 + pps.Length + 1] = 0;
        payload[4 + sps.Length + 4 + pps.Length + 2] = 0;
        payload[4 + sps.Length + 4 + pps.Length + 3] = 1;
        payload[4 + sps.Length + 4 + pps.Length + 4] = 0x65;
        return payload;
    }

    private static void WriteLength(byte[] destination, int offset, byte[] nal)
    {
        destination[offset] = 0;
        destination[offset + 1] = 0;
        destination[offset + 2] = 0;
        destination[offset + 3] = (byte)nal.Length;
        nal.CopyTo(destination, offset + 4);
    }

    private static byte[] Prefixed(byte[] annex)
    {
        byte[] sps = [0x67, 0x42, 0x00, 0x0A];
        byte[] pps = [0x68, 0xCE];
        var output = new byte[8 + sps.Length + pps.Length + annex.Length];
        output[3] = 1;
        sps.CopyTo(output, 4);
        output[4 + sps.Length + 3] = 1;
        pps.CopyTo(output, 8 + sps.Length);
        annex.CopyTo(output, 8 + sps.Length + pps.Length);
        return output;
    }

    private static int CountNal(byte[] annex, int nalType)
    {
        var count = 0;
        for (var i = 0; i + 4 < annex.Length; i++)
        {
            if (annex[i] == 0 && annex[i + 1] == 0 && annex[i + 2] == 0 && annex[i + 3] == 1 && (annex[i + 4] & 0x1F) == nalType)
            {
                count++;
            }
        }

        return count;
    }

    private sealed class SpyDecoder : IH264Decoder
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _annexes = [];
        private int _resets;
        private int _configures;

        public int Resets => Volatile.Read(ref _resets);

        public int Configures => Volatile.Read(ref _configures);

        public bool IsAvailable => true;

        public string Name => "spy";

        public byte[][] Snapshot()
        {
            lock (_gate)
            {
                return _annexes.ToArray();
            }
        }

        public void Configure(ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet)
            => Interlocked.Increment(ref _configures);

        public void Reset() => Interlocked.Increment(ref _resets);

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

        public int Count(string text)
        {
            lock (_lines)
            {
                var count = 0;
                foreach (var line in _lines)
                {
                    if (line.Contains(text, StringComparison.Ordinal))
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }
}
