using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AirPlayServer.Core.Media;
using AirPlayServer.Core.Notify;
using AirPlayServer.Core.Pairing;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class MirrorSessionNotifyTests
{
    private static readonly Regex IsoOffset = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d+(Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Attach_publishes_connected_and_zero_debounce_disconnects_immediately()
    {
        using var tracker = new MirrorSessionTracker();
        tracker.DisconnectDebounceMs = 0;
        var events = new List<MirrorSessionEventArgs>();
        tracker.Changed += (_, args) => events.Add(args);
        var calledDelay = false;
        tracker.DelayAsync = (_, _) =>
        {
            calledDelay = true;
            return Task.CompletedTask;
        };

        var idle = tracker.GetSnapshot();
        Assert.False(idle.Connected);
        Assert.Null(idle.Since);
        Assert.Null(idle.SessionId);

        var at = new DateTimeOffset(2026, 10, 19, 14, 30, 0, TimeSpan.Zero);
        tracker.NoteSessionStarting("客厅的 iPhone");
        var prepared = tracker.GetSnapshot();
        var generation = tracker.BeginData(at);

        Assert.True(tracker.GetSnapshot().Connected);
        Assert.Equal(at, tracker.GetSnapshot().Since);
        Assert.Equal(prepared.SessionId, tracker.GetSnapshot().SessionId);
        Assert.Equal("客厅的 iPhone", events[0].DeviceName);
        Assert.Equal(MirrorSessionReasons.MirrorData, events[0].Reason);
        Assert.Equal(MirrorSessionPhase.Connected, events[0].Phase);
        Assert.Matches(IsoOffset, SessionNotifyConfig.FormatTimestamp(events[0].At));

        tracker.EndData(generation);
        Assert.False(calledDelay);
        Assert.False(tracker.GetSnapshot().Connected);
        Assert.Null(tracker.GetSnapshot().Since);
        Assert.True(tracker.GetSnapshot().DisconnectPending == false);
        Assert.Equal(MirrorSessionPhase.Disconnected, events[1].Phase);
        Assert.Equal(MirrorSessionReasons.DataClosed, events[1].Reason);
        Assert.Equal(prepared.SessionId, events[1].SessionId);
        Assert.Equal(prepared.SessionId, tracker.GetSnapshot().SessionId);
    }

    [Fact]
    public async Task Debounce_suppresses_disconnect_when_data_returns()
    {
        using var tracker = new MirrorSessionTracker();
        tracker.DisconnectDebounceMs = 3000;
        var events = new List<MirrorSessionEventArgs>();
        tracker.Changed += (_, args) => events.Add(args);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.DelayAsync = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        tracker.NoteSessionStarting("Phone");
        var sessionId = tracker.GetSnapshot().SessionId;
        var first = tracker.BeginData();
        tracker.EndData(first);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(tracker.GetSnapshot().Connected);
        Assert.True(tracker.GetSnapshot().DisconnectPending);
        Assert.Single(events);

        var second = tracker.BeginData();
        Assert.NotEqual(first, second);
        Assert.True(tracker.GetSnapshot().Connected);
        Assert.False(tracker.GetSnapshot().DisconnectPending);
        Assert.Equal(sessionId, tracker.GetSnapshot().SessionId);
        await Task.Delay(30);
        Assert.Single(events);

        tracker.DisconnectDebounceMs = 0;
        tracker.EndData(second);
        Assert.Equal(2, events.Count);
        Assert.Equal(MirrorSessionPhase.Disconnected, events[1].Phase);
        Assert.Equal(sessionId, events[1].SessionId);
    }

    [Fact]
    public async Task Stale_detach_and_dispose_do_not_publish_disconnect()
    {
        using var tracker = new MirrorSessionTracker();
        var events = new List<MirrorSessionEventArgs>();
        tracker.Changed += (_, args) => events.Add(args);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.DelayAsync = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        var first = tracker.BeginData();
        var second = tracker.BeginData();
        tracker.EndData(first);
        Assert.False(entered.Task.IsCompleted);
        Assert.True(tracker.GetSnapshot().Connected);
        Assert.Single(events);

        tracker.EndData(second);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        tracker.Dispose();
        release.TrySetResult();
        await Task.Delay(30);
        Assert.Single(events);
        Assert.True(tracker.GetSnapshot().Connected);
    }

    [Fact]
    public void Full_teardown_clears_session_id_and_a_new_setup_mints_another()
    {
        using var tracker = new MirrorSessionTracker();
        tracker.DisconnectDebounceMs = 0;
        MirrorSessionEventArgs? disconnected = null;
        tracker.Changed += (_, args) =>
        {
            if (args.Phase == MirrorSessionPhase.Disconnected)
            {
                disconnected = args;
            }
        };

        tracker.NoteSessionStarting("Phone");
        var original = tracker.GetSnapshot().SessionId;
        var generation = tracker.BeginData();
        tracker.LatchDetachReason(MirrorSessionReasons.SessionTeardown);
        tracker.NoteSessionEnded();
        tracker.EndData(generation);

        Assert.NotNull(disconnected);
        Assert.Equal(MirrorSessionReasons.SessionTeardown, disconnected.Reason);
        Assert.Equal(original, disconnected.SessionId);
        Assert.Null(tracker.GetSnapshot().SessionId);

        tracker.NoteSessionStarting("Other");
        Assert.NotEqual(original, tracker.GetSnapshot().SessionId);
        Assert.Equal("Other", tracker.GetSnapshot().DeviceName);
    }

    [Fact]
    public async Task New_setup_during_debounce_keeps_the_old_id_on_the_disconnect_event()
    {
        using var tracker = new MirrorSessionTracker();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.DelayAsync = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        MirrorSessionEventArgs? disconnected = null;
        tracker.Changed += (_, args) =>
        {
            if (args.Phase == MirrorSessionPhase.Disconnected)
            {
                disconnected = args;
            }
        };

        tracker.NoteSessionStarting("Phone");
        var original = tracker.GetSnapshot().SessionId;
        tracker.BeginData();
        tracker.LatchDetachReason(MirrorSessionReasons.VideoTeardown);
        tracker.EndData(1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        tracker.NoteSessionStarting("Next");
        release.TrySetResult();
        await WaitUntilAsync(() => disconnected is not null);

        Assert.Equal(original, disconnected!.SessionId);
        Assert.Equal(MirrorSessionReasons.VideoTeardown, disconnected.Reason);
        Assert.NotEqual(original, tracker.GetSnapshot().SessionId);
        Assert.Equal("Next", tracker.GetSnapshot().DeviceName);
    }

    [Fact]
    public void TapClock_events_uri_appends_the_fixed_path_once()
    {
        var plain = TapClockIntegration.EventsUri("http://127.0.0.1:5088");
        Assert.Equal("127.0.0.1", plain.Host);
        Assert.Equal(5088, plain.Port);
        Assert.Equal("/api/integrations/airplay/events", plain.AbsolutePath);

        var slashed = TapClockIntegration.EventsUri("http://127.0.0.1:5088/");
        Assert.Equal("/api/integrations/airplay/events", slashed.AbsolutePath);

        var full = TapClockIntegration.EventsUri("http://127.0.0.1:5088/api/integrations/airplay/events");
        Assert.Equal("/api/integrations/airplay/events", full.AbsolutePath);
        Assert.Equal(string.Empty, full.Query);

        var nested = TapClockIntegration.EventsUri("https://tap.example:9/base/");
        Assert.Equal("/base/api/integrations/airplay/events", nested.AbsolutePath);

        var config = new SessionNotifyConfig
        {
            TapClockBaseUrl = "http://127.0.0.1:5088/api/integrations/airplay/events/",
        };
        config.Normalize();
        Assert.Equal("http://127.0.0.1:5088", config.TapClockBaseUrl);
        Assert.Throws<ArgumentException>(() => new SessionNotifyConfig { ConnectUrl = "ftp://files/x" }.Normalize());
        Assert.Throws<ArgumentException>(() => new SessionNotifyConfig { DisconnectDebounceMs = -1 }.Normalize());
    }

    [Fact]
    public void Config_round_trips_and_missing_fields_keep_defaults()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "session-notify.json");
            Assert.Equal(3000, SessionNotifyConfigStore.Load(path).DisconnectDebounceMs);
            Assert.Equal(TapClockIntegration.DefaultBaseUrl, SessionNotifyConfigStore.Load(path).TapClockBaseUrl);
            Assert.Equal(7099, SessionNotifyConfigStore.Load(path).ConfigPort);
            Assert.False(SessionNotifyConfigStore.Load(path).TapClockEventsEnabled);

            var config = new SessionNotifyConfig
            {
                WebhooksEnabled = true,
                ConnectUrl = "http://127.0.0.1:9/on",
                DisconnectUrl = "http://127.0.0.1:9/off",
                WebhookSecret = "hook-secret",
                WebhookSecretHeader = "X-Hook",
                TapClockEventsEnabled = true,
                TapClockBaseUrl = "http://127.0.0.1:5088",
                TapClockSecret = "tap-secret",
                DisconnectDebounceMs = 0,
                ConfigBindAddress = "127.0.0.1",
                ConfigPort = 7102,
            };
            SessionNotifyConfigStore.Save(path, config);
            var loaded = SessionNotifyConfigStore.Load(path);
            Assert.True(loaded.WebhooksEnabled);
            Assert.Equal("http://127.0.0.1:9/on", loaded.ConnectUrl);
            Assert.Equal("http://127.0.0.1:9/off", loaded.DisconnectUrl);
            Assert.Equal("hook-secret", loaded.WebhookSecret);
            Assert.Equal("X-Hook", loaded.WebhookSecretHeader);
            Assert.True(loaded.TapClockEventsEnabled);
            Assert.Equal("tap-secret", loaded.TapClockSecret);
            Assert.Equal(0, loaded.DisconnectDebounceMs);
            Assert.Equal(7102, loaded.ConfigPort);

            File.WriteAllText(path, """{"webhooks_enabled":true,"disconnect_debounce_ms":0}""");
            var partial = SessionNotifyConfigStore.Load(path);
            Assert.True(partial.WebhooksEnabled);
            Assert.Equal(0, partial.DisconnectDebounceMs);
            Assert.Equal("http://127.0.0.1:5088", partial.TapClockBaseUrl);
            Assert.Equal("X-Webhook-Token", partial.WebhookSecretHeader);

            File.WriteAllText(path, """{"connect_url":"ftp://files/x"}""");
            Assert.Throws<InvalidDataException>(() => SessionNotifyConfigStore.Load(path));
            File.WriteAllText(path, "not-json");
            Assert.Throws<InvalidDataException>(() => SessionNotifyConfigStore.Load(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_swaps_the_live_targets_and_debounce()
    {
        using var tracker = new MirrorSessionTracker();
        var handler = new CaptureHandler();
        var config = new SessionNotifyConfig
        {
            TapClockEventsEnabled = true,
            TapClockBaseUrl = "http://127.0.0.1:5088",
            DisconnectDebounceMs = 0,
        };
        using var dispatcher = new SessionEventDispatcher(tracker, config, handler: handler);
        tracker.BeginData();
        await dispatcher.CurrentDelivery.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(5088, handler.Requests[0].Uri.Port);

        dispatcher.Apply(new SessionNotifyConfig
        {
            WebhooksEnabled = true,
            DisconnectUrl = "http://127.0.0.1:9/off",
            TapClockEventsEnabled = false,
            DisconnectDebounceMs = 0,
        });
        Assert.Equal(0, tracker.DisconnectDebounceMs);
        tracker.EndData(1);
        await dispatcher.CurrentDelivery.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, handler.Count);
        Assert.Equal("http://127.0.0.1:9/off", handler.Requests[1].Uri.AbsoluteUri.TrimEnd('/'));
        Assert.Contains("disconnected", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Posts_tapclock_and_webhook_bodies_after_debounce()
    {
        using var tracker = new MirrorSessionTracker();
        var log = new ListLog();
        var handler = new CaptureHandler();
        var config = new SessionNotifyConfig
        {
            WebhooksEnabled = true,
            ConnectUrl = "http://127.0.0.1:9/connected",
            DisconnectUrl = "http://127.0.0.1:9/disconnected",
            WebhookSecret = "hook",
            WebhookSecretHeader = "X-Hook",
            TapClockEventsEnabled = true,
            TapClockBaseUrl = "http://127.0.0.1:5088",
            TapClockSecret = "tap",
            DisconnectDebounceMs = 3000,
        };
        using var dispatcher = new SessionEventDispatcher(tracker, config, log, handler);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.DelayAsync = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        tracker.NoteSessionStarting("客厅的 iPhone");
        var sessionId = tracker.GetSnapshot().SessionId;
        var generation = tracker.BeginData(new DateTimeOffset(2026, 10, 19, 14, 30, 0, TimeSpan.Zero));
        await dispatcher.CurrentDelivery.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, handler.Count);

        var tapConnect = handler.Find("http://127.0.0.1:5088/api/integrations/airplay/events");
        var hookConnect = handler.Find("http://127.0.0.1:9/connected");
        AssertBody(tapConnect, "connected", "mirror-data", sessionId, expectDevice: false, expectSource: true);
        Assert.Equal("客厅的 iPhone", Read(hookConnect).GetProperty("device").GetString());
        AssertBody(hookConnect, "connected", "mirror-data", sessionId, expectDevice: true, expectSource: false);
        Assert.Equal("tap", Header(tapConnect, "X-TapClock-Token"));
        Assert.Equal("hook", Header(hookConnect, "X-Hook"));
        Assert.Equal("application/json", tapConnect.ContentType);
        Assert.DoesNotContain(handler.Requests, item => item.Uri.AbsolutePath.Contains("/api/integrations/airplay/events/api/", StringComparison.Ordinal));

        tracker.LatchDetachReason(MirrorSessionReasons.DataClosed);
        tracker.EndData(generation);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, handler.Count);
        release.TrySetResult();
        await handler.WaitForAsync(4, TimeSpan.FromSeconds(2));
        await dispatcher.CurrentDelivery.WaitAsync(TimeSpan.FromSeconds(2));

        var tapDisconnect = handler.Find("http://127.0.0.1:5088/api/integrations/airplay/events", skip: 1);
        var hookDisconnect = handler.Find("http://127.0.0.1:9/disconnected");
        AssertBody(tapDisconnect, "disconnected", "data-closed", sessionId, expectDevice: false, expectSource: true);
        AssertBody(hookDisconnect, "disconnected", "data-closed", sessionId, expectDevice: true, expectSource: false);
        Assert.DoesNotContain(handler.Requests, item => item.Uri.AbsolutePath.EndsWith("/connected", StringComparison.Ordinal) && item.Body.Contains("disconnected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabled_channel_is_skipped_and_http_failures_do_not_fault()
    {
        using var tracker = new MirrorSessionTracker();
        var log = new ListLog();
        var handler = new CaptureHandler { Error = new HttpRequestException("down") };
        var config = new SessionNotifyConfig
        {
            WebhooksEnabled = false,
            ConnectUrl = "http://127.0.0.1:9/connected",
            TapClockEventsEnabled = true,
            TapClockSecret = null,
            DisconnectDebounceMs = 0,
        };
        using var dispatcher = new SessionEventDispatcher(tracker, config, log, handler);
        var generation = tracker.BeginData();
        await dispatcher.CurrentDelivery.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, handler.Count);
        Assert.Contains(log.Lines, line => line.Contains("没有发出", StringComparison.Ordinal));
        Assert.Equal(TaskStatus.RanToCompletion, dispatcher.CurrentDelivery.Status);

        handler.Error = null;
        handler.Status = HttpStatusCode.Conflict;
        tracker.EndData(generation);
        await dispatcher.CurrentDelivery.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, handler.Count);
        Assert.Equal("/api/integrations/airplay/events", handler.Requests[0].Uri.AbsolutePath);
        Assert.DoesNotContain("X-TapClock-Token", handler.Requests[0].Headers.Keys);
        Assert.Equal(HttpStatusCode.Conflict, handler.Requests[0].StatusUsed);
        Assert.Equal(TaskStatus.RanToCompletion, dispatcher.CurrentDelivery.Status);
    }

    [Fact]
    public async Task Config_page_saves_and_reports_status()
    {
        var directory = NewDirectory();
        using var tracker = new MirrorSessionTracker();
        var path = Path.Combine(directory, "session-notify.json");
        var current = new SessionNotifyConfig();
        current.Normalize();
        SessionNotifyServer? server = null;
        try
        {
            server = SessionNotifyServer.Start(
                IPAddress.Loopback,
                0,
                tracker,
                "客厅",
                () => current,
                incoming =>
                {
                    SessionNotifyConfigStore.Save(path, incoming);
                    current = incoming;
                    return incoming;
                });
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + server.EndPoint.Port + "/") };
            var page = await http.GetStringAsync("");
            Assert.Contains("防抖", page, StringComparison.Ordinal);
            Assert.Contains("TapClock", page, StringComparison.Ordinal);
            Assert.Contains("控制中心", page, StringComparison.Ordinal);
            Assert.Contains("cdn.tailwindcss.com", page, StringComparison.Ordinal);

            var idleJson = await http.GetStringAsync("api/status");
            Assert.Contains("\"since\": null", idleJson, StringComparison.Ordinal);
            using (var idleStatus = JsonDocument.Parse(idleJson))
            {
                Assert.False(idleStatus.RootElement.GetProperty("connected").GetBoolean());
                Assert.Equal("客厅", idleStatus.RootElement.GetProperty("name").GetString());
                Assert.True(idleStatus.RootElement.GetProperty("session_id").ValueKind == JsonValueKind.Null);
            }

            tracker.NoteSessionStarting("客厅的 iPhone");
            tracker.BeginData(new DateTimeOffset(2026, 10, 19, 14, 30, 0, TimeSpan.Zero));
            using var statusDocument = JsonDocument.Parse(await http.GetStringAsync("api/status"));
            Assert.True(statusDocument.RootElement.GetProperty("connected").GetBoolean());
            Assert.Equal("客厅的 iPhone", statusDocument.RootElement.GetProperty("name").GetString());
            Assert.False(statusDocument.RootElement.GetProperty("disconnect_pending").GetBoolean());
            Assert.Matches(IsoOffset, statusDocument.RootElement.GetProperty("since").GetString());

            var payload = """
                {
                  "webhooks_enabled": true,
                  "connect_url": "http://127.0.0.1:9/on",
                  "disconnect_url": "http://127.0.0.1:9/off",
                  "webhook_secret": "s",
                  "webhook_secret_header": "X-Hook",
                  "tapclock_events_enabled": true,
                  "tapclock_base_url": "http://127.0.0.1:5088",
                  "tapclock_secret": "t",
                  "disconnect_debounce_ms": 1500,
                  "config_bind_address": "127.0.0.1",
                  "config_port": 7099
                }
                """;
            using var response = await http.SendAsync(JsonPost("api/config", payload));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var saved = SessionNotifyConfigStore.Load(path);
            Assert.True(saved.TapClockEventsEnabled);
            Assert.Equal(1500, saved.DisconnectDebounceMs);
            Assert.Equal("s", saved.WebhookSecret);
            Assert.Equal("t", saved.TapClockSecret);

            using var rejected = await http.SendAsync(JsonPost("api/config", """{"connect_url":"notaurl"}"""));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Mirror_data_header_connects_and_video_teardown_is_the_disconnect_reason()
    {
        var log = new ListLog();
        await using var mirror = MirrorRuntime.Start(IPAddress.Loopback, 9, new FrameSlot(), new IdleDecoder(), null, ReadOnlySpan<byte>.Empty, log);
        var events = new List<MirrorSessionEventArgs>();
        var connected = new TaskCompletionSource<MirrorSessionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<MirrorSessionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        mirror.Sessions.Changed += (_, args) =>
        {
            lock (events)
            {
                events.Add(args);
            }

            if (args.Phase == MirrorSessionPhase.Connected)
            {
                connected.TrySetResult(args);
            }
            else
            {
                disconnected.TrySetResult(args);
            }
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mirror.Sessions.DelayAsync = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        using (var probe = new TcpClient { NoDelay = true })
        {
            await probe.ConnectAsync(IPAddress.Loopback, mirror.Activity.DataPort);
            await probe.GetStream().WriteAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray());
            await WaitUntilAsync(() => log.Contains("sent HTTP"));
            Assert.False(mirror.Sessions.GetSnapshot().Connected);
        }

        using var session = new PairingSession(PairingIdentity.Generate());
        Assert.Equal(200, mirror.HandleMedia(Request("SETUP", SessionBody("客厅的 iPhone")), session, null).Status);
        var sessionId = mirror.Sessions.GetSnapshot().SessionId;
        Assert.False(mirror.Sessions.GetSnapshot().Connected);
        Assert.Equal("客厅的 iPhone", mirror.Sessions.GetSnapshot().DeviceName);

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, mirror.Activity.DataPort);
        await client.GetStream().WriteAsync(new byte[128]);
        var connect = await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(sessionId, connect.SessionId);
        Assert.Equal("客厅的 iPhone", connect.DeviceName);
        Assert.Equal(MirrorSessionReasons.MirrorData, connect.Reason);

        Assert.Equal(200, mirror.HandleMedia(Request("TEARDOWN", StreamBody(96)), session, null).Status);
        await Task.Delay(30);
        Assert.True(mirror.Sessions.GetSnapshot().Connected);
        Assert.False(entered.Task.IsCompleted);

        Assert.Equal(200, mirror.HandleMedia(Request("TEARDOWN", StreamBody(110)), session, null).Status);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(mirror.Sessions.GetSnapshot().Connected);
        Assert.True(mirror.Sessions.GetSnapshot().DisconnectPending);
        release.TrySetResult();
        var disconnect = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MirrorSessionReasons.VideoTeardown, disconnect.Reason);
        Assert.Equal(sessionId, disconnect.SessionId);
        Assert.Equal(sessionId, mirror.Sessions.GetSnapshot().SessionId);
        Assert.False(mirror.Sessions.GetSnapshot().Connected);
    }

    private static void AssertBody(CapturedRequest request, string eventName, string reason, string? sessionId, bool expectDevice, bool expectSource)
    {
        var root = Read(request);
        Assert.Equal(eventName, root.GetProperty("event").GetString());
        Assert.Equal(reason, root.GetProperty("reason").GetString());
        Assert.Equal(sessionId, root.GetProperty("session_id").GetString());
        Assert.Matches(IsoOffset, root.GetProperty("at").GetString());
        Assert.Equal(expectDevice, root.TryGetProperty("device", out _));
        Assert.Equal(expectSource, root.TryGetProperty("source", out var source));
        if (expectSource)
        {
            Assert.Equal("AirPlayServer.NET", source.GetString());
        }
    }

    private static JsonElement Read(CapturedRequest request)
    {
        using var document = JsonDocument.Parse(request.Body);
        return document.RootElement.Clone();
    }

    private static string? Header(CapturedRequest request, string name)
        => request.Headers.TryGetValue(name, out var value) ? value : null;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 2000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private static HttpRequestMessage JsonPost(string path, string json)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        message.Headers.ExpectContinue = false;
        return message;
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "airplay-notify-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static Http1Request Request(string method, byte[] body)
    {
        var header = Encoding.ASCII.GetBytes(method + " * RTSP/1.0\r\nContent-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n\r\n");
        var buffer = new byte[header.Length + body.Length];
        header.CopyTo(buffer, 0);
        body.CopyTo(buffer, header.Length);
        Assert.Equal(HttpParseStatus.Ok, Http1Request.TryParse(buffer, out var request, out var consumed));
        Assert.Equal(buffer.Length, consumed);
        return request!;
    }

    private static byte[] SessionBody(string name)
    {
        var session = new PlistDictionary();
        session.Add("eiv", new PlistData(new byte[16]));
        session.Add("ekey", new PlistData(new byte[16]));
        session.Add("name", new PlistString(name));
        return BinaryPropertyList.Write(session);
    }

    private static byte[] StreamBody(int type)
    {
        var stream = new PlistDictionary();
        stream.Add("type", new PlistInteger(type));
        var streams = new PlistArray();
        streams.Add(stream);
        var root = new PlistDictionary();
        root.Add("streams", streams);
        return BinaryPropertyList.Write(root);
    }

    private sealed class IdleDecoder : IH264Decoder
    {
        public bool IsAvailable => true;

        public string Name => "idle";

        public void Configure(ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet)
        {
        }

        public void Reset()
        {
        }

        public bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame)
        {
            frame = default;
            return false;
        }
    }

    private sealed class ListLog : IAirPlayLog
    {
        public List<string> Lines { get; } = [];

        public void Log(AirPlayLogLevel level, string message)
        {
            lock (Lines)
            {
                Lines.Add(message);
            }
        }

        public bool Contains(string text)
        {
            lock (Lines)
            {
                return Lines.Any(line => line.Contains(text, StringComparison.Ordinal));
            }
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly List<CapturedRequest> _requests = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.NoContent;

        public Exception? Error { get; set; }

        public IReadOnlyList<CapturedRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        public int Count
        {
            get
            {
                lock (_requests)
                {
                    return _requests.Count;
                }
            }
        }

        public CapturedRequest Find(string uri, int skip = 0)
        {
            lock (_requests)
            {
                return _requests.Where(item => item.Uri.AbsoluteUri.TrimEnd('/') == uri.TrimEnd('/')).Skip(skip).First();
            }
        }

        public async Task WaitForAsync(int count, TimeSpan timeout)
        {
            var deadline = Environment.TickCount64 + timeout.TotalMilliseconds;
            while (Count < count)
            {
                if (Environment.TickCount64 > deadline)
                {
                    throw new TimeoutException("Saw " + Count + " requests.");
                }

                await Task.Delay(10);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Error is not null)
            {
                throw Error;
            }

            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                headers[header.Key] = string.Join(',', header.Value);
            }

            lock (_requests)
            {
                _requests.Add(new CapturedRequest(request.RequestUri!, body, request.Content?.Headers.ContentType?.MediaType, headers, Status));
            }

            return new HttpResponseMessage(Status);
        }
    }

    private sealed record CapturedRequest(Uri Uri, string Body, string? ContentType, Dictionary<string, string> Headers, HttpStatusCode StatusUsed);
}
