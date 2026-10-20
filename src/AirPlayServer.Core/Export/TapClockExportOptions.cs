namespace AirPlayServer.Core.Export;

/// <summary>
/// Frame export toward TapClock. Disabled unless an endpoint or a watch directory is set.
/// Clicks are not sent from here; TapClock keeps those on TCP 42123.
/// </summary>
public sealed class TapClockExportOptions
{
    public const double DefaultFramesPerSecond = 2;
    public const int DefaultJpegQuality = 80;

    /// <summary>IPv4 host or DNS name and port, for example 127.0.0.1:42125.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Directory that receives one JPEG per mirror data peer, named
    /// <c>mirror-192.168.5.20.jpg</c>. No peer yet is <c>mirror-unknown.jpg</c>.
    /// </summary>
    public string? WatchDirectory { get; set; }

    public double FramesPerSecond { get; set; } = DefaultFramesPerSecond;

    public int JpegQuality { get; set; } = DefaultJpegQuality;

    public bool Enabled => !string.IsNullOrWhiteSpace(Endpoint) || !string.IsNullOrWhiteSpace(WatchDirectory);

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Endpoint) && !TryParseEndpoint(Endpoint, out _, out _))
        {
            throw new ArgumentException("TapClock endpoint must be an IPv4 host or hostname, a colon, and a port.", nameof(Endpoint));
        }

        if (FramesPerSecond is <= 0 or > 30)
        {
            throw new ArgumentException("TapClock frame rate must be between 0 and 30.", nameof(FramesPerSecond));
        }

        if (JpegQuality is < 1 or > 100)
        {
            throw new ArgumentException("JPEG quality must be from 1 to 100.", nameof(JpegQuality));
        }
    }

    public static bool TryParseEndpoint(string text, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
        {
            return false;
        }

        host = text[..colon].Trim();
        if (host.Length == 0 || host.Contains(':'))
        {
            return false;
        }

        return int.TryParse(text[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
               && port is > 0 and <= 65535;
    }
}
