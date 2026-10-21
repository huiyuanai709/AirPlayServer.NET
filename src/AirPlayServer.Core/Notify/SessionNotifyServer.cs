using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AirPlayServer.Core.Notify;

public sealed class SessionNotifyServer : IAsyncDisposable
{
    private const int MaxRequestBytes = 64 * 1024;

    private readonly Socket _listen;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _loop;
    private readonly MirrorSessionTracker _sessions;
    private readonly Func<IReadOnlyList<MirrorSessionSnapshot>>? _listSessions;
    private readonly string _receiverName;
    private readonly Func<SessionNotifyConfig> _current;
    private readonly Func<SessionNotifyConfig, SessionNotifyConfig> _apply;
    private readonly IAirPlayLog _log;
    private int _disposed;

    private SessionNotifyServer(
        Socket listen,
        MirrorSessionTracker sessions,
        Func<IReadOnlyList<MirrorSessionSnapshot>>? listSessions,
        string receiverName,
        Func<SessionNotifyConfig> current,
        Func<SessionNotifyConfig, SessionNotifyConfig> apply,
        IAirPlayLog log)
    {
        _listen = listen;
        _sessions = sessions;
        _listSessions = listSessions;
        _receiverName = receiverName;
        _current = current;
        _apply = apply;
        _log = log;
        _loop = Task.Run(AcceptLoop);
    }

    public IPEndPoint EndPoint => (IPEndPoint)_listen.LocalEndPoint!;

    public static SessionNotifyServer Start(
        IPAddress address,
        int port,
        MirrorSessionTracker sessions,
        string receiverName,
        Func<SessionNotifyConfig> current,
        Func<SessionNotifyConfig, SessionNotifyConfig> apply,
        IAirPlayLog? log = null,
        Func<IReadOnlyList<MirrorSessionSnapshot>>? listSessions = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(apply);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("配置服务只监听 IPv4。", nameof(address));
        }

        if (port is < 0 or > 65535)
        {
            throw new ArgumentException("配置服务端口无效。", nameof(port));
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(address, port));
            socket.Listen(8);
            return new SessionNotifyServer(socket, sessions, listSessions, receiverName ?? "", current, apply, log ?? NullAirPlayLog.Instance);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);
        _listen.Dispose();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _cancellation.Dispose();
    }

    private async Task AcceptLoop()
    {
        var cancellationToken = _cancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listen.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(Socket client)
    {
        using (client)
        {
            try
            {
                var request = await ReadRequestAsync(client, _cancellation.Token).ConfigureAwait(false);
                var response = Handle(request);
                await client.SendAsync(response, _cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
            {
            }
            catch (Exception ex)
            {
                _log.Log(AirPlayLogLevel.Warning, "配置页请求失败: " + ex.Message);
            }
        }
    }

    private byte[] Handle(ParsedRequest request)
    {
        if (request.Method == "GET" && (request.Path is "/" or "/index.html"))
        {
            return Response(200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(SessionNotifyPage.Html));
        }

        if (request.Method == "GET" && request.Path == "/api/status")
        {
            var body = ToBody(_sessions.GetSnapshot(), _receiverName);
            if (_listSessions is not null)
            {
                var snapshots = _listSessions();
                var sessions = new MirrorStatusBody[snapshots.Count];
                for (var i = 0; i < snapshots.Count; i++)
                {
                    sessions[i] = ToBody(snapshots[i], _receiverName);
                }

                body.Sessions = sessions;
            }

            return Json(200, "OK", JsonSerializer.Serialize(body, SessionNotifyJsonContext.Default.MirrorStatusBody));
        }

        if (request.Method == "GET" && request.Path == "/api/config")
        {
            return Json(200, "OK", JsonSerializer.Serialize(_current(), SessionNotifyJsonContext.Default.SessionNotifyConfig));
        }

        if (request.Method == "POST" && request.Path == "/api/config")
        {
            return Save(request.Body);
        }

        return Response(404, "Not Found", "text/plain; charset=utf-8", "未找到。"u8.ToArray());
    }

    private static MirrorStatusBody ToBody(MirrorSessionSnapshot snapshot, string receiverName)
    {
        var name = string.IsNullOrWhiteSpace(snapshot.DeviceName) ? receiverName : snapshot.DeviceName;
        return new MirrorStatusBody
        {
            Connected = snapshot.Connected,
            Since = snapshot.Since is { } since ? SessionNotifyConfig.FormatTimestamp(since) : null,
            Name = name ?? "",
            SourceIp = snapshot.SourceIp ?? "",
            SessionId = snapshot.SessionId,
            DisconnectPending = snapshot.DisconnectPending,
            Ingress = string.IsNullOrEmpty(snapshot.Ingress) ? MirrorIngress.AirPlay : snapshot.Ingress,
            DeviceId = snapshot.DeviceId,
        };
    }

    private byte[] Save(byte[] body)
    {
        SessionNotifyConfig? config;
        try
        {
            config = JsonSerializer.Deserialize(body, SessionNotifyJsonContext.Default.SessionNotifyConfig);
        }
        catch (JsonException)
        {
            return Response(400, "Bad Request", "text/plain; charset=utf-8", "配置不是 JSON。"u8.ToArray());
        }

        if (config is null)
        {
            return Response(400, "Bad Request", "text/plain; charset=utf-8", "配置是空的。"u8.ToArray());
        }

        try
        {
            config.Normalize();
            var saved = _apply(config);
            var endpoint = EndPoint;
            var restart = saved.ConfigPort != endpoint.Port
                || !string.Equals(saved.ConfigBindAddress, endpoint.Address.ToString(), StringComparison.Ordinal);
            var result = new SessionNotifySaveResult
            {
                Ok = true,
                RestartRequired = restart,
                Message = restart ? "已保存。监听地址或端口在下次启动时生效。" : "已保存。",
                Config = saved,
            };
            return Json(200, "OK", JsonSerializer.Serialize(result, SessionNotifyJsonContext.Default.SessionNotifySaveResult));
        }
        catch (ArgumentException ex)
        {
            return Response(400, "Bad Request", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Response(500, "Internal Server Error", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("没有写进配置文件: " + ex.Message));
        }
    }

    private static byte[] Json(int status, string reason, string json)
        => Response(status, reason, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));

    private static byte[] Response(int status, string reason, string contentType, byte[] body)
    {
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + reason + "\r\n"
            + "Content-Type: " + contentType + "\r\n"
            + "Content-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n\r\n");
        var message = new byte[head.Length + body.Length];
        head.CopyTo(message, 0);
        body.CopyTo(message, head.Length);
        return message;
    }

    private static async Task<ParsedRequest> ReadRequestAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var received = 0;
        while (true)
        {
            var headerEnd = IndexOf(buffer.AsSpan(0, received), "\r\n\r\n"u8);
            if (headerEnd >= 0)
            {
                var header = Encoding.ASCII.GetString(buffer, 0, headerEnd);
                var contentLength = ParseContentLength(header);
                var total = headerEnd + 4 + contentLength;
                if (total > MaxRequestBytes)
                {
                    throw new InvalidOperationException("配置页请求太大。");
                }

                if (buffer.Length < total)
                {
                    Array.Resize(ref buffer, total);
                }

                while (received < total)
                {
                    var count = await socket.ReceiveAsync(buffer.AsMemory(received, total - received), cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        throw new IOException("配置页请求中断。");
                    }

                    received += count;
                }

                return Parse(buffer.AsSpan(0, total), header, headerEnd, contentLength);
            }

            if (received >= MaxRequestBytes)
            {
                throw new InvalidOperationException("配置页请求太大。");
            }

            if (received == buffer.Length)
            {
                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaxRequestBytes));
            }

            var read = await socket.ReceiveAsync(buffer.AsMemory(received, buffer.Length - received), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("配置页请求中断。");
            }

            received += read;
        }
    }

    private static ParsedRequest Parse(ReadOnlySpan<byte> message, string header, int headerEnd, int contentLength)
    {
        var lineEnd = header.IndexOf("\r\n", StringComparison.Ordinal);
        var requestLine = lineEnd < 0 ? header : header[..lineEnd];
        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
        {
            throw new InvalidOperationException("配置页请求行无效。");
        }

        var path = parts[1];
        var query = path.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            path = path[..query];
        }

        if (path.Length > 1)
        {
            path = path.TrimEnd('/');
        }

        var body = contentLength == 0 ? [] : message.Slice(headerEnd + 4, contentLength).ToArray();
        return new ParsedRequest(parts[0].ToUpperInvariant(), path, body);
    }

    private static int ParseContentLength(string header)
    {
        var lines = header.Split("\r\n");
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (!line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(line[(colon + 1)..].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length)
                || length < 0)
            {
                throw new InvalidOperationException("Content-Length 无效。");
            }

            return length;
        }

        return 0;
    }

    private static int IndexOf(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> pattern)
    {
        for (var i = 0; i + pattern.Length <= buffer.Length; i++)
        {
            if (buffer.Slice(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }

        return -1;
    }

    private readonly record struct ParsedRequest(string Method, string Path, byte[] Body);
}
