namespace AirPlayServer.Core.Notify;

/// <summary>
/// Forwards every mirror session's connect and disconnect. Each device keeps
/// its own <see cref="MirrorSessionTracker"/>, session id, and debounce.
/// </summary>
public sealed class MirrorSessionHub : IMirrorSessionEvents, IDisposable
{
    private readonly object _gate = new();
    private readonly List<MirrorSessionTracker> _trackers = [];
    private int _debounceMs = MirrorSessionTracker.DefaultDisconnectDebounceMs;
    private int _disposed;

    public event EventHandler<MirrorSessionEventArgs>? Changed;

    public int DisconnectDebounceMs
    {
        get => Volatile.Read(ref _debounceMs);
        set
        {
            var clamped = Math.Clamp(value, 0, SessionNotifyConfig.MaxDisconnectDebounceMs);
            Volatile.Write(ref _debounceMs, clamped);
            MirrorSessionTracker[] trackers;
            lock (_gate)
            {
                trackers = _trackers.ToArray();
            }

            foreach (var tracker in trackers)
            {
                tracker.DisconnectDebounceMs = clamped;
            }
        }
    }

    public void Attach(MirrorSessionTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        tracker.DisconnectDebounceMs = DisconnectDebounceMs;
        tracker.Changed += OnChanged;
        lock (_gate)
        {
            _trackers.Add(tracker);
        }
    }

    public IReadOnlyList<MirrorSessionSnapshot> Snapshots()
    {
        MirrorSessionTracker[] trackers;
        lock (_gate)
        {
            trackers = _trackers.ToArray();
        }

        var snapshots = new MirrorSessionSnapshot[trackers.Length];
        for (var i = 0; i < trackers.Length; i++)
        {
            snapshots[i] = trackers[i].GetSnapshot();
        }

        return snapshots;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        MirrorSessionTracker[] trackers;
        lock (_gate)
        {
            trackers = _trackers.ToArray();
            _trackers.Clear();
        }

        foreach (var tracker in trackers)
        {
            tracker.Changed -= OnChanged;
        }
    }

    private void OnChanged(object? sender, MirrorSessionEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0)
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
}
