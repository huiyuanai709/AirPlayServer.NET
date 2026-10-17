using System.Net.Sockets;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Export;

/// <summary>
/// Pushes the latest decoded frame to TapClock as an ATBF type-3 JPEG.
/// TCP is preferred (host port 42125). A watch directory is the fallback.
/// Nothing is sent until a frame exists, and a repeated generation is not sent again.
/// </summary>
internal sealed class TapClockExporter : IAsyncDisposable
{
    private readonly TapClockExportOptions _options;
    private readonly FrameSlot _frames;
    private readonly IAirPlayLog _log;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _loop;
    private int _delivered;
    private int _loggedSend;
    private int _disposed;

    private TapClockExporter(TapClockExportOptions options, FrameSlot frames, IAirPlayLog log, CancellationToken cancellationToken)
    {
        _options = options;
        _frames = frames;
        _log = log;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(RunAsync);
    }

    public int FramesDelivered => Volatile.Read(ref _delivered);

    public static TapClockExporter Start(TapClockExportOptions options, FrameSlot frames, IAirPlayLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(frames);
        options.Validate();
        return new TapClockExporter(options, frames, log, cancellationToken);
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
        long sentGeneration = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_frames.TryRead(out var width, out var height, out var stride, out var pixels, out var generation)
                        && generation != sentGeneration)
                    {
                        var jpeg = BaselineJpegEncoder.EncodeBgra(pixels, width, height, stride, _options.JpegQuality);
                        var attempt = await DeliverAsync(jpeg, socket, cancellationToken).ConfigureAwait(false);
                        socket = attempt.Socket;
                        if (attempt.Delivered)
                        {
                            sentGeneration = generation;
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
                    socket?.Dispose();
                    socket = null;
                    _log.Log(AirPlayLogLevel.Warning, "TapClock frame export failed: " + ex.Message);
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

    private async Task<ExportAttempt> DeliverAsync(byte[] jpeg, Socket? socket, CancellationToken cancellationToken)
    {
        var tcpOk = string.IsNullOrWhiteSpace(_options.Endpoint);
        if (!tcpOk)
        {
            socket = await SendJpegAsync(socket, jpeg, cancellationToken).ConfigureAwait(false);
            tcpOk = socket is not null;
        }

        var fileOk = string.IsNullOrWhiteSpace(_options.WatchDirectory);
        if (!string.IsNullOrWhiteSpace(_options.WatchDirectory))
        {
            await AtomicImageFile.WriteJpegAsync(_options.WatchDirectory, jpeg, cancellationToken).ConfigureAwait(false);
            fileOk = true;
            if (string.IsNullOrWhiteSpace(_options.Endpoint) && Interlocked.Exchange(ref _loggedSend, 1) == 0)
            {
                _log.Log(AirPlayLogLevel.Information, "TapClock JPEG written to " + Path.Combine(_options.WatchDirectory, "mirror.jpg"));
            }
        }

        return new ExportAttempt(tcpOk && fileOk, socket);
    }

    private async Task<Socket?> SendJpegAsync(Socket? socket, byte[] jpeg, CancellationToken cancellationToken)
    {
        if (!TapClockExportOptions.TryParseEndpoint(_options.Endpoint!, out var host, out var port))
        {
            return null;
        }

        var packet = AtbfFraming.ImageFrame(jpeg);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (socket is null || !socket.Connected)
                {
                    socket?.Dispose();
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true,
                    };
                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await socket.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);
                }

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

                if (Interlocked.Exchange(ref _loggedSend, 1) == 0)
                {
                    _log.Log(AirPlayLogLevel.Information, "TapClock ATBF JPEG sent to " + host + ":" + port + " (type 3, flags 0).");
                }

                return socket;
            }
            catch (Exception) when (attempt == 0 && !cancellationToken.IsCancellationRequested)
            {
                socket?.Dispose();
                socket = null;
            }
            catch
            {
                socket?.Dispose();
                throw;
            }
        }

        return null;
    }

    private readonly record struct ExportAttempt(bool Delivered, Socket? Socket);
}
