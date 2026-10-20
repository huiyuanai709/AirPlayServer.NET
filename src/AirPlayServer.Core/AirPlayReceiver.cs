using System.Net;
using System.Net.Sockets;
using AirPlayServer.Core.Discovery;
using AirPlayServer.Core.Export;
using AirPlayServer.Core.Media;
using AirPlayServer.Core.Notify;
using AirPlayServer.Core.Pairing;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core;

public sealed class AirPlayReceiver : IAsyncDisposable
{
    private readonly PairingIdentity _identity;
    private readonly ControlServer _control;
    private readonly MdnsAnnouncer? _announcer;
    private readonly MirrorRuntime _mirror;
    private readonly TapClockExporter? _tapClock;
    private readonly IAsyncDisposable? _windows;
    private readonly NativeMediaKeyModule? _plugin;
    private readonly int _displayWidth;
    private readonly int _displayHeight;
    private int _disposed;

    private AirPlayReceiver(
        PairingIdentity identity,
        ControlServer control,
        MdnsAnnouncer? announcer,
        MirrorRuntime mirror,
        TapClockExporter? tapClock,
        IAsyncDisposable? windows,
        NativeMediaKeyModule? plugin,
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
        _tapClock = tapClock;
        _windows = windows;
        _plugin = plugin;
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
            var frames = new FrameSlot();
            decoder = options.Decoder ?? H264Decoders.Create(log, load: null, frames.Buffers);
            if (options.AcceptHevc)
            {
                hevc = options.HevcDecoder ?? HevcDecoders.Create(log, load: null, frames.Buffers);
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
                () => CreateExtraMirror(log, advertisesHevc, separateFairPlay, unwrapper, fairPlay),
                fairPlay);
            if (options.PublishDemoFrame)
            {
                DemoFrame.Publish(frames, options.DisplayWidth, options.DisplayHeight);
            }

            if (options.TapClock is { Enabled: true })
            {
                tapClock = TapClockExporter.Start(options.TapClock, mirror.ExportSources, log, CancellationToken.None);
            }

            if (options.ShowWindow && !OperatingSystem.IsWindows())
            {
                log.Log(AirPlayLogLevel.Information, "Windows display window is not created on this OS. Frames can still be exported.");
            }
            var windowGroup = new MirrorWindowGroup();
            windows = windowGroup;
            var logicalWidth = options.TapClockLogicalScreen?.Width ?? 0;
            var logicalHeight = options.TapClockLogicalScreen?.Height ?? 0;
            if (options.ShowWindow)
            {
                windowGroup.Add(mirror.Primary, MirrorWindows.TryOpen(frames, instance, log, logicalWidth, logicalHeight, CancellationToken.None));
            }

            mirror.PipeAdded += pipe =>
            {
                if (!options.ShowWindow)
                {
                    return;
                }

                var title = string.IsNullOrWhiteSpace(pipe.DeviceName) ? "镜像 " + pipe.Activity.DataPort : pipe.DeviceName;
                windowGroup.Add(pipe, MirrorWindows.TryOpen(pipe.Frames, title, log, logicalWidth, logicalHeight, CancellationToken.None));
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
                announcer = MdnsAnnouncer.Start(advertisement, log);
                receiving = announcer.IsReceivingQueries;
            }

            var receiver = new AirPlayReceiver(
                identity,
                control,
                announcer,
                mirror,
                tapClock,
                windows,
                plugin,
                frames,
                advertisement.DeviceId,
                instance,
                receiving,
                options.DisplayWidth,
                options.DisplayHeight,
                advertisesHevc,
                hevc?.Name ?? "unavailable");
            mirror = null;
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

    private void OnSessionEstablished(object? sender, AirPlaySessionEstablishedEventArgs args)
        => SessionEstablished?.Invoke(this, args);

    private static MirrorEndpointResources CreateExtraMirror(
        IAirPlayLog log,
        bool acceptHevc,
        bool separateFairPlay,
        IMediaKeyUnwrapper? unwrapper,
        IFairPlaySetup? fairPlay)
    {
        var frames = new FrameSlot();
        var decoder = H264Decoders.Create(log, load: null, frames.Buffers);
        IHevcDecoder? hevc = acceptHevc ? HevcDecoders.Create(log, load: null, frames.Buffers) : null;
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
        private readonly List<IMirrorWindow> _open = [];
        private readonly Dictionary<MirrorPipe, IMirrorWindow> _byPipe = new();
        private int _disposed;

        public void Add(MirrorPipe pipe, IMirrorWindow? window)
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
                _byPipe[pipe] = window;
            }
        }

        public void Rename(MirrorPipe pipe)
        {
            IMirrorWindow? window;
            lock (_gate)
            {
                _byPipe.TryGetValue(pipe, out window);
            }

            window?.SetDeviceName(pipe.DeviceName);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            IMirrorWindow[] copy;
            lock (_gate)
            {
                copy = _open.ToArray();
                _open.Clear();
                _byPipe.Clear();
            }

            foreach (var window in copy)
            {
                await window.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
