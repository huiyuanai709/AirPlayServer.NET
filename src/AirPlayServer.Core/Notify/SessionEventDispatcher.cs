using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AirPlayServer.Core.Notify;

/// <summary>
/// Posts published mirror transitions. Connect is immediate. Disconnect is
/// already debounced by <see cref="MirrorSessionTracker"/>. TapClock and the
/// generic webhooks are independent. A failed POST is logged and does not
/// stop the receiver. Retry and click playback belong to TapClock.
/// </summary>
public sealed class SessionEventDispatcher : IDisposable
{
    private readonly IMirrorSessionEvents _sessions;
    private readonly IAirPlayLog _log;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private SessionNotifyConfig _config;
    private readonly MirrorClickHub? _clicks;
    private Task _lastDelivery = Task.CompletedTask;
    private int _stopped;

    public SessionEventDispatcher(IMirrorSessionEvents sessions, SessionNotifyConfig config, IAirPlayLog? log = null, HttpMessageHandler? handler = null, MirrorClickHub? clicks = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(config);
        config.Normalize();
        _sessions = sessions;
        _config = config;
        _clicks = clicks;
        _log = log ?? NullAirPlayLog.Instance;
        _http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        _sessions.DisconnectDebounceMs = config.DisconnectDebounceMs;
        _sessions.Changed += OnChanged;
        if (_clicks is not null)
        {
            _clicks.Published += OnClick;
        }
    }

    internal Task CurrentDelivery
    {
        get
        {
            lock (_gate)
            {
                return _lastDelivery;
            }
        }
    }

    public void Apply(SessionNotifyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Normalize();
        lock (_gate)
        {
            _config = config;
        }

        _sessions.DisconnectDebounceMs = config.DisconnectDebounceMs;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _sessions.Changed -= OnChanged;
        if (_clicks is not null)
        {
            _clicks.Published -= OnClick;
        }

        _http.Dispose();
    }

    private void OnClick(object? sender, MirrorClickEventArgs args)
    {
        var task = SendClickAsync(args);
        lock (_gate)
        {
            _lastDelivery = task;
        }
    }

    private async Task SendClickAsync(MirrorClickEventArgs args)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        SessionNotifyConfig config;
        lock (_gate)
        {
            config = _config;
        }

        if (!config.WebhooksEnabled || config.WebhookUrls.Length == 0)
        {
            return;
        }

        var gesture = args.Gesture;
        var body = new MirrorClickBody
        {
            Event = "mirror.click",
            Phase = PhaseName(gesture.Phase),
            At = SessionNotifyConfig.FormatTimestamp(gesture.At),
            SessionId = args.SessionId,
            Device = args.DeviceName ?? "",
            SourceIp = args.SourceIp,
            Button = ButtonName(gesture.Button),
            WindowX = gesture.WindowX,
            WindowY = gesture.WindowY,
            OnContent = gesture.OnContent,
            ContentX = gesture.ContentX,
            ContentY = gesture.ContentY,
            ContentWidth = gesture.ContentWidth,
            ContentHeight = gesture.ContentHeight,
            LogicalX = gesture.LogicalX,
            LogicalY = gesture.LogicalY,
            LogicalWidth = gesture.LogicalWidth,
            LogicalHeight = gesture.LogicalHeight,
        };
        var json = JsonSerializer.Serialize(body, SessionNotifyJsonContext.Default.MirrorClickBody);
        foreach (var url in config.WebhookUrls)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return;
            }

            await PostAsync(url, json, config.WebhookSecretHeader, config.WebhookSecret, "Webhook").ConfigureAwait(false);
        }
    }

    private static string PhaseName(MirrorPointerPhase phase) => phase switch
    {
        MirrorPointerPhase.Down => "down",
        MirrorPointerPhase.Move => "move",
        MirrorPointerPhase.Up => "up",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Mirror pointer phase is not down, move, or up."),
    };

    private static string ButtonName(MirrorPointerButton button) => button switch
    {
        MirrorPointerButton.Right => "right",
        MirrorPointerButton.Middle => "middle",
        MirrorPointerButton.X1 => "x1",
        MirrorPointerButton.X2 => "x2",
        _ => "left",
    };

    private void OnChanged(object? sender, MirrorSessionEventArgs args)
    {
        var task = SendAsync(args);
        lock (_gate)
        {
            _lastDelivery = task;
        }
    }

    private async Task SendAsync(MirrorSessionEventArgs args)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        SessionNotifyConfig config;
        lock (_gate)
        {
            config = _config;
        }

        var eventName = args.Phase == MirrorSessionPhase.Connected ? "connected" : "disconnected";
        var at = SessionNotifyConfig.FormatTimestamp(args.At);
        _log.Log(
            AirPlayLogLevel.Information,
            args.Phase == MirrorSessionPhase.Connected
                ? "镜像数据已连接。" + Describe(args)
                : "镜像数据已断开。原因: " + args.Reason + "。" + Describe(args));

        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        if (config.WebhooksEnabled)
        {
            var url = args.Phase == MirrorSessionPhase.Connected ? config.ConnectUrl : config.DisconnectUrl;
            if (!string.IsNullOrWhiteSpace(url))
            {
                var body = new WebhookEventBody
                {
                    Event = eventName,
                    At = at,
                    Device = args.DeviceName ?? "",
                    SourceIp = args.SourceIp ?? "",
                    SessionId = args.SessionId,
                    Reason = args.Reason,
                };
                await PostAsync(
                    url,
                    JsonSerializer.Serialize(body, SessionNotifyJsonContext.Default.WebhookEventBody),
                    config.WebhookSecretHeader,
                    config.WebhookSecret,
                    "Webhook").ConfigureAwait(false);
            }
        }

        if (!config.TapClockEventsEnabled || Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        Uri events;
        try
        {
            events = TapClockIntegration.EventsUri(config.TapClockBaseUrl);
        }
        catch (ArgumentException ex)
        {
            _log.Log(AirPlayLogLevel.Warning, "TapClock 地址无效: " + ex.Message);
            return;
        }

        var tap = new TapClockEventBody
        {
            Event = eventName,
            At = at,
            Device = args.DeviceName ?? "",
            SourceIp = args.SourceIp ?? "",
            SessionId = args.SessionId,
            Reason = args.Reason,
            Source = TapClockIntegration.Source,
        };
        await PostAsync(
            events.AbsoluteUri,
            JsonSerializer.Serialize(tap, SessionNotifyJsonContext.Default.TapClockEventBody),
            TapClockIntegration.TokenHeader,
            config.TapClockSecret,
            "TapClock").ConfigureAwait(false);
    }

    private async Task PostAsync(string url, string json, string? headerName, string? secret, string label)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content = content;
            request.Headers.TryAddWithoutValidation("User-Agent", TapClockIntegration.Source);
            if (!string.IsNullOrEmpty(secret) && !string.IsNullOrWhiteSpace(headerName))
            {
                request.Headers.TryAddWithoutValidation(headerName, secret);
            }

            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is >= 200 and <= 299 or 400 or 409)
            {
                _log.Log(AirPlayLogLevel.Information, label + " 事件 HTTP " + code + " " + url);
                return;
            }

            _log.Log(AirPlayLogLevel.Warning, label + " 事件失败 HTTP " + code + " " + url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            _log.Log(AirPlayLogLevel.Warning, label + " 事件没有发出: " + ex.Message);
        }
    }

    private static string Describe(MirrorSessionEventArgs args)
    {
        var device = string.IsNullOrWhiteSpace(args.DeviceName) ? "" : " 设备: " + args.DeviceName + "。";
        var source = string.IsNullOrWhiteSpace(args.SourceIp) ? "" : " 来源: " + args.SourceIp + "。";
        var session = string.IsNullOrWhiteSpace(args.SessionId) ? "" : " 会话: " + args.SessionId + "。";
        return device + source + session;
    }
}
