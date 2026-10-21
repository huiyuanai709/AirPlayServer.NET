using System.Globalization;
using System.Net;

namespace AirPlayServer.Core.Notify;

/// <summary>
/// Published mirror-data session.
/// Connected means the mirror data TCP port has parsed one valid 128-byte
/// packet header. An RTSP SETUP alone is not connected, and an HTTP probe
/// on that port is ignored. Disconnect is published only after
/// <see cref="DisconnectDebounceMs"/> with no replacement data client.
/// A gap shorter than the debounce does not emit and keeps <see cref="MirrorSessionSnapshot.SessionId"/>.
/// Process shutdown disposes this tracker first so the exit does not emit disconnected.
/// </summary>
public sealed class MirrorSessionTracker : IDisposable, IMirrorSessionEvents
{
    public const int DefaultDisconnectDebounceMs = 3000;

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private long _nextGeneration;
    private long _activeGeneration;
    private long _debounceToken;
    private long _latchedGeneration;
    private string? _latchedReason;
    private CancellationTokenSource? _debounceCts;
    private bool _connected;
    private bool _rotateAfterDisconnect;
    private bool _closeAfterDisconnect;
    private bool _disposed;
    private DateTimeOffset? _since;
    private string? _sessionId;
    private string? _deviceName;
    private string? _sourceIp;
    private bool _sourceFromData;
    private string? _generationDeviceName;
    private string? _generationSourceIp;
    private string? _pendingDeviceName;
    private string? _pendingSourceIp;
    private string _pendingReason = MirrorSessionReasons.DataClosed;
    private string _ingress = MirrorIngress.AirPlay;
    private string? _deviceId;
    private int _debounceMs = DefaultDisconnectDebounceMs;

    public MirrorSessionTracker()
        : this(TimeProvider.System)
    {
    }

    public MirrorSessionTracker(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>
    /// Tests hold this open to prove a disconnect is not published early.
    /// Production leaves it null and uses <see cref="Task.Delay(int, CancellationToken)"/>.
    /// </summary>
    internal Func<int, CancellationToken, Task>? DelayAsync { get; set; }

    public int DisconnectDebounceMs
    {
        get => Volatile.Read(ref _debounceMs);
        set => Volatile.Write(ref _debounceMs, Math.Clamp(value, 0, SessionNotifyConfig.MaxDisconnectDebounceMs));
    }

    public event EventHandler<MirrorSessionEventArgs>? Changed;

    public MirrorSessionSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return SnapshotLocked();
        }
    }

    /// <summary>
    /// Records whether this tracker is wireless AirPlay or QuickTime USB, and
    /// the stable USB serial when there is one. Call this before
    /// <see cref="BeginData"/>.
    /// </summary>
    public void NoteRoute(string ingress, string? deviceId)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (ingress is MirrorIngress.AirPlay or MirrorIngress.Usb)
            {
                _ingress = ingress;
            }

            var id = SanitizeDevice(deviceId);
            if (id is not null)
            {
                _deviceId = id;
            }
        }
    }

    /// <summary>Record the AirPlay sender name and, when idle, mint a new session id.</summary>
    public void NoteSessionStarting(string? deviceName)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var name = SanitizeDevice(deviceName);
            if (name is not null)
            {
                _deviceName = name;
                // A rename while this data generation is still up belongs to the
                // disconnect that generation will publish. A setup that arrives
                // during the debounce is the next session and must not rewrite it.
                if (_activeGeneration != 0 && _debounceCts is null)
                {
                    _generationDeviceName = name;
                }
            }

            if (!_connected && _debounceCts is null)
            {
                _sessionId = NewSessionId();
                _rotateAfterDisconnect = false;
                _closeAfterDisconnect = false;
                return;
            }

            _rotateAfterDisconnect = true;
        }
    }

    /// <summary>Full RTSP teardown. The id is cleared when the disconnect is published, or immediately when already idle.</summary>
    public void NoteSessionEnded()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _closeAfterDisconnect = true;
            if (!_connected && _debounceCts is null)
            {
                _sessionId = null;
                _closeAfterDisconnect = false;
                _rotateAfterDisconnect = false;
            }
        }
    }

    /// <summary>Attach a teardown reason to the data client that is active right now.</summary>
    public void LatchDetachReason(string reason)
    {
        lock (_gate)
        {
            if (_disposed || _activeGeneration == 0)
            {
                return;
            }

            _latchedGeneration = _activeGeneration;
            _latchedReason = SanitizeReason(reason) ?? MirrorSessionReasons.DataClosed;
        }
    }

    /// <summary>
    /// Records a peer address for status and the next connect event.
    /// A control-connection address does not replace one already taken from the data socket.
    /// </summary>
    public void NotePeer(IPAddress? address)
        => ApplyPeer(address, fromDataSocket: false, bindActiveGeneration: false);

    /// <summary>
    /// Records the mirror data socket's peer. This replaces a control-connection
    /// address and, while that generation is still connected, the address the
    /// disconnect event will publish.
    /// </summary>
    public void NoteDataPeer(IPAddress? address)
        => ApplyPeer(address, fromDataSocket: true, bindActiveGeneration: true);

    /// <summary>A valid mirror header was parsed. Returns 0 when the tracker is disposed.</summary>
    public long BeginData(DateTimeOffset? at = null, IPAddress? source = null)
    {
        MirrorSessionEventArgs? raised = null;
        long generation;
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            if (FormatPeer(source) is { } text)
            {
                _sourceIp = text;
                _sourceFromData = true;
            }

            generation = ++_nextGeneration;
            _activeGeneration = generation;
            CancelDebounceLocked();
            _rotateAfterDisconnect = false;
            _closeAfterDisconnect = false;
            _generationSourceIp = _sourceIp;
            _generationDeviceName = _deviceName;
            if (!_connected)
            {
                var stamp = at ?? _time.GetUtcNow();
                _connected = true;
                _since = stamp;
                if (string.IsNullOrEmpty(_sessionId))
                {
                    _sessionId = NewSessionId();
                }

                raised = BuildLocked(MirrorSessionPhase.Connected, MirrorSessionReasons.MirrorData, stamp);
            }
        }

        Raise(raised);
        return generation;
    }

    public void EndData(long generation)
    {
        MirrorSessionEventArgs? immediate = null;
        int delayMs;
        long token;
        CancellationTokenSource? cts = null;
        Func<int, CancellationToken, Task>? delay;
        lock (_gate)
        {
            if (_disposed || generation == 0 || generation != _activeGeneration)
            {
                return;
            }

            _activeGeneration = 0;
            var reason = _latchedGeneration == generation
                ? _latchedReason ?? MirrorSessionReasons.DataClosed
                : MirrorSessionReasons.DataClosed;
            _latchedGeneration = 0;
            _latchedReason = null;
            if (!_connected)
            {
                return;
            }

            _pendingReason = reason;
            _pendingDeviceName = _generationDeviceName;
            _pendingSourceIp = _generationSourceIp;
            delayMs = DisconnectDebounceMs;
            if (delayMs <= 0)
            {
                immediate = PublishDisconnectLocked();
                delay = null;
                token = 0;
            }
            else
            {
                token = ++_debounceToken;
                cts = new CancellationTokenSource();
                _debounceCts = cts;
                delay = DelayAsync;
            }
        }

        if (immediate is not null)
        {
            Raise(immediate);
            return;
        }

        if (cts is not null)
        {
            _ = RunDebounceAsync(token, delayMs, cts, delay);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _activeGeneration = 0;
            cts = _debounceCts;
            _debounceCts = null;
            _debounceToken++;
        }

        Cancel(cts);
    }

    private async Task RunDebounceAsync(long token, int delayMs, CancellationTokenSource cts, Func<int, CancellationToken, Task>? delay)
    {
        MirrorSessionEventArgs? raised = null;
        try
        {
            if (delay is not null)
            {
                await delay(delayMs, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
            }

            lock (_gate)
            {
                if (_disposed || _debounceToken != token || _activeGeneration != 0 || !_connected)
                {
                    return;
                }

                raised = PublishDisconnectLocked();
                if (ReferenceEquals(_debounceCts, cts))
                {
                    _debounceCts = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts.Dispose();
        }

        Raise(raised);
    }

    private MirrorSessionEventArgs PublishDisconnectLocked()
    {
        var at = _time.GetUtcNow();
        var announced = _sessionId;
        var reason = _pendingReason;
        var device = _pendingDeviceName;
        var source = _pendingSourceIp;
        _connected = false;
        _since = null;
        var rotate = _rotateAfterDisconnect;
        var close = _closeAfterDisconnect;
        _rotateAfterDisconnect = false;
        _closeAfterDisconnect = false;
        if (rotate)
        {
            _sessionId = NewSessionId();
        }
        else if (close)
        {
            _sessionId = null;
        }

        return new MirrorSessionEventArgs
        {
            Phase = MirrorSessionPhase.Disconnected,
            At = at,
            SessionId = announced,
            DeviceName = device,
            SourceIp = source,
            Reason = reason,
            Ingress = _ingress,
            DeviceId = _deviceId,
        };
    }

    private MirrorSessionEventArgs BuildLocked(MirrorSessionPhase phase, string reason, DateTimeOffset at)
        => new()
        {
            Phase = phase,
            At = at,
            SessionId = _sessionId,
            DeviceName = _deviceName,
            SourceIp = _sourceIp,
            Reason = reason,
            Ingress = _ingress,
            DeviceId = _deviceId,
        };

    private MirrorSessionSnapshot SnapshotLocked()
        => new()
        {
            Connected = _connected,
            Since = _since,
            SessionId = _sessionId,
            DeviceName = _deviceName,
            SourceIp = _sourceIp,
            DisconnectPending = _debounceCts is not null,
            Ingress = _ingress,
            DeviceId = _deviceId,
        };

    private void ApplyPeer(IPAddress? address, bool fromDataSocket, bool bindActiveGeneration)
    {
        var text = FormatPeer(address);
        if (text is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!fromDataSocket && _sourceFromData)
            {
                return;
            }

            _sourceIp = text;
            if (fromDataSocket)
            {
                _sourceFromData = true;
            }

            if (bindActiveGeneration && _connected && _activeGeneration != 0 && _debounceCts is null)
            {
                _generationSourceIp = text;
            }
        }
    }

    /// <summary>IP without a port. Unspecified addresses are not a peer.</summary>
    internal static string? FormatPeer(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6None))
        {
            return null;
        }

        return address.ToString();
    }

    private void CancelDebounceLocked()
    {
        _debounceToken++;
        var cts = _debounceCts;
        _debounceCts = null;
        Cancel(cts);
    }

    private static void Cancel(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Raise(MirrorSessionEventArgs? args)
    {
        if (args is null)
        {
            return;
        }

        try
        {
            Changed?.Invoke(this, args);
        }
        catch (Exception)
        {
            // A notify listener must not tear down the mirror read loop.
        }
    }

    private static string NewSessionId() => Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture);

    private static string? SanitizeDevice(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = StripControls(value.Trim());
        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length <= 128 ? trimmed : trimmed[..128];
    }

    private static string? SanitizeReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = StripControls(value.Trim());
        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length <= 64 ? trimmed : trimmed[..64];
    }

    private static string StripControls(string value)
    {
        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;
        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' or '\0')
            {
                continue;
            }

            buffer[length++] = ch;
        }

        return new string(buffer[..length]);
    }
}

public enum MirrorSessionPhase
{
    Connected = 1,
    Disconnected = 2,
}

public static class MirrorSessionReasons
{
    public const string MirrorData = "mirror-data";

    public const string DataClosed = "data-closed";

    public const string VideoTeardown = "video-teardown";

    public const string SessionTeardown = "session-teardown";
}

public sealed class MirrorSessionSnapshot
{
    public bool Connected { get; init; }

    public DateTimeOffset? Since { get; init; }

    public string? SessionId { get; init; }

    public string? DeviceName { get; init; }

    /// <summary>Peer address without a port. Null when the remote endpoint was not known.</summary>
    public string? SourceIp { get; init; }

    /// <summary>True after the data socket drops and before the debounce publishes disconnected.</summary>
    public bool DisconnectPending { get; init; }

    /// <summary><see cref="MirrorIngress.AirPlay"/> or <see cref="MirrorIngress.Usb"/>.</summary>
    public string Ingress { get; init; } = MirrorIngress.AirPlay;

    /// <summary>USB serial when <see cref="Ingress"/> is usb. Null for AirPlay.</summary>
    public string? DeviceId { get; init; }
}

public sealed class MirrorSessionEventArgs : EventArgs
{
    public required MirrorSessionPhase Phase { get; init; }

    public required DateTimeOffset At { get; init; }

    public string? SessionId { get; init; }

    public string? DeviceName { get; init; }

    /// <summary>Peer address without a port. Null when the remote endpoint was not known.</summary>
    public string? SourceIp { get; init; }

    public required string Reason { get; init; }

    /// <summary><see cref="MirrorIngress.AirPlay"/> or <see cref="MirrorIngress.Usb"/>.</summary>
    public string Ingress { get; init; } = MirrorIngress.AirPlay;

    /// <summary>USB serial when <see cref="Ingress"/> is usb. Null for AirPlay.</summary>
    public string? DeviceId { get; init; }
}
