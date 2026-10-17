using System.Net;
using System.Net.Sockets;
using AirPlayServer.Core.Discovery;
using AirPlayServer.Core.Pairing;
using AirPlayServer.Core.Protocol;

namespace AirPlayServer.Core;

public sealed class AirPlayReceiver : IAsyncDisposable
{
    private readonly PairingIdentity _identity;
    private readonly ControlServer _control;
    private readonly MdnsAnnouncer? _announcer;
    private int _disposed;

    private AirPlayReceiver(
        PairingIdentity identity,
        ControlServer control,
        MdnsAnnouncer? announcer,
        string deviceId,
        string name,
        bool discoveryReceiving)
    {
        _identity = identity;
        _control = control;
        _announcer = announcer;
        DeviceId = deviceId;
        Name = name;
        DiscoveryReceiving = discoveryReceiving;
        _control.SessionEstablished += OnSessionEstablished;
    }

    public string Name { get; }

    public string DeviceId { get; }

    public IPEndPoint AirPlayEndPoint => _control.AirPlayEndPoint;

    public IPEndPoint RaopEndPoint => _control.RaopEndPoint;

    public bool DiscoveryReceiving { get; }

    public ReadOnlyMemory<byte> PublicKey => _identity.PublicKey;

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
                DisplayWidth = options.DisplayWidth,
                DisplayHeight = options.DisplayHeight,
                MaxFramesPerSecond = options.MaxFramesPerSecond,
            };
            control = ControlServer.Start(
                airPlay,
                raop,
                advertisement,
                identity,
                options.EnableDiscovery,
                log,
                options.MaxConnections);
            airPlay = null;
            raop = null;
            MdnsAnnouncer? announcer = null;
            var receiving = false;
            if (options.EnableDiscovery)
            {
                announcer = MdnsAnnouncer.Start(advertisement, log);
                receiving = announcer.IsReceivingQueries;
            }

            return new AirPlayReceiver(identity, control, announcer, advertisement.DeviceId, instance, receiving);
        }
        catch
        {
            if (control is not null)
            {
                await control.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                airPlay?.Dispose();
                raop?.Dispose();
            }

            identity.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _control.SessionEstablished -= OnSessionEstablished;
        await _control.DisposeAsync().ConfigureAwait(false);
        if (_announcer is not null)
        {
            await _announcer.DisposeAsync().ConfigureAwait(false);
        }

        _identity.Dispose();
    }

    private void OnSessionEstablished(object? sender, AirPlaySessionEstablishedEventArgs args)
        => SessionEstablished?.Invoke(this, args);
}
