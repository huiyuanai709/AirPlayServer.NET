namespace AirPlayServer.Host.Notify;

internal sealed class NotifyLaunch
{
    public bool DisablePage { get; set; }

    public string? Listen { get; set; }

    public int? Port { get; set; }

    public string? ConfigPath { get; set; }

    public bool? TapClockEvents { get; set; }

    public string? TapClockBaseUrl { get; set; }

    public int? DebounceMs { get; set; }

    public List<string> WebhookUrls { get; } = [];
}
