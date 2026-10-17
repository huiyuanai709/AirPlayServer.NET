using System.Net;
using System.Net.Sockets;

namespace AirPlayServer.Core.Discovery;

internal sealed class MdnsAnnouncer : IAsyncDisposable
{
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint GroupEndpoint = new(Group, 5353);

    private readonly ReceiverAdvertisement _advertisement;
    private readonly IAirPlayLog _log;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<Task> _loops = [];
    private Socket? _socket;
    private bool _disposed;

    private MdnsAnnouncer(ReceiverAdvertisement advertisement, IAirPlayLog log)
    {
        _advertisement = advertisement;
        _log = log;
    }

    public bool IsReceivingQueries { get; private set; }

    public static MdnsAnnouncer Start(ReceiverAdvertisement advertisement, IAirPlayLog log)
    {
        var announcer = new MdnsAnnouncer(advertisement, log);
        announcer.StartSocket();
        return announcer;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cancellation.CancelAsync().ConfigureAwait(false);
        _socket?.Dispose();
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }

        _cancellation.Dispose();
    }

    private void StartSocket()
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
            var membership = _advertisement.Address is null || IPAddress.IsLoopback(_advertisement.Address)
                ? new MulticastOption(Group)
                : new MulticastOption(Group, _advertisement.Address);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, membership);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            IsReceivingQueries = true;
        }
        catch (SocketException ex)
        {
            socket?.Dispose();
            socket = CreateSendOnlySocket();
            _log.Log(
                AirPlayLogLevel.Warning,
                socket is null
                    ? "mDNS is disabled: " + ex.Message
                    : "mDNS query socket is unavailable; sending announcements only: " + ex.Message);
        }

        if (socket is null)
        {
            return;
        }

        _socket = socket;
        var token = _cancellation.Token;
        _loops.Add(Task.Run(() => AnnounceLoop(socket, token), token));
        if (IsReceivingQueries)
        {
            _loops.Add(Task.Run(() => ReceiveLoop(socket, token), token));
        }

        _log.Log(
            AirPlayLogLevel.Information,
            $"mDNS advertising _airplay._tcp:{_advertisement.AirPlayPort} and _raop._tcp:{_advertisement.RaopPort} as {_advertisement.InstanceName}.");
    }

    private Socket? CreateSendOnlySocket()
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            return socket;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private async Task AnnounceLoop(Socket socket, CancellationToken cancellationToken)
    {
        try
        {
            await SendAnnouncement(socket, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            await SendAnnouncement(socket, cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendAnnouncement(socket, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReceiveLoop(Socket socket, CancellationToken cancellationToken)
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

            if (!MdnsMessages.TryBuildQueryResponse(buffer.AsSpan(0, received.ReceivedBytes), _advertisement, response, out var written, out var unicast))
            {
                continue;
            }

            var destination = unicast ? received.RemoteEndPoint : GroupEndpoint;
            try
            {
                await socket.SendToAsync(response.AsMemory(0, written), SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                _log.Log(AirPlayLogLevel.Warning, "mDNS response was not sent: " + ex.Message);
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

    private async Task SendAnnouncement(Socket socket, CancellationToken cancellationToken)
    {
        var packet = MdnsMessages.BuildAnnouncement(_advertisement);
        await socket.SendToAsync(packet, SocketFlags.None, GroupEndpoint, cancellationToken).ConfigureAwait(false);
    }
}
