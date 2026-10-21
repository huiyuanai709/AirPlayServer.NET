namespace AirPlayServer.Core.Media;

/// <summary>
/// Decides whether one physical phone may be mirrored on USB and AirPlay at
/// the same time. The two paths do not share a UDID: AirPlay is keyed by the
/// control IPv4, USB by the USB serial. They are treated as the same phone
/// only when both have a non-empty device name and those names match.
/// Other phones are always allowed. <see cref="AllowSameDevice"/> is the
/// <c>--allow-dual</c> override.
/// </summary>
internal sealed class MirrorDevicePolicy
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _airPlay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _usb = new(StringComparer.OrdinalIgnoreCase);

    public MirrorDevicePolicy(bool allowSameDevice)
    {
        AllowSameDevice = allowSameDevice;
    }

    public bool AllowSameDevice { get; }

    public bool TryAdmitAirPlay(string key, string? name)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            if (!AllowSameDevice && Collides(name, _usb))
            {
                return false;
            }

            _airPlay[key] = name ?? "";
            return true;
        }
    }

    public void ReleaseAirPlay(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        lock (_gate)
        {
            _airPlay.Remove(key);
        }
    }

    public bool TryAdmitUsb(string key, string? name)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            if (!AllowSameDevice && Collides(name, _airPlay))
            {
                return false;
            }

            _usb[key] = name ?? "";
            return true;
        }
    }

    public void ReleaseUsb(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        lock (_gate)
        {
            _usb.Remove(key);
        }
    }

    private static bool Collides(string? name, Dictionary<string, string> other)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var existing in other.Values)
        {
            if (existing.Length > 0 && string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
