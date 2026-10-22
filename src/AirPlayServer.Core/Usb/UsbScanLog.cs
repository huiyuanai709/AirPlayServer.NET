namespace AirPlayServer.Core.Usb;

/// <summary>
/// Keeps USB enumeration from writing the same open failure every second.
/// A repeated reason is logged again after <see cref="IntervalMs"/>.
/// </summary>
internal sealed class UsbScanLog
{
    public const long IntervalMs = 30_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _at = new(StringComparer.Ordinal);
    private readonly HashSet<string> _once = new(StringComparer.Ordinal);

    public void Repeat(IAirPlayLog log, AirPlayLogLevel level, string key, string message)
    {
        ArgumentNullException.ThrowIfNull(log);
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_at.TryGetValue(key, out var at) && now - at < IntervalMs)
            {
                return;
            }

            _at[key] = now;
            if (_at.Count > 64)
            {
                var stale = new List<string>();
                foreach (var pair in _at)
                {
                    if (now - pair.Value >= IntervalMs && !string.Equals(pair.Key, key, StringComparison.Ordinal))
                    {
                        stale.Add(pair.Key);
                    }
                }

                foreach (var item in stale)
                {
                    _at.Remove(item);
                }
            }
        }

        log.Log(level, message);
    }

    public void Once(IAirPlayLog log, AirPlayLogLevel level, string key, string message)
    {
        ArgumentNullException.ThrowIfNull(log);
        lock (_gate)
        {
            if (!_once.Add(key))
            {
                return;
            }
        }

        log.Log(level, message);
    }
}
