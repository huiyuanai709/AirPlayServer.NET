using System.Net;
using System.Net.Sockets;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Export;

/// <summary>
/// Pushes the latest decoded frame to TapClock as an ATBF type-3 JPEG.
/// TCP is preferred (host port 42125). A watch directory writes one JPEG per
/// mirror data peer (<c>mirror-192.168.5.20.jpg</c>). The ATBF header stays
/// type 3 with flags 0; it has no peer field, so the log names the data peer.
/// Nothing is sent until a frame exists, and a repeated generation is not sent again.
/// </summary>
internal sealed class TapClockExporter : IAsyncDisposable
{
    internal static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(2);

    private readonly TapClockExportOptions _options;
    private readonly Func<IReadOnlyList<TapClockExportSource>> _sources;
    private readonly Dictionary<FrameSlot, long> _sentGeneration = new();
    private readonly HashSet<string> _loggedPaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedTcpPeers = new(StringComparer.Ordinal);
    private readonly IAirPlayLog _log;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _loop;
    private int _delivered;
    private int _connectFailed;
    private int _fileFailed;
    private long _nextConnectLog;
    private long _nextFileLog;
    private int _disposed;

    private TapClockExporter(TapClockExportOptions options, Func<IReadOnlyList<TapClockExportSource>> sources, IAirPlayLog log, CancellationToken cancellationToken)
    {
        _options = options;
        _sources = sources;
        _log = log;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(RunAsync);
    }

    public int FramesDelivered => Volatile.Read(ref _delivered);

    public static TapClockExporter Start(TapClockExportOptions options, FrameSlot frames, IAirPlayLog log, CancellationToken cancellationToken)
        => Start(options, frames, log, cancellationToken, peer: null);

    public static TapClockExporter Start(TapClockExportOptions options, FrameSlot frames, IAirPlayLog log, CancellationToken cancellationToken, Func<IPAddress?>? peer)
    {
        ArgumentNullException.ThrowIfNull(frames);
        return Start(options, () => new TapClockExportSource[] { new(frames, peer?.Invoke()) }, log, cancellationToken);
    }

    internal static TapClockExporter Start(TapClockExportOptions options, Func<IReadOnlyList<TapClockExportSource>> sources, IAirPlayLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sources);
        options.Validate();
        return new TapClockExporter(options, sources, log, cancellationToken);
    }

    public async Task<bool> WaitForDeliveryAsync(int count, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var start = Environment.TickCount64;
        while (Volatile.Read(ref _delivered) < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Environment.TickCount64 - start > timeout.TotalMilliseconds)
            {
                return false;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _cancellation.Dispose();
    }

    private async Task RunAsync()
    {
        var cancellationToken = _cancellation.Token;
        var interval = TimeSpan.FromSeconds(1d / _options.FramesPerSecond);
        if (interval < TimeSpan.FromMilliseconds(30))
        {
            interval = TimeSpan.FromMilliseconds(30);
        }

        Socket? socket = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.Endpoint))
            {
                socket = await EnsureConnectedAsync(socket, cancellationToken).ConfigureAwait(false);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var width = 0;
                var height = 0;
                try
                {
                    var pending = new List<PendingJpeg>(4);
                    foreach (var source in _sources())
                    {
                        if (!source.Frames.TryLease(out width, out height, out var stride, out var pixels, out var generation, out var lease))
                        {
                            continue;
                        }

                        using (lease)
                        {
                            if (_sentGeneration.TryGetValue(source.Frames, out var sent) && sent == generation)
                            {
                                continue;
                            }

                            var jpeg = BaselineJpegEncoder.EncodeBgra(pixels, width, height, stride, _options.JpegQuality);
                            pending.Add(new PendingJpeg(source.Frames, generation, jpeg, source.Peer));
                        }
                    }

                    if (pending.Count == 0)
                    {
                        if (socket is null && !string.IsNullOrWhiteSpace(_options.Endpoint))
                        {
                            socket = await EnsureConnectedAsync(socket, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        foreach (var item in pending)
                        {
                            var attempt = await DeliverAsync(item.Jpeg, socket, item.Peer, cancellationToken).ConfigureAwait(false);
                            socket = attempt.Socket;
                            if (!attempt.Delivered)
                            {
                                continue;
                            }

                            _sentGeneration[item.Frames] = item.Generation;
                            Interlocked.Increment(ref _delivered);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A file-publish failure used to land here and dispose the live
                    // TapClock socket. Encode failures stay on this connection;
                    // SendJpegAsync already drops the socket when the send itself fails.
                    _log.Log(AirPlayLogLevel.Warning, "TapClock frame export failed (" + width + "x" + height + "): " + ex.Message);
                }

                try
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            socket?.Dispose();
        }
    }

    private async Task<ExportAttempt> DeliverAsync(byte[] jpeg, Socket? socket, IPAddress? peer, CancellationToken cancellationToken)
    {
        var wantTcp = !string.IsNullOrWhiteSpace(_options.Endpoint);
        var tcpOk = !wantTcp;
        if (wantTcp)
        {
            socket = await SendJpegAsync(socket, jpeg, peer, cancellationToken).ConfigureAwait(false);
            tcpOk = socket is not null;
        }

        var wantFile = !string.IsNullOrWhiteSpace(_options.WatchDirectory);
        var fileOk = !wantFile;
        if (wantFile)
        {
            var fileName = MirrorExportNames.JpegFileName(peer);
            var fullPath = Path.GetFullPath(Path.Combine(_options.WatchDirectory!, fileName));
            try
            {
                await AtomicImageFile.WriteJpegAsync(_options.WatchDirectory!, fileName, jpeg, cancellationToken).ConfigureAwait(false);
                fileOk = true;
                if (_loggedPaths.Add(fullPath))
                {
                    var who = peer is null ? "未知 / unknown" : peer.ToString();
                    _log.Log(AirPlayLogLevel.Information, "TapClock JPEG 已写入 " + fullPath + "。来源 " + who + "。 TapClock JPEG written to " + fullPath + ". Source " + who + ".");
                }
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && !cancellationToken.IsCancellationRequested)
            {
                NoteFileFailure(ex);
            }
        }

        // TCP is the contract for --tapclock. A denied watch directory must not
        // drop that socket or keep the same generation retrying forever.
        var delivered = wantTcp ? tcpOk : fileOk;
        return new ExportAttempt(delivered, socket);
    }

    private void NoteFileFailure(Exception ex)
    {
        var now = Environment.TickCount64;
        if (_fileFailed == 1 && now < _nextFileLog)
        {
            return;
        }

        _fileFailed = 1;
        _nextFileLog = now + 5000;
        _log.Log(AirPlayLogLevel.Warning, "TapClock watch file was not published: " + ex.Message);
    }

    private async Task<Socket?> SendJpegAsync(Socket? socket, byte[] jpeg, IPAddress? peer, CancellationToken cancellationToken)
    {
        if (!TapClockExportOptions.TryParseEndpoint(_options.Endpoint!, out var host, out var port))
        {
            NoteConnectFailure(host, port, new IOException("TapClock endpoint could not be parsed."));
            return null;
        }

        var packet = AtbfFraming.ImageFrame(jpeg);
        socket = await EnsureConnectedAsync(socket, cancellationToken).ConfigureAwait(false);
        if (socket is null)
        {
            return null;
        }

        try
        {
            var sent = 0;
            while (sent < packet.Length)
            {
                var count = await socket.SendAsync(packet.AsMemory(sent), SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new IOException("TapClock closed the connection.");
                }

                sent += count;
            }

            var who = peer is null ? "unknown" : peer.ToString();
            if (_loggedTcpPeers.Add(who))
            {
                _log.Log(
                    AirPlayLogLevel.Information,
                    "TapClock ATBF JPEG 已发送到 " + host + ":" + port + "，镜像数据来源 " + who
                    + "（type 3, flags 0, " + jpeg.Length + " bytes）。ATBF 头不含来源地址。"
                    + " TapClock ATBF JPEG sent to " + host + ":" + port + ", mirror data peer " + who
                    + " (type 3, flags 0, " + jpeg.Length + " bytes). The ATBF header does not carry the source address.");
            }

            return socket;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            NoteConnectFailure(host, port, ex);
            return null;
        }
    }

    private async Task<Socket?> EnsureConnectedAsync(Socket? socket, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            return socket;
        }

        if (socket is { Connected: true })
        {
            return socket;
        }

        socket?.Dispose();
        socket = null;
        if (!TapClockExportOptions.TryParseEndpoint(_options.Endpoint, out var host, out var port))
        {
            NoteConnectFailure(_options.Endpoint, 0, new IOException("TapClock endpoint must be host:port."));
            return null;
        }

        var endpoint = await ResolveIPv4Async(host, port, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            NoteConnectFailure(host, port, new IOException("No IPv4 address for " + host + "."));
            return null;
        }

        return await ConnectWithinTimeoutAsync(endpoint, host, port, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Socket?> ConnectWithinTimeoutAsync(IPEndPoint endpoint, string host, int port, CancellationToken cancellationToken)
    {
        var connected = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        // A closed port fails immediately on Linux. Windows can leave the same
        // SYN pending, and cancelling ConnectAsync does not always complete it.
        // Own the deadline and close the socket so the export loop can retry.
        Task connectTask;
        try
        {
            connectTask = connected.ConnectAsync(endpoint);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            connected.Dispose();
            NoteConnectFailure(host, port, ex);
            return null;
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutTask = Task.Delay(ConnectAttemptTimeout, attempt.Token);
        await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            connected.Dispose();
            Observe(connectTask);
            throw new OperationCanceledException(cancellationToken);
        }

        if (!connectTask.IsCompleted)
        {
            NoteConnectFailure(host, port, new TimeoutException("timed out"));
            connected.Dispose();
            Observe(connectTask);
            return null;
        }

        try
        {
            await connectTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            connected.Dispose();
            NoteConnectFailure(host, port, ex);
            return null;
        }

        if (!connected.Connected)
        {
            connected.Dispose();
            NoteConnectFailure(host, port, new IOException("TapClock connect finished without a connection."));
            return null;
        }

        attempt.Cancel();
        _connectFailed = 0;
        _log.Log(AirPlayLogLevel.Information, "TapClock connected to " + host + ":" + port + ".");
        return connected;
    }

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<IPEndPoint?> ResolveIPv4Async(string host, int port, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var parsed))
        {
            return parsed.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(parsed, port) : null;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            NoteConnectFailure(host, port, ex);
            return null;
        }

        foreach (var address in addresses)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                return new IPEndPoint(address, port);
            }
        }

        return null;
    }

    private void NoteConnectFailure(string host, int port, Exception ex)
    {
        var now = Environment.TickCount64;
        if (_connectFailed == 1 && now < _nextConnectLog)
        {
            return;
        }

        _connectFailed = 1;
        _nextConnectLog = now + 5000;
        _log.Log(AirPlayLogLevel.Warning, "TapClock connect to " + host + ":" + port + " failed: " + ex.Message + ". Will keep retrying.");
    }

    private readonly record struct ExportAttempt(bool Delivered, Socket? Socket);

    private readonly record struct PendingJpeg(FrameSlot Frames, long Generation, byte[] Jpeg, IPAddress? Peer);
}
