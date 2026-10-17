# AirPlayServer.NET

把 [xenos1337/AirPlayServer](https://github.com/xenos1337/AirPlayServer)（MIT，Windows C/C++ AirPlay 接收端）的接收路径，用 .NET 11 Native AOT 重新实现。当前切片包含发现、瞬时配对、屏幕镜像的 RTSP/TCP 骨架、Windows 最新帧窗口，以及给 [TapClock](https://github.com/huiyuanai709/TapClock/blob/main/docs/airplay.md) 的 JPEG 画面输出。FairPlay 媒体密钥和 H.264 解码器仍然没有接上：没有内容密钥时，加密画面会被丢掉，不会当成手机屏幕显示。

This is an independent .NET port of the receive path, not a copy of the upstream C/C++ tree. Upstream binaries, FFmpeg, PlayFair, libplist, SDL, and the vendored Bonjour sources are not included. See [NOTICE.md](NOTICE.md).

Apple、AirPlay、Bonjour 是 Apple Inc. 的商标。广播里的 `AppleTV14,1` 和 `845.5.1` 只是发送端认得出的兼容字符串。

## 现在能做什么

- 用 UDP 套接字向 `224.0.0.251:5353` 通告 `_airplay._tcp` 和 `_raop._tcp`，并回答匹配的 mDNS 查询。
- 在 AirPlay 端口（默认 7001）和 RAOP 端口（默认 5001）上接受 HTTP/1.1 与 RTSP/1.0 控制连接。读取用 `System.IO.Pipelines`。
- 完成瞬时配对：`POST /pair-setup` 返回 Ed25519 公钥；`POST /pair-verify` 做 X25519、对两端临时公钥签名，再用 SHA-512 派生的 AES-128-CTR 掩住签名。
- `GET /server-info` 返回 XML plist，`GET|POST /info` 返回二进制 plist。
- `POST /reverse` 回答 `101 Switching Protocols`（`PTTH/1.0`），连接先挂住，事件还不解析。
- `ANNOUNCE`、镜像 `SETUP`（会话 + type 110）、`RECORD`、`GET_PARAMETER`（volume）、`SET_PARAMETER`、`PAUSE`、`FLUSH`、`TEARDOWN` 已接通。音频 type 96 仍返回 501。
- 镜像画面走 TCP，不是 UDP RTP。每个包是 128 字节小端头加负载。Type 0 是 AES-128-CTR 的 AVCC；type 1 是明文 SPS/PPS 和几何信息。
- `POST /fp-setup`、`/pair-setup-pin`、`/play` 仍返回 501。72 字节 FairPlay `ekey` 解不开时，SETUP 仍然 200，随后的加密包只计数、不解码。
- Windows 上可以用 GDI 窗口显示最新一帧 BGRA。Linux 上这个窗口不创建，代码也不进 linux-x64 的 AOT 二进制。
- 可选地把最新帧编码成 JPEG，按 TapClock ATBF type 3 推到 `host:42125`，或原子写入监视目录。默认关闭。

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
                          v
                 AES-CTR (only with a 16-byte content key)
                 IH264Decoder  ->  FrameSlot (latest BGRA)
                    |                         |
                    |                         +-- Windows GDI window
                    |                         +-- ATBF type 3 JPEG -> TapClock :42125
                    |                         +-- mirror.jpg.part -> mirror.jpg
                    +-- unavailable until a decoder is supplied
```

| 项目 | 职责 |
| --- | --- |
| `src/AirPlayServer.Core` | 协议库。`IsAotCompatible`。发现、配对、镜像、JPEG/ATBF。 |
| `src/AirPlayServer.Host` | 控制台宿主。`PublishAot`。 |
| `tests/AirPlayServer.Core.Tests` | 握手、镜像包、解密、SETUP plist、ATBF 与目录改名。 |
| `packaging/` | AOT 发布脚本和 systemd 单元草稿。不包含密钥，也不做发布。 |
| `.github/workflows/ci.yml` | restore、build、test，以及 linux-x64 / win-x64 的 AOT 发布冒烟。 |

握手用的是运行时的 X25519 和 AES（平台会走 AES-NI 或等价指令），外加一份 RFC 8032 Ed25519。Ed25519 的域运算走 `BigInteger`。镜像 CTR 使用同一套 AES-128-CTR，密钥按 `AirPlayStreamKey` / `AirPlayStreamIV` 和 `streamConnectionID` 的十进制文本从内容密钥与 pair-verify 的 X25519 共享秘密派生。像素路径目前是 BGRA 帧槽；还没有 SIMD YUV 转换。

和上游状态机的一个差别：TXT 里带了 `pk` 时，pair-verify 可以是连接上的第一条消息。上游要求先 pair-setup。两种请求这里都接受。

## 构建、测试、AOT 发布

需要 .NET 11 SDK（当前验证过 `11.0.100-rc.2`）。`global.json` 允许在 11.x 上向前滚动，包括预览版。

```bash
dotnet build
dotnet test
```

Linux 上的 Native AOT 需要 `clang` 和 `zlib1g-dev`。

```bash
./packaging/publish.sh linux-x64
./artifacts/linux-x64/AirPlayServer.Host --self-check
./artifacts/linux-x64/AirPlayServer.Host --name 客厅 --listen 0.0.0.0
```

Windows（需要 Visual Studio 的 C++ 生成工具，AOT 用它做链接）：

```powershell
./packaging/publish.ps1 -Runtime win-x64
./artifacts/win-x64/AirPlayServer.Host.exe --help
```

`--self-check` 在发布出的原生二进制里重跑 RFC 7748、RFC 8032、一次 pair-verify，以及一张 JPEG 的 ATBF 封装，用来确认 AOT 之后这些路径还在。`--seed` 是 32 字节十六进制，只留在内存里，仓库和单元文件都不写种子。宿主默认 `--screen 480x1040`，并打开窗口标志；在 Linux 上窗口不会创建。

## 相对上游还没移植的部分

上游是 `AirPlay.sln` → `AirPlayServerLib`（协议）→ `airplay2dll`（解码）→ `AirPlayServer`（SDL/ImGui），外加 `dnssd` 和 `MirrorSimAdapter`。

| 上游 | 本仓库 |
| --- | --- |
| `dnssd` 注册 `_airplay._tcp` / `_raop._tcp` | 已实现：自带 IPv4 mDNS 通告和查询应答。没有探测冲突，也没有 IPv6。 |
| `pairing.c` 瞬时 pair-setup / pair-verify | 已实现。Ed25519 标量乘法还不是常数时间。 |
| `pinpair.c` 的 SRP PIN | 未实现。`/pair-setup-pin` 返回 501。 |
| `fairplay_playfair.c` / `POST /fp-setup` | **故意不做。** PlayFair 是 GPL-3.0，不放进这个进程。见下面的媒体密钥。 |
| `raop_rtp_mirror.c` 的镜像数据 | 已实现骨架：TCP 128 字节头、type 0/1、UDP 计时探测。不是经典 UDP 视频 RTP。 |
| `raop_rtp.c` 音频 | 未实现。type 96 的 SETUP 返回 501。 |
| FFmpeg H.264 / AAC-ELD、YUV、SDL、ImGui | 解码器是 `IH264Decoder`。默认实现不产出画面。Windows 只显示已经解码的 BGRA，或 `--demo-frame` 的合成图。不链接 FFmpeg。 |
| `MirrorSimAdapter` JSONL | 未实现。TapClock 走 ATBF，不走这条。 |
| URL / HLS `/play` | 与上游一样不提供。这里直接 501。 |

`/reverse` 只完成升级，不读事件。pair-verify 的 X25519 共享秘密会参与镜像流密钥派生；它本身不是 FairPlay 内容密钥。

## 媒体密钥

真实的 iOS 镜像把 72 字节 `ekey` 用 FairPlay 包成 16 字节 AES 内容密钥。本仓库不实现这个解包，也不响应 `/fp-setup`。

可以用的做法：

- `--lab-key`：32 个十六进制字符，16 字节实验内容密钥。发送端如果直接给出 16 字节 `ekey`，也会当成原始密钥。这不是 FairPlay。
- 进程外实现 `IMediaKeyUnwrapper`，在 `AirPlayReceiverOptions.MediaKeyUnwrapper` 上接入。返回 false 就保持失败关闭。
- 不要把 GPL-3.0 的 PlayFair 编进这个二进制。

没有内容密钥，或者还没有 pair-verify 的 32 字节共享秘密时，type 0 负载会被丢弃并计数（`Mirror.DroppedEncryptedPayloads`）。type 1 的 SPS/PPS 是明文，仍然会解析。

## 画面与 TapClock

解码器把 Annex-B 访问单元变成 BGRA 后写入 `FrameSlot`（只保留最新一帧）。没有解码器时，访问单元仍然计数，但不会冒充成手机画面。

Windows 宿主默认打开 GDI 窗口，把最新一帧拉伸进去。`--no-window` 关掉它。窗口里的鼠标点击不会发给手机。

TapClock 的点击留在它自己的主机 TCP `42123`（ATB/1）。坐标是板上的 HID 计数（AssistiveTouch），不是这台电脑窗口里的像素。`--screen` 是向手机宣告的逻辑分辨率（`/info` 里的宽高）。和 TapClock 一起用时，镜像宣告常用 `480x1040`，宿主默认就是这个尺寸，用来对齐那次校准。库的默认值仍是 1920×1080，避免把协议测试绑死在这块板上。

帧导出默认关闭。合同以 [TapClock airplay.md](https://github.com/huiyuanai709/TapClock/blob/main/docs/airplay.md) 为准：

- 优先：TCP 连到 `主机:42125`。12 字节小端头是 `ATBF` | version = 1 | type u8 | flags u16 | length u32，然后是负载。
- Type 3 是一整张 PNG 或 JPEG。本进程发送 JPEG，flags = 0。不发送 H.264 type 1/2。
- 备用：把 `mirror.jpg.part` 写进监视目录，再改名为 `mirror.jpg`。
- 不用共享内存、命名管道或 HTTP。
- 节拍默认约 2 fps，同一代画面不重复发送。`--tapclock-fps` 可调，上限 30。

```bash
./artifacts/linux-x64/AirPlayServer.Host --screen 480x1040 --no-window --demo-frame \
  --tapclock 127.0.0.1:42125 --tapclock-dir /tmp/tapclock-frames
```

`--demo-frame` 只推一张合成图，用来检查窗口和导出。H.264 硬件解码（Windows Media Foundation）是以后接在 `IH264Decoder` 上的位置；这个切片没有把 COM MFT 编进来，因为在 Native AOT 里还没有验证过。若要使用 FFmpeg，请放在接口后面，并单独遵守 LGPL，不要链进这个仓库。

## 许可与致谢

本仓库原创代码使用 [MIT](LICENSE)。Copyright (c) 2026 AirPlayServer.NET contributors.

协议行为参考了 [xenos1337/AirPlayServer](https://github.com/xenos1337/AirPlayServer)（MIT，Copyright (c) 2025 xenos1337），以及它的上游 [fingergit/airplay2-win](https://github.com/fingergit/airplay2-win)。实现是按 .NET 的方式重写的，不是逐行翻译，也没有拷贝那些仓库里的二进制文件。
