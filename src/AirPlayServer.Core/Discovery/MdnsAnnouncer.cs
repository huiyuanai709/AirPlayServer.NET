using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AirPlayServer.Core.Discovery;

internal sealed class MdnsAnnouncer : IAsyncDisposable
{
    private readonly ReceiverAdvertisement _advertisement;
    private readonly IAirPlayLog _log;
    private readonly Func<IReadOnlyList<AdvertisedNic>> _addresses;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<Task> _loops = [];
    private readonly object _sync = new();
    private readonly List<BoundSocket> _nicSockets = [];
    private readonly List<IPAddress> _joined = [];
    private readonly HashSet<string> _answeredPeers = new(StringComparer.Ordinal);
    private Socket? _anySocket;
    private volatile IReadOnlyList<AdvertisedNic> _nics = [];
    private volatile bool _receiving;
    private bool _networkHooked;
    private bool _seenNics;
    private bool _noAddressLogged;
    private bool _multicastIfWarned;
    private int _networkEpoch;
    private int _sequence;
    private bool _disposed;

    private MdnsAnnouncer(ReceiverAdvertisement advertisement, IAirPlayLog log, Func<IReadOnlyList<AdvertisedNic>> addresses)
    {
        _advertisement = advertisement;
        _log = log;
        _addresses = addresses;
    }

    public bool IsReceivingQueries => _receiving;

    public static MdnsAnnouncer Start(ReceiverAdvertisement advertisement, IAirPlayLog log)
        => Start(advertisement, log, null);

    public static MdnsAnnouncer Start(
        ReceiverAdvertisement advertisement,
        IAirPlayLog log,
        Func<IReadOnlyList<AdvertisedNic>>? addresses)
    {
        addresses ??= () => advertisement.Address is null
            ? []
            : [new AdvertisedNic(advertisement.Address, PrefixLength: -1, Name: "listen")];
        var announcer = new MdnsAnnouncer(advertisement, log, addresses);
        announcer.Start();
        return announcer;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnhookNetwork();
        await _cancellation.CancelAsync().ConfigureAwait(false);
        Task[] pending;
        lock (_sync)
        {
            DisposeSockets();
            pending = _loops.ToArray();
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }

        _cancellation.Dispose();
    }

    private void Start()
    {
        HookNetwork();
        var nics = ReadNics();
        _nics = nics;
        _seenNics = true;
        if (nics.Count > 0)
        {
            _advertisement.Address = nics[0].Address;
        }

        var bindFailure = EnsureSockets(nics);
        var canSend = HasSendableSocket();
        _log.Log(
            _receiving ? AirPlayLogLevel.Information : AirPlayLogLevel.Warning,
            MdnsCadence.DescribeStartup(
                _receiving,
                canSend,
                _advertisement.InstanceName,
                _advertisement.DeviceId,
                MdnsCadence.FormatNics(nics),
                bindFailure));

        var token = _cancellation.Token;
        lock (_sync)
        {
            _loops.Add(Task.Run(() => AnnounceLoop(token), token));
        }
    }

    private async Task AnnounceLoop(CancellationToken cancellationToken)
    {
        try
        {
            await RunStartupBurst(cancellationToken).ConfigureAwait(false);
            var nextSteady = DateTime.UtcNow + MdnsCadence.SteadyIntervalFor(_receiving);
            while (!cancellationToken.IsCancellationRequested)
            {
                var epoch = Volatile.Read(ref _networkEpoch);
                var remaining = nextSteady - DateTime.UtcNow;
                if (remaining < TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                }

                var slice = remaining > MdnsCadence.AddressPollInterval ? MdnsCadence.AddressPollInterval : remaining;
                if (slice > TimeSpan.Zero)
                {
                    await Task.Delay(slice, cancellationToken).ConfigureAwait(false);
                }

                var steadyDue = DateTime.UtcNow >= nextSteady;
                var epochChanged = Volatile.Read(ref _networkEpoch) != epoch;
                var changed = false;
                var revived = false;
                if (epochChanged || steadyDue)
                {
                    // One read after the flap goes quiet. Idle mirrors do not enumerate
                    // on every NetworkChange callback, and a hotspot-only change does
                    // not send. This task never resets a decoder or an RTSP session.
                    if (epochChanged)
                    {
                        await Task.Delay(MdnsCadence.NetworkChangeDebounce, cancellationToken).ConfigureAwait(false);
                    }

                    changed = ApplyNics(ReadNics());
                    revived = RetryQuerySocket();
                }
                if (_nics.Count == 0 || !HasSendableSocket())
                {
                    if (_nics.Count == 0)
                    {
                        LogNoAddress();
                    }

                    if (steadyDue)
                    {
                        nextSteady = DateTime.UtcNow + MdnsCadence.SteadyIntervalFor(false);
                    }

                    continue;
                }

                _noAddressLogged = false;
                if (!changed && !steadyDue && !revived)
                {
                    // Same A record. Do not send again and do not touch the mirror.
                    continue;
                }

                await SendAnnouncement(cancellationToken).ConfigureAwait(false);
                nextSteady = DateTime.UtcNow + MdnsCadence.SteadyIntervalFor(_receiving);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunStartupBurst(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var epoch = Volatile.Read(ref _networkEpoch);
            var nics = ReadNics();
            ApplyNics(nics);
            if (_nics.Count == 0 || !HasSendableSocket())
            {
                if (_nics.Count == 0)
                {
                    LogNoAddress();
                }

                await Task.Delay(MdnsCadence.SteadyIntervalFor(false), cancellationToken).ConfigureAwait(false);
                continue;
            }

            _noAddressLogged = false;
            var interrupted = await SendBurst(epoch, cancellationToken).ConfigureAwait(false);
            if (!interrupted)
            {
                return;
            }

            await Task.Delay(MdnsCadence.NetworkChangeDebounce, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> SendBurst(int epoch, CancellationToken cancellationToken)
    {
        await SendAnnouncement(cancellationToken).ConfigureAwait(false);
        foreach (var gap in MdnsCadence.StartupGaps())
        {
            var reason = await WaitFor(gap, epoch, _nics, cancellationToken, interruptOnNetwork: false).ConfigureAwait(false);
            if (reason == WakeReason.AddressesChanged)
            {
                return true;
            }

            await SendAnnouncement(cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<WakeReason> WaitFor(
        TimeSpan delay,
        int epoch,
        IReadOnlyList<AdvertisedNic> baseline,
        CancellationToken cancellationToken,
        bool interruptOnNetwork)
    {
        var elapsed = TimeSpan.Zero;
        while (elapsed < delay)
        {
            var slice = delay - elapsed;
            if (slice > TimeSpan.FromSeconds(2))
            {
                slice = TimeSpan.FromSeconds(2);
            }

            await Task.Delay(slice, cancellationToken).ConfigureAwait(false);
            elapsed += slice;
            if (!MdnsCadence.SameAddresses(baseline, ReadNics()))
            {
                return WakeReason.AddressesChanged;
            }

            if (interruptOnNetwork && Volatile.Read(ref _networkEpoch) != epoch)
            {
                return WakeReason.NetworkEvent;
            }
        }

        return WakeReason.Elapsed;
    }

    private bool ApplyNics(IReadOnlyList<AdvertisedNic> nics)
    {
        var previous = _nics;
        // Address text only. MirrorPipe, decoders, and RTSP are not in this type.
        var changed = MdnsCadence.ShouldRefreshAnnouncement(previous, nics);
        _nics = nics;
        if (nics.Count > 0)
        {
            _advertisement.Address = nics[0].Address;
        }

        if (!changed)
        {
            return false;
        }

        if (_seenNics)
        {
            _log.Log(
                AirPlayLogLevel.Information,
                MdnsCadence.DescribeAddressChange(
                    _advertisement.InstanceName,
                    _advertisement.DeviceId,
                    MdnsCadence.FormatNics(previous),
                    MdnsCadence.FormatNics(nics)));
        }

        var failure = EnsureSockets(nics);
        if (!_receiving && failure is not null)
        {
            _log.Log(
                AirPlayLogLevel.Warning,
                MdnsCadence.DescribeStartup(
                    receiving: false,
                    HasSendableSocket(),
                    _advertisement.InstanceName,
                    _advertisement.DeviceId,
                    MdnsCadence.FormatNics(nics),
                    failure));
        }
        else if (_receiving)
        {
            _log.Log(
                AirPlayLogLevel.Information,
                MdnsCadence.DescribeStartup(
                    receiving: true,
                    canSend: true,
                    _advertisement.InstanceName,
                    _advertisement.DeviceId,
                    MdnsCadence.FormatNics(nics),
                    bindFailure: null));
        }

        return true;
    }

    private bool RetryQuerySocket()
    {
        if (_receiving || _nics.Count == 0)
        {
            return false;
        }

        EnsureSockets(_nics);
        if (!_receiving)
        {
            return false;
        }

        _log.Log(
            AirPlayLogLevel.Information,
            MdnsCadence.DescribeStartup(
                receiving: true,
                canSend: true,
                _advertisement.InstanceName,
                _advertisement.DeviceId,
                MdnsCadence.FormatNics(_nics),
                bindFailure: null));
        return true;
    }

    private async Task SendAnnouncement(CancellationToken cancellationToken)
    {
        var nics = _nics;
        if (nics.Count == 0)
        {
            LogNoAddress();
            return;
        }

        var targets = SnapshotTargets(nics);
        if (targets.Length == 0)
        {
            LogNoAddress();
            return;
        }

        // One datagram, one A record. A second packet with another address for
        // the same hostname is what made phones keep RTSP and stall the picture.
        var target = PrimaryTarget(targets, nics);
        var sent = 0;
        var port = 0;
        string? lastError = null;
        if (!IPAddress.Any.Equals(target.PacketAddress) && !IPAddress.IsLoopback(target.PacketAddress))
        {
            if (!MdnsSockets.TrySetMulticastInterface(target.Socket, target.PacketAddress) && !_multicastIfWarned)
            {
                _multicastIfWarned = true;
                _log.Log(
                    AirPlayLogLevel.Warning,
                    "mDNS 无法把多播出口固定到 " + target.PacketAddress
                    + "。Could not set IP_MULTICAST_IF for " + target.PacketAddress + ".");
            }
        }

        var packet = MdnsMessages.BuildAnnouncement(_advertisement, target.PacketAddress);
        try
        {
            await target.Socket.SendToAsync(packet, SocketFlags.None, MdnsSockets.GroupEndpoint, cancellationToken).ConfigureAwait(false);
            sent++;
            port = target.Port;
        }
        catch (SocketException ex)
        {
            lastError = ex.Message;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (sent == 0)
        {
            _log.Log(AirPlayLogLevel.Warning, "mDNS 通告没有发出: " + (lastError ?? "no socket") + " mDNS announcement was not sent: " + (lastError ?? "no socket"));
            return;
        }

        var sequence = Interlocked.Increment(ref _sequence);
        _log.Log(
            AirPlayLogLevel.Information,
            MdnsCadence.DescribeAnnouncement(
                sequence,
                _advertisement.InstanceName,
                _advertisement.DeviceId,
                MdnsCadence.FormatNics(LoggedNic(nics, target)),
                port,
                _receiving));
    }

    private async Task ReceiveLoop(Socket socket, IPAddress? boundAddress, CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        var response = new byte[2048];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var remoteAddress = received.RemoteEndPoint is IPEndPoint endpoint ? endpoint.Address : null;
            var answerAddress = boundAddress is not null && !IPAddress.Any.Equals(boundAddress)
                ? boundAddress
                : remoteAddress is null
                    ? _advertisement.Address
                    : LocalNetwork.MatchRemote(remoteAddress, _nics);
            if (!MdnsMessages.TryBuildQueryResponse(
                    buffer.AsSpan(0, received.ReceivedBytes),
                    _advertisement,
                    response,
                    out var written,
                    out var unicast,
                    answerAddress))
            {
                continue;
            }

            if (!unicast && answerAddress is not null)
            {
                MdnsSockets.TrySetMulticastInterface(socket, answerAddress);
            }

            var destination = unicast ? received.RemoteEndPoint : MdnsSockets.GroupEndpoint;
            try
            {
                await socket.SendToAsync(response.AsMemory(0, written), SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);
                NoteQuery(received.RemoteEndPoint, unicast);
            }
            catch (SocketException ex)
            {
                _log.Log(AirPlayLogLevel.Warning, "mDNS 应答没有发出: " + ex.Message + " mDNS response was not sent: " + ex.Message);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private void NoteQuery(EndPoint remote, bool unicast)
    {
        var key = remote.ToString() ?? "";
        bool first;
        lock (_answeredPeers)
        {
            first = _answeredPeers.Add(key);
        }

        if (!first)
        {
            return;
        }

        _log.Log(
            AirPlayLogLevel.Information,
            "mDNS 已应答来自 " + key + " 的查询（unicast=" + unicast + "）。Answered an mDNS query from " + key + ", unicast=" + unicast + ".");
    }

    private string? EnsureSockets(IReadOnlyList<AdvertisedNic> nics)
    {
        lock (_sync)
        {
            if (_anySocket is not null)
            {
                SyncJoins(nics);
                _receiving = true;
                return null;
            }

            string? anyError = null;
            if (!HasReceivingLocked())
            {
                anyError = TryOpenAny(nics);
                if (_anySocket is not null)
                {
                    CloseNicSockets();
                    _receiving = true;
                    return null;
                }
            }

            var errors = new List<string>();
            if (anyError is not null)
            {
                errors.Add("0.0.0.0:5353 " + anyError);
            }

            for (var i = _nicSockets.Count - 1; i >= 0; i--)
            {
                if (nics.Count == 0 || !_nicSockets[i].Address.Equals(nics[0].Address))
                {
                    _nicSockets[i].Socket.Dispose();
                    _nicSockets.RemoveAt(i);
                }
            }

            // One socket. Extra NIC sockets were joined and sent a second A record.
            if (nics.Count > 0 && !ContainsBound(nics[0].Address))
            {
                var nic = nics[0];
                var receiveError = TryOpenNic(nic, receive: true);
                if (!ContainsBound(nic.Address))
                {
                    if (receiveError is not null)
                    {
                        errors.Add(nic.Address + ":5353 " + receiveError);
                    }

                    var sendError = TryOpenNic(nic, receive: false);
                    if (!ContainsBound(nic.Address) && sendError is not null)
                    {
                        errors.Add(nic.Address + " send " + sendError);
                    }
                }
            }

            _receiving = HasReceivingLocked();
            if (_receiving || _nicSockets.Count > 0)
            {
                return _receiving ? null : string.Join("; ", errors);
            }

            return errors.Count == 0 ? "no IPv4 address to bind" : string.Join("; ", errors);
        }
    }

    private string? TryOpenAny(IReadOnlyList<AdvertisedNic> nics)
    {
        Socket? socket = null;
        try
        {
            socket = MdnsSockets.CreateSharedUdp();
            MdnsSockets.ConfigureMulticast(socket);
            socket.Bind(new IPEndPoint(IPAddress.Any, MdnsSockets.Port));
            _anySocket = socket;
            SyncJoins(nics);
            if (_joined.Count == 0)
            {
                MdnsSockets.TryJoinGroup(socket, IPAddress.Any);
            }

            StartReceive(socket, boundAddress: null);
            return null;
        }
        catch (SocketException ex)
        {
            socket?.Dispose();
            _anySocket = null;
            _joined.Clear();
            return ex.SocketErrorCode + " " + ex.Message;
        }
    }

    private string? TryOpenNic(AdvertisedNic nic, bool receive)
    {
        Socket? socket = null;
        try
        {
            socket = MdnsSockets.CreateSharedUdp();
            MdnsSockets.ConfigureMulticast(socket);
            socket.Bind(new IPEndPoint(nic.Address, receive ? MdnsSockets.Port : 0));
            if (!IPAddress.IsLoopback(nic.Address))
            {
                MdnsSockets.TrySetMulticastInterface(socket, nic.Address);
            }

            if (receive)
            {
                MdnsSockets.TryJoinGroup(socket, nic.Address);
            }

            _nicSockets.Add(new BoundSocket(socket, nic.Address, receive));
            if (receive)
            {
                StartReceive(socket, nic.Address);
            }

            return null;
        }
        catch (SocketException ex)
        {
            socket?.Dispose();
            return ex.SocketErrorCode + " " + ex.Message;
        }
    }

    private void SyncJoins(IReadOnlyList<AdvertisedNic> nics)
    {
        if (_anySocket is null)
        {
            return;
        }

        var desired = new List<IPAddress>();
        if (nics.Count > 0 && !IPAddress.IsLoopback(nics[0].Address))
        {
            desired.Add(nics[0].Address);
        }

        for (var i = _joined.Count - 1; i >= 0; i--)
        {
            if (ContainsIp(desired, _joined[i]))
            {
                continue;
            }

            MdnsSockets.TryDropGroup(_anySocket, _joined[i]);
            _joined.RemoveAt(i);
        }

        foreach (var address in desired)
        {
            if (ContainsIp(_joined, address))
            {
                continue;
            }

            if (MdnsSockets.TryJoinGroup(_anySocket, address))
            {
                _joined.Add(address);
            }
        }
    }

    private void StartReceive(Socket socket, IPAddress? boundAddress)
    {
        var token = _cancellation.Token;
        _loops.Add(Task.Run(() => ReceiveLoop(socket, boundAddress, token), token));
    }

    private static IReadOnlyList<AdvertisedNic> LoggedNic(IReadOnlyList<AdvertisedNic> nics, SendTarget target)
    {
        foreach (var nic in nics)
        {
            if (nic.Address.Equals(target.PacketAddress))
            {
                return [nic];
            }
        }

        return [new AdvertisedNic(target.PacketAddress, PrefixLength: -1, Name: "primary")];
    }

    private static SendTarget PrimaryTarget(SendTarget[] targets, IReadOnlyList<AdvertisedNic> nics)
    {
        if (nics.Count == 0)
        {
            return targets[0];
        }

        var primary = nics[0].Address;
        foreach (var target in targets)
        {
            if (target.PacketAddress.Equals(primary))
            {
                return target;
            }
        }

        return targets[0];
    }

    private SendTarget[] SnapshotTargets(IReadOnlyList<AdvertisedNic> nics)
    {
        lock (_sync)
        {
            if (_anySocket is not null)
            {
                var port = LocalPort(_anySocket);
                if (nics.Count == 0)
                {
                    return _advertisement.Address is null
                        ? []
                        : [new SendTarget(_anySocket, _advertisement.Address, port)];
                }

                var targets = new SendTarget[nics.Count];
                for (var i = 0; i < nics.Count; i++)
                {
                    targets[i] = new SendTarget(_anySocket, nics[i].Address, port);
                }

                return targets;
            }

            if (_nicSockets.Count == 0)
            {
                return [];
            }

            var list = new SendTarget[_nicSockets.Count];
            for (var i = 0; i < _nicSockets.Count; i++)
            {
                var bound = _nicSockets[i];
                list[i] = new SendTarget(bound.Socket, bound.Address, LocalPort(bound.Socket));
            }

            return list;
        }
    }

    private bool HasSendableSocket()
    {
        lock (_sync)
        {
            return _anySocket is not null || _nicSockets.Count > 0;
        }
    }

    private bool HasReceivingLocked()
    {
        if (_anySocket is not null)
        {
            return true;
        }

        foreach (var bound in _nicSockets)
        {
            if (bound.Receives)
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsBound(IPAddress address)
    {
        foreach (var bound in _nicSockets)
        {
            if (bound.Address.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    private void CloseNicSockets()
    {
        foreach (var bound in _nicSockets)
        {
            bound.Socket.Dispose();
        }

        _nicSockets.Clear();
    }

    private void DisposeSockets()
    {
        _anySocket?.Dispose();
        _anySocket = null;
        _joined.Clear();
        CloseNicSockets();
        _receiving = false;
    }

    private void LogNoAddress()
    {
        if (_noAddressLogged)
        {
            return;
        }

        _noAddressLogged = true;
        _log.Log(
            AirPlayLogLevel.Warning,
            "mDNS 没有可通告的 IPv4，先不发送。网卡恢复后会重新连发。No IPv4 address is available to advertise. Announcements resume when a NIC address returns.");
    }

    private IReadOnlyList<AdvertisedNic> ReadNics()
    {
        try
        {
            return _addresses();
        }
        catch (NetworkInformationException ex)
        {
            _log.Log(AirPlayLogLevel.Warning, "mDNS 读取网卡失败，沿用上次的地址: " + ex.Message + " Reading NICs failed; keeping the previous addresses: " + ex.Message);
            return _nics;
        }
    }

    private void HookNetwork()
    {
        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
            _networkHooked = true;
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private void UnhookNetwork()
    {
        if (!_networkHooked)
        {
            return;
        }

        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        _networkHooked = false;
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // Counter only. Do not enumerate adapters, send, or touch the mirror on
        // this callback. Windows raises it in bursts for Mobile Hotspot / ICS.
        Interlocked.Increment(ref _networkEpoch);
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        => Interlocked.Increment(ref _networkEpoch);

    private static bool ContainsIp(List<IPAddress> addresses, IPAddress address)
    {
        foreach (var candidate in addresses)
        {
            if (candidate.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    private static int LocalPort(Socket socket)
        => socket.LocalEndPoint is IPEndPoint endPoint ? endPoint.Port : 0;

    private readonly record struct SendTarget(Socket Socket, IPAddress PacketAddress, int Port);

    private sealed class BoundSocket(Socket socket, IPAddress address, bool receives)
    {
        public Socket Socket { get; } = socket;

        public IPAddress Address { get; } = address;

        public bool Receives { get; } = receives;
    }

    private enum WakeReason
    {
        Elapsed,
        AddressesChanged,
        NetworkEvent,
    }
}
