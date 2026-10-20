# AirPlayServer.NET

把 [xenos1337/AirPlayServer](https://github.com/xenos1337/AirPlayServer)（MIT，Windows C/C++ AirPlay 接收端）的接收路径，用 .NET 11 Native AOT 重新实现。当前切片包含发现、瞬时配对、屏幕镜像的 RTSP/TCP 路径、Windows Media Foundation H.264 解码、最新帧窗口、给 [TapClock](https://github.com/huiyuanai709/TapClock/blob/main/docs/airplay.md) 的 JPEG 画面输出，以及进程内 FairPlay（`POST /fp-setup` 与 72 字节 `ekey` 解包）。发布出来的 Native AOT 宿主是 **GPL-3.0-only**，因为里面编进了 [AirPlayFairPlay](https://github.com/huiyuanai709/AirPlayFairPlayPlugin)。

This is an independent .NET port of the receive path, not a copy of the upstream C/C++ tree. Upstream binaries, FFmpeg, libplist, SDL, and the vendored Bonjour sources are not included. FairPlay is compiled in-process from the pure .NET AirPlayFairPlay library, a port of PlayFair. See [NOTICE.md](NOTICE.md). The published AOT host is GPL-3.0-only because that library is part of the binary.

Apple、AirPlay、Bonjour 是 Apple Inc. 的商标。广播里的 `AppleTV14,1` 和 `845.5.1` 只是发送端认得出的兼容字符串。

## 现在能做什么

- 用 UDP 向 `224.0.0.251:5353` 通告 `_airplay._tcp` 和 `_raop._tcp`，并回答匹配的 mDNS 查询。查询套接字使用 `SO_REUSEADDR`（Linux/macOS 再加 `SO_REUSEPORT`），5353 已被 Bonjour 占用时仍尽量一起收包；整机绑定失败再按网卡绑定。两种都失败时不能改用别的端口收查询（查询的目的端口就是 5353），于是每 10 秒从选定网卡主动通告。启动后在 0、1、2、4 秒连发四次；能应答查询时之后每 60 秒刷新。记录 TTL 是 4500 秒，刷新远早于 80%（3600 秒）。网卡变化先静默 2 秒。通告地址没变（例如只是移动热点网卡抖动）就不再发，也不重置已经连上的镜像。真正换了地址时只再发一条，不重开启动连发。默认 `--listen 0.0.0.0` 时为每一张可达的非虚拟 RFC1918 网卡各发一条通告，这条通告只带该网卡自己的地址，并在每张这样的网卡上加入多播。跳过 Hyper-V `vEthernet`、WSL、Docker、Clash TAP、VPN、`169.254.0.0/16`，以及 Windows 移动热点 / ICS / Wi-Fi Direct（网卡名 `Local Area Connection*`、`本地连接*`，描述里的 Microsoft Wi-Fi Direct / Hosted Network / Internet Sharing Device，和 `192.168.137.0/24`）。热点地址不会出现在任何通告里。查询若发往 `224.0.0.251`，即使查询方在另一子网（例如本机 `172.16.88.168`、查询来自 `172.16.93.211`）也在收到查询的那张网卡上多播应答；查询若直接发到本机单播地址，则单播回查询方。多播应答的 DNS id 置 0。只剩这些被跳过的地址时才退回第一张非回环网卡。`--listen 192.168.x.y` 则监听和通告都只用该地址。
- 在 AirPlay 端口（默认 7001）和 RAOP 端口（默认 5001）上接受 HTTP/1.1 与 RTSP/1.0 控制连接。读取用 `System.IO.Pipelines`。
- 完成瞬时配对：`POST /pair-setup` 返回 Ed25519 公钥；`POST /pair-verify` 做 X25519、对两端临时公钥签名，再用 SHA-512 派生的 AES-128-CTR 掩住签名。
- `GET /server-info` 返回 XML plist，`GET|POST /info` 返回二进制 plist。
- `POST /reverse` 回答 `101 Switching Protocols`（`PTTH/1.0`），连接先挂住，事件还不解析。
- `ANNOUNCE`、镜像 `SETUP`（会话 + type 110 + 伴音 type 96）、`RECORD`、`GET_PARAMETER`（volume）、`SET_PARAMETER`、`PAUSE`、`FLUSH`、`TEARDOWN` 已接通。type 96 收 AAC-ELD，解成 PCM 后写到系统默认播放设备。
- 镜像画面走 TCP，不是 UDP RTP。每个包是 128 字节小端头加负载。Type 0 是 AES-128-CTR 的 AVCC；type 1 是明文 SPS/PPS 和几何信息。
- 宿主默认用进程内 `FairPlayKeys` 回答 `POST /fp-setup`，并在握手之后解开 72 字节 `ekey`。同一次 `FairPlaySession` 同时服务这两步。没有完成握手时，SETUP 仍然 200，随后的加密包只计数、不解码。`/pair-setup-pin`、`/play` 仍返回 501。`--media-key-plugin` 是可选的原生覆盖，FairPlay 不再依赖它。
- Windows 上用 Media Foundation 把 H.264 解成 BGRA。HEVC 先试 Media Foundation，打不开时用程序旁边的 FFmpeg。发布包有两种：不带 `-ui` 的精简包没有窗口；带 `-ui` 的把最新一帧画在 Avalonia 窗口里（Windows、macOS、Linux 各一路一个窗口）。linux-x64 的 Native AOT 不链 `mfplat` / `user32`；H.264 / HEVC 在运行时加载 FFmpeg（`libavcodec` / `libavutil`）。没有解码器时可以用 `--demo-frame`。`--h265` 只有在 HEVC 解码器真能打开时才宣告 features 第 42 位。ui 包在无图形会话时加 `--no-window`。精简包默认就没有窗口。
- 可选地把最新帧编码成 JPEG，按 TapClock ATBF type 3 推到 `host:42125`，或原子写入监视目录。默认关闭。
- 镜像数据连上或断开时，可以 POST Webhook，并按 TapClock 的集成接口通知它重连。配置页默认开在 `127.0.0.1:7099`。本程序不点击控制中心。
- 多台手机可以同时镜像。默认不限制路数。需要上限时用 `--max-mirrors N`（N 为正整数，没有硬上限；内存和 CPU 才是限制）。每一路有自己的数据端口、计时端口、解密、解码器、画面、`session_id` 和窗口。新手机不会断开已经连上的那一路。同一 IPv4 再次 `SETUP` 仍是原来那一路（锁屏后重连）。进程内 FairPlay 每路一次握手。`--media-key-plugin` 的原生库在各路之间共用。

## 多设备镜像

控制连接按对端 IPv4 归到一路。第一台手机用接收端启动时打开的数据端口；后面每一台在自己的 type 110 `SETUP` 应答里拿到单独的 `dataPort` 和 `timingPort`。数据连接只替换这一路自己的旧套接字，用来处理同一台手机锁屏后的重连。另一路的套接字、密钥和 `FrameSlot` 不动。

写了 `--max-mirrors N` 时，第 N+1 路的控制请求返回 HTTP 503，已有会话保持连接。不写这个参数则不限制。日志同时写中文和英文。

`--tapclock` 的 ATBF 仍是 type 3、flags 0 的 JPEG，头部没有来源字段。多路画面会先后推到同一条 TCP；日志写明这一帧的镜像数据对端 IP。要按手机分开保存，用 `--tapclock-dir`：文件名是 `mirror-192.168.5.20.jpg`（IP 来自镜像数据套接字的对端，不是控制连接）。IPv6 里的 `:` 会换成 `-`。还没有数据对端时（例如 `--demo-frame`）写 `mirror-unknown.jpg`。两台手机不会互相覆盖。

`GET /api/status` 的顶层字段仍是第一路的快照。多路时多一个 `sessions` 数组，每项是 `connected`、`since`、`name`、`source_ip`、`session_id`、`disconnect_pending`。`source_ip` 是这一路的对端 IP，不带端口；还没有对端时为空字符串。每一路各自 POST `POST {base}/api/integrations/airplay/events`，`session_id`、`device`、`source_ip` 都按这一路填写。断连防抖按路计算。

Several phones can mirror at once. There is no default cap. `--max-mirrors N` (any positive count) is an optional limit; one more session then gets HTTP 503. Memory and CPU are the practical limit. Each IPv4 gets its own data port, decoder, frame slot, `session_id`, and Avalonia window. The same address reconnects that session instead of opening another. JPEG files are `mirror-<data-peer-ip>.jpg`. ATBF stays a type-3 flags-0 JPEG; the log records the data peer because the header has no source field.

## 镜像连接通知

「已连接」指镜像**数据** TCP 端口已经解析到至少一个合法的 128 字节包头。只完成 RTSP `SETUP`、或有人往数据端口发 HTTP，都不算连接。锁屏的 type 110 `TEARDOWN`、完整 `TEARDOWN`、以及数据连接自己断开，都会进入断连。断连先等防抖（默认 3000 毫秒）。这段时间里新的数据连接又建立，就不发 `disconnected`，会话 id 也不变。防抖结束仍没有数据连接，才把状态改成未连接并 POST。进程退出不补发断连，避免接收端已经停了 TapClock 还去重连。

`session_id` 在同一次镜像会话里保持不变，包括锁屏后的 type 110 重连。完整 `TEARDOWN` 被发布成断连之后会清掉，下一次会话用新的 id。

配置写在程序旁边的 `session-notify.json`，也可以用 `--notify-config` 指定。页面用 Tailwind CDN，没有 Node 构建。监听地址和端口改完后下次启动生效，默认只绑本机。

TapClock 主路径（与通用 Webhook 并行，互不替代）：

```http
POST {tapclock_base}/api/integrations/airplay/events
Content-Type: application/json
X-TapClock-Token: <共享密钥，可省略>
```

`tapclock_base` 默认 `http://127.0.0.1:5088`。正文：

```json
{
  "event": "connected",
  "at": "2026-10-19T14:30:00.0000000+00:00",
  "device": "客厅的 iPhone",
  "source_ip": "192.168.5.20",
  "session_id": "32位十六进制，可省略",
  "reason": "mirror-data",
  "source": "AirPlayServer.NET"
}
```

`event` 是 `connected` 或 `disconnected`。`at` 是带时区偏移的 ISO-8601。`device` 是发送端在会话 SETUP 里报的名字，不知道时为空字符串。`source_ip` 是镜像数据套接字的对端 IP（不带端口）；数据口还没连上时用控制连接的 IPv4。两者都没有时为空字符串。`reason` 可能是 `mirror-data`、`data-closed`、`video-teardown`、`session-teardown`。非 2xx（包括 202、409、400 以外的失败）只记日志，不让宿主退出。重试、冷却和「控制中心 → 屏幕镜像 → AirPlay → 上一台设备」的点击都由 TapClock 做。

通用 Webhook 另有连接地址和断开地址。正文是 `event`、`at`、`device`、`source_ip`、`session_id`、`reason`。`device` 和 `source_ip` 与 TapClock 相同。可选一个自定义头带共享密钥。

镜像窗口的按下和抬起走另一份地址列表 `webhook_urls`（命令行 `--webhook`，可重复；配置页「点击地址」，每行一个）。启用 Webhook 之后才会 POST。连接和断开地址不会收到点击。事件名是 `mirror.click`，`phase` 是 `down` 或 `up`（没有合成的 click，订阅方自己配对）。字段：

| 字段 | 含义 |
| --- | --- |
| `at` | 手势时刻，ISO-8601，带时区偏移 |
| `session_id` | 这一路镜像的会话。数据端口还没连上时可能没有 |
| `device` | 设备名，没有时为空字符串 |
| `source_ip` | 镜像数据对端；还没有数据连接时用 RTSP 对端。没有则为空并省略 |
| `button` | `left`、`right`、`middle`、`x1`、`x2` |
| `window_x`、`window_y` | 窗口客户区设备像素。原点在左上，Y 向下。DIP 乘上渲染缩放后向零截断 |
| `on_content` | 点是否落在按比例摆好的画面上。黑边是 false |
| `content_x`、`content_y` | 解码帧像素。`frame = (客户区点 - 画面原点) * 帧尺寸 / 画面矩形尺寸`，整数截断，再夹到 `[0, 帧尺寸)`。黑边时省略 |
| `content_width`、`content_height` | 当前帧宽高。还没有帧时省略 |
| `logical_x`、`logical_y` | 仅当命令行或配置给出了逻辑屏幕尺寸（`--model`）并且点在画面上。`logical = content * logicalSize / frameSize`，整数除法，X 和 Y 分开算。否则省略 |
| `logical_width`、`logical_height` | 配置的逻辑屏幕尺寸。没配置就省略 |

点击不发给手机，也不改 HID。订阅示例（本机先听 9 号端口，再让窗口点一下）：

```bash
nc -l 127.0.0.1 9
```

收到的正文类似：

```json
{
  "event": "mirror.click",
  "phase": "down",
  "at": "2026-10-20T07:40:00.0000000+00:00",
  "session_id": "32位十六进制",
  "device": "客厅的 iPhone",
  "source_ip": "192.168.1.20",
  "button": "left",
  "window_x": 240,
  "window_y": 400,
  "on_content": true,
  "content_x": 120,
  "content_y": 260,
  "content_width": 480,
  "content_height": 1040,
  "logical_x": 107,
  "logical_y": 232,
  "logical_width": 430,
  "logical_height": 932
}
```

用 curl 自测这条 JSON 能否被你的服务收下（把地址换成 `webhook_urls` 里的那一个）：

```bash
curl -sS -X POST http://127.0.0.1:9/mirror/click \
  -H 'Content-Type: application/json' \
  -d '{"event":"mirror.click","phase":"down","at":"2026-10-20T07:40:00.0000000+00:00","device":"客厅的 iPhone","button":"left","window_x":240,"window_y":400,"on_content":true,"content_x":120,"content_y":260,"content_width":480,"content_height":1040}'
```

`GET http://127.0.0.1:7099/api/status`：

```json
{ "connected": true, "since": "2026-10-19T14:30:00.0000000+00:00", "name": "设备名或接收端名", "source_ip": "192.168.5.20", "session_id": "...", "disconnect_pending": false }
```

`connected` 在防抖期间仍为 true，`disconnect_pending` 为 true。`since` 在未连接时为 null。

Connected means a parsed 128-byte mirror data header, not RTSP SETUP by itself. Disconnect is posted only after the debounce (default 3000 ms) if no replacement data client appears. TapClock and the generic webhook both post `device` and `source_ip` (IP without a port; empty when unknown). `GET /api/status` includes `source_ip` on the primary snapshot and on each `sessions` entry. This process does not synthesize Control Center clicks.

## 镜像伴音和音量

type 96 的 SETUP 返回本会话自己的 `dataPort`、`controlPort` 和 `timingPort`。多路镜像各用各的套接字、解码器、音量和播放，互不共用。密钥是 SHA-512(16 字节内容密钥 || 32 字节 pair-verify 秘密) 的前 16 字节，AES-128-CBC，IV 是会话 `eiv`，每个 RTP 包重置。载荷是 AAC-ELD（AudioSpecificConfig `F8 E8 50 00`，44.1 kHz 立体声）。解码用已经按需加载的 libavcodec，不链进 AOT 二进制。

播放写系统默认设备。Windows 用 WASAPI 共享模式，让系统做采样率转换。Linux 先试 PulseAudio（`libpulse-simple.so.0`），再试 ALSA（`libasound.so.2`）的 `default`。都打不开时日志写明降级，PCM 丢掉，视频镜像继续。macOS 这一版没有 CoreAudio，同样走 Pulse/ALSA，没有就丢掉。解码器和播放设备都等到第一包 PCM 才打开。

`SET_PARAMETER` 的 `text/parameters`（`volume: -11.25`）或二进制 plist 的 `volume` 把分贝钳在 [-144, 0]，并按上游播放器的增益用到 PCM 上：-30 dB 及以下静音，0 dB 原样，中间线性。`GET_PARAMETER volume` 返回当前保存的分贝，默认 `volume: 0.0`。`PAUSE` 丢掉已排队的伴音并静音，直到 `FLUSH` 或 `RECORD`。`FLUSH` 的 `RTP-Info: seq=N` 只清音频缓冲。这三条都不动视频，也不改锁屏恢复时保留的解码器和内容密钥。只拆 type 96 时停音频套接字、留视频和密钥。type 110（哪怕同一条里还有 type 96）仍只拆视频，密钥和 SPS/PPS 保留；若 plist 里有 type 96，音频套接字一并停下。

## 架构

```text
mDNS announcer                         control listeners :7001 and :5001
(Sockets, RFC 6762/6763)               (Sockets + System.IO.Pipelines)
        |                                          |
        |  _airplay._tcp / _raop._tcp              |  pair-setup / pair-verify
        |                                          |  ANNOUNCE / SETUP / RECORD
        v                                          v
                 AirPlayReceiver
                          |
                          |  TCP mirror data + UDP timing
                          |  session notify -> webhooks + TapClock events
                          |  config page 127.0.0.1:7099  GET /api/status
                          v
                 FairPlayKeys (fp-setup + 72-byte ekey) -> AES-CTR
                 IH264Decoder  ->  FrameSlot (latest BGRA)
                    |                         |
                    |                         +-- Avalonia window (one per session; Win32 / X11 / AppKit)
                    |                         +-- ATBF type 3 JPEG -> TapClock :42125
                    |                         +-- temp file -> mirror-<data-peer-ip>.jpg
                    +-- Windows: Media Foundation H.264 / HEVC -> BGRA
                    +-- FFmpeg libavcodec when MF HEVC is missing, and for Linux H.264 / HEVC
                    +-- no library: unavailable, and --h265 is not advertised
```

| 项目 | 职责 |
| --- | --- |
| `deps/AirPlayFairPlay` | 进程内 FairPlay。`FairPlaySession` 来自 AirPlayFairPlayPlugin `8a6a9c46`。GPL-3.0-only。 |
| `src/AirPlayServer.Core` | 协议库。`IsAotCompatible`。发现、配对、`FairPlayKeys`、镜像、JPEG/ATBF、镜像会话通知。 |
| `src/AirPlayServer.Host` | 控制台宿主。`PublishAot`。`EnableAvalonia=true` 才引用 Avalonia。 |
| `tests/AirPlayServer.Core.Tests` | 握手、镜像包、解密、SETUP plist、ATBF 与目录改名、机型表与 `/info` 广告尺寸、会话通知与防抖。 |
| `tests/AirPlayServer.Host.Tests` | `--model` 解析 UIKit 点，`--screen` 单独决定广告分辨率。版本比较、发布资源名、SHA256 和自更新替换也在这里。 |
| `packaging/` | 本地 AOT 发布脚本、systemd 草稿，以及 Release 包里的 `RUN.txt`。脚本本身不创建 GitHub Release。 |
| `.github/workflows/ci.yml` | PR 和 push 上的 restore、build、test，以及 linux-x64 / win-x64 / osx-arm64 的 lean 和 ui 两套 AOT 发布冒烟。 |
| `.github/workflows/release.yml` | `v*` tag 或手动触发时构建 win-x64 与 linux-x64 的 lean / ui 四个 zip，并创建 GitHub Release。 |

握手用的是运行时的 X25519 和 AES（平台会走 AES-NI 或等价指令），外加一份 RFC 8032 Ed25519。Ed25519 的域运算走 `BigInteger`。镜像 CTR 使用同一套 AES-128-CTR，密钥按 `AirPlayStreamKey` / `AirPlayStreamIV` 和 `streamConnectionID` 的十进制文本从内容密钥与 pair-verify 的 X25519 共享秘密派生。像素路径目前是 BGRA 帧槽；还没有 SIMD YUV 转换。

和上游状态机的一个差别：TXT 里带了 `pk` 时，pair-verify 可以是连接上的第一条消息。上游要求先 pair-setup。两种请求这里都接受。

## 构建、测试、AOT 发布

需要 .NET 11 SDK（当前验证过 `11.0.100-rc.2`）。`global.json` 允许在 11.x 上向前滚动，包括预览版。

FairPlay 源码在 `deps/AirPlayFairPlay`（AirPlayFairPlayPlugin `8a6a9c46` 的 `src/AirPlayFairPlay`）。这个副本随仓库一起检出，构建时不需要再拉子模块。

```bash
dotnet build
dotnet test
```

Linux 上的 Native AOT 需要 `clang` 和 `zlib1g-dev`。

```bash
./packaging/publish.sh linux-x64
./artifacts/linux-x64/AirPlayServer.Host --self-check
./artifacts/linux-x64/AirPlayServer.Host --name 客厅 --listen 0.0.0.0
./packaging/publish.sh linux-x64 Release ui
./artifacts/linux-x64-ui/AirPlayServer.Host --help
```

Windows（需要 Visual Studio 的 C++ 生成工具，AOT 用它做链接）：

```powershell
./packaging/publish.ps1 -Runtime win-x64
./packaging/publish.ps1 -Runtime win-x64 -Flavor ui
./artifacts/win-x64/AirPlayServer.Host.exe --help
./artifacts/win-x64-ui/AirPlayServer.Host.exe --help
```

不带第三个参数（或 `-Flavor lean`）的是精简包，输出在 `artifacts/<rid>/`，不引用 Avalonia。`ui` 输出在 `artifacts/<rid>-ui/`。平时 `dotnet build` / `dotnet test` 也是精简宿主。要在本机打开窗口：`dotnet run --project src/AirPlayServer.Host -p:EnableAvalonia=true`。

`--self-check` 在发布出的原生二进制里重跑 RFC 7748、RFC 8032、一次 pair-verify，以及一张 JPEG 的 ATBF 封装，用来确认 AOT 之后这些路径还在。`--seed` 是 32 字节十六进制，只留在内存里，仓库和单元文件都不写种子。宿主默认 `--screen 480x1040`（这是 `/info` 里的广告分辨率）。ui 构建默认打开 Avalonia 窗口。没有 `DISPLAY`（或 macOS 图形会话）时窗口打不开，接收端继续跑，日志会写明；这种机器加 `--no-window`。精简构建默认就没有窗口。`--model` 另选 TapClock 点击对齐的 UIKit 逻辑点，不改这个广告尺寸。`--list-models` 打印 slug。

macOS 上 Avalonia 占用进程主线程（AppKit 的要求），接收循环在线程池里。Windows 和 Linux 把 Avalonia 放在单独的 UI 线程。画面是代码建的窗口，没有 XAML，也没有嵌入 Inter。Native AOT 按 RID 只编进一个窗口系统：win 用 Win32（软件绘制，不带 `av_libglesv2.dll`），linux 用 X11，osx 用 Avalonia.Native。Skia 和 HarfBuzz 的原生库只出现在 ui 包里，跟在程序旁边，第一次打开窗口时才加载。`--help` 和 `--self-check` 不加载它们。

## 更新

默认不检查更新；用 `--update` 手动更新。普通启动不访问 GitHub，开机自启和常驻进程不打更新日志。

手动更新。已经是最新稳定版时只打印版本并退出，不下载，也不启动接收端：

```bash
./AirPlayServer.Host --check-update
./AirPlayServer.Host --update
```

`--update` 会请求 `https://api.github.com/repos/huiyuanai709/AirPlayServer.NET/releases/latest`，只看最新稳定版，跳过预发布。本地版本优先读可执行文件旁边的 `VERSION.txt`（`tag=`）。没有这个文件时，用编译进去的版本号。`dotnet run` 和本地 `publish.sh` 的默认版本是 `1.0.0`，高于目前的 `v0.1.x` 标签，所以不会把编译输出目录换掉。GitHub Release 的 zip 里带有 `VERSION.txt`，那才是安装包的版本。

找到较新的 `vX.Y.Z` 时，下载当前平台、同一种构建的 zip。精简包是 `AirPlayServer.Host-<tag>-win-x64.zip` 或 `linux-x64.zip`。ui 包是同名再加 `-ui`。`--update` 不会把一种换成另一种。Release 里有 `SHA256SUMS.txt` 就先校验，GitHub 资源上的 `sha256:` 摘要也会对一下。通过后替换同目录里的程序、`VERSION.txt`、`LICENSE`、`NOTICE.md`、`RUN.txt`，以及 linux 包里的 `airplayserver.service`。不会把 `.pdb` / `.dbg` 写进目录。然后用原来的参数重新启动，并去掉 `--update` / `--check-update`。Linux 上用 `exec` 换进程，systemd 的 PID 保持不变。Windows 上如果正在运行的 exe 不能直接覆盖，会在退出后由一个短 PowerShell 脚本替换并重启。显式更新失败时进程退出，不启动接收端。v0.1.14 的更新器只认不带 `-ui` 的文件名，从那一版执行 `--update` 会落到精简包；要窗口请另外下载 `-ui`。

`--auto-update` 恢复启动时检查。网络失败、404、GitHub 速率限制、校验不一致或没有写入权限时，只把原因打到日志里，继续跑当前这个程序。这次检查的 API 等待大约 12 秒，下载最多大约 3 分钟，不会一直卡在启动。

```bash
./AirPlayServer.Host --auto-update --name 客厅
```

`--no-update` 仍然接受，效果和默认一样，可以和其他参数一起写。它不能和 `--update` 或 `--auto-update` 一起用。

`--version` 只打印程序内版本和 `VERSION.txt`，不访问网络。`--help`、`--self-check`、`--list-models` 也不检查更新。

公开的 Release 不需要令牌。这个仓库如果是私有的，未认证的请求会得到 404。设置环境变量 `GITHUB_TOKEN` 或 `GH_TOKEN`（只需读取 Release 的权限）后再试。令牌只发给 `api.github.com` 和 `github.com`，不会写进日志，也不会跟着跳转到 GitHub 的资源主机。下载地址只允许这个仓库的 Release，以及 `release-assets.githubusercontent.com`、`objects.githubusercontent.com`、`github-releases.githubusercontent.com` 上的跳转。

## 切一个 GitHub Release

合并到 `main` 之后，用下面两种方式之一发布 win-x64 和 linux-x64 的 Native AOT 宿主，每种平台两个 zip（lean 和 ui）。两种方式走同一个 workflow：`.github/workflows/release.yml`。它调用 `packaging/publish.sh`，跑 `--help` 和 `--self-check`，再把 zip 挂到 GitHub Release 上。每个 zip 里有二进制、`RUN.txt`、`LICENSE` 和 `NOTICE.md`，不含 .pdb / .dbg 调试符号。许可仍是 GPL-3.0-only。workflow 不读取密钥，仓库里也不放密钥。macOS 只在 CI 里发布冒烟，不进 GitHub Release。

版本号必须是 `v1.2.3` 这种形式，也可以带预发布后缀，例如 `v1.2.3-rc.1`。带连字符的版本会标成 prerelease，不会成为 Latest。`workflow_dispatch` 的输入可以不写前缀 `v`，workflow 会补上。

CI（`.github/workflows/ci.yml`）仍然在 PR 和普通 push 上做测试和 AOT 冒烟。它监听所有 push，所以推 tag 时也会再跑一遍。Release workflow 是另外一条，不改 CI 里的冒烟步骤。

### 推一个 tag

tag 必须打在已经包含 `release.yml` 的提交上。先把这条 workflow 合并进 `main`，再打 tag。

```bash
git checkout main
git pull
git tag -a v0.1.0 -m "AirPlayServer.NET v0.1.0"
git push origin v0.1.0
```

推上去之后，Actions 里的 Release workflow 会构建并创建 Release。不需要再手动跑一遍。

### 在 Actions 里手动跑

`workflow_dispatch` 只有在 workflow 文件已经在默认分支 `main` 上时才会出现。

1. 打开 Actions → Release → Run workflow。
2. 选 `main`，或者你确实想发布的那次提交所在的分支。
3. Version 填 `0.1.0` 或 `v0.1.0`。
4. 想先看构建、先不发 Release 时，勾上 dry run。

不勾 dry run 时，workflow 用 `GITHUB_TOKEN` 在这次提交上创建 tag 并发布。这个 token 创建的 tag 不会再触发第二次构建。dry run 只把 zip 留在这次 Actions 的 artifacts 里（保留 14 天），不创建 tag，也不创建 GitHub Release。

已经存在、而且指向别的提交的 tag 不会被覆盖。要重发同一个版本，先删掉旧的 tag 和 Release，或者在原提交上重跑 workflow。

如果 Release 作业报 `Resource not accessible by integration`，到仓库 Settings → Actions → General → Workflow permissions，允许 `GITHUB_TOKEN` 读写 contents。workflow 已经请求了 `contents: write`，仓库上限不能比它更严。

本地的 `packaging/publish.sh` 和 `publish.ps1` 仍然只负责编出 `artifacts/<rid>/`。编完之后可以用 `packaging/stage-release.sh` 在本地打出同样的 zip，这一步不会创建 GitHub Release。

## 相对上游还没移植的部分

上游是 `AirPlay.sln` → `AirPlayServerLib`（协议）→ `airplay2dll`（解码）→ `AirPlayServer`（SDL/ImGui），外加 `dnssd` 和 `MirrorSimAdapter`。

| 上游 | 本仓库 |
| --- | --- |
| `dnssd` 注册 `_airplay._tcp` / `_raop._tcp` | 已实现：自带 IPv4 mDNS。启动按 0/1/2/4 秒连发，之后在 TTL 的 80% 之前刷新；默认按网卡发布非热点的 RFC1918 地址；叠在同一链路上的另一子网查询仍走多播应答。UDP 5353 尽量与 Bonjour 共享。没有名字冲突探测，也没有 IPv6。 |
| `pairing.c` 瞬时 pair-setup / pair-verify | 已实现。Ed25519 标量乘法还不是常数时间。 |
| `pinpair.c` 的 SRP PIN | 未实现。`/pair-setup-pin` 返回 501。 |
| `fairplay_playfair.c` / `POST /fp-setup` | 已接入。进程内 [AirPlayFairPlay](https://github.com/huiyuanai709/AirPlayFairPlayPlugin)（GPL-3.0-only），源自 PlayFair / xenos1337。宿主默认启用。`--media-key-plugin` 只是可选的原生覆盖。 |
| `raop_rtp_mirror.c` 的镜像数据 | 已实现骨架：TCP 128 字节头、type 0/1、UDP 计时探测。不是经典 UDP 视频 RTP。 |
| `raop_rtp.c` / `raop_buffer.c` 音频 | type 96：独立 UDP 数据/控制/计时口，AES-128-CBC（密钥是 SHA-512(contentKey \|\| ecdh) 的前 16 字节，IV 是会话 eiv，每包重置），AAC-ELD 经运行时 FFmpeg 解成 44.1 kHz 立体声 S16。不向手机要重传。FFmpeg 或播放设备打不开时 SETUP 仍 200，PCM 丢掉，视频继续。 |
| FFmpeg H.264 / AAC-ELD、YUV、SDL、ImGui | H.264 / HEVC 画面：Windows 优先 Media Foundation。HEVC 的 MFT 打不开时，以及 Linux 上，运行时加载 FFmpeg 的 `libavcodec` / `libavutil`（不链进 AOT 二进制）。NV12 / YUV420P 在进程内转成 BGRA。没有这些库就不产出画面，并且不宣告 `--h265`。AAC 仍未实现。 |
| `MirrorSimAdapter` JSONL | 未实现。TapClock 走 ATBF，不走这条。 |
| URL / HLS `/play` | 与上游一样不提供。这里直接 501。 |

`/reverse` 只完成升级，不读事件。pair-verify 的 X25519 共享秘密会参与镜像流密钥派生；它本身不是 FairPlay 内容密钥。

## 媒体密钥

真实的 iOS 镜像先走 `POST /fp-setup`，再把 72 字节 `ekey` 解成 16 字节 AES 内容密钥。宿主默认调用 `AirPlayReceiverOptions.EnableInProcessFairPlay()`，把**同一个** `FairPlayKeys`（里面是一个 `FairPlaySession`）同时交给 `IFairPlaySetup` 和 `IMediaKeyUnwrapper`。16 字节 setup 回复 142 字节并清掉旧握手；164 字节握手回复 32 字节并留下 key message；之后的 72 字节 `ekey` 才能解开。握手没完成时，加密画面仍然丢掉，不会当成手机屏幕。

可以用的做法：

- 默认：进程内 AirPlayFairPlay。不需要 `--media-key-plugin`。
- `--lab-key`：32 个十六进制字符，16 字节实验内容密钥。SETUP 用这把密钥，不再解 `ekey`。`/fp-setup` 仍然由进程内 FairPlay 回答。发送端如果直接给出 16 字节 `ekey`，也会当成原始密钥。pair-verify 完成之后，加密的 type 0 会解密并送进解码器。
- `--media-key-plugin 路径`：可选。Native AOT 不能在运行时加载托管程序集，所以这是一条原生覆盖，FairPlay 不再依赖它。传入之后，宿主不再挂上进程内 `FairPlayKeys`。导出可以只有其中一个：

```c
int AirPlay_TryUnwrapContentKey(const uint8_t* wrapped, int wrappedLength, uint8_t* contentKey16);
int AirPlay_FairPlaySetup(const uint8_t* request, int requestLength,
                          uint8_t* response, int responseCapacity, int* written);
```

返回 1 表示成功。没有对应导出，或者它返回 0 时，那一项失败关闭。

没有内容密钥，或者还没有 pair-verify 的 32 字节共享秘密时，type 0 负载先放进 hold 缓冲（`Mirror.PendingEncryptedPayloads`），不推进 AES-CTR。握手和 stream id 到齐后按原顺序解密。缓冲放不下，或者中间丢掉了字节，这一条流才会记入 `Mirror.DroppedEncryptedPayloads`，并且要等下一次会话才能重新对齐。type 1 的 SPS/PPS 是明文，仍然会解析。

## 画面与 TapClock

Windows 上 Media Foundation 把 Annex-B 访问单元变成 BGRA，写入 `FrameSlot`（只保留最新一帧）。同步硬件变换如果还能锁定 NV12、RGB32 或 ARGB32 缓冲就优先用它。变换列出 RGB 时，由 Media Foundation 做 YUV 转换，输出类型上写着解析出来的矩阵和范围。微软 H.264 解码器本身只出 NV12 这一类 YUV，那种帧在进程内转换：SPS 的 VUI 优先，其次是媒体类型上的矩阵和 nominal range，都没有时按 iPhone 镜像的 BT.709 全范围。硬件变换的输出如果是 DXGI 表面，或者只有异步硬件变换时，退回系统自带的软件解码器。HEVC 同样先走 Media Foundation。`CLSID_CMSH265DecoderMFT` 不在时改用 FFmpeg。Linux 上 H.264 和 HEVC 都只走 FFmpeg。没有画面时，访问单元仍然计数，但不会冒充成手机屏幕。Linux 上继续可以用 `--demo-frame` 检查导出。

ui 构建默认打开 Avalonia 窗口，把最新一帧按比例放进客户区（比例不同时留黑边，不拉变形）。缩放是最近邻，和以前的 GDI 软件缩放同一套整数格子，alpha 写成不透明。`--no-window` 关掉它。精简构建没有这个窗口，接收和导出不变。鼠标悬停时，标题和左上角显示 TapClock 绝对屏幕坐标，和 TapClock 日志里的「屏幕坐标」用同一套整数除法：客户区上的点先换成解码帧像素（画面按比例居中，黑边和画面矩形之外不算），再 `screen = 帧像素 * --model 宽高 / 帧宽高`，X 和 Y 各算各的。例如 `--model iphone-15-pro-max` 是 430×932；480×1040 的帧上，像素 (323, 850) 读成 `TapClock 289, 761`。没写 `--model` 时，图像尺寸就是屏幕，标签退回 `像素 x, y`。悬停不读 `--screen`。窗口里的鼠标按下和抬起会按上面的 `mirror.click` 发给 `webhook_urls`，不发给手机，也不改 HID。Linux 窗口走 X11，运行时需要 `libX11`。macOS 走 Avalonia.Native。

TapClock 的点击留在它自己的主机 TCP `42123`（ATB/1）。坐标是板上的 HID 计数（AssistiveTouch），不是这台电脑窗口里的像素。点击对齐和镜像广告是两套尺寸，写在不同的参数上：

| 参数 | 写入哪里 | 单位 | 宿主默认 |
| --- | --- | --- | --- |
| `--screen WxH` | AirPlay `GET`/`POST /info` 的 `width`、`height`、`widthPixels`、`heightPixels`。手机按这个尺寸送镜像。 | 广告分辨率 | `480x1040` |
| `--model <slug>` | TapClock 点击对齐。slug 来自和 TapClock `AppleScreens` 相同的表，解析成竖屏 UIKit 逻辑点。例如 `iphone-15-pro` → 393×852。 | point | 不设置 |

显式 `--screen` 决定广告。`--model` 只填逻辑点，不会把 393×852 写进 `/info`。窗口悬停读数用的也是这套逻辑点。库里 `AirPlayReceiverOptions` 的显示默认值仍是 1920×1080，协议测试不绑在宿主的 `480x1040` 上。

`--list-models` 打印全部 slug 和竖屏逻辑点。表内是标准显示的默认值，不是物理像素，也不是 @2x / @3x。USB HID 读不到手机型号，本进程不会自动探测。Display Zoom、横屏、台前调度和分屏会让有效逻辑点偏离表内默认值。折叠屏不在表里。

和 TapClock 一起用时，镜像广告仍常用 `480x1040`（这是能工作的广告值）。点击对齐另选机型。

帧导出默认关闭。合同以 [TapClock airplay.md](https://github.com/huiyuanai709/TapClock/blob/main/docs/airplay.md) 为准。`--tapclock` 订阅的是直播的 `FrameSlot`：FairPlay 解开并解码出的画面会推过去，不依赖 `--demo-frame`。`--demo-frame` 只是一张合成图。

Windows 上的顺序：

1. 先启动 TapClock，让它监听 ATBF（TCP `42125`）。点击仍是 TapClock 自己的 ATB/1，TCP `42123`。
2. 再启动宿主。FairPlay 默认开着，不要加 `--demo-frame`：

```powershell
./artifacts/win-x64/AirPlayServer.Host.exe --screen 480x1040 --model iphone-15-pro --tapclock 127.0.0.1:42125
```

上面这条里，`/info` 仍宣告 `480x1040`。`iphone-15-pro` 的 393×852 只作为 TapClock 的 UIKit 逻辑点。省略 `--model` 时广告尺寸不变，逻辑点留空。只写 `--model iphone-15-pro`、不写 `--screen` 时，广告仍是宿主默认的 `480x1040`。

3. iPhone 在同一局域网里发现接收端并镜像。锁屏再解锁后，带 type 110 的 TEARDOWN（即使同一条里还有 type 96）只停视频：FairPlay 内容密钥和上一组 SPS/PPS 保留，下一次 type-110 SETUP 从计数器 0 重新开始，并丢掉锁屏前积压的密文。解不开的重复会话 SETUP 不会清掉正在用的密钥。

   只出现 `Mirror sender paused` / `Mirror sender resumed`（H.264 的 codec option 是 `0x56` / `0x16`，HEVC 是 `0x5E` / `0x1E`）时，解码器保持打开。恢复包里的 SPS/PPS（或 VPS）照常解析，下一张关键帧才会拼上去；紧接着的 P 帧直接送进原来的解码器。这条路径不重建上下文，也不等待下一个关键帧。H.264 和 `--h265` 的 ffmpeg-hevc 都是这样。日志里不应再出现「已重建解码器」或「要等下一个关键帧」。`720x1560` 这种几何尺寸是手机这次送出的画面，比 `--screen` 小是正常的，窗口按它缩放。

   已经出过画之后，如果连续两个访问单元都解不出画面，才会丢掉参考帧并等到下一个 IDR/IRAP。那是参考帧真的对不上，不是锁屏暂停本身。正在出画的变换不做 drain。

`POST /fp-setup` 如果比带 `ekey` 的 SETUP 晚到，72 字节 `ekey` 会留着，握手完成后重试解开，这段时间的密文会按到达顺序补解密，而不是直接丢掉。关键帧如果比 SPS/PPS 早到，会留到 codec 数据到达后再解码。Hold 缓冲溢出会让这一条流保持丢弃，直到手机重新开一次镜像。`Mirror.DecodedFrames` 只在真正写出 BGRA 时增加；`DroppedEncryptedPayloads` 只计被丢掉的密文；`PendingEncryptedPayloads` 是还在等密钥的包。连不上 TapClock 时，控制台会写出 `TapClock connect to host:port failed` 并继续重试。

合同：

- 优先：TCP 连到 `主机:42125`。12 字节小端头是 `ATBF` | version = 1 | type u8 | flags u16 | length u32，然后是负载。
- Type 3 是一整张 PNG 或 JPEG。本进程发送 JPEG，flags = 0。不发送 H.264 type 1/2。
- 备用：把 JPEG 写进监视目录。文件名是 `mirror-<镜像数据对端 IP>.jpg`（例如 `mirror-192.168.5.20.jpg`）。还没有对端时是 `mirror-unknown.jpg`。目录不可写或文件被占用时，日志写出完整路径；同时开着 `--tapclock` 时，目录失败不会拆掉 TCP。
- 不用共享内存、命名管道或 HTTP。
- 节拍默认约 2 fps，同一代画面不重复发送。`--tapclock-fps` 可调，上限 30。

```bash
./artifacts/linux-x64/AirPlayServer.Host --screen 480x1040 --no-window --demo-frame \
  --tapclock 127.0.0.1:42125 --tapclock-dir /tmp/tapclock-frames
```

`--demo-frame` 只推一张合成图，用来检查窗口和导出。真正的手机画面来自 H.264 或 HEVC 解码。FFmpeg 是可选的运行时动态库，不链进这个仓库。放法和许可见下一节。

## FFmpeg（可选，运行时加载）

发布包和 Native AOT 二进制都不包含 FFmpeg。宿主用 `NativeLibrary.TryLoad` 加载 `libavcodec` 和 `libavutil`（`libavcodec` 还会依赖 `libswresample`）。找不到库时程序照常启动。Linux 上这时没有手机画面。`--h265` 如果同时也没有 Media Foundation HEVC，不会宣告 features 第 42 位，手机继续发 H.264。日志用中文和英文写明原因。

选择顺序：

| 平台 | H.264 | HEVC（`--h265`） |
| --- | --- | --- |
| Windows | Media Foundation。打不开才试 FFmpeg。 | 先 Media Foundation（同步硬件 MFT，否则 `CLSID_CMSH265DecoderMFT`）。打不开再试 FFmpeg。 |
| Linux | FFmpeg。 | FFmpeg。 |

认这些共享库主版本：FFmpeg 4.4 到 9（libavcodec 58–63，libavutil 56–61，libswresample 3–7）。BtbN 的 n7、n8，以及当前带 avcodec-63 的构建（n9.0 或 master）都在这个范围内。HEVC 和 H.264 按解码器名字打开（`hevc` / `h264`），不写死 `AVCodecID`。FFmpeg 9 删掉了 `AV_CODEC_ID_V410`，libavcodec 63 里 HEVC 是 172，173 变成了 FIC。旧二进制用 173 时，日志是 `[fic @ ...] Zero slices found` 和 `ffmpeg-hevc send failed (Invalid data found when processing input)`，`DecodedFrames` 停在 0，窗口是黑的。软件解码输出 NV12、NV21 或 YUV420P（含 10-bit LE）时，进程内转成 BGRA。SPS 里的矩阵和范围优先；都没写时按 AirPlay 的 BT.709 全范围。自更新只替换发布包里的文件，不会删掉你另外拷进去的这些库。

`--h265` 仍然要加，才会宣告 features 第 42 位。不加时手机继续发 H.264；Windows 上那条路径用 Media Foundation，不需要这些 DLL。`--screen 1290x2796` 只是广告分辨率。手机实际送出的高度可以少几个像素（例如 1290×2794），这不是黑屏的原因。较新的苹果设备只有在广告分辨率够高、并且第 42 位已宣告时才会改发 HEVC。

### Windows（没有商店 HEVC 扩展）

从 [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases) 下载 `win64-lgpl-shared`（`win64-gpl-shared` 也可以，宿主本身是 GPL-3.0-only）。不要用 `*-static`。n7、n8，以及当前带 `avcodec-63.dll` 的构建（`n9.0` 或 `master`）都可以。把 zip 里 `bin` 目录下的 DLL 全部拷到 `AirPlayServer.Host.exe` 旁边。至少要有同一版本的 `avcodec-*.dll`、`avutil-*.dll` 和 `swresample-*.dll`。当前这组是 `avcodec-63.dll`、`avutil-61.dll`、`swresample-7.dll`。

```powershell
.\AirPlayServer.Host.exe --h265 --screen 1290x2796 --model iphone-15-pro-max
```

日志里应出现 `Decoder: ffmpeg-hevc`，并且 features 第 42 位被宣告。DLL 不在时，`--h265` 被忽略，手机仍走 H.264。H.264 在 Windows 上仍然用 Media Foundation，不依赖这些 DLL。

### Linux

```bash
sudo apt-get install ffmpeg
./AirPlayServer.Host --h265 --screen 1290x2796 --no-window
```

也可以把 `libavcodec.so.N`、`libavutil.so.N`、`libswresample.so.N` 放在二进制旁边。`ffmpeg` 包带上的 SONAME 会被系统加载器找到。当前主版本是 `libavcodec.so.63`、`libavutil.so.61`、`libswresample.so.7`。

分发这些 DLL / SO 时要遵守该构建的许可证。LGPL-2.1-or-later 的构建必须允许替换库；带 GPL 组件的构建按 GPL 分发。本仓库不附带 FFmpeg 源码或二进制。见 [NOTICE.md](NOTICE.md)。

## 许可与致谢

本仓库使用 [GPL-3.0-only](LICENSE)。Copyright (c) 2026 AirPlayServer.NET contributors.

发布出来的 Native AOT 宿主是 GPL-3.0-only，因为它把 FairPlay 编进了进程。FairPlay 实现是 [AirPlayFairPlayPlugin](https://github.com/huiyuanai709/AirPlayFairPlayPlugin) `8a6a9c46` 的纯 .NET 库，源自 PlayFair（[xenos1337/AirPlayServer](https://github.com/xenos1337/AirPlayServer) 的 `AirPlayServerLib/lib/playfair` 与 `fairplay_playfair.c`）。

协议行为还参考了 xenos1337/AirPlayServer（MIT，Copyright (c) 2025 xenos1337），以及它的上游 [fingergit/airplay2-win](https://github.com/fingergit/airplay2-win)。除 FairPlay 表和算法移植外，接收路径是按 .NET 的方式重写的，没有拷贝那些仓库里的二进制文件。
