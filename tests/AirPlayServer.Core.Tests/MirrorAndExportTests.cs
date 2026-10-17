using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlayServer.Core;
using AirPlayServer.Core.Crypto;
using AirPlayServer.Core.Export;
using AirPlayServer.Core.Media;
using AirPlayServer.Core.Pairing;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class MirrorAndExportTests
{
    [Fact]
    public void Stream_cipher_round_trips_across_payload_boundaries()
    {
        var content = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        var secret = Enumerable.Range(20, 32).Select(value => (byte)value).ToArray();
        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[16];
        StreamCipher.Derive(content, secret, 4964383553955644435UL, key, iv);
        var plain = new byte[40];
        for (var i = 0; i < plain.Length; i++)
        {
            plain[i] = (byte)(i * 3);
        }

        var cipher = new byte[plain.Length];
        using (var forward = new AesCtr(key, iv))
        {
            forward.Transform(plain, cipher);
        }

        var first = new byte[15];
        var second = new byte[25];
        using var backward = new AesCtr(key, iv);
        backward.Transform(cipher.AsSpan(0, 15), first);
        backward.Transform(cipher.AsSpan(15), second);
        Assert.Equal(plain.AsSpan(0, 15).ToArray(), first);
        Assert.Equal(plain.AsSpan(15).ToArray(), second);
    }

    [Fact]
    public void Avcc_lengths_become_annex_b_start_codes()
    {
        var payload = new byte[] { 0, 0, 0, 1, 0x65, 0, 0, 0, 2, 0x41, 0x9A };
        Assert.True(AvccAnnexB.TryConvertInPlace(payload));
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65, 0, 0, 0, 1, 0x41, 0x9A }, payload);
        Assert.False(AvccAnnexB.TryConvertInPlace([0, 0, 0, 5, 1]));
    }

    [Fact]
    public void Mirror_header_is_little_endian_and_keyframe_is_the_second_type_byte()
    {
        var header = new byte[128];
        header[0] = 5;
        header[4] = 0;
        header[5] = 0x10;
        header[6] = 0x16;
        BitConverter.GetBytes(480f).CopyTo(header, 56);
        Assert.True(MirrorHeader.TryParse(header, out var parsed));
        Assert.Equal(5, parsed.PayloadSize);
        Assert.Equal(0, parsed.PayloadType);
        Assert.True(parsed.IsKeyframe);
        Assert.Equal(480f, parsed.Width);
    }

    [Fact]
    public void FairPlay_unwrapper_stub_rejects_a_72_byte_ekey()
    {
        var unwrapped = new byte[16];
        Assert.False(new UnavailableMediaKeyUnwrapper().TryUnwrap(new byte[72], unwrapped));
        Assert.Equal(new byte[16], unwrapped);
    }

    [Fact]
    public void Frame_slot_keeps_only_the_latest_generation()
    {
        var slot = new FrameSlot();
        Assert.False(slot.TryRead(out _, out _, out _, out _, out _));
        var first = slot.Publish(2, 2, new byte[16], 8);
        var second = slot.Publish(1, 1, [1, 2, 3, 4], 4);
        Assert.True(slot.TryRead(out var width, out var height, out _, out var pixels, out var generation));
        Assert.Equal(1, width);
        Assert.Equal(1, height);
        Assert.Equal(second, generation);
        Assert.True(second > first);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, pixels);
    }

    [Fact]
    public void Jpeg_and_atbf_self_check_round_trip_the_header()
    {
        ExportSelfCheck.Run();
    }

    [Fact]
    public async Task Directory_export_renames_part_to_jpg_and_tcp_receives_type3()
    {
        var directory = Path.Combine(Path.GetTempPath(), "airplay-tapclock-" + Guid.NewGuid().ToString("N"));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var slot = new FrameSlot();
        DemoFrame.Publish(slot, 32, 24);
        var options = new TapClockExportOptions
        {
            Endpoint = "127.0.0.1:" + port,
            WatchDirectory = directory,
            FramesPerSecond = 20,
        };
        await using var exporter = TapClockExporter.Start(options, slot, NullAirPlayLog.Instance, CancellationToken.None);
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var header = new byte[AtbfFraming.HeaderLength];
        await stream.ReadExactlyAsync(header);
        Assert.True(AtbfFraming.TryReadHeader(header, out var type, out var flags, out var length));
        Assert.Equal(AtbfFraming.TypeImage, type);
        Assert.Equal(AtbfFraming.FlagJpeg, flags);
        var jpeg = new byte[length];
        await stream.ReadExactlyAsync(jpeg);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpeg[^2..]);
        Assert.True(await exporter.WaitForDeliveryAsync(1, TimeSpan.FromSeconds(2)));
        Assert.True(File.Exists(Path.Combine(directory, "mirror.jpg")));
        Assert.False(File.Exists(Path.Combine(directory, "mirror.jpg.part")));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Armed_mirror_stream_decrypts_avcc_and_unkeyed_stream_drops_it()
    {
        var decoder = new RecordingDecoder();
        var frames = new FrameSlot();
        await using var mirror = MirrorRuntime.Start(IPAddress.Loopback, 9, frames, decoder, null, ReadOnlySpan<byte>.Empty, NullAirPlayLog.Instance);
        var content = new byte[16];
        content[0] = 7;
        var secret = new byte[32];
        secret[0] = 9;
        const ulong streamId = 42;
        mirror.ArmForStream(content, secret, streamId);

        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[16];
        StreamCipher.Derive(content, secret, streamId, key, iv);
        var access = new byte[] { 0, 0, 0, 1, 0x65 };
        var encrypted = new byte[access.Length];
        using (var ctr = new AesCtr(key, iv))
        {
            ctr.Transform(access, encrypted);
        }

        var codec = CodecPayload();
        var codecHeader = new byte[128];
        BitConverter.GetBytes(codec.Length).CopyTo(codecHeader, 0);
        codecHeader[4] = 1;
        BitConverter.GetBytes(480f).CopyTo(codecHeader, 56);
        BitConverter.GetBytes(1040f).CopyTo(codecHeader, 60);
        var encryptedPacket = Packet(0, 0x10, encrypted);
        var streamBytes = new byte[encryptedPacket.Length + codecHeader.Length + codec.Length];
        encryptedPacket.CopyTo(streamBytes, 0);
        codecHeader.CopyTo(streamBytes, encryptedPacket.Length);
        codec.CopyTo(streamBytes, encryptedPacket.Length + codecHeader.Length);

        using (var client = new TcpClient { NoDelay = true })
        {
            await client.ConnectAsync(IPAddress.Loopback, mirror.Activity.DataPort);
            await using var stream = client.GetStream();
            await stream.WriteAsync(streamBytes);
            await stream.FlushAsync();
            await WaitUntilAsync(() =>
                decoder.Decoded.Task.IsCompleted
                && mirror.Activity.AcceptedAccessUnits == 1
                && mirror.Activity.CodecConfigurations == 1
                && decoder.LastAnnexB.Length > 5);
            Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0x00, 0x0A, 0, 0, 0, 1, 0x68, 0xCE, 0, 0, 0, 1, 0x65 }, decoder.LastAnnexB);
            Assert.True(decoder.LastWasKeyframe);
        }

        await using var unkeyed = MirrorRuntime.Start(IPAddress.Loopback, 9, new FrameSlot(), new RecordingDecoder(), null, ReadOnlySpan<byte>.Empty, NullAirPlayLog.Instance);
        using (var other = new TcpClient { NoDelay = true })
        {
            await other.ConnectAsync(IPAddress.Loopback, unkeyed.Activity.DataPort);
            await using var otherStream = other.GetStream();
            await otherStream.WriteAsync(Packet(0, 0, encrypted));
            await otherStream.FlushAsync();
            await WaitUntilAsync(() => unkeyed.Activity.PendingEncryptedPayloads == 1);
        }

        Assert.Equal(1, unkeyed.Activity.PendingEncryptedPayloads);
        Assert.Equal(0, unkeyed.Activity.DroppedEncryptedPayloads);
        Assert.False(unkeyed.Activity.MediaKeysReady);
        unkeyed.ArmForStream(content, secret, streamId);
        await WaitUntilAsync(() => unkeyed.Activity.PendingEncryptedPayloads == 0 && unkeyed.Activity.MediaKeysReady);
        Assert.Equal(0, unkeyed.Activity.DroppedEncryptedPayloads);
        Assert.Equal(1, unkeyed.Activity.AcceptedAccessUnits);
    }

    [Fact]
    public async Task TapClock_option_exports_a_live_frame_without_demo_frame()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var receiver = await AirPlayReceiver.StartAsync(new AirPlayReceiverOptions
        {
            Name = "Export",
            ListenAddress = IPAddress.Loopback,
            AirPlayPort = 0,
            RaopPort = 0,
            EnableDiscovery = false,
            ShowWindow = false,
            PublishDemoFrame = false,
            HardwareAddress = [0x02, 0x10, 0x20, 0x30, 0x40, 0x71],
            Decoder = new UnavailableH264Decoder(),
            TapClock = new TapClockExportOptions
            {
                Endpoint = "127.0.0.1:" + port,
                FramesPerSecond = 30,
            },
            Log = NullAirPlayLog.Instance,
        });

        Assert.False(receiver.Frames.TryRead(out _, out _, out _, out _, out _));
        const int width = 17;
        const int height = 13;
        var stride = (width * 4) + 12;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 9;
            pixels[i + 1] = 40;
            pixels[i + 2] = 210;
            pixels[i + 3] = 255;
        }

        Assert.True(receiver.Frames.Publish(width, height, pixels, stride) > 0);
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var jpeg = await ReadAtbfJpegAsync(stream);
        Assert.True(BaselineJpegEncoder.TryReadSize(jpeg, out var jpegWidth, out var jpegHeight));
        Assert.Equal(width, jpegWidth);
        Assert.Equal(height, jpegHeight);
        Assert.Equal(0, receiver.Mirror.DecodedFrames);
    }

    [Fact]
    public async Task Keyframe_before_codec_is_decoded_and_pushed_to_tapclock()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var frames = new FrameSlot();
        var decoder = new GatedDecoder();
        await using var mirror = MirrorRuntime.Start(IPAddress.Loopback, 9, frames, decoder, null, ReadOnlySpan<byte>.Empty, NullAirPlayLog.Instance);
        var content = new byte[16];
        content[2] = 5;
        var secret = new byte[32];
        secret[3] = 8;
        const ulong streamId = 19;
        mirror.ArmForStream(content, secret, streamId);
        var export = new TapClockExportOptions { Endpoint = "127.0.0.1:" + port, FramesPerSecond = 30 };
        await using var exporter = TapClockExporter.Start(export, frames, NullAirPlayLog.Instance, CancellationToken.None);

        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[16];
        StreamCipher.Derive(content, secret, streamId, key, iv);
        var access = new byte[] { 0, 0, 0, 1, 0x65 };
        var encrypted = new byte[access.Length];
        using (var ctr = new AesCtr(key, iv))
        {
            ctr.Transform(access, encrypted);
        }

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, mirror.Activity.DataPort);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Packet(0, 0x10, encrypted));
        await WaitUntilAsync(() => mirror.Activity.AcceptedAccessUnits == 1);
        Assert.Equal(0, mirror.Activity.DecodedFrames);
        Assert.False(frames.TryRead(out _, out _, out _, out _, out _));

        var codec = CodecPayload();
        var header = new byte[128];
        BitConverter.GetBytes(codec.Length).CopyTo(header, 0);
        header[4] = 1;
        await stream.WriteAsync(header);
        await stream.WriteAsync(codec);
        Assert.True(await exporter.WaitForDeliveryAsync(1, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, mirror.Activity.DecodedFrames);
        using var tap = await listener.AcceptTcpClientAsync();
        await using var tapStream = tap.GetStream();
        var jpeg = await ReadAtbfJpegAsync(tapStream);
        Assert.True(BaselineJpegEncoder.TryReadSize(jpeg, out var width, out var height));
        Assert.Equal(8, width);
        Assert.Equal(8, height);
    }

    [Fact]
    public async Task Late_fairplay_replays_held_ciphertext_into_tapclock_without_demo_frame()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var content = new byte[16];
        content[0] = 0x44;
        var ekey = new byte[72];
        content.AsSpan().CopyTo(ekey.AsSpan(56));
        var gate = new GateUnwrapper(content);
        await using var receiver = await AirPlayReceiver.StartAsync(new AirPlayReceiverOptions
        {
            Name = "Late",
            ListenAddress = IPAddress.Loopback,
            AirPlayPort = 0,
            RaopPort = 0,
            EnableDiscovery = false,
            ShowWindow = false,
            PublishDemoFrame = false,
            HardwareAddress = [0x02, 0x19, 0x28, 0x37, 0x46, 0x55],
            PairingSeed = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
            MediaKeyUnwrapper = gate,
            FairPlaySetup = new ScriptedFairPlay(),
            Decoder = new GatedDecoder(),
            TapClock = new TapClockExportOptions
            {
                Endpoint = "127.0.0.1:" + port,
                FramesPerSecond = 30,
            },
            Log = NullAirPlayLog.Instance,
        });
        Assert.False(receiver.Frames.TryRead(out _, out _, out _, out _, out _));

        using var control = new TcpClient();
        await control.ConnectAsync(IPAddress.Loopback, receiver.AirPlayEndPoint.Port);
        await using var conversation = new HttpConversation(control.GetStream());
        using var senderKey = PairingIdentity.Generate();
        using var sender = new PairingSender(senderKey);
        var setup = await conversation.ExchangeAsync(
            "POST /pair-setup HTTP/1.1\r\nCSeq: 1\r\nContent-Type: application/octet-stream",
            sender.CreateSetupRequest());
        sender.AcceptSetup(setup.Body);
        var verify = await conversation.ExchangeAsync(
            "POST /pair-verify HTTP/1.1\r\nCSeq: 2\r\nContent-Type: application/octet-stream",
            sender.CreateVerifyStart());
        await conversation.ExchangeAsync(
            "POST /pair-verify HTTP/1.1\r\nCSeq: 3",
            sender.AcceptVerifyStart(verify.Body));
        var secret = sender.SharedSecret.ToArray();
        Assert.Equal(32, secret.Length);

        const ulong streamId = 88;
        var session = new PlistDictionary();
        session.Add("eiv", new PlistData(new byte[16]));
        session.Add("ekey", new PlistData(ekey));
        session.Add("timingPort", new PlistInteger(46011));
        var first = await conversation.ExchangeAsync(
            "SETUP * RTSP/1.0\r\nCSeq: 4\r\nContent-Type: application/x-apple-binary-plist",
            BinaryPropertyList.Write(session));
        Assert.Equal(200, first.Status);
        var streamInfo = new PlistDictionary();
        streamInfo.Add("type", new PlistInteger(110));
        streamInfo.Add("streamConnectionID", new PlistInteger((long)streamId));
        var streams = new PlistArray();
        streams.Add(streamInfo);
        var secondBody = new PlistDictionary();
        secondBody.Add("streams", streams);
        var second = await conversation.ExchangeAsync(
            "SETUP * RTSP/1.0\r\nCSeq: 5\r\nContent-Type: application/x-apple-binary-plist",
            BinaryPropertyList.Write(secondBody));
        Assert.Equal(200, second.Status);
        Assert.False(receiver.Mirror.MediaKeysReady);

        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[16];
        StreamCipher.Derive(content, secret, streamId, key, iv);
        var access = new byte[] { 0, 0, 0, 1, 0x65 };
        var encrypted = new byte[access.Length];
        using (var ctr = new AesCtr(key, iv))
        {
            ctr.Transform(access, encrypted);
        }

        using var data = new TcpClient { NoDelay = true };
        await data.ConnectAsync(IPAddress.Loopback, receiver.Mirror.DataPort);
        await using var dataStream = data.GetStream();
        await dataStream.WriteAsync(Packet(0, 0x10, encrypted));
        await WaitUntilAsync(() => receiver.Mirror.PendingEncryptedPayloads == 1);
        Assert.Equal(0, receiver.Mirror.DroppedEncryptedPayloads);
        Assert.Equal(0, receiver.Mirror.DecodedFrames);

        gate.Open();
        var fairPlay = await conversation.ExchangeAsync("POST /fp-setup HTTP/1.1", [9, 8, 7]);
        Assert.Equal(200, fairPlay.Status);
        await WaitUntilAsync(() => receiver.Mirror.MediaKeysReady && receiver.Mirror.PendingEncryptedPayloads == 0);
        Assert.Equal(0, receiver.Mirror.DecodedFrames);

        var codec = CodecPayload();
        var header = new byte[128];
        BitConverter.GetBytes(codec.Length).CopyTo(header, 0);
        header[4] = 1;
        await dataStream.WriteAsync(header);
        await dataStream.WriteAsync(codec);
        await WaitUntilAsync(() => receiver.Mirror.DecodedFrames >= 1);

        using var tap = await listener.AcceptTcpClientAsync();
        await using var tapStream = tap.GetStream();
        var jpeg = await ReadAtbfJpegAsync(tapStream);
        Assert.True(BaselineJpegEncoder.TryReadSize(jpeg, out var width, out var height));
        Assert.Equal(8, width);
        Assert.Equal(8, height);
    }

    [Fact]
    public async Task Encrypted_hold_overflow_drops_the_stream_until_a_new_session()
    {
        await using var mirror = MirrorRuntime.Start(IPAddress.Loopback, 9, new FrameSlot(), new RecordingDecoder(), null, ReadOnlySpan<byte>.Empty, NullAirPlayLog.Instance);
        mirror.PendingEncryptedByteLimit = 20;
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, mirror.Activity.DataPort);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Packet(0, 0, new byte[16]));
        await WaitUntilAsync(() => mirror.Activity.PendingEncryptedPayloads == 1);
        await stream.WriteAsync(Packet(0, 0, new byte[16]));
        await WaitUntilAsync(() => mirror.Activity.DroppedEncryptedPayloads >= 1);
        Assert.Equal(0, mirror.Activity.PendingEncryptedPayloads);
        Assert.False(mirror.Activity.MediaKeysReady);
        await stream.WriteAsync(Packet(0, 0, new byte[4]));
        await WaitUntilAsync(() => mirror.Activity.DroppedEncryptedPayloads >= 2);
        Assert.Equal(0, mirror.Activity.DecodedFrames);
    }

    [Fact]
    public async Task TapClock_connect_failure_is_logged_before_a_frame_is_delivered()
    {
        var log = new ListLog();
        var slot = new FrameSlot();
        var options = new TapClockExportOptions { Endpoint = "127.0.0.1:1", FramesPerSecond = 20 };
        await using var exporter = TapClockExporter.Start(options, slot, log, CancellationToken.None);
        await WaitUntilAsync(() => log.Contains("TapClock connect to 127.0.0.1:1 failed"));
        slot.Publish(4, 4, new byte[64], 16);
        await Task.Delay(80);
        Assert.Equal(0, exporter.FramesDelivered);
    }

    [Fact]
    public void Jpeg_encoder_accepts_a_phone_sized_bgra_buffer()
    {
        const int width = 320;
        const int height = 180;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 17);
        }

        var started = Environment.TickCount64;
        var jpeg = BaselineJpegEncoder.EncodeBgra(pixels, width, height, width * 4, 80);
        var elapsed = Environment.TickCount64 - started;
        Assert.True(BaselineJpegEncoder.TryReadSize(jpeg, out var jpegWidth, out var jpegHeight));
        Assert.Equal(width, jpegWidth);
        Assert.Equal(height, jpegHeight);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpeg[^2..]);
        Assert.True(elapsed < 2000, "JPEG encode took " + elapsed + " ms.");
        var high = BaselineJpegEncoder.EncodeBgra(pixels, width, height, width * 4, 100);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, high[..2]);
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, high[^2..]);
    }

    [Fact]
    public async Task Setup_returns_mirror_ports_and_keeps_fairplay_closed()
    {
        var decoder = new RecordingDecoder();
        var lab = new byte[16];
        lab[3] = 4;
        await using var receiver = await AirPlayReceiver.StartAsync(new AirPlayReceiverOptions
        {
            Name = "Mirror",
            ListenAddress = IPAddress.Loopback,
            AirPlayPort = 0,
            RaopPort = 0,
            EnableDiscovery = false,
            HardwareAddress = [0x02, 0x10, 0x20, 0x30, 0x40, 0x50],
            PairingSeed = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
            LabContentKey = lab,
            Decoder = decoder,
            Log = NullAirPlayLog.Instance,
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.AirPlayEndPoint.Port);
        await using var conversation = new HttpConversation(client.GetStream());
        using var senderKey = PairingIdentity.Generate();
        using var sender = new PairingSender(senderKey);
        var setup = await conversation.ExchangeAsync(
            "POST /pair-setup HTTP/1.1\r\nCSeq: 1\r\nContent-Type: application/octet-stream",
            sender.CreateSetupRequest());
        sender.AcceptSetup(setup.Body);
        var verify = await conversation.ExchangeAsync(
            "POST /pair-verify HTTP/1.1\r\nCSeq: 2\r\nContent-Type: application/octet-stream",
            sender.CreateVerifyStart());
        await conversation.ExchangeAsync(
            "POST /pair-verify HTTP/1.1\r\nCSeq: 3",
            sender.AcceptVerifyStart(verify.Body));

        var session = new PlistDictionary();
        session.Add("eiv", new PlistData(new byte[16]));
        session.Add("ekey", new PlistData(new byte[72]));
        session.Add("timingPort", new PlistInteger(46000));
        session.Add("name", new PlistString("phone"));
        var first = await conversation.ExchangeAsync(
            "SETUP * RTSP/1.0\r\nCSeq: 4\r\nContent-Type: application/x-apple-binary-plist",
            BinaryPropertyList.Write(session));
        Assert.Equal(200, first.Status);
        Assert.False(receiver.Mirror.MediaKeysReady);

        var stream = new PlistDictionary();
        stream.Add("type", new PlistInteger(110));
        stream.Add("streamConnectionID", new PlistInteger(123456789));
        var streams = new PlistArray();
        streams.Add(stream);
        var secondBody = new PlistDictionary();
        secondBody.Add("streams", streams);
        var second = await conversation.ExchangeAsync(
            "SETUP * RTSP/1.0\r\nCSeq: 5\r\nContent-Type: application/x-apple-binary-plist",
            BinaryPropertyList.Write(secondBody));
        Assert.Equal(200, second.Status);
        Assert.Equal("application/x-apple-binary-plist", second.Headers["Content-Type"]);
        var body = Assert.IsType<PlistDictionary>(BinaryPropertyList.Read(second.Body));
        Assert.Equal(receiver.Mirror.DataPort, Assert.IsType<PlistInteger>(Assert.IsType<PlistDictionary>(Assert.IsType<PlistArray>(body.Require("streams")).Items[0]).Require("dataPort")).Value);
        Assert.Equal(receiver.Mirror.TimingPort, Assert.IsType<PlistInteger>(body.Require("timingPort")).Value);
        Assert.Equal(receiver.RaopEndPoint.Port, Assert.IsType<PlistInteger>(body.Require("eventPort")).Value);
        Assert.True(receiver.Mirror.MediaKeysReady);

        var record = await conversation.ExchangeAsync("RECORD * RTSP/1.0\r\nCSeq: 6\r\n\r\n");
        Assert.Equal(200, record.Status);
        Assert.Equal("11025", record.Headers["Audio-Latency"]);
        var parameter = await conversation.ExchangeAsync(
            "GET_PARAMETER * RTSP/1.0\r\nCSeq: 7\r\nContent-Type: text/parameters",
            "volume\r\n"u8.ToArray());
        Assert.Equal("volume: 0.0\r\n", Encoding.ASCII.GetString(parameter.Body));

        var audio = new PlistDictionary();
        var audioStream = new PlistDictionary();
        audioStream.Add("type", new PlistInteger(96));
        var audioStreams = new PlistArray();
        audioStreams.Add(audioStream);
        audio.Add("streams", audioStreams);
        var audioResponse = await conversation.ExchangeAsync(
            "SETUP * RTSP/1.0\r\nCSeq: 8\r\nContent-Type: application/x-apple-binary-plist",
            BinaryPropertyList.Write(audio));
        Assert.Equal(501, audioResponse.Status);

        var fairPlay = await conversation.ExchangeAsync("POST /fp-setup HTTP/1.1", new byte[16]);
        Assert.Equal(501, fairPlay.Status);
    }

    private static async Task<byte[]> ReadAtbfJpegAsync(NetworkStream stream)
    {
        var header = new byte[AtbfFraming.HeaderLength];
        await stream.ReadExactlyAsync(header);
        Assert.True(AtbfFraming.TryReadHeader(header, out var type, out var flags, out var length));
        Assert.Equal(AtbfFraming.TypeImage, type);
        Assert.Equal(AtbfFraming.FlagJpeg, flags);
        Assert.True(length > 4);
        var jpeg = new byte[length];
        await stream.ReadExactlyAsync(jpeg);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpeg[^2..]);
        return jpeg;
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

    private static byte[] Packet(byte type, byte flags, byte[] payload)
    {
        var packet = new byte[128 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(packet, 0);
        packet[4] = type;
        packet[5] = flags;
        payload.CopyTo(packet, 128);
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

    private sealed class RecordingDecoder : IH264Decoder
    {
        public TaskCompletionSource Decoded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public byte[] LastAnnexB { get; private set; } = [];

        public bool LastWasKeyframe { get; private set; }

        public bool IsAvailable => true;

        public string Name => "recording";

        public void Configure(ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet)
        {
        }

        public bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame)
        {
            LastAnnexB = annexB.ToArray();
            LastWasKeyframe = keyFrame;
            frame = default;
            Decoded.TrySetResult();
            return false;
        }
    }

    private sealed class GatedDecoder : IH264Decoder
    {
        private int _configured;

        public bool IsAvailable => true;

        public string Name => "gated";

        public void Configure(ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet)
        {
            Volatile.Write(ref _configured, 1);
        }

        public bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame)
        {
            if (Volatile.Read(ref _configured) == 0)
            {
                frame = default;
                return false;
            }

            var pixels = new byte[8 * 8 * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 2] = 0x3C;
                pixels[i + 3] = 255;
            }

            frame = new DecodedFrame(8, 8, pixels, 32);
            return true;
        }
    }

    private sealed class GateUnwrapper(byte[] key) : IMediaKeyUnwrapper
    {
        private int _open;

        public void Open() => Volatile.Write(ref _open, 1);

        public bool TryUnwrap(ReadOnlySpan<byte> wrappedKey, Span<byte> contentKey)
        {
            if (Volatile.Read(ref _open) == 0 || wrappedKey.Length != 72 || contentKey.Length < 16)
            {
                return false;
            }

            key.CopyTo(contentKey);
            return true;
        }
    }

    private sealed class ScriptedFairPlay : IFairPlaySetup
    {
        public bool TryRespond(ReadOnlySpan<byte> request, Span<byte> response, out int written)
        {
            written = 0;
            if (request.IsEmpty || response.Length < 4)
            {
                return false;
            }

            response[0] = 1;
            response[1] = 2;
            response[2] = 3;
            response[3] = 4;
            written = 4;
            return true;
        }
    }

    private sealed class ListLog : IAirPlayLog
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
                foreach (var line in _lines)
                {
                    if (line.Contains(text, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
