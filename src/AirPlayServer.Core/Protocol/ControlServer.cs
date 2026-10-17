using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlayServer.Core.Discovery;
using AirPlayServer.Core.Media;
using AirPlayServer.Core.Pairing;

namespace AirPlayServer.Core.Protocol;

internal sealed class ControlServer : IAsyncDisposable
{
    private readonly Socket _airPlay;
    private readonly Socket _raop;
    private readonly ControlRouter _router;
    private readonly PairingIdentity _identity;
    private readonly IAirPlayLog _log;
    private readonly SemaphoreSlim _gate;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<Task> _loops = [];
    private readonly List<Task> _clients = [];
    private readonly object _clientGate = new();
    private int _disposed;

    private ControlServer(Socket airPlay, Socket raop, ControlRouter router, PairingIdentity identity, IAirPlayLog log, int maxConnections)
    {
        _airPlay = airPlay;
        _raop = raop;
        _router = router;
        _identity = identity;
        _log = log;
        _gate = new SemaphoreSlim(maxConnections, maxConnections);
    }

    public IPEndPoint AirPlayEndPoint => (IPEndPoint)_airPlay.LocalEndPoint!;

    public IPEndPoint RaopEndPoint => (IPEndPoint)_raop.LocalEndPoint!;

    public event EventHandler<AirPlaySessionEstablishedEventArgs>? SessionEstablished;

    public static Socket Bind(IPAddress listen, ushort port) => Listen(listen, port);

    public static ControlServer Start(
        Socket airPlay,
        Socket raop,
        ReceiverAdvertisement advertisement,
        PairingIdentity identity,
        bool discoveryEnabled,
        IAirPlayLog log,
        MirrorRuntime mirror,
        int maxConnections,
        IFairPlaySetup? fairPlay = null)
    {
        var server = new ControlServer(
            airPlay,
            raop,
            new ControlRouter(advertisement, identity.PublicKey.Span, discoveryEnabled, log, mirror, fairPlay),
            identity,
            log,
            maxConnections);
        server._loops.Add(Task.Run(() => server.AcceptLoop(airPlay, "airplay")));
        server._loops.Add(Task.Run(() => server.AcceptLoop(raop, "raop")));
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);
        _airPlay.Dispose();
        _raop.Dispose();
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
            Task[] clients;
            lock (_clientGate)
            {
                clients = _clients.ToArray();
            }

            if (clients.Length > 0)
            {
                await Task.WhenAll(clients).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Accept loops and connections surface teardown as socket or cancellation errors.
        }

        _gate.Dispose();
        _cancellation.Dispose();
    }

    private async Task AcceptLoop(Socket listener, string listenerName)
    {
        var cancellationToken = _cancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Socket client;
            try
            {
                client = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                _gate.Release();
                break;
            }

            client.NoDelay = true;
            var clientTask = Task.Run(() => HandleClientAsync(client, listenerName));
            lock (_clientGate)
            {
                _clients.Add(clientTask);
            }
        }
    }

    private async Task HandleClientAsync(Socket client, string listenerName)
    {
        using var session = new PairingSession(_identity);
        try
        {
            client.NoDelay = true;
            await using var stream = new NetworkStream(client, ownsSocket: false);
            var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var read = await reader.ReadAsync(_cancellation.Token).ConfigureAwait(false);
                    var buffer = read.Buffer;
                    if (buffer.Length > Http1Request.MaxHeaderBytes + Http1Request.MaxBodyBytes)
                    {
                        break;
                    }

                    var length = (int)buffer.Length;
                    var rented = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
                    try
                    {
                        buffer.CopyTo(rented);
                        var status = Http1Request.TryParse(rented.AsSpan(0, length), out var request, out var consumed);
                        if (status == HttpParseStatus.NeedMore)
                        {
                            if (read.IsCompleted)
                            {
                                break;
                            }

                            reader.AdvanceTo(buffer.Start, buffer.End);
                            continue;
                        }

                        if (status != HttpParseStatus.Ok || request is null)
                        {
                            break;
                        }

                        var before = session.Phase;
                        var response = _router.Dispatch(request, session, client.RemoteEndPoint);
                        reader.AdvanceTo(buffer.GetPosition(consumed));
                        if (before != PairingPhase.Finished && session.Phase == PairingPhase.Finished)
                        {
                            SessionEstablished?.Invoke(this, new AirPlaySessionEstablishedEventArgs
                            {
                                Listener = listenerName,
                                PeerPublicKeyBase64 = Convert.ToBase64String(session.PeerPublicKey.Span),
                                RemoteEndPoint = client.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0),
                            });
                        }

                        await WriteAsync(stream, request, response, _cancellation.Token).ConfigureAwait(false);

                        if (response.SwitchToReverse)
                        {
                            await ParkReverseAsync(reader, _cancellation.Token).ConfigureAwait(false);
                            break;
                        }

                        if (response.Close)
                        {
                            break;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                    }
                }
            }
            finally
            {
                await reader.CompleteAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _log.Log(AirPlayLogLevel.Error, "Control connection failed: " + ex.Message);
        }
        finally
        {
            client.Dispose();
            _gate.Release();
        }
    }

    private static async Task ParkReverseAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            reader.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
            {
                return;
            }
        }
    }

    private static async Task WriteAsync(Stream stream, Http1Request request, Http1Response response, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append(request.Protocol).Append(' ').Append(response.Status).Append(' ').Append(response.Reason).Append("\r\n");
        builder.Append("Server: ").Append(AirPlayCompatibility.ServerHeader).Append("\r\n");
        if (request.Headers.TryGetValue("CSeq", out var cseq))
        {
            builder.Append("CSeq: ").Append(cseq).Append("\r\n");
        }

        if (response.SwitchToReverse)
        {
            builder.Append("Upgrade: PTTH/1.0\r\n");
            builder.Append("Connection: Upgrade\r\n");
        }
        else
        {
            builder.Append("Connection: ").Append(response.Close ? "close" : "keep-alive").Append("\r\n");
        }

        if (response.ContentType is not null)
        {
            builder.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        }

        foreach (var (name, value) in response.ExtraHeaders)
        {
            builder.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        builder.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n\r\n");
        var header = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (response.Body.Length > 0)
        {
            await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Socket Listen(IPAddress address, ushort port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.NoDelay = true;
            socket.Bind(new IPEndPoint(address, port));
            socket.Listen(16);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public sealed class AirPlaySessionEstablishedEventArgs : EventArgs
{
    public required string Listener { get; init; }

    public required string PeerPublicKeyBase64 { get; init; }

    public required EndPoint RemoteEndPoint { get; init; }
}
