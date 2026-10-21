using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AirPlayServer.Core.Media;
using AirPlayServer.Core.Notify;
using AirPlayServer.Core.Pairing;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Core.Usb;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class UsbQuickTimeTests
{
    [Fact]
    public void Hpd1_and_hpa1_match_the_public_fixtures()
    {
        Assert.Equal(Read("qt-asyn-hpd1.bin"), QtPackets.Hpd1(1920, 1200));

        var hpa = Read("qt-asyn-hpa1.bin");
        var clock = BinaryPrimitives.ReadUInt64LittleEndian(hpa.AsSpan(8));
        Assert.Equal(hpa, QtPackets.Hpa1(clock));

        var afmt = Read("qt-afmt-reply.bin");
        var correlation = BinaryPrimitives.ReadUInt64LittleEndian(afmt.AsSpan(8));
        Assert.Equal(afmt, QtPackets.ErrorReply(correlation));

        var ping = QtPackets.Ping();
        Assert.Equal(16, ping.Length);
        Assert.Equal(QtMagic.Ping, BinaryPrimitives.ReadUInt32LittleEndian(ping.AsSpan(4)));
    }

    [Fact]
    public void Reassembler_splits_a_length_prefixed_feed()
    {
        var packet = Read("qt-asyn-feed.bin");
        var framer = new QtReassembler();
        var bodies = new List<byte[]>();
        for (var offset = 0; offset < packet.Length; offset += 1000)
        {
            var take = Math.Min(1000, packet.Length - offset);
            framer.Push(packet.AsSpan(offset, take), body => bodies.Add(body.ToArray()));
        }

        var body = Assert.Single(bodies);
        Assert.Equal(packet.AsSpan(4).ToArray(), body);
    }

    [Fact]
    public void Handshake_answers_cwpa_cvrp_and_demuxes_feed()
    {
        var session = new QtSession(1920, 1200, () => 42);
        var cwpa = session.Accept(Body("qt-cwpa-request.bin"));
        Assert.Equal(4, cwpa.Writes.Length);
        Assert.Equal(QtPackets.Hpd1(1920, 1200), cwpa.Writes[0]);
        Assert.Equal(cwpa.Writes[0], cwpa.Writes[1]);
        Assert.Equal(QtMagic.Reply, Magic(cwpa.Writes[2]));
        Assert.Equal(QtMagic.Asyn, Magic(cwpa.Writes[3]));
        Assert.Equal(QtMagic.Hpa1, BinaryPrimitives.ReadUInt32LittleEndian(cwpa.Writes[3].AsSpan(16)));

        var cvrp = session.Accept(Body("qt-cvrp-request.bin"));
        Assert.Equal(2, cvrp.Writes.Length);
        Assert.Equal(QtMagic.Need, BinaryPrimitives.ReadUInt32LittleEndian(cvrp.Writes[0].AsSpan(16)));
        Assert.Equal(QtMagic.Reply, Magic(cvrp.Writes[1]));
        Assert.Null(cvrp.Video);

        var feed = session.Accept(Body("qt-asyn-feed.bin"));
        var video = Assert.IsType<QtVideoUnit>(feed.Video);
        Assert.True(video.KeyFrame);
        Assert.Contains(feed.Writes, write => BinaryPrimitives.ReadUInt32LittleEndian(write.AsSpan(16)) == QtMagic.Need);
        Assert.True(HasNal(video.AnnexB, 5));
        Assert.NotNull(video.Sequence);
        Assert.NotNull(video.Picture);
        Assert.Equal(7, video.Sequence[0] & 0x1F);
        Assert.Equal(8, video.Picture[0] & 0x1F);
        Assert.Equal(0x27, video.Sequence[0]);
        Assert.Equal(0x28, video.Picture[0]);
    }

    [Fact]
    public void Cvrp_and_feed_expose_parameter_sets()
    {
        Assert.True(QtSampleBuffer.TryFindParameterSets(Body("qt-cvrp-request.bin"), out var sequence, out var picture));
        Assert.Equal(7, sequence[0] & 0x1F);
        Assert.Equal(8, picture[0] & 0x1F);

        var feedBody = Body("qt-asyn-feed.bin").ToArray();
        Assert.True(QtSampleBuffer.TryReadVideo(SampleAtom(feedBody), out var unit));
        Assert.True(unit.KeyFrame);
        Assert.True(HasNal(unit.AnnexB, 5));
        Assert.True(QtSampleBuffer.TryAnnexB(AvccOf(unit.AnnexB), out var roundTrip, out var key));
        Assert.True(key);
        Assert.Equal(unit.AnnexB, roundTrip);
    }

    [Fact]
    public void Serial_inserts_the_udid_dash_and_the_filter_matches_either_form()
    {
        Assert.Equal("01234567-89ABCDEF01234567", UsbSerial.Correct("0123456789ABCDEF01234567"));
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", UsbSerial.Correct("0123456789abcdef0123456789abcdef01234567"));
        Assert.True(UsbSerial.Matches("0123456789ABCDEF01234567", "01234567-89abcdef01234567"));
        Assert.False(UsbSerial.Matches("0123456789ABCDEF01234567", "ffffffffffffffff"));
        Assert.True(UsbSerial.Matches("anything", null));
    }

    [Fact]
    public void Same_phone_is_the_matching_name_and_other_phones_stay_open()
    {
        var policy = new MirrorDevicePolicy(allowSameDevice: false);
        Assert.True(policy.TryAdmitAirPlay("10.0.0.8", "Kitchen"));
        Assert.False(policy.TryAdmitUsb("USB-1", "kitchen"));
        Assert.True(policy.TryAdmitUsb("USB-2", "iPhone"));
        Assert.True(policy.TryAdmitUsb("USB-3", null));
        Assert.True(policy.TryAdmitAirPlay("10.0.0.9", "Desk"));

        var dual = new MirrorDevicePolicy(allowSameDevice: true);
        Assert.True(dual.TryAdmitAirPlay("10.0.0.8", "Kitchen"));
        Assert.True(dual.TryAdmitUsb("USB-1", "Kitchen"));
    }

    [Fact]
    public async Task Usb_hold_shares_the_mirror_budget_with_the_idle_airplay_slot()
    {
        await using var mirror = MirrorRuntime.Start(
            IPAddress.Loopback,
            9,
            new FrameSlot(),
            new RecordingDecoder(),
            null,
            ReadOnlySpan<byte>.Empty,
            NullAirPlayLog.Instance,
            maxMirrors: 1);
        Assert.False(mirror.TryHoldUsb());

        var log = new ListLog();
        await using var both = MirrorRuntime.Start(
            IPAddress.Loopback,
            9,
            new FrameSlot(),
            new RecordingDecoder(),
            null,
            ReadOnlySpan<byte>.Empty,
            log,
            maxMirrors: 2);
        Assert.True(both.TryHoldUsb());
        Assert.False(both.TryHoldUsb());
        using var pairing = new PairingSession(PairingIdentity.Generate());
        var first = both.HandleMedia(
            Request("SETUP", SessionBody("Phone A")),
            pairing,
            new IPEndPoint(IPAddress.Parse("10.40.0.1"), 7000));
        Assert.Equal(200, first.Status);
        var second = both.HandleMedia(
            Request("SETUP", SessionBody("Phone B")),
            pairing,
            new IPEndPoint(IPAddress.Parse("10.40.0.2"), 7000));
        Assert.Equal(503, second.Status);
        Assert.Contains("Concurrent mirror limit 2 reached", log.Text, StringComparison.Ordinal);
        both.ReleaseUsb();
        Assert.True(both.TryHoldUsb());
    }

    [Fact]
    public async Task Airplay_still_accepts_a_different_phone_when_a_usb_name_is_held()
    {
        var policy = new MirrorDevicePolicy(allowSameDevice: false);
        Assert.True(policy.TryAdmitUsb("USB-KITCHEN", "Kitchen"));
        await using var mirror = MirrorRuntime.Start(
            IPAddress.Loopback,
            9,
            new FrameSlot(),
            new RecordingDecoder(),
            null,
            ReadOnlySpan<byte>.Empty,
            NullAirPlayLog.Instance,
            devicePolicy: policy);
        using var pairing = new PairingSession(PairingIdentity.Generate());
        var blocked = mirror.HandleMedia(
            Request("SETUP", SessionBody("Kitchen")),
            pairing,
            new IPEndPoint(IPAddress.Parse("10.41.0.1"), 7000));
        Assert.Equal(503, blocked.Status);
        var allowed = mirror.HandleMedia(
            Request("SETUP", SessionBody("客厅的 iPhone")),
            pairing,
            new IPEndPoint(IPAddress.Parse("10.41.0.2"), 7000));
        Assert.Equal(200, allowed.Status);
    }

    [Fact]
    public async Task Dispatcher_keeps_the_tapclock_source_and_tags_usb_ingress()
    {
        using var air = new MirrorSessionTracker();
        air.DisconnectDebounceMs = 0;
        air.NoteSessionStarting("客厅的 iPhone");
        var airHandler = new CaptureHandler();
        using var airDispatcher = new SessionEventDispatcher(air, NotifyConfig(), handler: airHandler);
        air.BeginData(source: IPAddress.Parse("192.168.5.20"));
        await airHandler.WaitForAsync(2, TimeSpan.FromSeconds(2));
        var airTap = airHandler.Find("http://127.0.0.1:5088/api/integrations/airplay/events");
        var airHook = airHandler.Find("http://127.0.0.1:9/connected");
        Assert.Equal("airplay", Json(airTap).GetProperty("ingress").GetString());
        Assert.Equal("AirPlayServer.NET", Json(airTap).GetProperty("source").GetString());
        Assert.False(Json(airTap).TryGetProperty("device_id", out _));
        Assert.Equal("airplay", Json(airHook).GetProperty("ingress").GetString());
        Assert.False(Json(airHook).TryGetProperty("source", out _));

        using var usb = new MirrorSessionTracker();
        usb.DisconnectDebounceMs = 0;
        usb.NoteRoute(MirrorIngress.Usb, "00008110-001A2B3C4D5E6F70");
        usb.NoteSessionStarting("USB Phone");
        var usbHandler = new CaptureHandler();
        using var usbDispatcher = new SessionEventDispatcher(usb, NotifyConfig(), handler: usbHandler);
        usb.BeginData();
        await usbHandler.WaitForAsync(2, TimeSpan.FromSeconds(2));
        var usbTap = usbHandler.Find("http://127.0.0.1:5088/api/integrations/airplay/events");
        var usbHook = usbHandler.Find("http://127.0.0.1:9/connected");
        Assert.Equal("usb", Json(usbTap).GetProperty("ingress").GetString());
        Assert.Equal("AirPlayServer.NET", Json(usbTap).GetProperty("source").GetString());
        Assert.Equal("00008110-001A2B3C4D5E6F70", Json(usbTap).GetProperty("device_id").GetString());
        Assert.Equal("", Json(usbTap).GetProperty("source_ip").GetString());
        Assert.Equal("usb", Json(usbHook).GetProperty("ingress").GetString());
        Assert.Equal("00008110-001A2B3C4D5E6F70", Json(usbHook).GetProperty("device_id").GetString());
        Assert.False(Json(usbHook).TryGetProperty("source", out _));
    }

    [Fact]
    public async Task Two_usb_phones_publish_separate_sessions_beside_airplay()
    {
        var policy = new MirrorDevicePolicy(allowSameDevice: false);
        Assert.True(policy.TryAdmitAirPlay("10.0.0.8", "Kitchen"));
        var log = new ListLog();
        await using var mirror = MirrorRuntime.Start(
            IPAddress.Loopback,
            9,
            new FrameSlot(),
            new RecordingDecoder(),
            null,
            ReadOnlySpan<byte>.Empty,
            log,
            devicePolicy: policy);
        mirror.SessionEvents.DisconnectDebounceMs = 0;
        var events = new List<MirrorSessionEventArgs>();
        mirror.SessionEvents.Changed += (_, args) =>
        {
            lock (events)
            {
                events.Add(args);
            }
        };

        var feed = Read("qt-asyn-feed.bin");
        var desk = new ScriptChannel("00008110-DESK0001", feed) { Product = "Desk" };
        var kitchen = new ScriptChannel("00008110-KITCHEN1", feed) { Product = "Kitchen" };
        var catalog = new ScriptCatalog(
        [
            new UsbPhone { Id = kitchen.Id, Product = "Kitchen" },
            new UsbPhone { Id = desk.Id, Product = "Desk" },
        ],
        phone => phone.Id == desk.Id ? desk : kitchen);

        var decoders = new List<RecordingDecoder>();
        await using var usb = UsbQtIngress.Start(
            mirror,
            mirror.SessionEvents,
            policy,
            catalog,
            () =>
            {
                var decoder = new RecordingDecoder();
                lock (decoders)
                {
                    decoders.Add(decoder);
                }

                return new UsbMirrorResources { Frames = new FrameSlot(), Decoder = decoder };
            },
            1920,
            1200,
            null,
            log);

        Assert.True(await WaitUntilAsync(() =>
        {
            lock (events)
            {
                return events.Count(item => item.Phase == MirrorSessionPhase.Connected && item.Ingress == MirrorIngress.Usb) >= 1;
            }
        }));

        var sources = usb.ExportSources();
        Assert.Contains(sources, source => source.FileKey == "usb-" + desk.Id);
        Assert.DoesNotContain(sources, source => source.FileKey == "usb-" + kitchen.Id);
        desk.Release();
        kitchen.Release();

        Assert.True(await WaitUntilAsync(() =>
        {
            lock (events)
            {
                return events.Any(item => item.Phase == MirrorSessionPhase.Disconnected && item.DeviceId == desk.Id);
            }
        }));

        MirrorSessionEventArgs connected;
        lock (events)
        {
            connected = events.Single(item => item.Phase == MirrorSessionPhase.Connected && item.Ingress == MirrorIngress.Usb);
            Assert.DoesNotContain(events, item => item.DeviceId == kitchen.Id);
        }

        Assert.Equal(desk.Id, connected.DeviceId);
        Assert.Equal("Desk", connected.DeviceName);
        Assert.True(string.IsNullOrEmpty(connected.SourceIp));
        Assert.Equal(MirrorSessionReasons.MirrorData, connected.Reason);
        Assert.Contains("--allow-dual", log.Text, StringComparison.Ordinal);
        Assert.True(await WaitUntilAsync(() =>
        {
            lock (decoders)
            {
                return decoders.Any(decoder => decoder.AccessUnits.Any(unit => HasNal(unit, 5)));
            }
        }));
    }

    [Fact]
    public void Libusb_catalog_loads_without_a_phone()
    {
        var log = new ListLog();
        if (!LibUsbCatalog.TryCreate(log, out var catalog) || catalog is null)
        {
            Assert.Contains("libusb", log.Text, StringComparison.OrdinalIgnoreCase);
            return;
        }

        var phones = catalog.List();
        Assert.NotNull(phones);
        Assert.Contains("libusb", log.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] Read(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static ReadOnlySpan<byte> Body(string name)
    {
        var packet = Read(name);
        return packet.AsSpan(4);
    }

    private static ReadOnlySpan<byte> SampleAtom(ReadOnlySpan<byte> asynBody)
    {
        Assert.True(QtPackets.TryAsyn(asynBody, out _, out var subtype, out var rest));
        Assert.Equal(QtMagic.Feed, subtype);
        return rest;
    }

    private static uint Magic(byte[] packet)
        => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));

    private static bool HasNal(ReadOnlySpan<byte> annexB, int nalType)
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

    private static byte[] AvccOf(ReadOnlySpan<byte> annexB)
    {
        var units = new List<byte[]>();
        var index = 0;
        while (index + 4 < annexB.Length)
        {
            if (annexB[index] != 0 || annexB[index + 1] != 0 || annexB[index + 2] != 0 || annexB[index + 3] != 1)
            {
                index++;
                continue;
            }

            var start = index + 4;
            var next = start;
            while (next + 3 < annexB.Length && !(annexB[next] == 0 && annexB[next + 1] == 0 && annexB[next + 2] == 0 && annexB[next + 3] == 1))
            {
                next++;
            }

            if (next + 3 >= annexB.Length)
            {
                next = annexB.Length;
            }

            units.Add(annexB[start..next].ToArray());
            index = next;
        }

        var size = units.Sum(unit => 4 + unit.Length);
        var avcc = new byte[size];
        var offset = 0;
        foreach (var unit in units)
        {
            BinaryPrimitives.WriteInt32BigEndian(avcc.AsSpan(offset), unit.Length);
            unit.CopyTo(avcc.AsSpan(offset + 4));
            offset += 4 + unit.Length;
        }

        return avcc;
    }

    private static SessionNotifyConfig NotifyConfig()
        => new()
        {
            WebhooksEnabled = true,
            ConnectUrl = "http://127.0.0.1:9/connected",
            TapClockEventsEnabled = true,
            TapClockBaseUrl = "http://127.0.0.1:5088",
            DisconnectDebounceMs = 0,
        };

    private static JsonElement Json(CapturedRequest request)
    {
        using var document = JsonDocument.Parse(request.Body);
        return document.RootElement.Clone();
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

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 4000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            await Task.Delay(15);
        }

        return true;
    }

    private sealed class ScriptCatalog(IReadOnlyList<UsbPhone> phones, Func<UsbPhone, IUsbBulkChannel?> open) : IUsbDeviceCatalog
    {
        public IReadOnlyList<UsbPhone> List() => phones;

        public IUsbBulkChannel? TryOpen(UsbPhone phone, CancellationToken cancellationToken) => open(phone);
    }

    private sealed class ScriptChannel : IUsbBulkChannel
    {
        private readonly byte[] _payload;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _offset;

        public ScriptChannel(string id, byte[] payload)
        {
            Id = id;
            _payload = payload;
        }

        public string Id { get; }

        public string? Product { get; init; }

        public void Release() => _release.TrySetResult();

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_offset < _payload.Length)
            {
                var count = Math.Min(buffer.Length, _payload.Length - _offset);
                _payload.AsSpan(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            if (!_release.Task.IsCompleted && !_release.Task.Wait(Math.Max(1, timeoutMs)))
            {
                return 0;
            }

            return -1;
        }

        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public void Dispose() => Release();
    }

    private sealed class RecordingDecoder : IH264Decoder
    {
        public List<byte[]> AccessUnits { get; } = [];

        public bool IsAvailable => true;

        public string Name => "recording";

        public void Configure(ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet)
        {
        }

        public void Reset()
        {
        }

        public bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame)
        {
            lock (AccessUnits)
            {
                AccessUnits.Add(annexB.ToArray());
            }

            frame = new DecodedFrame(2, 2, new byte[16], 8);
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
    }

    private sealed class CapturedRequest
    {
        public required Uri Uri { get; init; }

        public required string Body { get; init; }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly List<CapturedRequest> _requests = [];

        public CapturedRequest Find(string uri)
        {
            lock (_requests)
            {
                return _requests.First(item => item.Uri.AbsoluteUri.TrimEnd('/') == uri.TrimEnd('/'));
            }
        }

        public async Task WaitForAsync(int count, TimeSpan timeout)
        {
            var deadline = Environment.TickCount64 + timeout.TotalMilliseconds;
            while (Count < count)
            {
                if (Environment.TickCount64 > deadline)
                {
                    throw new TimeoutException("Saw " + Count + " requests.");
                }

                await Task.Delay(10);
            }
        }

        private int Count
        {
            get
            {
                lock (_requests)
                {
                    return _requests.Count;
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_requests)
            {
                _requests.Add(new CapturedRequest { Uri = request.RequestUri!, Body = body });
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
    }
}
