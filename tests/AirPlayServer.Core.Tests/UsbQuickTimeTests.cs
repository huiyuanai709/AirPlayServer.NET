using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AirPlayServer.Core.Audio;
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
    public void Driver_hint_separates_a_missing_library_from_a_claim_failure()
    {
        var missing = UsbDriverHint.LibraryMissing();
        Assert.Contains("libusb-1.0.dll", missing, StringComparison.Ordinal);
        Assert.Contains("windows-usb-driver.ps1", missing, StringComparison.Ordinal);
        Assert.Contains("-FetchLibusb", missing, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT_SUPPORTED", missing, StringComparison.Ordinal);

        var unsupported = UsbDriverHint.ForLibusb(-12, "set QuickTime USB configuration 4");
        Assert.Contains("libusb -12", unsupported, StringComparison.Ordinal);
        Assert.Contains("NOT_SUPPORTED", unsupported, StringComparison.Ordinal);
        Assert.Contains("usbccgp", unsupported, StringComparison.Ordinal);
        Assert.Contains("UpperFilter", unsupported, StringComparison.Ordinal);
        Assert.Contains("Do not run Zadig on the Apple composite parent", unsupported, StringComparison.Ordinal);
        Assert.Contains("0x2A", unsupported, StringComparison.Ordinal);

        var busy = UsbDriverHint.ForLibusb(-3, "claim QuickTime USB interface 2");
        Assert.Contains("ACCESS", busy, StringComparison.Ordinal);
        Assert.Contains("Do not run Zadig on the Apple composite parent", busy, StringComparison.Ordinal);
        Assert.Contains("libusb -6", UsbDriverHint.ForLibusb(-6, "claim"), StringComparison.Ordinal);
        Assert.Contains("Trust This Computer", UsbDriverHint.ForLibusb(-5, "open"), StringComparison.Ordinal);

        var io = UsbDriverHint.ForLibusb(-1, "open Apple USB device 05ac:12a8");
        Assert.Contains("libusb -1", io, StringComparison.Ordinal);
        Assert.Contains("LIBUSB_ERROR_IO", io, StringComparison.Ordinal);
        Assert.Contains("Do not uninstall Apple Devices", io, StringComparison.Ordinal);
        Assert.Contains("不要卸载 Apple Devices", io, StringComparison.Ordinal);

        var storeOnly = new UsbDriverSnapshot
        {
            IsWindows = true,
            LibraryLoaded = true,
            LibraryPath = "libusb-1.0.dll",
            AppleStoreStack = true,
            Libusb0ServicePresent = true,
            Libusb0DriverFilePresent = true,
            Parents =
            [
                new UsbParentBinding { InstanceId = "USB\\VID_05AC&PID_12A8\\phone", Service = "usbccgp", Libusb0UpperFilter = true },
            ],
        };
        Assert.True(storeOnly.AppleStackPresent);
        Assert.False(UsbDriverHint.NeedsAttention(storeOnly));
        var storeText = UsbDriverHint.Readiness(storeOnly);
        Assert.Contains("AppleInc.AppleDevices", storeText, StringComparison.Ordinal);
        Assert.Contains("AppleLowerFilter", storeText, StringComparison.Ordinal);
        Assert.Contains("Do not uninstall Apple Devices", storeText, StringComparison.Ordinal);
        Assert.DoesNotContain("was not found", storeText, StringComparison.Ordinal);

        var replaced = new UsbDriverSnapshot
        {
            IsWindows = true,
            LibraryLoaded = true,
            LibraryPath = "libusb-1.0.dll",
            AppleMobileDeviceService = true,
            Parents =
            [
                new UsbParentBinding { InstanceId = "USB\\VID_05AC&PID_12A8\\phone", Service = "WinUSB", Libusb0UpperFilter = false },
            ],
        };
        Assert.True(UsbDriverHint.NeedsAttention(replaced));
        var replacedText = UsbDriverHint.Readiness(replaced);
        Assert.Contains("replaces usbccgp", replacedText, StringComparison.Ordinal);
        Assert.Contains("Do not use Zadig on the Apple composite parent", replacedText, StringComparison.Ordinal);

        var ready = new UsbDriverSnapshot
        {
            IsWindows = true,
            LibraryLoaded = true,
            AppleMobileDeviceService = true,
            Libusb0ServicePresent = true,
            Libusb0DriverFilePresent = true,
            Parents =
            [
                new UsbParentBinding { InstanceId = "USB\\VID_05AC&PID_12A8\\phone", Service = "usbccgp", Libusb0UpperFilter = true },
            ],
        };
        Assert.False(UsbDriverHint.NeedsAttention(ready));
        Assert.False(UsbDriverHint.DriverRegisteredWithoutFile(ready));
        var readyText = UsbDriverHint.Readiness(ready);
        Assert.Contains("libusb0 UpperFilter", readyText, StringComparison.Ordinal);
        Assert.Contains("libusb0.sys is installed", readyText, StringComparison.Ordinal);
        Assert.DoesNotContain("Code 39", readyText, StringComparison.Ordinal);

        var filterWithoutFile = new UsbDriverSnapshot
        {
            IsWindows = true,
            LibraryLoaded = true,
            AppleMobileDeviceService = true,
            Libusb0ServicePresent = true,
            Libusb0DriverFilePresent = false,
            Parents =
            [
                new UsbParentBinding { InstanceId = "USB\\VID_05AC&PID_12A8\\phone", Service = "usbccgp", Libusb0UpperFilter = true },
            ],
        };
        Assert.True(UsbDriverHint.NeedsAttention(filterWithoutFile));
        Assert.True(UsbDriverHint.DriverRegisteredWithoutFile(filterWithoutFile));
        var broken = UsbDriverHint.Readiness(filterWithoutFile);
        Assert.Contains("libusb0.sys", broken, StringComparison.Ordinal);
        Assert.Contains("Code 39", broken, StringComparison.Ordinal);
        Assert.Contains("CM_PROB_DRIVER_FAILED_LOAD", broken, StringComparison.Ordinal);
        Assert.Contains("-Setup", broken, StringComparison.Ordinal);
        Assert.Contains(@"bin\amd64\libusb0.sys", broken, StringComparison.Ordinal);
        Assert.DoesNotContain("libusb0.sys is installed", broken, StringComparison.Ordinal);
        Assert.DoesNotContain("已有 libusb0 UpperFilter", broken, StringComparison.Ordinal);

        var serviceWithoutFile = new UsbDriverSnapshot
        {
            IsWindows = true,
            LibraryLoaded = true,
            AppleMobileDeviceService = true,
            Libusb0ServicePresent = true,
            Libusb0DriverFilePresent = false,
        };
        Assert.True(UsbDriverHint.NeedsAttention(serviceWithoutFile));
        Assert.Contains("Code 39", UsbDriverHint.Readiness(serviceWithoutFile), StringComparison.Ordinal);

        var filterMissing = new UsbDriverSnapshot
        {
            IsWindows = true,
            LibraryLoaded = true,
            AppleMobileDeviceService = true,
            Parents =
            [
                new UsbParentBinding { InstanceId = "USB\\VID_05AC&PID_12A8\\phone", Service = "usbccgp", Libusb0UpperFilter = false },
            ],
        };
        Assert.True(UsbDriverHint.NeedsAttention(filterMissing));
        Assert.False(UsbDriverHint.DriverRegisteredWithoutFile(filterMissing));
        var filterMissingText = UsbDriverHint.Readiness(filterMissing);
        Assert.Contains("Do not bind WinUSB to the parent", filterMissingText, StringComparison.Ordinal);
        Assert.DoesNotContain("Code 39", filterMissingText, StringComparison.Ordinal);
        Assert.Contains("-InstallFilter", filterMissingText, StringComparison.Ordinal);

        var linux = UsbDriverHint.Readiness(new UsbDriverSnapshot { LibraryLoaded = true, LibraryPath = "libusb-1.0.so.0" });
        Assert.Contains("No extra USB driver", linux, StringComparison.Ordinal);
        Assert.DoesNotContain("Zadig", linux, StringComparison.Ordinal);

        var script = File.ReadAllText(FindRepoFile("packaging", "windows-usb-driver.ps1"));
        Assert.Contains("usbccgp", script, StringComparison.Ordinal);
        Assert.Contains("Do not run Zadig", script, StringComparison.Ordinal);
        Assert.Contains("does not vendor libusb0.sys", script, StringComparison.Ordinal);
        Assert.Contains("--device-id=", script, StringComparison.Ordinal);
        Assert.Contains("-Setup", script, StringComparison.Ordinal);
        Assert.Contains("7fb1dfec805b97983763d7d0ae244320da12add1003d4249c96cc4d586398c79", script, StringComparison.Ordinal);
        Assert.Contains("00004c92cdb99be36e17fb2377165eb97e63b48ba895bfc04a642ea9c3e26d94", script, StringComparison.Ordinal);
        Assert.Contains("https://github.com/mcuee/libusb-win32/releases/download/release_1.4.0.2/libusb-win32-bin-1.4.0.2.zip", script, StringComparison.Ordinal);
        Assert.Contains(@"bin\amd64\libusb0.sys", script, StringComparison.Ordinal);
        Assert.Contains(@"bin\amd64\libusb0.dll", script, StringComparison.Ordinal);
        Assert.Contains(@"drivers\libusb0.sys", script, StringComparison.Ordinal);
        Assert.Contains("Code 39", script, StringComparison.Ordinal);
        Assert.Contains("CM_PROB_DRIVER_FAILED_LOAD", script, StringComparison.Ordinal);
        Assert.Contains("Test-Path", script, StringComparison.Ordinal);
        Assert.Contains("AppleInc.AppleDevices", script, StringComparison.Ordinal);
        Assert.Contains("AppleLowerFilter", script, StringComparison.Ordinal);
        Assert.Contains("AppleKmdfFilter", script, StringComparison.Ordinal);
        Assert.Contains("Do not uninstall Apple Devices", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--all-classes", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--device-classes", script, StringComparison.Ordinal);
        var setup = File.ReadAllText(FindRepoFile("packaging", "windows-usb-setup.cmd"));
        Assert.Contains("-Setup", setup, StringComparison.Ordinal);
        Assert.Contains("usbccgp", setup, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(FindRepoFile("packaging", "windows-usb-driver.ps1"))!, "*.sys"));
    }

    [Fact]
    public void Eat_lpcm_is_decoded_and_a_later_feed_still_demuxes()
    {
        var session = new QtSession(1920, 1200, () => 42);
        Assert.Equal(48000, session.AudioFormat.SampleRateHertz);
        Assert.Equal(2, session.AudioFormat.Channels);

        var afmt = SyncBody(QtMagic.Afmt, 99, Asbd(44100, channels: 1, flags: 0x4C, bits: 16, bytesPerFrame: 2));
        var reply = session.Accept(afmt);
        Assert.Equal(QtPackets.ErrorReply(99), Assert.Single(reply.Writes));
        Assert.Equal(44100, session.AudioFormat.SampleRateHertz);
        Assert.Equal(1, session.AudioFormat.Channels);
        Assert.Null(reply.Pcm);

        short[] pcm = [1, -2, 300];
        var eat = session.Accept(AsynBody(QtMagic.Eat, SampleBuffer(S16(pcm))));
        var chunk = Assert.IsType<QtPcmChunk>(eat.Pcm);
        Assert.Equal(pcm, chunk.Samples);
        Assert.Equal(1, chunk.Channels);
        Assert.Equal(44100, chunk.SampleRate);
        Assert.Null(eat.Video);
        Assert.Equal(1, session.AudioPackets);

        var feed = session.Accept(Body("qt-asyn-feed.bin"));
        Assert.True(feed.Video is { KeyFrame: true });
        Assert.Null(feed.Pcm);
    }

    [Fact]
    public void Eat_formats_cover_endian_planes_float_and_a_rejected_codec()
    {
        var stereo = QtAudioFormat.Default;
        Assert.True(QtPcm.TryDecode(S16([1, 2, 3, 4]), stereo, out var native));
        Assert.Equal(new short[] { 1, 2, 3, 4 }, native);

        var big = stereo with { FormatFlags = 0x0E };
        var swapped = new byte[] { 0x00, 0x01, 0xFF, 0xFE };
        Assert.True(QtPcm.TryDecode(swapped, big, out var wide));
        Assert.Equal(new short[] { 0x0001, -2 }, wide);

        var planar = stereo with { FormatFlags = 0x2C, BytesPerFrame = 2, BytesPerPacket = 2 };
        var planes = S16([10, 11, 20, 21]);
        Assert.True(QtPcm.TryDecode(planes, planar, out var interleaved));
        Assert.Equal(new short[] { 10, 20, 11, 21 }, interleaved);

        var floats = new QtAudioFormat
        {
            SampleRate = 48000,
            FormatId = QtAudioFormat.Lpcm,
            FormatFlags = 0x09,
            BytesPerPacket = 8,
            FramesPerPacket = 1,
            BytesPerFrame = 8,
            Channels = 2,
            BitsPerChannel = 32,
        };
        var floatBytes = new byte[8];
        BitConverter.TryWriteBytes(floatBytes.AsSpan(0), 0f);
        BitConverter.TryWriteBytes(floatBytes.AsSpan(4), 1f);
        Assert.True(QtPcm.TryDecode(floatBytes, floats, out var scaled));
        Assert.Equal(new short[] { 0, short.MaxValue }, scaled);

        var aac = stereo with { FormatId = 0x20636161 };
        Assert.False(aac.IsPlayable);
        var rejected = new QtSession(100, 100, () => 1);
        var ignored = rejected.Accept(SyncBody(QtMagic.Afmt, 7, Asbd(48000, 2, 0x0C, 16, 4, formatId: 0x20636161)));
        Assert.Equal(QtPackets.ErrorReply(7), Assert.Single(ignored.Writes));
        var noise = rejected.Accept(AsynBody(QtMagic.Eat, SampleBuffer(S16([9, 8]))));
        Assert.Null(noise.Pcm);
        Assert.True(noise.UnplayableAudio);
        var again = rejected.Accept(AsynBody(QtMagic.Eat, SampleBuffer(S16([9, 8]))));
        Assert.False(again.UnplayableAudio);
    }

    [Fact]
    public void Usb_pcm_player_writes_the_queue_and_drops_when_playback_blocks()
    {
        var played = new List<short>();
        var done = new ManualResetEventSlim(false);
        using var player = new UsbPcmPlayer(new ListLog(), (_, _) => new MemoryPcmOutput(played, 4, done));
        player.Submit(new QtPcmChunk([5, 6, 7, 8], 2, 48000));
        Assert.True(done.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(new short[] { 5, 6, 7, 8 }, played);

        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        using var blocked = new UsbPcmPlayer(new ListLog(), (_, _) => new GatePcmOutput(entered, release));
        blocked.Submit(new QtPcmChunk([1, 2], 2, 48000));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        for (var i = 0; i < UsbPcmPlayer.QueueCapacity + 3; i++)
        {
            blocked.Submit(new QtPcmChunk([(short)i, 0], 2, 48000));
        }

        Assert.True(blocked.Dropped >= 3);
        release.Set();
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

    [Fact]
    public void Mtp_only_apple_device_is_queued_for_quicktime_enable_and_mux_is_not()
    {
        Assert.True(UsbMirrorCandidate.ScanLogIntervalMs >= 15_000);
        var ptp = new UsbMirrorCandidate.Alt[] { new(0x06, 0x01, 0x01) };
        var ptpScan = new UsbMirrorCandidate.Scan(0x12A8, 1, 5, 4, 1, ptp, false);
        Assert.True(UsbMirrorCandidate.ShouldAttemptQuickTimeEnable(ptpScan));
        Assert.True(UsbMirrorCandidate.IsNormalModeIos(0x12A8));
        Assert.False(UsbMirrorCandidate.IsNormalModeIos(0x1227));
        Assert.False(UsbMirrorCandidate.IsNormalModeIos(0x1281));

        var audioOnly = new UsbMirrorCandidate.Scan(0x12A8, 1, 5, 4, 1, [new UsbMirrorCandidate.Alt(0x01, 0x01, 0x00)], false);
        Assert.True(UsbMirrorCandidate.ShouldAttemptQuickTimeEnable(audioOnly));

        var ptpOutsideRange = new UsbMirrorCandidate.Scan(0x0001, 1, 5, 1, 1, ptp, false);
        Assert.True(UsbMirrorCandidate.ShouldAttemptQuickTimeEnable(ptpOutsideRange));

        var unread = new UsbMirrorCandidate.Scan(0x12A8, 1, 5, 4, 0, [], false);
        Assert.False(UsbMirrorCandidate.ShouldAttemptQuickTimeEnable(unread));

        var mux = new UsbMirrorCandidate.Alt[] { new(0x06, 0x01, 0x01), new(0xFF, 0xFE, 0x02) };
        var muxScan = new UsbMirrorCandidate.Scan(0x12A8, 1, 5, 4, 4, mux, true);
        Assert.False(UsbMirrorCandidate.ShouldAttemptQuickTimeEnable(muxScan));
        Assert.True(UsbMirrorCandidate.IsMux(0xFF, 0xFE));
        Assert.False(UsbMirrorCandidate.ShouldAttemptQuickTimeEnable(muxScan with { SawMuxOrQuickTime = false }));

        var quickTime = new UsbMirrorCandidate.Scan(
            0x12A8,
            1,
            5,
            5,
            5,
            [new UsbMirrorCandidate.Alt(0xFF, 0x2A, 0xFF), new UsbMirrorCandidate.Alt(0x06, 0x01, 0x01)],
            true);
        Assert.False(UsbMirrorCandidate.ShouldAttemptQuickTimeEnable(quickTime));
        Assert.True(UsbMirrorCandidate.IsQuickTime(0xFF, 0x2A));
        Assert.True(UsbMirrorCandidate.NodeRank(quickTime: true, mux: true) > UsbMirrorCandidate.NodeRank(quickTime: false, mux: true));
        Assert.True(UsbMirrorCandidate.NodeRank(quickTime: false, mux: true) > UsbMirrorCandidate.NodeRank(quickTime: false, mux: false));
        Assert.False(UsbMirrorCandidate.ShouldAttemptQuickTimeEnable(quickTime with { SawMuxOrQuickTime = false }));

        var dfu = new UsbMirrorCandidate.Scan(0x1227, 1, 2, 1, 1, [new UsbMirrorCandidate.Alt(0xFE, 0x01, 0x00)], false);
        Assert.False(UsbMirrorCandidate.ShouldAttemptQuickTimeEnable(dfu));

        var skipped = UsbMirrorCandidate.ClassifySkipped(dfu);
        Assert.Contains("05ac:1227", skipped, StringComparison.Ordinal);
        Assert.Contains("0xFF", skipped, StringComparison.Ordinal);
        Assert.Contains("0x2A", skipped, StringComparison.Ordinal);
        Assert.Contains("0xFE", skipped, StringComparison.Ordinal);
        Assert.Contains("fe/01/00", skipped, StringComparison.Ordinal);
        Assert.Contains("没有", skipped, StringComparison.Ordinal);
        Assert.Contains("neither mux", skipped, StringComparison.Ordinal);

        var denied = UsbMirrorCandidate.OpenFailed(ptpScan, -3);
        Assert.Contains("libusb -3", denied, StringComparison.Ordinal);
        Assert.Contains("ACCESS", denied, StringComparison.Ordinal);
        Assert.Contains("06/01/01", denied, StringComparison.Ordinal);
        Assert.Contains("0x40/0x52", denied, StringComparison.Ordinal);
        Assert.Contains("没有发出", denied, StringComparison.Ordinal);

        var claimed = UsbMirrorCandidate.OpenFailed(muxScan, -12);
        Assert.Contains("libusb -12", claimed, StringComparison.Ordinal);
        Assert.Contains("NOT_SUPPORTED", claimed, StringComparison.Ordinal);
        Assert.Contains("已经有 mux", claimed, StringComparison.Ordinal);

        var pending = UsbMirrorCandidate.MtpEnable(ptpScan, "00008110-001A");
        Assert.Contains("0x40/0x52", pending, StringComparison.Ordinal);
        Assert.Contains("MTP", pending, StringComparison.Ordinal);
        Assert.Contains("控制中心", pending, StringComparison.Ordinal);
        Assert.Contains("Control Center", pending, StringComparison.Ordinal);
        Assert.Contains("1/4", pending, StringComparison.Ordinal);

        var empty = UsbMirrorCandidate.EmptyList(0, 0);
        Assert.Contains("--usb", empty, StringComparison.Ordinal);
        Assert.Contains("0", empty, StringComparison.Ordinal);
        Assert.Contains("MTP", empty, StringComparison.Ordinal);
        Assert.Contains("存储", empty, StringComparison.Ordinal);

        var noApple = UsbMirrorCandidate.EmptyList(3, 0);
        Assert.Contains("0x05AC", noApple, StringComparison.Ordinal);
        Assert.Contains("3", noApple, StringComparison.Ordinal);

        var skippedApple = UsbMirrorCandidate.EmptyList(4, 1);
        Assert.Contains("Apple VID", skippedApple, StringComparison.Ordinal);
        Assert.Contains("没有可镜像", skippedApple, StringComparison.Ordinal);

        var again = UsbMirrorCandidate.ReopenFailed("00008110-001A", -3);
        Assert.Contains("00008110-001A", again, StringComparison.Ordinal);
        Assert.Contains("libusb -3", again, StringComparison.Ordinal);
        Assert.Contains("没有发出", again, StringComparison.Ordinal);
        Assert.Contains("was not sent", UsbMirrorCandidate.ReopenFailed("phone", 0), StringComparison.Ordinal);
    }

    private static string FindRepoFile(string directory, string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(name);
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

    private static byte[] SyncBody(uint subtype, ulong correlation, byte[] payload)
    {
        var body = new byte[24 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, QtMagic.Sync);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), subtype);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(16), correlation);
        payload.CopyTo(body.AsSpan(24));
        return body;
    }

    private static byte[] AsynBody(uint subtype, byte[] payload)
    {
        var body = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, QtMagic.Asyn);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), subtype);
        payload.CopyTo(body.AsSpan(16));
        return body;
    }

    private static byte[] SampleBuffer(byte[] pcm)
    {
        var timing = Atom(0x6F707473, new byte[24]);
        var data = Atom(QtMagic.SampleData, pcm);
        return Atom(QtMagic.SampleBuffer, Join(timing, data));
    }

    private static byte[] Asbd(int rate, int channels, uint flags, int bits, int bytesPerFrame, uint formatId = QtAudioFormat.Lpcm)
    {
        var data = new byte[40];
        BitConverter.TryWriteBytes(data.AsSpan(0), (double)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), formatId);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), flags);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), bytesPerFrame);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), bytesPerFrame);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), channels);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(32), bits);
        return data;
    }

    private static byte[] S16(short[] samples)
    {
        var data = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);
        }

        return data;
    }

    private static byte[] Atom(uint magic, byte[] payload)
    {
        var atom = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(atom, atom.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(atom.AsSpan(4), magic);
        payload.CopyTo(atom.AsSpan(8));
        return atom;
    }

    private static byte[] Join(byte[] left, byte[] right)
    {
        var data = new byte[left.Length + right.Length];
        left.CopyTo(data, 0);
        right.CopyTo(data.AsSpan(left.Length));
        return data;
    }

    private sealed class MemoryPcmOutput : IPcmOutput
    {
        private readonly List<short> _samples;
        private readonly int _expect;
        private readonly ManualResetEventSlim _done;

        public MemoryPcmOutput(List<short> samples, int expect, ManualResetEventSlim done)
        {
            _samples = samples;
            _expect = expect;
            _done = done;
        }

        public bool IsOpen => true;

        public string Name => "memory";

        public void Write(ReadOnlySpan<short> interleaved, int channels, int sampleRate)
        {
            lock (_samples)
            {
                foreach (var sample in interleaved)
                {
                    _samples.Add(sample);
                }

                if (_samples.Count >= _expect)
                {
                    _done.Set();
                }
            }
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class GatePcmOutput : IPcmOutput
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;

        public GatePcmOutput(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public bool IsOpen => true;

        public string Name => "gate";

        public void Write(ReadOnlySpan<short> interleaved, int channels, int sampleRate)
        {
            _entered.Set();
            _release.Wait(TimeSpan.FromSeconds(2));
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
            _release.Set();
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
