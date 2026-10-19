using System.Net;
using AirPlayServer.Core;
using AirPlayServer.Core.Notify;

namespace AirPlayServer.Host.Notify;

internal sealed class SessionNotifyPlan
{
    public required SessionNotifyConfig Config { get; init; }

    public required string Path { get; init; }

    public required bool ServePage { get; init; }
}

internal sealed class SessionNotifyService : IAsyncDisposable
{
    private readonly SessionNotifyPlan _plan;
    private readonly SessionEventDispatcher _dispatcher;
    private readonly object _save = new();
    private SessionNotifyServer? _server;
    private SessionNotifyConfig _config;
    private int _disposed;

    private SessionNotifyService(SessionNotifyPlan plan, SessionEventDispatcher dispatcher)
    {
        _plan = plan;
        _config = plan.Config;
        _dispatcher = dispatcher;
    }

    public static SessionNotifyPlan Load(NotifyLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var path = string.IsNullOrWhiteSpace(launch.ConfigPath) ? SessionNotifyConfigStore.DefaultPath : launch.ConfigPath;
        var config = SessionNotifyConfigStore.Load(path);
        if (!string.IsNullOrWhiteSpace(launch.Listen))
        {
            config.ConfigBindAddress = launch.Listen;
        }

        if (launch.Port is int port)
        {
            config.ConfigPort = port;
        }

        if (launch.TapClockEvents is bool enabled)
        {
            config.TapClockEventsEnabled = enabled;
        }

        if (!string.IsNullOrWhiteSpace(launch.TapClockBaseUrl))
        {
            config.TapClockBaseUrl = launch.TapClockBaseUrl;
        }

        if (launch.DebounceMs is int debounce)
        {
            config.DisconnectDebounceMs = debounce;
        }

        config.Normalize();
        return new SessionNotifyPlan
        {
            Config = config,
            Path = path,
            ServePage = !launch.DisablePage && config.ConfigPort != 0,
        };
    }

    public static SessionNotifyService Start(SessionNotifyPlan plan, AirPlayReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(receiver);
        var dispatcher = new SessionEventDispatcher(receiver.Sessions, plan.Config, ConsoleAirPlayLog.Instance);
        var service = new SessionNotifyService(plan, dispatcher);
        if (!plan.ServePage)
        {
            return service;
        }

        try
        {
            service._server = SessionNotifyServer.Start(
                IPAddress.Parse(plan.Config.ConfigBindAddress),
                plan.Config.ConfigPort,
                receiver.Sessions,
                receiver.Name,
                () => service._config,
                service.Apply,
                ConsoleAirPlayLog.Instance);
            return service;
        }
        catch
        {
            dispatcher.Dispose();
            throw;
        }
    }

    public IEnumerable<string> StartupLines()
    {
        var config = _config;
        if (_server is null)
        {
            yield return "通知配置页: 已关闭。Webhook 和 TapClock 仍按配置文件发送。";
        }
        else
        {
            var endpoint = _server.EndPoint;
            yield return "通知配置页: http://" + endpoint.Address + ":" + endpoint.Port + "/";
            yield return "镜像状态: GET http://" + endpoint.Address + ":" + endpoint.Port + "/api/status";
        }

        if (config.TapClockEventsEnabled)
        {
            yield return "TapClock 事件: POST " + TapClockIntegration.EventsUri(config.TapClockBaseUrl)
                + " ，断连防抖 " + config.DisconnectDebounceMs + " 毫秒。点击不在本程序。";
        }
        else
        {
            yield return "TapClock 事件: 未启用。在配置页打开「向 TapClock 发送镜像事件」。";
        }

        if (config.WebhooksEnabled)
        {
            yield return "Webhook: 连接 " + (config.ConnectUrl ?? "（空）") + "，断开 " + (config.DisconnectUrl ?? "（空）") + "。";
        }
        else
        {
            yield return "Webhook: 未启用。";
        }

        yield return "通知配置文件: " + _plan.Path;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }

        _dispatcher.Dispose();
    }

    private SessionNotifyConfig Apply(SessionNotifyConfig incoming)
    {
        lock (_save)
        {
            incoming.Normalize();
            SessionNotifyConfigStore.Save(_plan.Path, incoming);
            _dispatcher.Apply(incoming);
            _config = incoming;
            return incoming;
        }
    }
}
