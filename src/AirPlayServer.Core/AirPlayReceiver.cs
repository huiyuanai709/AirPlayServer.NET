using System.Net;
using System.Net.Sockets;
using AirPlayServer.Core.Discovery;
using AirPlayServer.Core.Export;
using AirPlayServer.Core.Media;
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
    private readonly IAsyncDisposable? _window;
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
        IAsyncDisposable? window,
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
        _window = window;
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
        IAsyncDisposable? window = null;
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
                hevc);
            if (options.PublishDemoFrame)
            {
                DemoFrame.Publish(frames, options.DisplayWidth, options.DisplayHeight);
            }

            if (options.TapClock is { Enabled: true })
            {
                tapClock = TapClockExporter.Start(options.TapClock, frames, log, CancellationToken.None);
            }

            if (options.ShowWindow && !OperatingSystem.IsWindows())
            {
                log.Log(AirPlayLogLevel.Information, "Windows display window is not created on this OS. Frames can still be exported.");
            }
#if WINDOWS_HOST
            if (options.ShowWindow && OperatingSystem.IsWindows())
            {
                // 0,0 keeps the hover readout in decoded-frame pixels (TapClock identity
                // when no screen is configured). A --model size is the absolute screen.
                var logical = options.TapClockLogicalScreen;
                window = WindowsDisplayWindow.Start(
                    frames,
                    instance,
                    log,
                    logical?.Width ?? 0,
                    logical?.Height ?? 0,
                    CancellationToken.None);
            }
#endif
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
                window,
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
            window = null;
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

            if (window is not null)
            {
                await window.DisposeAsync().ConfigureAwait(false);
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
        if (_window is not null)
        {
            await _window.DisposeAsync().ConfigureAwait(false);
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
}
