# AirPlayServer.NET

把 [xenos1337/AirPlayServer](https://github.com/xenos1337/AirPlayServer)（MIT，Windows C/C++ AirPlay 接收端）的接收路径，用 .NET 11 Native AOT 重新实现。当前切片包含发现、瞬时配对、屏幕镜像的 RTSP/TCP 路径、Windows Media Foundation H.264 解码、最新帧窗口、给 [TapClock](https://github.com/huiyuanai709/TapClock/blob/main/docs/airplay.md) 的 JPEG 画面输出，以及进程内 FairPlay（`POST /fp-setup` 与 72 字节 `ekey` 解包）。发布出来的 Native AOT 宿主是 **GPL-3.0-only**，因为里面编进了 [AirPlayFairPlay](https://github.com/huiyuanai709/AirPlayFairPlayPlugin)。

This is an independent .NET port of the receive path, not a copy of the upstream C/C++ tree. Upstream binaries, FFmpeg, libplist, SDL, and the vendored Bonjour sources are not included. FairPlay is compiled in-process from the pure .NET AirPlayFairPlay library, a port of PlayFair. See [NOTICE.md](NOTICE.md). The published AOT host is GPL-3.0-only because that library is part of the binary.

Apple、AirPlay、Bonjour 是 Apple Inc. 的商标。广播里的 `AppleTV14,1` 和 `845.5.1` 只是发送端认得出的兼容字符串。

## 现在能做什么

- 用 UDP 套接字向 `224.0.0.251:5353` 通告 `_airplay._tcp` 和 `_raop._tcp`，并回答匹配的 mDNS 查询。
- 在 AirPlay 端口（默认 7001）和 RAOP 端口（默认 5001）上接受 HTTP/1.1 与 RTSP/1.0 控制连接。读取用 `System.IO.Pipelines`。
- 完成瞬时配对：`POST /pair-setup` 返回 Ed25519 公钥；`POST /pair-verify` 做 X25519、对两端临时公钥签名，再用 SHA-512 派生的 AES-128-CTR 掩住签名。
- `GET /server-info` 返回 XML plist，`GET|POST /info` 返回二进制 plist。
- `POST /reverse` 回答 `101 Switching Protocols`（`PTTH/1.0`），连接先挂住，事件还不解析。
- `ANNOUNCE`、镜像 `SETUP`（会话 + type 110）、`RECORD`、`GET_PARAMETER`（volume）、`SET_PARAMETER`、`PAUSE`、`FLUSH`、`TEARDOWN` 已接通。音频 type 96 仍返回 501。
- 镜像画面走 TCP，不是 UDP RTP。每个包是 128 字节小端头加负载。Type 0 是 AES-128-CTR 的 AVCC；type 1 是明文 SPS/PPS 和几何信息。
- 宿主默认用进程内 `FairPlayKeys` 回答 `POST /fp-setup`，并在握手之后解开 72 字节 `ekey`。同一次 `FairPlaySession` 同时服务这两步。没有完成握手时，SETUP 仍然 200，随后的加密包只计数、不解码。`/pair-setup-pin`、`/play` 仍返回 501。`--media-key-plugin` 是可选的原生覆盖，FairPlay 不再依赖它。
- Windows 上用 Media Foundation 把 H.264 解成 BGRA，GDI 窗口显示最新一帧。Linux 上不创建窗口，也不把 `mfplat` / `user32` 编进 linux-x64 的 AOT 二进制。没有解码器时可以用 `--demo-frame`。
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
                 FairPlayKeys (fp-setup + 72-byte ekey) -> AES-CTR
                 IH264Decoder  ->  FrameSlot (latest BGRA)
                    |                         |
                    |                         +-- Windows GDI window
                    |                         +-- ATBF type 3 JPEG -> TapClock :42125
                    |                         +-- temp file -> mirror.jpg
                    +-- Windows: Media Foundation H.264 -> BGRA
                    +-- other OS: unavailable unless a decoder is supplied
```

| 项目 | 职责 |
| --- | --- |
| `deps/AirPlayFairPlay` | 进程内 FairPlay。`FairPlaySession` 来自 AirPlayFairPlayPlugin `8a6a9c46`。GPL-3.0-only。 |
| `src/AirPlayServer.Core` | 协议库。`IsAotCompatible`。发现、配对、`FairPlayKeys`、镜像、JPEG/ATBF。 |
| `src/AirPlayServer.Host` | 控制台宿主。`PublishAot`。 |
| `tests/AirPlayServer.Core.Tests` | 握手、镜像包、解密、SETUP plist、ATBF 与目录改名、机型表与 `/info` 广告尺寸。 |
| `tests/AirPlayServer.Host.Tests` | `--model` 解析 UIKit 点，`--screen` 单独决定广告分辨率。版本比较、发布资源名、SHA256 和自更新替换也在这里。 |
| `packaging/` | 本地 AOT 发布脚本、systemd 草稿，以及 Release 包里的 `RUN.txt`。脚本本身不创建 GitHub Release。 |
| `.github/workflows/ci.yml` | PR 和 push 上的 restore、build、test，以及 linux-x64 / win-x64 的 AOT 发布冒烟。 |
| `.github/workflows/release.yml` | `v*` tag 或手动触发时构建两个 RID，并创建 GitHub Release。 |

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
```

Windows（需要 Visual Studio 的 C++ 生成工具，AOT 用它做链接）：

```powershell
./packaging/publish.ps1 -Runtime win-x64
./artifacts/win-x64/AirPlayServer.Host.exe --help
```

`--self-check` 在发布出的原生二进制里重跑 RFC 7748、RFC 8032、一次 pair-verify，以及一张 JPEG 的 ATBF 封装，用来确认 AOT 之后这些路径还在。`--seed` 是 32 字节十六进制，只留在内存里，仓库和单元文件都不写种子。宿主默认 `--screen 480x1040`（这是 `/info` 里的广告分辨率），并打开窗口标志；在 Linux 上窗口不会创建。`--model` 另选 TapClock 点击对齐的 UIKit 逻辑点，不改这个广告尺寸。`--list-models` 打印 slug。

## 自动更新

发布包启动时会请求 `https://api.github.com/repos/huiyuanai709/AirPlayServer.NET/releases/latest`，只看最新稳定版，跳过预发布。本地版本优先读可执行文件旁边的 `VERSION.txt`（`tag=`）。没有这个文件时，用编译进去的版本号。`dotnet run` 和本地 `publish.sh` 的默认版本是 `1.0.0`，高于目前的 `v0.1.x` 标签，所以不会把编译输出目录换掉。GitHub Release 的 zip 里带有 `VERSION.txt`，那才是安装包的版本。

找到较新的 `vX.Y.Z` 时，下载当前平台的 zip（`AirPlayServer.Host-<tag>-win-x64.zip` 或 `linux-x64.zip`）。Release 里有 `SHA256SUMS.txt` 就先校验，GitHub 资源上的 `sha256:` 摘要也会对一下。通过后替换同目录里的程序、`VERSION.txt`、`LICENSE`、`NOTICE.md`、`RUN.txt`，以及 linux 包里的 `airplayserver.service`。不会把 `.pdb` / `.dbg` 写进目录。然后用原来的参数重新启动。Linux 上用 `exec` 换进程，systemd 的 PID 保持不变。Windows 上如果正在运行的 exe 不能直接覆盖，会在退出后由一个短 PowerShell 脚本替换并重启。

网络失败、404、校验不一致或没有写入权限时，只把原因打到日志里，继续跑当前这个程序。自动检查的 API 等待大约 12 秒，下载最多大约 3 分钟，不会一直卡在启动。

关掉自动检查：

```bash
./AirPlayServer.Host --no-update --name 客厅
```

手动检查。已经是最新稳定版时只打印版本并退出，不下载，也不启动接收端。这是对当前最新 Release 的干跑：

```bash
./AirPlayServer.Host --check-update
./AirPlayServer.Host --update
```

`--version` 只打印程序内版本和 `VERSION.txt`，不访问网络。`--help`、`--self-check`、`--list-models` 也不检查更新。

公开的 Release 不需要令牌。这个仓库如果是私有的，未认证的请求会得到 404。设置环境变量 `GITHUB_TOKEN` 或 `GH_TOKEN`（只需读取 Release 的权限）后再试。令牌只发给 `api.github.com` 和 `github.com`，不会写进日志，也不会跟着跳转到 GitHub 的资源主机。下载地址只允许这个仓库的 Release，以及 `release-assets.githubusercontent.com`、`objects.githubusercontent.com`、`github-releases.githubusercontent.com` 上的跳转。

## 切一个 GitHub Release

合并到 `main` 之后，用下面两种方式之一发布 win-x64 和 linux-x64 的 Native AOT 宿主。两种方式走同一个 workflow：`.github/workflows/release.yml`。它用和 `packaging/publish.sh` / `publish.ps1` 相同的 `dotnet publish` 参数做 Native AOT，跑 `--help` 和 `--self-check`，再把 zip 挂到 GitHub Release 上。每个 zip 里有二进制、`RUN.txt`、`LICENSE` 和 `NOTICE.md`，不含 .pdb / .dbg 调试符号。许可仍是 GPL-3.0-only。workflow 不读取密钥，仓库里也不放密钥。

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
| `dnssd` 注册 `_airplay._tcp` / `_raop._tcp` | 已实现：自带 IPv4 mDNS 通告和查询应答。没有探测冲突，也没有 IPv6。 |
| `pairing.c` 瞬时 pair-setup / pair-verify | 已实现。Ed25519 标量乘法还不是常数时间。 |
| `pinpair.c` 的 SRP PIN | 未实现。`/pair-setup-pin` 返回 501。 |
| `fairplay_playfair.c` / `POST /fp-setup` | 已接入。进程内 [AirPlayFairPlay](https://github.com/huiyuanai709/AirPlayFairPlayPlugin)（GPL-3.0-only），源自 PlayFair / xenos1337。宿主默认启用。`--media-key-plugin` 只是可选的原生覆盖。 |
| `raop_rtp_mirror.c` 的镜像数据 | 已实现骨架：TCP 128 字节头、type 0/1、UDP 计时探测。不是经典 UDP 视频 RTP。 |
| `raop_rtp.c` 音频 | 未实现。type 96 的 SETUP 返回 501。 |
| FFmpeg H.264 / AAC-ELD、YUV、SDL、ImGui | Windows 的 `IH264Decoder` 是 Media Foundation（能锁住输出缓冲的同步硬件变换优先，否则用系统自带的 H.264 软件解码器），输出 BGRA。其它平台默认不产出画面。不链接 FFmpeg。 |
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

Windows 上 Media Foundation 把 Annex-B 访问单元变成 BGRA，写入 `FrameSlot`（只保留最新一帧）。同步硬件变换如果还能锁定 NV12、RGB32 或 ARGB32 缓冲就优先用它。变换列出 RGB 时，由 Media Foundation 做 YUV 转换，输出类型上写着解析出来的矩阵和范围。微软 H.264 解码器本身只出 NV12 这一类 YUV，那种帧在进程内转换：SPS 的 VUI 优先，其次是媒体类型上的矩阵和 nominal range，都没有时按 iPhone 镜像的 BT.709 全范围。硬件变换的输出如果是 DXGI 表面，或者只有异步硬件变换时，退回系统自带的软件解码器。其它操作系统默认不解码。没有画面时，访问单元仍然计数，但不会冒充成手机屏幕。Linux 上继续可以用 `--demo-frame` 检查窗口标志和 TapClock。

Windows 宿主默认打开 GDI 窗口，把最新一帧按比例放进客户区（比例不同时留黑边，不拉变形）。目标尺寸和帧不一样时用 HALFTONE。`--no-window` 关掉它。鼠标悬停时，标题和左上角显示 TapClock 绝对屏幕坐标，和 TapClock 日志里的「屏幕坐标」用同一套整数除法：客户区上的点先换成解码帧像素（画面按比例居中，黑边和画面矩形之外不算），再 `screen = 帧像素 * --model 宽高 / 帧宽高`，X 和 Y 各算各的。例如 `--model iphone-15-pro-max` 是 430×932；480×1040 的帧上，像素 (323, 850) 读成 `TapClock 289, 761`。没写 `--model` 时，图像尺寸就是屏幕，标签退回 `像素 x, y`。悬停不读 `--screen`。窗口里的鼠标点击不会发给手机，也不改 HID。

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

3. iPhone 在同一局域网里发现接收端并镜像。锁屏再解锁后，带 type 110 的 TEARDOWN（即使同一条里还有 type 96）只停视频：FairPlay 内容密钥和上一组 SPS/PPS 保留，下一次 type-110 SETUP 从计数器 0 重新开始，并丢掉锁屏前积压的密文。解不开的重复会话 SETUP 不会清掉正在用的密钥。Windows 上 Media Foundation 会释放当前解码器，下一帧重建，避免画面停在锁屏前的最后一帧。

`POST /fp-setup` 如果比带 `ekey` 的 SETUP 晚到，72 字节 `ekey` 会留着，握手完成后重试解开，这段时间的密文会按到达顺序补解密，而不是直接丢掉。关键帧如果比 SPS/PPS 早到，会留到 codec 数据到达后再解码。Hold 缓冲溢出会让这一条流保持丢弃，直到手机重新开一次镜像。`Mirror.DecodedFrames` 只在真正写出 BGRA 时增加；`DroppedEncryptedPayloads` 只计被丢掉的密文；`PendingEncryptedPayloads` 是还在等密钥的包。连不上 TapClock 时，控制台会写出 `TapClock connect to host:port failed` 并继续重试。

合同：

- 优先：TCP 连到 `主机:42125`。12 字节小端头是 `ATBF` | version = 1 | type u8 | flags u16 | length u32，然后是负载。
- Type 3 是一整张 PNG 或 JPEG。本进程发送 JPEG，flags = 0。不发送 H.264 type 1/2。
- 备用：把 JPEG 写进监视目录里的临时文件，再替换 `mirror.jpg`。目录不可写或文件被占用时，日志写出完整路径；同时开着 `--tapclock` 时，目录失败不会拆掉 TCP。
- 不用共享内存、命名管道或 HTTP。
- 节拍默认约 2 fps，同一代画面不重复发送。`--tapclock-fps` 可调，上限 30。

```bash
./artifacts/linux-x64/AirPlayServer.Host --screen 480x1040 --no-window --demo-frame \
  --tapclock 127.0.0.1:42125 --tapclock-dir /tmp/tapclock-frames
```

`--demo-frame` 只推一张合成图，用来检查窗口和导出。真正的手机画面来自 H.264 解码。若必须换用 FFmpeg，请放在 `IH264Decoder` 后面并单独遵守 LGPL，不要链进这个仓库。

## 许可与致谢

本仓库使用 [GPL-3.0-only](LICENSE)。Copyright (c) 2026 AirPlayServer.NET contributors.

发布出来的 Native AOT 宿主是 GPL-3.0-only，因为它把 FairPlay 编进了进程。FairPlay 实现是 [AirPlayFairPlayPlugin](https://github.com/huiyuanai709/AirPlayFairPlayPlugin) `8a6a9c46` 的纯 .NET 库，源自 PlayFair（[xenos1337/AirPlayServer](https://github.com/xenos1337/AirPlayServer) 的 `AirPlayServerLib/lib/playfair` 与 `fairplay_playfair.c`）。

协议行为还参考了 xenos1337/AirPlayServer（MIT，Copyright (c) 2025 xenos1337），以及它的上游 [fingergit/airplay2-win](https://github.com/fingergit/airplay2-win)。除 FairPlay 表和算法移植外，接收路径是按 .NET 的方式重写的，没有拷贝那些仓库里的二进制文件。
