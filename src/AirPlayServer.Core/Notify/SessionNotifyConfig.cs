using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirPlayServer.Core.Notify;

public static class TapClockIntegration
{
    public const string DefaultBaseUrl = "http://127.0.0.1:5088";

    public const string EventsPath = "/api/integrations/airplay/events";

    public const string TokenHeader = "X-TapClock-Token";

    public const string Source = "AirPlayServer.NET";

    public static Uri EventsUri(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new ArgumentException("TapClock 地址必须是 http 或 https。");
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(EventsPath, StringComparison.OrdinalIgnoreCase))
        {
            return new UriBuilder(uri) { Query = "", Fragment = "" }.Uri;
        }

        path = path.Length == 0 ? EventsPath : path + EventsPath;
        return new UriBuilder(uri) { Path = path, Query = "", Fragment = "" }.Uri;
    }

    public static string NormalizeBaseUrl(string baseUrl)
    {
        var events = EventsUri(baseUrl);
        var path = events.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(EventsPath, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^EventsPath.Length];
        }

        var builder = new UriBuilder(events)
        {
            Path = path.Length == 0 ? "/" : path,
            Query = "",
            Fragment = "",
        };
        return builder.Uri.ToString().TrimEnd('/');
    }
}

public sealed class SessionNotifyConfig
{
    public const int DefaultConfigPort = 7099;

    public const int MaxDisconnectDebounceMs = 120_000;

    public const string DefaultWebhookHeader = "X-Webhook-Token";

    public const int MaxWebhookUrls = 8;

    [JsonPropertyName("webhooks_enabled")]
    public bool WebhooksEnabled { get; set; }

    [JsonPropertyName("connect_url")]
    public string? ConnectUrl { get; set; }

    [JsonPropertyName("disconnect_url")]
    public string? DisconnectUrl { get; set; }

    [JsonPropertyName("webhook_secret")]
    public string? WebhookSecret { get; set; }

    [JsonPropertyName("webhook_secret_header")]
    public string WebhookSecretHeader { get; set; } = DefaultWebhookHeader;

    /// <summary>
    /// Extra http(s) endpoints. Mirror window presses and releases are posted
    /// here when <see cref="WebhooksEnabled"/> is true. Connect and disconnect
    /// keep using <see cref="ConnectUrl"/> and <see cref="DisconnectUrl"/>.
    /// </summary>
    [JsonPropertyName("webhook_urls")]
    public string[] WebhookUrls { get; set; } = [];

    [JsonPropertyName("tapclock_events_enabled")]
    public bool TapClockEventsEnabled { get; set; }

    [JsonPropertyName("tapclock_base_url")]
    public string TapClockBaseUrl { get; set; } = TapClockIntegration.DefaultBaseUrl;

    [JsonPropertyName("tapclock_secret")]
    public string? TapClockSecret { get; set; }

    [JsonPropertyName("disconnect_debounce_ms")]
    public int DisconnectDebounceMs { get; set; } = MirrorSessionTracker.DefaultDisconnectDebounceMs;

    [JsonPropertyName("config_bind_address")]
    public string ConfigBindAddress { get; set; } = "127.0.0.1";

    [JsonPropertyName("config_port")]
    public int ConfigPort { get; set; } = DefaultConfigPort;

    public void Normalize()
    {
        ConnectUrl = OptionalHttpUrl(ConnectUrl, "连接 Webhook");
        DisconnectUrl = OptionalHttpUrl(DisconnectUrl, "断开 Webhook");
        WebhookUrls = NormalizeWebhookUrls(WebhookUrls);
        WebhookSecret = OptionalSecret(WebhookSecret);
        WebhookSecretHeader = string.IsNullOrWhiteSpace(WebhookSecretHeader)
            ? DefaultWebhookHeader
            : WebhookSecretHeader.Trim();
        if (!IsHeaderName(WebhookSecretHeader))
        {
            throw new ArgumentException("Webhook 密钥头名称无效。");
        }

        TapClockSecret = OptionalSecret(TapClockSecret);
        TapClockBaseUrl = TapClockIntegration.NormalizeBaseUrl(
            string.IsNullOrWhiteSpace(TapClockBaseUrl) ? TapClockIntegration.DefaultBaseUrl : TapClockBaseUrl);

        if (DisconnectDebounceMs is < 0 or > MaxDisconnectDebounceMs)
        {
            throw new ArgumentException("断连防抖必须在 0 到 120000 毫秒之间。");
        }

        if (string.IsNullOrWhiteSpace(ConfigBindAddress))
        {
            ConfigBindAddress = "127.0.0.1";
        }

        if (!IPAddress.TryParse(ConfigBindAddress.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("配置服务只监听 IPv4 地址。");
        }

        ConfigBindAddress = address.ToString();
        if (ConfigPort is < 0 or > 65535)
        {
            throw new ArgumentException("配置服务端口无效。");
        }
    }

    public static string FormatTimestamp(DateTimeOffset at)
        => at.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static string[] NormalizeWebhookUrls(string[]? urls)
    {
        if (urls is null || urls.Length == 0)
        {
            return [];
        }

        var kept = new List<string>(Math.Min(urls.Length, MaxWebhookUrls));
        foreach (var raw in urls)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (kept.Count == MaxWebhookUrls)
            {
                break;
            }

            var text = OptionalHttpUrl(raw, "点击 Webhook");
            if (text is null || kept.Contains(text, StringComparer.Ordinal))
            {
                continue;
            }

            kept.Add(text);
        }

        return kept.ToArray();
    }

    private static string? OptionalHttpUrl(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.Length > 2048
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new ArgumentException(label + " 必须是 http 或 https 地址。");
        }

        return uri.AbsoluteUri;
    }

    private static string? OptionalSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 512 || trimmed.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("密钥无效。");
        }

        return trimmed;
    }

    private static bool IsHeaderName(string name)
    {
        if (name.Length is < 1 or > 64)
        {
            return false;
        }

        foreach (var ch in name)
        {
            if (ch is < (char)33 or > (char)126 or ':')
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class TapClockEventBody
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    [JsonPropertyName("at")]
    public string At { get; set; } = "";

    [JsonPropertyName("device")]
    public string Device { get; set; } = "";

    [JsonPropertyName("source_ip")]
    public string SourceIp { get; set; } = "";

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = TapClockIntegration.Source;

    /// <summary><c>airplay</c> or <c>usb</c>. <see cref="Source"/> stays the product name TapClock already matches.</summary>
    [JsonPropertyName("ingress")]
    public string Ingress { get; set; } = MirrorIngress.AirPlay;

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }
}

public sealed class WebhookEventBody
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    [JsonPropertyName("at")]
    public string At { get; set; } = "";

    [JsonPropertyName("device")]
    public string Device { get; set; } = "";

    [JsonPropertyName("source_ip")]
    public string SourceIp { get; set; } = "";

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary><c>airplay</c> or <c>usb</c>.</summary>
    [JsonPropertyName("ingress")]
    public string Ingress { get; set; } = MirrorIngress.AirPlay;

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }
}

public sealed class MirrorStatusBody
{
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonPropertyName("since")]
    public string? Since { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("source_ip")]
    public string SourceIp { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("disconnect_pending")]
    public bool DisconnectPending { get; set; }

    /// <summary><c>airplay</c> or <c>usb</c>.</summary>
    [JsonPropertyName("ingress")]
    public string Ingress { get; set; } = MirrorIngress.AirPlay;

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    /// <summary>
    /// Every mirror session. Omitted when the caller only has the primary tracker.
    /// Child objects do not nest this array again.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("sessions")]
    public MirrorStatusBody[]? Sessions { get; set; }
}

public sealed class SessionNotifySaveResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("restart_required")]
    public bool RestartRequired { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("config")]
    public SessionNotifyConfig? Config { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionNotifyConfig))]
[JsonSerializable(typeof(TapClockEventBody))]
[JsonSerializable(typeof(WebhookEventBody))]
[JsonSerializable(typeof(MirrorClickBody))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(MirrorStatusBody))]
[JsonSerializable(typeof(MirrorStatusBody[]))]
[JsonSerializable(typeof(SessionNotifySaveResult))]
public sealed partial class SessionNotifyJsonContext : JsonSerializerContext;

public static class SessionNotifyConfigStore
{
    public const string FileName = "session-notify.json";

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static SessionNotifyConfig Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            var created = new SessionNotifyConfig();
            created.Normalize();
            return created;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("无法读取通知配置 " + path + ": " + ex.Message, ex);
        }

        SessionNotifyConfig? config;
        try
        {
            config = JsonSerializer.Deserialize(json, SessionNotifyJsonContext.Default.SessionNotifyConfig);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("通知配置不是 JSON: " + path, ex);
        }

        if (config is null)
        {
            throw new InvalidDataException("通知配置是空的: " + path);
        }

        try
        {
            config.Normalize();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("通知配置无效: " + ex.Message, ex);
        }

        return config;
    }

    public static void Save(string path, SessionNotifyConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(config);
        config.Normalize();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, SessionNotifyJsonContext.Default.SessionNotifyConfig);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }
}
