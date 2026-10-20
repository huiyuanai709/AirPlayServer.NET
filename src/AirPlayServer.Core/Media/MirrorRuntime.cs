using System.Net;
using System.Net.Sockets;
using AirPlayServer.Core.Export;
using AirPlayServer.Core.Notify;
using AirPlayServer.Core.Pairing;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Media;

/// <summary>
/// Accepts several mirror sessions at once. Each IPv4 control peer gets its own
/// data port, timing port, decryptor, decoder, frame slot, and session id.
/// A new phone does not close an existing data socket. The same address reuses
/// its session, which is how one phone reconnects after the screen locks.
/// </summary>
internal sealed class MirrorRuntime : IAsyncDisposable
{
    /// <summary><see cref="int.MaxValue"/> means there is no concurrent-mirror cap.</summary>
    public const int DefaultMaxMirrors = int.MaxValue;

    private readonly object _gate = new();
    private readonly List<MirrorPipe> _pipes = [];
    private readonly MirrorPipe _primary;
    private readonly MirrorSessionHub _hub;
    private readonly IPAddress _listen;
    private readonly int _eventPort;
    private readonly byte[]? _labKey;
    private readonly IMediaKeyUnwrapper _unwrapper;
    private readonly IAirPlayLog _log;
    private readonly bool _acceptHevc;
    private readonly int _maxMirrors;
    private readonly Func<MirrorEndpointResources>? _extra;
    private int _pendingLimit = 8 * 1024 * 1024;
    private int _disposed;

    private MirrorRuntime(
        MirrorPipe primary,
        MirrorSessionHub hub,
        IPAddress listen,
        int eventPort,
        byte[]? labKey,
        IMediaKeyUnwrapper unwrapper,
        IAirPlayLog log,
        bool acceptHevc,
        int maxMirrors,
        Func<MirrorEndpointResources>? extra)
    {
        _primary = primary;
        _hub = hub;
        _listen = listen;
        _eventPort = eventPort;
        _labKey = labKey;
        _unwrapper = unwrapper;
        _log = log;
        _acceptHevc = acceptHevc;
        _maxMirrors = maxMirrors;
        _extra = extra;
        _pipes.Add(primary);
        primary.DeviceNamed += OnDeviceNamed;
    }

    public MirrorActivity Activity => _primary.Activity;

    public MirrorSessionTracker Sessions => _primary.Sessions;

    public MirrorSessionHub SessionEvents => _hub;

    internal MirrorPipe Primary => _primary;

    internal event Action<MirrorPipe>? PipeAdded;

    internal event Action<MirrorPipe>? DeviceNamed;

    internal int PendingEncryptedByteLimit
    {
        get => Volatile.Read(ref _pendingLimit);
        set
        {
            Volatile.Write(ref _pendingLimit, value);
            foreach (var pipe in Snapshot())
            {
                pipe.PendingEncryptedByteLimit = value;
            }
        }
    }

    public static MirrorRuntime Start(
        IPAddress listen,
        int eventPort,
        FrameSlot frames,
        IH264Decoder decoder,
        IMediaKeyUnwrapper? unwrapper,
        ReadOnlySpan<byte> labKey,
        IAirPlayLog log,
        bool acceptHevc = false,
        IHevcDecoder? hevcDecoder = null,
        int maxMirrors = DefaultMaxMirrors,
        Func<MirrorEndpointResources>? extra = null,
        IFairPlaySetup? fairPlay = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(log);
        if (maxMirrors < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMirrors), "Max mirrors must be a positive integer.");
        }

        var keys = unwrapper ?? new UnavailableMediaKeyUnwrapper();
        var hub = new MirrorSessionHub();
        MirrorPipe? primary = null;
        try
        {
            primary = MirrorPipe.Start(
                listen,
                eventPort,
                frames,
                decoder,
                keys,
                labKey,
                log,
                acceptHevc,
                hevcDecoder,
                fairPlay,
                hub);
            return new MirrorRuntime(
                primary,
                hub,
                listen,
                eventPort,
                labKey.Length == 0 ? null : labKey.ToArray(),
                keys,
                log,
                acceptHevc,
                maxMirrors,
                extra);
        }
        catch
        {
            if (primary is not null)
            {
                primary.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            hub.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var pipes = Snapshot();
        _hub.Dispose();
        foreach (var pipe in pipes)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal MirrorPipe[] Snapshot()
    {
        lock (_gate)
        {
            return _pipes.ToArray();
        }
    }

    internal TapClockExportSource[] ExportSources()
    {
        var pipes = Snapshot();
        var sources = new TapClockExportSource[pipes.Length];
        for (var i = 0; i < pipes.Length; i++)
        {
            sources[i] = new TapClockExportSource(pipes[i].Frames, pipes[i].DataPeer);
        }

        return sources;
    }

    internal void ArmForStream(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> ecdhSecret, ulong streamConnectionId)
        => _primary.ArmForStream(contentKey, ecdhSecret, streamConnectionId);

    internal void NotePairingSecret(ReadOnlySpan<byte> secret)
        => NotePairingSecret(secret, null);

    internal void NotePairingSecret(ReadOnlySpan<byte> secret, EndPoint? remote)
    {
        var pipe = Resolve(remote, create: true, out _);
        pipe?.NotePairingSecret(secret);
    }

    internal void RetryPendingFairPlay()
        => RetryPendingFairPlay(null);

    internal void RetryPendingFairPlay(EndPoint? remote)
    {
        var pipe = Resolve(remote, create: false, out _);
        pipe?.RetryPendingFairPlay();
    }

    internal bool TryRespondFairPlay(
        EndPoint? remote,
        ReadOnlySpan<byte> body,
        Span<byte> buffer,
        IFairPlaySetup? fallback,
        out int written,
        out bool refused)
    {
        var pipe = Resolve(remote, create: true, out refused);
        if (pipe is null)
        {
            written = 0;
            return false;
        }

        refused = false;
        var fairPlay = pipe.FairPlay ?? fallback;
        if (fairPlay is null || !fairPlay.TryRespond(body, buffer, out written) || written <= 0 || written > buffer.Length)
        {
            written = 0;
            return false;
        }

        pipe.RetryPendingFairPlay();
        return true;
    }

    internal Http1Response HandleMedia(Http1Request request, PairingSession session, EndPoint? remote)
    {
        var pipe = Resolve(remote, create: true, out var refused);
        if (pipe is null)
        {
            return Http1Response.Text(refused ? 503 : 500, refused
                ? "Concurrent mirror limit reached."
                : "Mirror session was not opened.");
        }

        return pipe.HandleMedia(request, session, remote);
    }

    private MirrorPipe? Resolve(EndPoint? remote, bool create, out bool refused)
    {
        refused = false;
        var ip = remote is IPEndPoint endpoint && endpoint.Address.AddressFamily == AddressFamily.InterNetwork
            ? endpoint.Address
            : null;
        if (ip is null)
        {
            return _primary;
        }

        lock (_gate)
        {
            var existing = FindLocked(ip);
            if (existing is not null)
            {
                return existing;
            }

            if (_primary.ClaimedAddress is null)
            {
                _primary.Claim(ip);
                return _primary;
            }

            if (!create || _pipes.Count >= _maxMirrors)
            {
                refused = true;
                LogLimit(ip);
                return null;
            }
        }

        var created = OpenExtra();
        MirrorPipe? abandoned = null;
        MirrorPipe published;
        lock (_gate)
        {
            var existing = FindLocked(ip);
            if (existing is not null)
            {
                abandoned = created;
                published = existing;
            }
            else if (_primary.ClaimedAddress is null)
            {
                _primary.Claim(ip);
                abandoned = created;
                published = _primary;
            }
            else if (_pipes.Count >= _maxMirrors)
            {
                abandoned = created;
                refused = true;
                LogLimit(ip);
                published = null!;
            }
            else
            {
                created.Claim(ip);
                _pipes.Add(created);
                published = created;
            }
        }

        if (abandoned is not null)
        {
            _ = abandoned.DisposeAsync();
        }

        if (refused || !ReferenceEquals(published, created))
        {
            return refused ? null : published;
        }

        _log.Log(
            AirPlayLogLevel.Information,
            "新的镜像会话已打开，数据端口 " + created.Activity.DataPort
            + "，计时端口 " + created.Activity.TimingPort
            + "，控制来源 " + ip
            + "。已有会话不会被断开。 Opened another mirror. data=" + created.Activity.DataPort
            + " timing=" + created.Activity.TimingPort
            + " control=" + ip
            + ". Existing sessions stay connected.");
        try
        {
            PipeAdded?.Invoke(created);
        }
        catch (Exception ex)
        {
            _log.Log(AirPlayLogLevel.Warning, "额外镜像窗口没有打开: " + ex.Message + " The extra mirror window was not opened: " + ex.Message);
        }

        return created;
    }

    private MirrorPipe? FindLocked(IPAddress ip)
    {
        foreach (var pipe in _pipes)
        {
            if (pipe.ClaimedAddress is { } claimed && claimed.Equals(ip))
            {
                return pipe;
            }
        }

        return null;
    }

    private MirrorPipe OpenExtra()
    {
        FrameSlot frames;
        IH264Decoder decoder;
        IHevcDecoder? hevc;
        IMediaKeyUnwrapper? unwrapper;
        IFairPlaySetup? fairPlay;
        var acceptHevc = _acceptHevc;
        if (_extra is not null)
        {
            var resources = _extra();
            frames = resources.Frames;
            decoder = resources.Decoder;
            hevc = resources.Hevc;
            unwrapper = resources.Unwrapper;
            fairPlay = resources.FairPlay;
            acceptHevc = _acceptHevc && hevc is not null;
        }
        else
        {
            frames = new FrameSlot(_primary.Frames.Buffers);
            decoder = new UnavailableH264Decoder();
            hevc = null;
            unwrapper = null;
            fairPlay = null;
            acceptHevc = false;
            _log.Log(
                AirPlayLogLevel.Warning,
                "额外镜像没有单独的解码器，这一路不会出画面。 No per-device decoder was configured, so this extra mirror will not publish frames.");
        }

        fairPlay ??= _primary.FairPlay is FairPlayKeys ? new FairPlayKeys() : _primary.FairPlay;
        var pipe = MirrorPipe.Start(
            _listen,
            _eventPort,
            frames,
            decoder,
            unwrapper ?? _unwrapper,
            _labKey is null ? ReadOnlySpan<byte>.Empty : _labKey,
            _log,
            acceptHevc,
            hevc,
            fairPlay,
            _hub);
        pipe.PendingEncryptedByteLimit = Volatile.Read(ref _pendingLimit);
        pipe.DeviceNamed += OnDeviceNamed;
        return pipe;
    }

    private void OnDeviceNamed(MirrorPipe pipe)
    {
        try
        {
            DeviceNamed?.Invoke(pipe);
        }
        catch (Exception ex)
        {
            _log.Log(AirPlayLogLevel.Warning, "镜像设备名没有送到窗口: " + ex.Message + " Device name was not applied to the window: " + ex.Message);
        }
    }

    private void LogLimit(IPAddress ip)
    {
        _log.Log(
            AirPlayLogLevel.Warning,
            "同时镜像已达上限 " + _maxMirrors + "，拒绝 " + ip + "。已有会话保持连接。 Concurrent mirror limit "
            + _maxMirrors + " reached. Refused " + ip + ". Existing sessions stay connected.");
    }
}
