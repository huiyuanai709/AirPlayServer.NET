using AirPlayServer.Core.Export;
using AirPlayServer.Core.Media;
using AirPlayServer.Core.Notify;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Usb;

/// <summary>
/// Watches for iPhones and runs one QuickTime USB mirror per serial.
/// Each phone gets its own decoder, frame slot, and session id. Wireless
/// AirPlay sessions stay on <see cref="MirrorRuntime"/> and share the same
/// mirror budget and session hub.
/// </summary>
internal sealed class UsbQtIngress : IAsyncDisposable
{
    private readonly MirrorRuntime _mirror;
    private readonly MirrorSessionHub _hub;
    private readonly MirrorDevicePolicy _policy;
    private readonly IUsbDeviceCatalog _catalog;
    private readonly Func<UsbMirrorResources> _resources;
    private readonly double _width;
    private readonly double _height;
    private readonly string? _filter;
    private readonly IAirPlayLog _log;
    private readonly Action<UsbMirrorSession>? _opened;
    private readonly Action<UsbMirrorSession>? _closed;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private readonly List<UsbMirrorSession> _sessions = [];
    private readonly List<Task> _tasks = [];
    private readonly Dictionary<string, long> _cooldown = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _logged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _loop;
    private int _stopping;
    private int _disposed;

    private UsbQtIngress(
        MirrorRuntime mirror,
        MirrorSessionHub hub,
        MirrorDevicePolicy policy,
        IUsbDeviceCatalog catalog,
        Func<UsbMirrorResources> resources,
        double width,
        double height,
        string? filter,
        IAirPlayLog log,
        Action<UsbMirrorSession>? opened,
        Action<UsbMirrorSession>? closed)
    {
        _mirror = mirror;
        _hub = hub;
        _policy = policy;
        _catalog = catalog;
        _resources = resources;
        _width = width;
        _height = height;
        _filter = string.IsNullOrWhiteSpace(filter) ? null : filter;
        _log = log;
        _opened = opened;
        _closed = closed;
        _loop = Task.Run(WatchAsync);
    }

    public static UsbQtIngress Start(
        MirrorRuntime mirror,
        MirrorSessionHub hub,
        MirrorDevicePolicy policy,
        IUsbDeviceCatalog catalog,
        Func<UsbMirrorResources> resources,
        double width,
        double height,
        string? udidFilter,
        IAirPlayLog log,
        Action<UsbMirrorSession>? opened = null,
        Action<UsbMirrorSession>? closed = null)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(log);
        return new UsbQtIngress(mirror, hub, policy, catalog, resources, width, height, udidFilter, log, opened, closed);
    }

    public TapClockExportSource[] ExportSources()
    {
        lock (_gate)
        {
            var sources = new TapClockExportSource[_sessions.Count];
            for (var i = 0; i < _sessions.Count; i++)
            {
                var session = _sessions[i];
                sources[i] = new TapClockExportSource(session.Frames, null, "usb-" + session.Id);
            }

            return sources;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _stopping, 1);
        _cancellation.Cancel();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        Task[] tasks;
        lock (_gate)
        {
            tasks = _tasks.ToArray();
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _cancellation.Dispose();
    }

    private async Task WatchAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            IReadOnlyList<UsbPhone> phones;
            try
            {
                phones = _catalog.List();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                _log.Log(AirPlayLogLevel.Warning, "枚举 USB 手机失败: " + ex.Message + " USB phone enumeration failed: " + ex.Message);
                phones = [];
            }

            foreach (var phone in phones)
            {
                Consider(phone);
            }

            try
            {
                await Task.Delay(1000, _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Consider(UsbPhone phone)
    {
        if (!UsbSerial.Matches(phone.Id, _filter))
        {
            return;
        }

        if (Cooling(phone.Id))
        {
            return;
        }

        if (!_liveAdd(phone.Id))
        {
            return;
        }

        if (!_policy.TryAdmitUsb(phone.Id, phone.Product))
        {
            _liveRemove(phone.Id);
            LogOnce(
                phone.Id,
                "USB 手机 " + phone.Id + " 的名字已经在 AirPlay 镜像里，不再从 USB 收第二路。加上 --allow-dual 才会两路一起收。"
                + " USB phone " + phone.Id + " matches an AirPlay device name, so a second mirror was not opened. Pass --allow-dual to receive both.");
            return;
        }

        if (!_mirror.TryHoldUsb())
        {
            _policy.ReleaseUsb(phone.Id);
            _liveRemove(phone.Id);
            LogOnce(
                "limit:" + phone.Id,
                "同时镜像已达上限，暂不打开 USB " + phone.Id + "。已有会话保持连接。 Concurrent mirror limit reached. USB " + phone.Id
                + " was not opened. Existing sessions stay connected.");
            return;
        }

        var task = Task.Run(() => RunPhoneAsync(phone));
        lock (_gate)
        {
            _tasks.Add(task);
        }
    }

    private readonly HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);

    private bool _liveAdd(string id)
    {
        lock (_gate)
        {
            return _live.Add(id);
        }
    }

    private void _liveRemove(string id)
    {
        lock (_gate)
        {
            _live.Remove(id);
        }
    }

    private async Task RunPhoneAsync(UsbPhone phone)
    {
        IUsbBulkChannel? channel = null;
        UsbMirrorSession? session = null;
        long generation = 0;
        var opened = false;
        try
        {
            channel = _catalog.TryOpen(phone, _cancellation.Token);
            if (channel is null || _cancellation.IsCancellationRequested)
            {
                Cool(phone.Id);
                return;
            }

            var resources = _resources();
            session = new UsbMirrorSession(phone.Id, channel.Product ?? phone.Product, resources.Frames, resources.Decoder, _log);
            lock (_gate)
            {
                _sessions.Add(session);
            }

            session.Sessions.NoteRoute(MirrorIngress.Usb, session.Id);
            session.Sessions.NoteSessionStarting(session.DeviceName);
            _hub.Attach(session.Sessions);
            opened = true;
            SafeOpen(session);
            _log.Log(
                AirPlayLogLevel.Information,
                "USB 镜像已打开 " + session.Id + "（" + (session.DeviceName ?? "未命名") + "）。无线会话不会被断开。 USB mirror opened for "
                + session.Id + " (" + (session.DeviceName ?? "unnamed") + "). Wireless sessions stay connected.");

            var qt = new QtSession(_width, _height);
            var framer = new QtReassembler();
            var buffer = new byte[256 * 1024];
            var lastNeed = Environment.TickCount64;
            while (!_cancellation.IsCancellationRequested)
            {
                var count = channel.Read(buffer, 1000);
                if (count < 0)
                {
                    break;
                }

                if (count == 0)
                {
                    var need = qt.NeedPacket;
                    if (!need.IsEmpty && Environment.TickCount64 - lastNeed > 5000)
                    {
                        channel.Write(need.Span);
                        lastNeed = Environment.TickCount64;
                    }

                    continue;
                }

                var localSession = session;
                var localChannel = channel;
                framer.Push(buffer.AsSpan(0, count), body =>
                {
                    var step = qt.Accept(body);
                    foreach (var write in step.Writes)
                    {
                        localChannel.Write(write);
                        lastNeed = Environment.TickCount64;
                    }

                    if (step.Video is { } video)
                    {
                        if (generation == 0)
                        {
                            generation = localSession.Sessions.BeginData();
                        }

                        localSession.Feed.Submit(video);
                    }

                    if (qt.ConsumeAudioNotice())
                    {
                        _log.Log(
                            AirPlayLogLevel.Information,
                            "USB 伴音（ASYN EAT!，LPCM）已收到，这一版先不播放。画面继续。 USB audio (ASYN EAT!, LPCM) arrived and is not played yet. Video continues.");
                    }
                });
            }

            if (Volatile.Read(ref _stopping) == 0)
            {
                try
                {
                    foreach (var stop in qt.Stop())
                    {
                        channel.Write(stop);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _log.Log(AirPlayLogLevel.Warning, "USB 镜像 " + phone.Id + " 已结束: " + ex.Message + " USB mirror " + phone.Id + " ended: " + ex.Message);
        }
        finally
        {
            channel?.Dispose();
            if (session is not null)
            {
                lock (_gate)
                {
                    _sessions.Remove(session);
                }

                SafeClose(session);
                if (opened && generation != 0 && Volatile.Read(ref _stopping) == 0)
                {
                    var tracked = session;
                    void OnChanged(object? sender, MirrorSessionEventArgs args)
                    {
                        if (args.Phase != MirrorSessionPhase.Disconnected)
                        {
                            return;
                        }

                        tracked.Sessions.Changed -= OnChanged;
                        _hub.Detach(tracked.Sessions);
                        tracked.DisposeResources();
                    }

                    session.Sessions.Changed += OnChanged;
                    session.Sessions.EndData(generation);
                }
                else if (opened)
                {
                    session.Sessions.Dispose();
                    _hub.Detach(session.Sessions);
                    session.DisposeResources();
                }
                else
                {
                    session.DisposeResources();
                }
            }

            _policy.ReleaseUsb(phone.Id);
            _mirror.ReleaseUsb();
            _liveRemove(phone.Id);
        }
    }

    private void SafeOpen(UsbMirrorSession session)
    {
        try
        {
            _opened?.Invoke(session);
        }
        catch (Exception ex)
        {
            _log.Log(AirPlayLogLevel.Warning, "USB 镜像窗口没有打开: " + ex.Message + " The USB mirror window was not opened: " + ex.Message);
        }
    }

    private void SafeClose(UsbMirrorSession session)
    {
        try
        {
            _closed?.Invoke(session);
        }
        catch (Exception ex)
        {
            _log.Log(AirPlayLogLevel.Warning, "USB 镜像窗口没有关上: " + ex.Message + " The USB mirror window was not closed: " + ex.Message);
        }
    }

    private bool Cooling(string id)
    {
        lock (_gate)
        {
            return _cooldown.TryGetValue(id, out var until) && until > Environment.TickCount64;
        }
    }

    private void Cool(string id)
    {
        lock (_gate)
        {
            _cooldown[id] = Environment.TickCount64 + 5000;
        }
    }

    private void LogOnce(string key, string message)
    {
        lock (_gate)
        {
            if (!_logged.Add(key))
            {
                return;
            }
        }

        _log.Log(AirPlayLogLevel.Warning, message);
    }
}

internal sealed class UsbMirrorResources
{
    public required FrameSlot Frames { get; init; }

    public required IH264Decoder Decoder { get; init; }
}

internal sealed class UsbMirrorSession
{
    private readonly IH264Decoder _decoder;

    public UsbMirrorSession(string id, string? deviceName, FrameSlot frames, IH264Decoder decoder, IAirPlayLog log)
    {
        Id = id;
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim();
        Frames = frames;
        _decoder = decoder;
        Feed = new ClearH264Feed(frames, decoder, log);
        Sessions = new MirrorSessionTracker();
    }

    public string Id { get; }

    public string? DeviceName { get; }

    public FrameSlot Frames { get; }

    public MirrorSessionTracker Sessions { get; }

    public ClearH264Feed Feed { get; }

    public void DisposeResources()
    {
        Sessions.Dispose();
        if (_decoder is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
