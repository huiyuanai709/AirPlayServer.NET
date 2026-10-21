namespace AirPlayServer.Core.Notify;

/// <summary>
/// Which path produced a mirror session. Wireless AirPlay is the default.
/// <see cref="Usb"/> is the QuickTime-over-USB screen path. TapClock's existing
/// <c>source</c> field stays the product name; this value is the JSON field
/// <c>ingress</c>.
/// </summary>
public static class MirrorIngress
{
    public const string AirPlay = "airplay";

    public const string Usb = "usb";
}
