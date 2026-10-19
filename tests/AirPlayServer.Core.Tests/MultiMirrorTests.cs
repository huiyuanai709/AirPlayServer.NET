using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlayServer.Core;
using AirPlayServer.Core.Crypto;
using AirPlayServer.Core.Export;
using AirPlayServer.Core.Media;
using AirPlayServer.Core.Notify;
using AirPlayServer.Core.Pairing;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class MultiMirrorTests
{
    [Fact]
    public void Hub_forwards_each_device_without_mixing_session_ids()
    {
        using var hub = new MirrorSessionHub();
        using var first = new MirrorSessionTracker();
        using var second = new MirrorSessionTracker();
        hub.Attach(first);
        hub.Attach(second);
        var events = new List<MirrorSessionEventArgs>();
        hub.Changed += (_, args) => events.Add(args);
        first.NoteSessionStarting("Phone A");
        second.NoteSessionStarting("Phone B");
        first.BeginData();
        second.BeginData();
        Assert.Equal(2, events.Count);
        Assert.Equal(MirrorSessionPhase.Connected, events[0].Phase);
        Assert.Equal(MirrorSessionPhase.Connected, events[1].Phase);
        Assert.Equal("Phone A", events[0].DeviceName);
        Assert.Equal("Phone B", events[1].DeviceName);
        Assert.NotEqual(events[0].SessionId, events[1].SessionId);
        Assert.False(string.IsNullOrEmpty(events[0].SessionId));
    }

    [Fact]
    public async Task Second_device_does_not_drop_the_first_and_teardown_is_isolated()
    {
        var lab = new byte[16];
        lab[0] = 4;
        var secretA = new byte[32];
        secretA[0] = 1;
        var secretB = new byte[32];
        secretB[0] = 2;
        var framesA = new FrameSlot();
        var framesB = new FrameSlot();
        var log = new ListLog();
        var directory = Path.Combine(Path.GetTempPath(), "airplay-multi-" + Guid.NewGuid().ToString("N"));
        await using var mirror = MirrorRuntime.Start(
            IPAddress.Loopback,
            9,
            framesA,
            new MarkDecoder(0x11),
            null,
            lab,
            log,
            maxMirrors: 4,
            extra: () => new MirrorEndpointResources
            {
                Frames = framesB,
                Decoder = new MarkDecoder(0x22),
            });
        mirror.SessionEvents.DisconnectDebounceMs = 0;
        var events = new List<MirrorSessionEventArgs>();
        mirror.SessionEvents.Changed += (_, args) =>
        {
            lock (events)
            {
                events.Add(args);
            }
        };

        var endpointA = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 7000);
        var endpointB = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 7000);
        using var pairingA = new PairingSession(PairingIdentity.Generate());
        using var pairingB = new PairingSession(PairingIdentity.Generate());
        mirror.NotePairingSecret(secretA, endpointA);
        mirror.NotePairingSecret(secretB, endpointB);
        Assert.Equal(200, mirror.HandleMedia(Request("SETUP", SessionBody("Phone A")), pairingA, endpointA).Status);
        Assert.Equal(200, mirror.HandleMedia(Request("SETUP", SessionBody("Phone B")), pairingB, endpointB).Status);
        var streamA = mirror.HandleMedia(Request("SETUP", StreamBody(11)), pairingA, endpointA);
        var streamB = mirror.HandleMedia(Request("SETUP", StreamBody(22)), pairingB, endpointB);
        Assert.Equal(200, streamA.Status);
        Assert.Equal(200, streamB.Status);
        var portA = DataPort(streamA);
        var portB = DataPort(streamB);
        Assert.NotEqual(portA, portB);
        Assert.Equal(mirror.Activity.DataPort, portA);
        Assert.NotSame(framesA.Buffers, framesB.Buffers);

        var idA = mirror.Sessions.GetSnapshot().SessionId;
        var pipeB = mirror.Snapshot().Single(pipe => pipe.Activity.DataPort == portB);
        var idB = pipeB.Sessions.GetSnapshot().SessionId;
        Assert.False(string.IsNullOrEmpty(idA));
        Assert.False(string.IsNullOrEmpty(idB));
        Assert.NotEqual(idA, idB);
        Assert.Equal("Phone A", mirror.Sessions.GetSnapshot().DeviceName);
        Assert.Equal("Phone B", pipeB.Sessions.GetSnapshot().DeviceName);
        Assert.Contains("不会被断开", log.Text, StringComparison.Ordinal);
        Assert.Contains("Existing sessions stay connected", log.Text, StringComparison.Ordinal);

        await using var exporter = TapClockExporter.Start(
            new TapClockExportOptions { WatchDirectory = directory, FramesPerSecond = 30 },
            mirror.ExportSources,
            log,
            CancellationToken.None);

        using var clientA = ConnectFrom("127.0.0.1", portA);
        using var clientB = ConnectFrom("127.0.0.2", portB);
        await clientA.GetStream().WriteAsync(VideoPacket(lab, secretA, 11, skip: 0));
        await clientB.GetStream().WriteAsync(VideoPacket(lab, secretB, 22, skip: 0));
        Assert.True(await WaitUntilAsync(() => framesA.Generation > 0 && framesB.Generation > 0));
        Assert.True(framesA.TryRead(out _, out _, out _, out var pixelsA, out _));
        Assert.True(framesB.TryRead(out _, out _, out _, out var pixelsB, out _));
        Assert.NotSame(pixelsA, pixelsB);
        Assert.Equal(0x11, pixelsA[0]);
        Assert.Equal(0x22, pixelsB[0]);
        Assert.True(await WaitUntilAsync(() => ConnectedIds(events).Count >= 2));
        var connected = ConnectedIds(events);
        Assert.Contains(idA, connected);
        Assert.Contains(idB, connected);

        var generationA = framesA.Generation;
        await clientA.GetStream().WriteAsync(VideoPacket(lab, secretA, 11, skip: 5));
        Assert.True(await WaitUntilAsync(() => framesA.Generation > generationA));
        Assert.True(framesB.TryRead(out _, out _, out _, out var stillB, out var generationB));
        Assert.Equal(0x22, stillB[0]);
        Assert.Equal(pixelsB, stillB);
        Assert.True(clientA.Connected);
        Assert.True(clientB.Connected);

        Assert.True(await exporter.WaitForDeliveryAsync(2, TimeSpan.FromSeconds(2)));
        var pathA = Path.GetFullPath(Path.Combine(directory, "mirror-127.0.0.1.jpg"));
        var pathB = Path.GetFullPath(Path.Combine(directory, "mirror-127.0.0.2.jpg"));
        Assert.True(File.Exists(pathA));
        Assert.True(File.Exists(pathB));
        Assert.False(File.Exists(Path.Combine(directory, "mirror.jpg")));
        Assert.True(log.Contains(pathA));
        Assert.True(log.Contains(pathB));
        Assert.NotEqual(await File.ReadAllBytesAsync(pathA), await File.ReadAllBytesAsync(pathB));

        Assert.Equal(200, mirror.HandleMedia(Request("TEARDOWN", []), pairingA, endpointA).Status);
        Assert.True(await WaitUntilAsync(() => HasDisconnect(events, idA)));
        Assert.False(HasDisconnect(events, idB));
        Assert.Equal(idB, pipeB.Sessions.GetSnapshot().SessionId);
        Assert.True(clientB.Connected);

        await clientB.GetStream().WriteAsync(VideoPacket(lab, secretB, 22, skip: 5));
        Assert.True(await WaitUntilAsync(() => framesB.Generation > generationB));
        Assert.True(framesB.TryRead(out _, out _, out _, out var after, out _));
        Assert.Equal(0x22, after[0]);
        Assert.False(HasDisconnect(events, idB));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Configured_max_above_eight_accepts_that_many_and_refuses_the_next()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MirrorRuntime.Start(
            IPAddress.Loopback,
            9,
            new FrameSlot(),
            new MarkDecoder(1),
            null,
            ReadOnlySpan<byte>.Empty,
            NullAirPlayLog.Instance,
            maxMirrors: 0));

        var log = new ListLog();
        await using var mirror = MirrorRuntime.Start(
            IPAddress.Loopback,
            9,
            new FrameSlot(),
            new MarkDecoder(1),
            null,
            ReadOnlySpan<byte>.Empty,
            log,
            maxMirrors: 20);
        using var pairing = new PairingSession(PairingIdentity.Generate());
        for (var n = 1; n <= 20; n++)
        {
            var remote = new IPEndPoint(IPAddress.Parse("10.20.0." + n.ToString(System.Globalization.CultureInfo.InvariantCulture)), 7000);
            var response = mirror.HandleMedia(Request("SETUP", SessionBody("Phone " + n.ToString(System.Globalization.CultureInfo.InvariantCulture))), pairing, remote);
            Assert.Equal(200, response.Status);
        }

        Assert.Equal(20, mirror.Snapshot().Length);
        var refused = mirror.HandleMedia(
            Request("SETUP", SessionBody("Extra")),
            pairing,
            new IPEndPoint(IPAddress.Parse("10.20.0.21"), 7000));
        Assert.Equal(503, refused.Status);
        Assert.Equal(20, mirror.Snapshot().Length);
        Assert.Contains("上限 20", log.Text, StringComparison.Ordinal);
        Assert.Contains("Concurrent mirror limit 20 reached", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mirror_limit_refuses_the_new_address_and_keeps_the_first()
    {
        var lab = new byte[16];
        lab[1] = 9;
        var secret = new byte[32];
        secret[1] = 3;
        var frames = new FrameSlot();
        var log = new ListLog();
        var opened = 0;
        await using var mirror = MirrorRuntime.Start(
            IPAddress.Loopback,
            9,
            frames,
            new MarkDecoder(0x44),
            null,
            lab,
            log,
            maxMirrors: 1,
            extra: () =>
            {
                Interlocked.Increment(ref opened);
                return new MirrorEndpointResources
                {
                    Frames = new FrameSlot(),
                    Decoder = new MarkDecoder(0x55),
                };
            });
        var first = new IPEndPoint(IPAddress.Parse("10.1.0.1"), 1);
        var second = new IPEndPoint(IPAddress.Parse("10.1.0.2"), 1);
        using var pairing = new PairingSession(PairingIdentity.Generate());
        mirror.NotePairingSecret(secret, first);
        Assert.Equal(200, mirror.HandleMedia(Request("SETUP", SessionBody("Only")), pairing, first).Status);
        var stream = mirror.HandleMedia(Request("SETUP", StreamBody(7)), pairing, first);
        Assert.Equal(200, stream.Status);
        var refused = mirror.HandleMedia(Request("SETUP", SessionBody("Extra")), pairing, second);
        Assert.Equal(503, refused.Status);
        Assert.Equal(0, opened);
        Assert.Contains("上限", log.Text, StringComparison.Ordinal);
        Assert.Contains("Existing sessions stay connected", log.Text, StringComparison.Ordinal);

        using var client = ConnectFrom("127.0.0.1", DataPort(stream));
        await client.GetStream().WriteAsync(VideoPacket(lab, secret, 7, skip: 0));
        Assert.True(await WaitUntilAsync(() => frames.Generation > 0));
        Assert.True(frames.TryRead(out _, out _, out _, out var pixels, out _));
        Assert.Equal(0x44, pixels[0]);
        Assert.Single(mirror.Snapshot());
    }

    [Fact]
    public async Task FairPlay_handshakes_stay_on_the_device_that_sent_them()
    {
        var primary = new CountingFairPlay();
        var secondary = new CountingFairPlay();
        await using var mirror = MirrorRuntime.Start(
            IPAddress.Loopback,
            9,
            new FrameSlot(),
            new MarkDecoder(1),
            primary,
            ReadOnlySpan<byte>.Empty,
            NullAirPlayLog.Instance,
            extra: () => new MirrorEndpointResources
            {
                Frames = new FrameSlot(),
                Decoder = new MarkDecoder(2),
                Unwrapper = secondary,
                FairPlay = secondary,
            },
            fairPlay: primary);
        var buffer = new byte[16];
        var body = new byte[16];
        body[0] = 9;
        Assert.True(mirror.TryRespondFairPlay(new IPEndPoint(IPAddress.Parse("10.8.0.1"), 2), body, buffer, primary, out var written, out var refused));
        Assert.False(refused);
        Assert.Equal(4, written);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, secondary.Calls);

        Assert.True(mirror.TryRespondFairPlay(new IPEndPoint(IPAddress.Parse("10.8.0.2"), 2), body, buffer, primary, out written, out refused));
        Assert.False(refused);
        Assert.Equal(4, written);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, secondary.Calls);
    }

    private static List<string?> ConnectedIds(List<MirrorSessionEventArgs> events)
    {
        lock (events)
        {
            return events.Where(item => item.Phase == MirrorSessionPhase.Connected).Select(item => item.SessionId).ToList();
        }
    }

    private static bool HasDisconnect(List<MirrorSessionEventArgs> events, string? sessionId)
    {
        lock (events)
        {
            return events.Any(item => item.Phase == MirrorSessionPhase.Disconnected && item.SessionId == sessionId);
        }
    }

    private static TcpClient ConnectFrom(string localAddress, int port)
    {
        var client = new TcpClient(new IPEndPoint(IPAddress.Parse(localAddress), 0))
        {
            NoDelay = true,
        };
        client.Connect(IPAddress.Loopback, port);
        return client;
    }

    private static int DataPort(Http1Response response)
    {
        var body = Assert.IsType<PlistDictionary>(BinaryPropertyList.Read(response.Body));
        var streams = Assert.IsType<PlistArray>(body.Require("streams"));
        var stream = Assert.IsType<PlistDictionary>(streams.Items[0]);
        return (int)Assert.IsType<PlistInteger>(stream.Require("dataPort")).Value;
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

    private static byte[] SessionBody(string name)
    {
        var session = new PlistDictionary();
        session.Add("eiv", new PlistData(new byte[16]));
        session.Add("ekey", new PlistData(new byte[16]));
        session.Add("timingPort", new PlistInteger(46000));
        session.Add("name", new PlistString(name));
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

    private static byte[] VideoPacket(byte[] content, byte[] secret, ulong streamId, int skip)
    {
        var access = new byte[] { 0, 0, 0, 1, 0x65 };
        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[16];
        StreamCipher.Derive(content, secret, streamId, key, iv);
        var encrypted = new byte[access.Length];
        using (var ctr = new AesCtr(key, iv))
        {
            if (skip > 0)
            {
                ctr.Skip(skip);
            }

            ctr.Transform(access, encrypted);
        }

        var packet = new byte[128 + encrypted.Length];
        BitConverter.GetBytes(encrypted.Length).CopyTo(packet, 0);
        packet[5] = 0x10;
        encrypted.CopyTo(packet, 128);
        return packet;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 2000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    private sealed class MarkDecoder(byte mark) : IH264Decoder
    {
        public bool IsAvailable => true;

        public string Name => "mark-" + mark.ToString("x2");

        public void Configure(ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet)
        {
        }

        public void Reset()
        {
        }

        public bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame)
        {
            var pixels = new byte[16];
            pixels[0] = mark;
            frame = new DecodedFrame(2, 2, pixels, 8);
            return true;
        }
    }

    private sealed class CountingFairPlay : IMediaKeyUnwrapper, IFairPlaySetup
    {
        public int Calls;

        public bool TryUnwrap(ReadOnlySpan<byte> wrappedKey, Span<byte> contentKey) => false;

        public bool TryRespond(ReadOnlySpan<byte> request, Span<byte> response, out int written)
        {
            Interlocked.Increment(ref Calls);
            written = 0;
            if (response.Length < 4)
            {
                return false;
            }

            response[0] = 1;
            written = 4;
            return true;
        }
    }

    private sealed class ListLog : IAirPlayLog
    {
        private readonly StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public void Log(AirPlayLogLevel level, string message)
        {
            lock (_text)
            {
                _text.AppendLine(message);
            }
        }

        public bool Contains(string text)
        {
            lock (_text)
            {
                return _text.ToString().Contains(text, StringComparison.Ordinal);
            }
        }
    }
}
