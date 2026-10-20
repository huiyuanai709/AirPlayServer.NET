namespace AirPlayServer.Core.Notify;

internal static class SessionNotifyPage
{
    public const string Html = """
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>镜像通知</title>
          <script src="https://cdn.tailwindcss.com"></script>
        </head>
        <body class="bg-slate-100 text-slate-900">
          <main class="max-w-xl mx-auto p-6 space-y-6">
            <header class="space-y-2">
              <h1 class="text-2xl font-semibold">镜像通知</h1>
              <p class="text-sm text-slate-600">已连接表示镜像数据端口收到了合法的画面包，而不是只完成了 RTSP SETUP。断连会先等防抖，这段时间里重新连上就不会发 disconnected。控制中心点击由 TapClock 完成，本程序不模拟按键。</p>
            </header>
            <section class="bg-white rounded-xl shadow p-4 space-y-2">
              <h2 class="font-medium">当前状态</h2>
              <p id="status-line" class="text-lg">正在读取…</p>
              <dl class="text-sm text-slate-600 space-y-1">
                <div>名称 <span id="status-name" class="text-slate-900"></span></div>
                <div>来源 IP <span id="status-source" class="font-mono text-slate-900"></span></div>
                <div>开始时间 <span id="status-since" class="text-slate-900"></span></div>
                <div>会话 <span id="status-session" class="font-mono text-slate-900"></span></div>
                <div>各路会话 <span id="status-sessions" class="text-slate-900"></span></div>
              </dl>
            </section>
            <form id="form" class="bg-white rounded-xl shadow p-4 space-y-4">
              <fieldset class="space-y-3">
                <legend class="font-medium">Webhook</legend>
                <label class="flex items-center gap-2 text-sm"><input id="webhooks_enabled" type="checkbox" class="h-4 w-4" /> 启用 Webhook</label>
                <label class="block text-sm">连接地址<input id="connect_url" type="url" placeholder="http://127.0.0.1:9/airplay/connected" class="mt-1 w-full rounded border border-slate-300 px-3 py-2" /></label>
                <label class="block text-sm">断开地址<input id="disconnect_url" type="url" placeholder="http://127.0.0.1:9/airplay/disconnected" class="mt-1 w-full rounded border border-slate-300 px-3 py-2" /></label>
                <label class="block text-sm">密钥（可选）<input id="webhook_secret" type="text" autocomplete="off" class="mt-1 w-full rounded border border-slate-300 px-3 py-2" /></label>
                <label class="block text-sm">密钥 HTTP 头<input id="webhook_secret_header" type="text" class="mt-1 w-full rounded border border-slate-300 px-3 py-2" /></label>
                <p class="text-xs text-slate-500">连接和断开 POST JSON：event、at、device、source_ip、session_id、reason。device 是手机名，source_ip 是对端 IP（不带端口）。不知道时为空字符串。有密钥时带上上面的头。</p>
                <label class="block text-sm">点击地址（每行一个 http 或 https，最多 8 个）<textarea id="webhook_urls" rows="4" placeholder="http://127.0.0.1:9/mirror/click" class="mt-1 w-full rounded border border-slate-300 px-3 py-2 font-mono text-xs"></textarea></label>
                <p class="text-xs text-slate-500">连接和断开仍走上面两个地址。镜像窗口的按下、按住移动和抬起另 POST 到点击地址，event 仍为 mirror.click，phase 为 down、move 或 up，同样带 device 和 source_ip。悬停不发 move。move 只在按键被窗口按住且点在画面上时发送，设备像素没变会丢掉，大约每秒最多 60 次。window_x/y 是客户区设备像素，原点在左上，Y 向下。画面按比例 letterbox，黑边上 on_content 为 false，content_x/y 为空。画面内 content_x/y 是解码帧像素，整数截断。配置了逻辑屏幕尺寸（--model）时，另有 logical_x/y = 帧像素 * 逻辑宽高 / 帧宽高。--screen 不填逻辑坐标。点击不发给手机。有密钥时带上上面的头。</p>
              </fieldset>
              <fieldset class="space-y-3">
                <legend class="font-medium">TapClock</legend>
                <label class="flex items-center gap-2 text-sm"><input id="tapclock_events_enabled" type="checkbox" class="h-4 w-4" /> 向 TapClock 发送镜像事件</label>
                <label class="block text-sm">TapClock 基址<input id="tapclock_base_url" type="url" class="mt-1 w-full rounded border border-slate-300 px-3 py-2" /></label>
                <label class="block text-sm">共享密钥（可选，请求头 X-TapClock-Token）<input id="tapclock_secret" type="text" autocomplete="off" class="mt-1 w-full rounded border border-slate-300 px-3 py-2" /></label>
                <label class="block text-sm">断连防抖（毫秒）<input id="disconnect_debounce_ms" type="number" min="0" max="120000" class="mt-1 w-full rounded border border-slate-300 px-3 py-2" /></label>
                <p class="text-xs text-slate-500">连上立即 POST connected。断开发出前等待防抖。地址后面固定加上 /api/integrations/airplay/events。重试和点击归 TapClock。</p>
                <pre class="text-xs bg-slate-50 rounded p-3 overflow-x-auto">{
          "event": "disconnected",
          "at": "2026-10-19T14:30:00.0000000+00:00",
          "device": "客厅的 iPhone",
          "source_ip": "192.168.5.20",
          "session_id": "...",
          "reason": "data-closed",
          "source": "AirPlayServer.NET"
        }</pre>
              </fieldset>
              <fieldset class="space-y-3">
                <legend class="font-medium">配置页</legend>
                <label class="block text-sm">监听地址（下次启动生效）<input id="config_bind_address" type="text" class="mt-1 w-full rounded border border-slate-300 px-3 py-2" /></label>
                <label class="block text-sm">端口（0 表示下次不打开页面）<input id="config_port" type="number" min="0" max="65535" class="mt-1 w-full rounded border border-slate-300 px-3 py-2" /></label>
                <p class="text-xs text-slate-500">默认只监听 127.0.0.1。改成局域网地址后，同一网络可以读到密钥。</p>
              </fieldset>
              <button type="submit" class="rounded bg-slate-900 text-white px-4 py-2 text-sm">保存</button>
              <p id="message" class="text-sm"></p>
            </form>
          </main>
          <script>
            const fields = ["webhooks_enabled", "connect_url", "disconnect_url", "webhook_secret", "webhook_secret_header", "tapclock_events_enabled", "tapclock_base_url", "tapclock_secret", "disconnect_debounce_ms", "config_bind_address", "config_port"];
            const checks = new Set(["webhooks_enabled", "tapclock_events_enabled"]);
            const numbers = new Set(["disconnect_debounce_ms", "config_port"]);

            function showStatus(status) {
              document.getElementById("status-line").textContent = status.connected
                ? (status.disconnect_pending ? "已连接（断连防抖中）" : "已连接")
                : "未连接";
              document.getElementById("status-name").textContent = status.name || "—";
              document.getElementById("status-source").textContent = status.source_ip || "—";
              document.getElementById("status-since").textContent = status.since || "—";
              document.getElementById("status-session").textContent = status.session_id || "—";
              const sessions = Array.isArray(status.sessions) ? status.sessions : [];
              const list = document.getElementById("status-sessions");
              list.textContent = sessions.length === 0
                ? "—"
                : sessions.map((item) => (item.connected ? "已连接 " : "未连接 ") + (item.name || "—") + " " + (item.source_ip || "—") + " " + (item.session_id || "")).join("；");
            }

            async function refreshStatus() {
              const response = await fetch("/api/status", { cache: "no-store" });
              if (!response.ok) return;
              showStatus(await response.json());
            }

            function fill(config) {
              for (const name of fields) {
                const input = document.getElementById(name);
                const value = config[name];
                if (checks.has(name)) input.checked = !!value;
                else input.value = value ?? "";
              }
              const urls = Array.isArray(config.webhook_urls) ? config.webhook_urls : [];
              document.getElementById("webhook_urls").value = urls.join("\n");
            }

            function readForm() {
              const body = {};
              for (const name of fields) {
                const input = document.getElementById(name);
                if (checks.has(name)) body[name] = input.checked;
                else if (numbers.has(name)) body[name] = Number(input.value);
                else body[name] = input.value.trim() === "" ? null : input.value.trim();
              }
              if (!body.webhook_secret_header) body.webhook_secret_header = "X-Webhook-Token";
              body.webhook_urls = document.getElementById("webhook_urls").value.split(/\r?\n/).map((line) => line.trim()).filter((line) => line.length > 0);
              return body;
            }

            async function load() {
              await refreshStatus();
              const response = await fetch("/api/config", { cache: "no-store" });
              if (!response.ok) {
                document.getElementById("message").textContent = "读不到配置。";
                return;
              }
              fill(await response.json());
            }

            document.getElementById("form").addEventListener("submit", async (event) => {
              event.preventDefault();
              const message = document.getElementById("message");
              message.className = "text-sm text-slate-600";
              message.textContent = "正在保存…";
              const response = await fetch("/api/config", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(readForm())
              });
              const text = await response.text();
              if (!response.ok) {
                message.className = "text-sm text-red-700";
                message.textContent = text;
                return;
              }
              const saved = JSON.parse(text);
              if (saved.config) fill(saved.config);
              message.className = "text-sm text-emerald-700";
              message.textContent = saved.message || "已保存。";
            });

            load().catch((error) => {
              document.getElementById("message").textContent = String(error);
            });
            setInterval(() => { refreshStatus().catch(() => {}); }, 2000);
          </script>
        </body>
        </html>
        """;
}
