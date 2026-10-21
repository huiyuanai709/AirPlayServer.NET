using System.Net;
using System.Net.Sockets;
using AirPlayServer.Core.Discovery;
using AirPlayServer.Core.Export;
using AirPlayServer.Core.Media;
using AirPlayServer.Core.Notify;
using AirPlayServer.Core.Pairing;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Core.Usb;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core;

public sealed class AirPlayReceiver : IAsyncDisposable
{
    private readonly PairingIdentity _identity;
    private readonly ControlServer _control;
    private readonly MdnsAnnouncer? _announcer;
    private readonly MirrorRuntime _mirror;
    private readonly UsbQtIngress? _usb;
    private readonly TapClockExporter? _tapClock;
    private readonly IAsyncDisposable? _windows;
    private readonly NativeMediaKeyModule? _plugin;
    private readonly MirrorClickHub _clicks;
    private readonly int _displayWidth;
    private readonly int _displayHeight;
    private int _disposed;

    private AirPlayReceiver(
        PairingIdentity identity,
        ControlServer control,
        MdnsAnnouncer? announcer,
        MirrorRuntime mirror,
        UsbQtIngress? usb,
        TapClockExporter? tapClock,
        IAsyncDisposable? windows,
        NativeMediaKeyModule? plugin,
        MirrorClickHub clicks,
        FrameSlot frames,
        string deviceId,
        string name,
        bool discoveryReceiving,
        int displayWidth,
        int displayHeight,
        bool advertisesHevc,
        string hevcDecoderName)
    {
        _identity = identity;
        _control = control;
        _announcer = announcer;
        _mirror = mirror;
        _usb = usb;
        _tapClock = tapClock;
        _windows = windows;
        _plugin = plugin;
        _clicks = clicks;
        Frames = frames;
        DeviceId = deviceId;
        Name = name;
        DiscoveryReceiving = discoveryReceiving;
        _displayWidth = displayWidth;
        _displayHeight = displayHeight;
        AdvertisesHevc = advertisesHevc;
        HevcDecoderName = hevcDecoderName;
        _control.SessionEstablished += OnSessionEstablished;
    }

    /// <summary>
    /// True when mDNS and <c>/info</c> include Features bit 42. This stays false
    /// when <c>--h265</c> was set but no HEVC decoder opened.
    /// </summary>
    public bool AdvertisesHevc { get; }

    public string HevcDecoderName { get; }

    public string Name { get; }

    public string DeviceId { get; }

    public IPEndPoint AirPlayEndPoint => _control.AirPlayEndPoint;

    public IPEndPoint RaopEndPoint => _control.RaopEndPoint;

    public bool DiscoveryReceiving { get; }

    public ReadOnlyMemory<byte> PublicKey => _identity.PublicKey;

    public FrameSlot Frames { get; }

    public MirrorActivity Mirror => _mirror.Activity;

    /// <summary>
    /// Mirror data session. <see cref="MirrorSessionTracker.GetSnapshot"/> stays
    /// connected through the disconnect debounce. Shutdown does not emit disconnected.
    /// </summary>
    public MirrorSessionTracker Sessions => _mirror.Sessions;

    /// <summary>
    /// Connect and disconnect events for every concurrent mirror. Each device
    /// keeps its own <c>session_id</c>. Shutdown does not emit disconnected.
    /// </summary>
    public MirrorSessionHub SessionEvents => _mirror.SessionEvents;

    /// <summary>
    /// Pointer presses, drags, and releases from each mirror window. The host
    /// publishes them to the configured webhook list. They are not sent to the phone.
    /// </summary>
    public MirrorClickHub Clicks => _clicks;

    public event EventHandler<AirPlaySessionEstablishedEventArgs>? SessionEstablished;

    public static async Task<AirPlayReceiver> StartAsync(AirPlayReceiverOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var log = options.Log ?? NullAirPlayLog.Instance;
        var identity = options.PairingSeed is null
            ? PairingIdentity.Generate()
            : PairingIdentity.FromSeed(options.PairingSeed);
        Socket? airPlay = null;
        Socket? raop = null;
        ControlServer? control = null;
        MirrorRuntime? mirror = null;
        UsbQtIngress? usb = null;
        TapClockExporter? tapClock = null;
        MirrorWindowGroup? windows = null;
        NativeMediaKeyModule? plugin = null;
        IH264Decoder? decoder = null;
        IHevcDecoder? hevc = null;
        try
        {
            airPlay = ControlServer.Bind(options.ListenAddress, options.AirPlayPort);
            raop = ControlServer.Bind(options.ListenAddress, options.RaopPort);
            var airPlayPort = (ushort)((IPEndPoint)airPlay.LocalEndPoint!).Port;
            var raopPort = (ushort)((IPEndPoint)raop.LocalEndPoint!).Port;
            var hardware = options.HardwareAddress ?? LocalNetwork.SelectHardwareAddress(
                LocalNetwork.SelectAdvertisementAddress(options.ListenAddress),
                options.Name);
            var instance = ReceiverNames.InstanceLabel(options.Name);
            var buffers = BgraBufferPool.CreateShared();
            var frames = new FrameSlot(buffers);
            log.Log(
                AirPlayLogLevel.Information,
                "画面缓冲在各路镜像之间复用（按容量分档，空闲数组按档限流，未租出的画面不会按路各留一块）。"
                + " BGRA frame buffers are shared across mirror sessions in size tiers. Idle arrays are capped per tier instead of keeping a spare full frame for every phone.");
            decoder = options.Decoder ?? H264Decoders.Create(log, load: null, buffers);
            if (options.AcceptHevc)
            {
                hevc = options.HevcDecoder ?? HevcDecoders.Create(log, load: null, buffers);
            }

            var advertisesHevc = HevcDecoders.ShouldAdvertise(options.AcceptHevc, hevc, log);
            var advertisement = new ReceiverAdvertisement
            {
                InstanceName = instance,
                HostLabel = ReceiverNames.HostLabel(options.Name),
                DeviceId = ReceiverNames.FormatDeviceId(hardware),
                RaopInstance = ReceiverNames.RaopInstance(hardware, instance),
                AirPlayPort = airPlayPort,
                RaopPort = raopPort,
                Address = options.EnableDiscovery ? LocalNetwork.SelectAdvertisementAddress(options.ListenAddress) : null,
                PublicKeyBase64 = identity.PublicKeyBase64,
                // Advertised mirror size only. TapClockLogicalScreen is UIKit points and stays off /info.
                DisplayWidth = options.DisplayWidth,
                DisplayHeight = options.DisplayHeight,
                MaxFramesPerSecond = options.MaxFramesPerSecond,
                AcceptHevc = advertisesHevc,
            };
            if (!string.IsNullOrWhiteSpace(options.MediaKeyPluginPath))
            {
                if (!NativeMediaKeyModule.TryLoad(options.MediaKeyPluginPath, out plugin, out var pluginError))
                {
                    throw new IOException(pluginError);
                }
            }

            var unwrapper = options.MediaKeyUnwrapper ?? (plugin is { HasUnwrap: true } ? plugin : null);
            var fairPlay = options.FairPlaySetup ?? (plugin is { HasFairPlay: true } ? plugin : null);
            var separateFairPlay = fairPlay is FairPlayKeys && ReferenceEquals(fairPlay, unwrapper);
            ReadOnlySpan<byte> labKey = options.LabContentKey is null ? [] : options.LabContentKey;
            ArgumentNullException.ThrowIfNull(decoder);
            var devicePolicy = new MirrorDevicePolicy(options.AllowDualIngress);
            mirror = MirrorRuntime.Start(
                options.ListenAddress,
                raopPort,
                frames,
                decoder,
                unwrapper,
                labKey,
                log,
                advertisesHevc,
                hevc,
                options.MaxMirrors,
                () => CreateExtraMirror(log, advertisesHevc, separateFairPlay, unwrapper, fairPlay, buffers),
                fairPlay,
                options.DecodeTiming,
                devicePolicy);
            if (options.DecodeTiming != DecodeTimingMode.Off)
            {
                log.Log(AirPlayLogLevel.Information, DecodeTiming.Describe(options.DecodeTiming));
            }
            if (options.PublishDemoFrame)
            {
                DemoFrame.Publish(frames, options.DisplayWidth, options.DisplayHeight);
            }

            var logicalWidth = options.TapClockLogicalScreen?.Width ?? 0;
            var logicalHeight = options.TapClockLogicalScreen?.Height ?? 0;
            var windowGroup = new MirrorWindowGroup();
            windows = windowGroup;
            var clicks = new MirrorClickHub();
            if (options.EnableUsb && LibUsbCatalog.TryCreate(log, out var usbCatalog) && usbCatalog is not null)
            {
                var runtime = mirror;
                usb = UsbQtIngress.Start(
                    runtime,
                    runtime.SessionEvents,
                    devicePolicy,
                    usbCatalog,
                    () => new UsbMirrorResources
                    {
                        Frames = new FrameSlot(buffers),
                        Decoder = H264Decoders.Create(log, load: null, buffers),
                    },
                    options.DisplayWidth,
                    options.DisplayHeight,
                    options.UsbUdid,
                    log,
                    session =>
                    {
                        if (!options.ShowWindow)
                        {
                            return;
                        }

                        var title = string.IsNullOrWhiteSpace(session.DeviceName) ? "USB " + session.Id : session.DeviceName;
                        windowGroup.Add(session, OpenSurface(session.Frames, title, gesture =>
                        {
                            var snapshot = session.Sessions.GetSnapshot();
                            clicks.Publish(new MirrorClickEventArgs
                            {
                                SessionId = snapshot.SessionId,
                                DeviceName = session.DeviceName,
                                Ingress = MirrorIngress.Usb,
                                Gesture = gesture,
                            });
                        }));
                    },
                    windowGroup.Close);
                log.Log(
                    AirPlayLogLevel.Information,
                    string.IsNullOrWhiteSpace(options.UsbUdid)
                        ? "有线镜像已开启，和无线 AirPlay 同时收。每台 USB 手机单独一路。 Wired mirroring is on and runs beside wireless AirPlay. Each USB phone is its own session."
                        : "有线镜像已开启，只收 UDID " + options.UsbUdid + "。 Wired mirroring is on, limited to UDID " + options.UsbUdid + ".");
            }

            if (options.TapClock is { Enabled: true })
            {
                var runtime = mirror;
                var wired = usb;
                tapClock = TapClockExporter.Start(options.TapClock, () => CombineSources(runtime, wired), log, CancellationToken.None);
            }

            if (options.ShowWindow && options.OpenMirrorSurface is null)
            {
                log.Log(AirPlayLogLevel.Information, "没有接上镜像窗口，画面仍可导出。No mirror window was attached. Frames can still be exported.");
            }

            IMirrorSurface? OpenSurface(FrameSlot slot, string title, Action<MirrorPointerGesture> report)
            {
                if (!options.ShowWindow || options.OpenMirrorSurface is null)
                {
                    return null;
                }

                try
                {
                    return options.OpenMirrorSurface(new MirrorSurfaceRequest
                    {
                        Frames = slot,
                        Title = title,
                        Log = log,
                        TapClockScreenWidth = logicalWidth,
                        TapClockScreenHeight = logicalHeight,
                        CancellationToken = cancellationToken,
                        ReportPointer = report,
                    });
                }
                catch (Exception ex)
                {
                    log.Log(AirPlayLogLevel.Warning, "镜像窗口没有打开: " + ex.Message + " The mirror window was not opened: " + ex.Message);
                    return null;
                }
            }

            if (options.ShowWindow)
            {
                var primary = mirror.Primary;
                windowGroup.Add(primary, OpenSurface(frames, instance, gesture =>
                {
                    var snapshot = primary.Sessions.GetSnapshot();
                    clicks.Publish(new MirrorClickEventArgs
                    {
                        SessionId = snapshot.SessionId,
                        DeviceName = primary.DeviceName,
                        SourceIp = primary.SourceAddress?.ToString(),
                        Ingress = MirrorIngress.AirPlay,
                        Gesture = gesture,
                    });
                }));
            }

            mirror.PipeAdded += pipe =>
            {
                if (!options.ShowWindow)
                {
                    return;
                }

                var title = string.IsNullOrWhiteSpace(pipe.DeviceName) ? "镜像 " + pipe.Activity.DataPort : pipe.DeviceName;
                windowGroup.Add(pipe, OpenSurface(pipe.Frames, title, gesture =>
                {
                    var snapshot = pipe.Sessions.GetSnapshot();
                    clicks.Publish(new MirrorClickEventArgs
                    {
                        SessionId = snapshot.SessionId,
                        DeviceName = pipe.DeviceName,
                        SourceIp = pipe.SourceAddress?.ToString(),
                        Ingress = MirrorIngress.AirPlay,
                        Gesture = gesture,
                    });
                }));
            };
            mirror.DeviceNamed += windowGroup.Rename;
            control = ControlServer.Start(
                airPlay,
                raop,
                advertisement,
                identity,
                options.EnableDiscovery,
                log,
                mirror,
                options.MaxConnections,
                fairPlay);
            airPlay = null;
            raop = null;
            MdnsAnnouncer? announcer = null;
            var receiving = false;
            if (options.EnableDiscovery)
            {
                announcer = MdnsAnnouncer.Start(
                    advertisement,
                    log,
                    () => LocalNetwork.SelectAdvertisedNics(options.ListenAddress));
                receiving = announcer.IsReceivingQueries;
            }

            var receiver = new AirPlayReceiver(
                identity,
                control,
                announcer,
                mirror,
                usb,
                tapClock,
                windows,
                plugin,
                clicks,
                frames,
                advertisement.DeviceId,
                instance,
                receiving,
                options.DisplayWidth,
                options.DisplayHeight,
                advertisesHevc,
                hevc?.Name ?? "unavailable");
            mirror = null;
            usb = null;
            tapClock = null;
            windows = null;
            plugin = null;
            return receiver;
        }
        catch
        {
            if (mirror is null)
            {
                (decoder as IDisposable)?.Dispose();
                if (hevc is IDisposable hevcDisposable && !ReferenceEquals(hevcDisposable, decoder))
                {
                    hevcDisposable.Dispose();
                }
            }

            if (usb is not null)
            {
                await usb.DisposeAsync().ConfigureAwait(false);
            }

            if (windows is not null)
            {
                await windows.DisposeAsync().ConfigureAwait(false);
            }

            if (tapClock is not null)
            {
                await tapClock.DisposeAsync().ConfigureAwait(false);
            }

            if (control is not null)
            {
                await control.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                airPlay?.Dispose();
                raop?.Dispose();
            }

            if (mirror is not null)
            {
                await mirror.DisposeAsync().ConfigureAwait(false);
            }

            plugin?.Dispose();
            identity.Dispose();
            throw;
        }
    }

    public long PublishDemoFrame() => DemoFrame.Publish(Frames, _displayWidth, _displayHeight);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _control.SessionEstablished -= OnSessionEstablished;
        if (_usb is not null)
        {
            await _usb.DisposeAsync().ConfigureAwait(false);
        }

        if (_windows is not null)
        {
            await _windows.DisposeAsync().ConfigureAwait(false);
        }

        if (_tapClock is not null)
        {
            await _tapClock.DisposeAsync().ConfigureAwait(false);
        }

        await _mirror.DisposeAsync().ConfigureAwait(false);
        await _control.DisposeAsync().ConfigureAwait(false);
        _plugin?.Dispose();
        if (_announcer is not null)
        {
            await _announcer.DisposeAsync().ConfigureAwait(false);
        }

        _identity.Dispose();
    }

    private static TapClockExportSource[] CombineSources(MirrorRuntime mirror, UsbQtIngress? usb)
    {
        var wireless = mirror.ExportSources();
        if (usb is null)
        {
            return wireless;
        }

        var wired = usb.ExportSources();
        if (wired.Length == 0)
        {
            return wireless;
        }

        var combined = new TapClockExportSource[wireless.Length + wired.Length];
        wireless.CopyTo(combined, 0);
        wired.CopyTo(combined, wireless.Length);
        return combined;
    }

    private void OnSessionEstablished(object? sender, AirPlaySessionEstablishedEventArgs args)
        => SessionEstablished?.Invoke(this, args);

    private static MirrorEndpointResources CreateExtraMirror(
        IAirPlayLog log,
        bool acceptHevc,
        bool separateFairPlay,
        IMediaKeyUnwrapper? unwrapper,
        IFairPlaySetup? fairPlay,
        BgraBufferPool buffers)
    {
        var frames = new FrameSlot(buffers);
        var decoder = H264Decoders.Create(log, load: null, buffers);
        IHevcDecoder? hevc = acceptHevc ? HevcDecoders.Create(log, load: null, buffers) : null;
        FairPlayKeys? keys = separateFairPlay ? new FairPlayKeys() : null;
        return new MirrorEndpointResources
        {
            Frames = frames,
            Decoder = decoder,
            Hevc = hevc,
            Unwrapper = keys ?? unwrapper,
            FairPlay = keys ?? fairPlay,
        };
    }

    private sealed class MirrorWindowGroup : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<IMirrorSurface> _open = [];
        private readonly Dictionary<object, IMirrorSurface> _byKey = new();
        private int _disposed;

        public void Add(object key, IMirrorSurface? window)
        {
            if (window is null)
            {
                return;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                _ = window.DisposeAsync();
                return;
            }

            lock (_gate)
            {
                _open.Add(window);
                _byKey[key] = window;
            }
        }

        public void Close(object key)
        {
            IMirrorSurface? window;
            lock (_gate)
            {
                if (!_byKey.Remove(key, out window))
                {
                    return;
                }

                _open.Remove(window);
            }

            _ = window.DisposeAsync();
        }

        public void Rename(MirrorPipe pipe)
        {
            IMirrorSurface? window;
            lock (_gate)
            {
                _byKey.TryGetValue(pipe, out window);
            }

            window?.SetDeviceName(pipe.DeviceName);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            IMirrorSurface[] copy;
            lock (_gate)
            {
                copy = _open.ToArray();
                _open.Clear();
                _byKey.Clear();
            }

            foreach (var window in copy)
            {
                await window.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
