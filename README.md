# AirPlayServer.NET

把 [xenos1337/AirPlayServer](https://github.com/xenos1337/AirPlayServer)（MIT，Windows C/C++ AirPlay 接收端）的接收路径，用 .NET 11 Native AOT 重新实现。这是第一段垂直切片：设备发现，以及可信的配对 / 会话握手。视频、音频、FairPlay 和 Windows 显示路径还没有做。

This is an independent .NET port of the receive path, not a copy of the upstream C/C++ tree. Upstream binaries, FFmpeg, PlayFair, libplist, SDL, and the vendored Bonjour sources are not included. See [NOTICE.md](NOTICE.md).

Apple、AirPlay、Bonjour 是 Apple Inc. 的商标。广播里的 `AppleTV14,1` 和 `845.5.1` 只是发送端认得出的兼容字符串。

## 现在能做什么

- 用 UDP 套接字向 `224.0.0.251:5353` 通告 `_airplay._tcp` 和 `_raop._tcp`，并回答匹配的 mDNS 查询。
- 在 AirPlay 端口（默认 7001）和 RAOP 端口（默认 5001）上接受 HTTP/1.1 与 RTSP/1.0 控制连接。读取用 `System.IO.Pipelines`。
- 完成瞬时配对：`POST /pair-setup` 返回 Ed25519 公钥；`POST /pair-verify` 做 X25519、对两端临时公钥签名，再用 SHA-512 派生的 AES-128-CTR 掩住签名。
- `GET /server-info` 返回 XML plist，`GET|POST /info` 返回二进制 plist。
- `POST /reverse` 回答 `101 Switching Protocols`（`PTTH/1.0`），连接先挂住，事件还不解析。
- `POST /fp-setup`、`/pair-setup-pin`、`/play` 以及 `ANNOUNCE` / `SETUP` / `RECORD` 明确返回 501。

## 架构

```text
mDNS announcer                         control listeners :7001 and :5001
(Sockets, RFC 6762/6763)               (Sockets + System.IO.Pipelines)
        |                                          |
        |  _airplay._tcp / _raop._tcp              |  /pair-setup  /pair-verify
        |  TXT: deviceid, features, pk, ...        |  /server-info /info /reverse
        v                                          v
                 AirPlayReceiver
                 PairingIdentity (Ed25519 seed, memory only)
                 PairingSession per connection
```

| 项目 | 职责 |
| --- | --- |
| `src/AirPlayServer.Core` | 协议库。`IsAotCompatible`。发现、配对、控制连接、二进制 plist。 |
| `src/AirPlayServer.Host` | 控制台宿主。`PublishAot`。 |
| `tests/AirPlayServer.Core.Tests` | 已知答案、握手往返、mDNS 报文、回环上的控制连接。 |
| `packaging/` | AOT 发布脚本和 systemd 单元草稿。不包含密钥，也不做发布。 |
| `.github/workflows/ci.yml` | restore、build、test，以及 linux-x64 / win-x64 的 AOT 发布冒烟。 |

握手用的是运行时的 X25519 和 AES（平台会走 AES-NI 或等价指令），外加一份 RFC 8032 Ed25519。Ed25519 的域运算走 `BigInteger`：一次连接只签一次名，这里不值得再写一套 SIMD 域。CTR 只负责计数器和异或。YUV 转换才是以后该上 `Vector128` / `Vector256` 的地方，本切片还没有像素路径，所以没有为了“用上 SIMD”而硬加一条。

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

`--self-check` 在发布出的原生二进制里重跑 RFC 7748、RFC 8032 和一次 pair-verify，用来确认 AOT 之后原语还在。`--seed` 是 32 字节十六进制，只留在内存里，仓库和单元文件都不写种子。

## 相对上游还没移植的部分

上游是 `AirPlay.sln` → `AirPlayServerLib`（协议）→ `airplay2dll`（解码）→ `AirPlayServer`（SDL/ImGui），外加 `dnssd` 和 `MirrorSimAdapter`。

| 上游 | 本仓库 |
| --- | --- |
| `dnssd` 注册 `_airplay._tcp` / `_raop._tcp` | 已实现：自带 IPv4 mDNS 通告和查询应答。没有探测冲突，也没有 IPv6。 |
| `pairing.c` 瞬时 pair-setup / pair-verify | 已实现。Ed25519 标量乘法还不是常数时间。 |
| `pinpair.c` 的 SRP PIN | 未实现。`/pair-setup-pin` 返回 501。 |
| `fairplay_playfair.c` / `POST /fp-setup` | **故意不做。** 那是 GPL-3.0 的媒体解密，不是本切片的握手。 |
| `raop_rtp.c` / `raop_rtp_mirror.c` 的音频和镜像 RTP | 未实现。对应 RTSP 方法返回 501。 |
| FFmpeg H.264 / AAC-ELD、YUV 显示、SDL、ImGui | 未实现。没有 Windows 显示路径、干净画面输出或快捷键。 |
| `MirrorSimAdapter` JSONL | 未实现。 |
| URL / HLS `/play` | 与上游一样不提供。这里直接 501。 |

`/reverse` 只完成升级，不读事件。pair-verify 的 X25519 共享密钥留在会话对象里，给以后的加密控制通道用；它不是 FairPlay 媒体密钥，也不会拿去解视频。

## 许可与致谢

本仓库原创代码使用 [MIT](LICENSE)。Copyright (c) 2026 AirPlayServer.NET contributors.

协议行为参考了 [xenos1337/AirPlayServer](https://github.com/xenos1337/AirPlayServer)（MIT，Copyright (c) 2025 xenos1337），以及它的上游 [fingergit/airplay2-win](https://github.com/fingergit/airplay2-win)。实现是按 .NET 的方式重写的，不是逐行翻译，也没有拷贝那些仓库里的二进制文件。
