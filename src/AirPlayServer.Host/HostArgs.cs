using System.Globalization;
using System.Net;
using AirPlayServer.Core;
using AirPlayServer.Core.Export;
using AirPlayServer.Host.Notify;

namespace AirPlayServer.Host;

internal enum HostUpdateMode
{
    Automatic,
    Explicit,
    Disabled,
}

internal sealed class HostArgs
{
    public bool Help { get; private set; }

    public bool ShowVersion { get; private set; }

    public bool ListModels { get; private set; }

    public bool SelfCheck { get; private set; }

    public HostUpdateMode UpdateMode { get; private set; } = HostUpdateMode.Disabled;

    public AirPlayReceiverOptions Options { get; private set; } = new();

    public NotifyLaunch Notify { get; private set; } = new();

    public const int DefaultAdvertisedWidth = 480;

    public const int DefaultAdvertisedHeight = 1040;

    public static string HelpText => FlavorBanner + """
        AirPlayServer.NET

        用法:
          AirPlayServer.Host [--name 名称] [--listen 0.0.0.0] [--airplay-port 7001] [--raop-port 5001]
                             [--screen 480x1040] [--model iphone-15-pro] [--list-models] [--h265]
                             [--decode-timing [sample|every]] [--no-decode-timing]
                             [--max-mirrors N] [--no-discovery] [--no-window] [--demo-frame]
                             [--tapclock 主机:42125] [--tapclock-dir 目录] [--tapclock-fps 2]
                             [--tapclock-events] [--tapclock-base http://127.0.0.1:5088] [--notify-debounce-ms 3000]
                             [--notify-listen 127.0.0.1] [--notify-port 7099] [--notify-config 路径] [--no-notify]
                             [--lab-key 32位十六进制] [--media-key-plugin 路径] [--seed 64位十六进制]
                             [--no-update] [--auto-update]
          AirPlayServer.Host --self-check
          AirPlayServer.Host --update
          AirPlayServer.Host --check-update
          AirPlayServer.Host --version
          AirPlayServer.Host --help

        --listen 默认 0.0.0.0。控制端口仍监听全部 IPv4。mDNS 给每一张非虚拟 RFC1918 网卡
        和正在工作的 Windows 移动热点各发一条只含该网卡地址的通告，并在该网卡上加入多播。
        跳过 Hyper-V vEthernet、WSL、Docker、Clash TAP、VPN、169.254 链路本地。
        热点（Local Area Connection*、Microsoft Wi-Fi Direct Virtual Adapter、192.168.137.0/24）
        单独通告，不会写进局域网那条报文，局域网报文也不会带上热点地址。有局域网时主地址仍是局域网。
        发往 224.0.0.251 的查询，即使来自另一子网
        （例如本机 172.16.88.x、查询来自 172.16.93.x），也在收到它的那张网卡上多播应答。
        直接发到本机地址的查询则单播回去。只剩虚拟网卡时才用第一张非回环网卡；只剩热点时通告热点。
        写成具体地址（例如 --listen 192.168.5.15）时，监听和通告都用它。
        网卡抖动不会拆掉正在镜像的会话；通告地址集合没变就不再发。

        FairPlay 默认开启。POST /fp-setup 和 72 字节 ekey 共用同一次进程内握手。
        TapClock 仍然用 --tapclock、--tapclock-dir、--tapclock-fps。
        Windows 上让手机画面进 TapClock 的顺序：
          1. 先启动 TapClock，让它监听 ATBF（TCP 42125）。点击仍走 TapClock 自己的 TCP 42123。
          2. 再启动本宿主，FairPlay 默认开着，不要加 --demo-frame：
             AirPlayServer.Host --screen 480x1040 --model iphone-15-pro --tapclock 127.0.0.1:42125
          3. iPhone 发现这台电脑并开始镜像。解码出的最新帧会推给 TapClock。
        握手或 SPS/PPS 比第一帧晚到时，主机会先保住数据，就绪后再解码并推送。
        连不上 TapClock 时，控制台会写出失败原因并继续重试。
        --self-check     核对 X25519、Ed25519、一次 pair-verify，以及一张 JPEG/ATBF。
        --seed           仅存在于内存的 32 字节身份种子，不会写入磁盘。
        --screen WxH     AirPlay /info 向手机宣告的镜像分辨率（width、height、widthPixels、heightPixels）。
                         宿主默认 480x1040。这是广告尺寸，用来让手机送出镜像。
                         它不是 UIKit 逻辑点，也不是镜像窗口里的点击坐标。
                         显式写出的 --screen 决定宣告。--model 不改这个值。
        --model <slug>   TapClock 点击对齐用的竖屏 UIKit 逻辑点，和 TapClock 的机型表相同。
                         例如 iphone-15-pro 是 393×852 point，不是 1179×2556 物理像素。
                         只写入点击对齐尺寸。/info 宣告仍是 --screen（默认 480x1040）。
        --list-models    打印 slug 和竖屏 UIKit 逻辑点，然后退出。
        --h265 / --hevc  可选。默认关闭，只收 H.264。
                         只有本机真能打开 HEVC 解码器时，mDNS 和 /info 的 features 才置上第 42 位（SupportsScreenMultiCodec）。
                         没有解码器时忽略这个开关，中英文日志会写明，手机继续发送 H.264。
                         不改变 --screen。宿主默认仍是 480x1040。较新的苹果设备只有在宣告的分辨率够高时才会改发 HEVC。
                         例如：AirPlayServer.Host --h265 --screen 1290x2796 --model iphone-15-pro-max
                         Windows 先用 Media Foundation（同步硬件 MFT 能锁住输出时优先，否则 CLSID_CMSH265DecoderMFT）。
                         打不开时改用程序旁边或系统路径里的 FFmpeg（libavcodec / libavutil）。
                         Linux 上 H.264 和 HEVC 都只走 FFmpeg。库怎么放见 packaging/RUN.txt。
                         --model 仍只决定 UIKit 点击点，不决定广告分辨率。
        --decode-timing [sample|every]
                         打印每次解码调用的耗时，单位固定为微秒（us）。默认关闭。
                         不写参数，或写成 sample：第 1 帧、之后每 30 帧一行，
                         以及任何达到 20000 us 的访问单元立刻一行。
                         行里有解码器名（media-foundation、media-foundation-hardware、
                         media-foundation-hevc、media-foundation-hevc-hardware、
                         ffmpeg-h264、ffmpeg-hevc）、分辨率和已出画序号。
                         every：每个访问单元一行，包括还没出画的。
                         环境变量 AIRPLAY_DECODE_TIMING=off|sample|every（也接受 0、1、all）。
                         命令行优先于环境变量。
        --no-decode-timing
                         关闭解码耗时。环境变量开着时用它关掉。
        --tapclock       把最新解码帧按 ATBF type 3 JPEG 推到 host:42125。不发送 H.264。
        --max-mirrors    同时镜像的手机数量。省略则不限制。写成正整数 N 时，第 N+1 路返回 503。
                         必须是正整数，0 和负数无效，没有硬上限。内存和 CPU 不够时会先撑不住。
                         每一路有自己的数据端口、解密、解码、画面和 session_id。
                         新手机不会踢掉已经连上的那一路。同一 IPv4 再次 SETUP 仍算同一路（锁屏重连）。
                         带 -ui 的包在 Windows、macOS 和 Linux 上每一路一个窗口，标题是设备名。精简包不打开窗口。
        --tapclock-dir   备用：把 JPEG 写进该目录。文件名带来源镜像数据对端的 IP，
                         例如 mirror-192.168.5.20.jpg。IPv6 的冒号会换成短横线。
                         还不知道对端时写 mirror-unknown.jpg。两台手机各写各的文件，互不覆盖。
                         目录不可写或目标文件被占用时，日志会写出完整路径。
                         同时指定了 --tapclock 时，目录失败不会拆掉 TCP 42125。
                         ATBF 仍是 type 3、flags 0 的 JPEG，头里没有来源地址；日志会写明数据对端 IP。
        --tapclock-fps   导出节拍，默认约 2，上限 30。
        --tapclock-events
                         这次启动向 TapClock POST 镜像连接和断连。默认关闭，也可以在配置页打开。
                         重试和点击归 TapClock。本程序不操作控制中心，也不发送 HID。
        --tapclock-base  TapClock 根地址，默认 http://127.0.0.1:5088。
                         事件固定 POST 到 {基址}/api/integrations/airplay/events。
                         可选请求头 X-TapClock-Token 在配置页填写。
        --notify-debounce-ms
                         断连后等待多少毫秒再发出 disconnected。默认 3000。这段时间内数据又连上就不发。
                         connected 不等待。进程退出不补发断连。
        --notify-listen  配置页监听地址，默认 127.0.0.1。只接受 IPv4。
        --notify-port    配置页端口，默认 7099。0 表示不打开页面。
        --notify-config  配置 JSON 路径。默认是程序旁边的 session-notify.json。
        --webhook URL    可重复。镜像窗口的按下、按住移动和抬起 POST 到这个 http(s) 地址。
                         事件名仍是 mirror.click，phase 为 down、move 或 up。悬停不发。
                         同时启用 Webhook，并和配置文件里的 webhook_urls 合并，最多 8 个。
                         坐标说明见 README。这些指针事件不发给手机。
        --no-notify      不打开配置页。文件里已经启用的 Webhook 和 TapClock 事件仍会发送。
        镜像「已连接」是指数据端口解析到了合法的 128 字节镜像包，不是只完成了 RTSP SETUP。
        配置页提供 GET /api/status，返回 connected、since、name、session_id、disconnect_pending。
        --lab-key        实验用 16 字节内容密钥。SETUP 用这把密钥。/fp-setup 仍由进程内 FairPlay 回答。
                         pair-verify 完成后，加密的 type 0 会解密。
        --media-key-plugin
                         可选的原生 DLL 或 .so，用来代替进程内 FairPlay。FairPlay 不再依赖它。
                         导出 AirPlay_TryUnwrapContentKey，可选 AirPlay_FairPlaySetup。
                         Native AOT 不能在运行时加载托管程序集，所以这里只接受原生库。
        --demo-frame     推送一张合成画面，用来检查窗口和 TapClock。那不是手机屏幕。
        点击不在本进程发送。TapClock 的点击仍走 TCP 42123（ATB/1），坐标按 --model 的 UIKit 点对齐。
        带窗口的包里，鼠标停在画面上时，标题和左上角显示 TapClock 绝对屏幕坐标（标签「TapClock x, y」）。
        画面按比例放进客户区，比例不同时留黑边。点先换成解码帧像素（整数截断，画面外的黑边不算），再按 TapClock 的算法换成屏幕点：screen = 帧像素 * --model 宽高 / 帧宽高（整数除法，X 和 Y 分开算）。
        例如 --model iphone-15-pro-max 是 430×932。480×1040 的帧上，像素 (323, 850) 读成 TapClock 289, 761，和 TapClock 日志里的「屏幕坐标」是同一套点。
        没写 --model 时，图像尺寸就是屏幕，标签退回「像素 x, y」。悬停不使用 --screen 的广告分辨率。点击仍然不发给手机。
        默认不检查更新；用 --update 手动更新。普通启动不访问 GitHub。
        版本优先读可执行文件旁边的 VERSION.txt；没有这个文件时用编译进去的版本。
        有较新的 vX.Y.Z 时，下载和这个构建相同的包。精简包是 AirPlayServer.Host-<tag>-win-x64.zip 或 linux-x64.zip，带窗口的包是同名再加 -ui。--update 不会把一种换成另一种。Release 里有 SHA256SUMS.txt 就先校验，再替换本目录里的程序并重新启动。
        检查有超时，不会一直卡住。--update 失败时退出，不启动接收端。--auto-update 失败时只记日志，继续跑当前程序。
        --no-update      不检查更新。这是默认行为，可以和其他参数一起写。
        --update         检查并安装更新，然后重新启动。已经是最新则退出，不启动接收端。
        --check-update   与 --update 相同。
        --auto-update    这次启动检查更新。失败时继续跑当前程序。不能和 --update 或 --no-update 一起用。
        --version        打印程序内版本和 VERSION.txt，然后退出。不访问网络。
        公开的 Release 不需要令牌。私有仓库请设置环境变量 GITHUB_TOKEN 或 GH_TOKEN。
        """;

    private static string FlavorBanner => HostBuild.HasAvaloniaWindow
        ? """
        构建: ui（Avalonia 镜像窗口）。无图形会话时加 --no-window。
        精简包文件名不带 -ui，没有窗口，给 TapClock 用。

        """
        : """
        构建: lean（无 Avalonia 窗口）。不打开镜像窗口，画面仍可导出到 TapClock。
        --no-window 可以写，效果和默认一样。要窗口请用文件名带 -ui 的包。

        """;

    public static HostArgs Parse(string[] args)
    {
        var parsed = new HostArgs();
        var notify = new NotifyLaunch();
        HostUpdateMode? updateMode = null;
        bool? decodeTimingOn = null;
        var options = new AirPlayReceiverOptions
        {
            DisplayWidth = DefaultAdvertisedWidth,
            DisplayHeight = DefaultAdvertisedHeight,
            ShowWindow = HostBuild.HasAvaloniaWindow,
        };
        TapClockExportOptions? tapClock = null;
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            switch (token)
            {
                case "--help":
                case "-h":
                    parsed.Help = true;
                    break;
                case "--list-models":
                    parsed.ListModels = true;
                    break;
                case "--h265":
                case "--hevc":
                    options.AcceptHevc = true;
                    break;
                case "--decode-timing":
                    decodeTimingOn = CombineDecodeTiming(decodeTimingOn, true);
                    options.DecodeTiming = TakeTimingMode(args, ref i);
                    break;
                case "--no-decode-timing":
                    decodeTimingOn = CombineDecodeTiming(decodeTimingOn, false);
                    options.DecodeTiming = DecodeTimingMode.Off;
                    break;
                case "--self-check":
                    parsed.SelfCheck = true;
                    break;
                case "--version":
                    parsed.ShowVersion = true;
                    break;
                case "--update":
                case "--check-update":
                    updateMode = CombineUpdateMode(updateMode, HostUpdateMode.Explicit);
                    break;
                case "--auto-update":
                    updateMode = CombineUpdateMode(updateMode, HostUpdateMode.Automatic);
                    break;
                case "--no-update":
                    updateMode = CombineUpdateMode(updateMode, HostUpdateMode.Disabled);
                    break;
                case "--no-discovery":
                    options.EnableDiscovery = false;
                    break;
                case "--name":
                    options.Name = Next(args, ref i, token);
                    break;
                case "--listen":
                    options.ListenAddress = IPAddress.Parse(Next(args, ref i, token));
                    break;
                case "--airplay-port":
                    options.AirPlayPort = ushort.Parse(Next(args, ref i, token), CultureInfo.InvariantCulture);
                    break;
                case "--raop-port":
                    options.RaopPort = ushort.Parse(Next(args, ref i, token), CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    options.PairingSeed = Convert.FromHexString(Next(args, ref i, token));
                    break;
                case "--screen":
                    ParseScreen(Next(args, ref i, token), options);
                    break;
                case "--model":
                    options.TapClockLogicalScreen = AppleScreens.Resolve(Next(args, ref i, token));
                    break;
                case "--no-window":
                    options.ShowWindow = false;
                    break;
                case "--max-mirrors":
                    options.MaxMirrors = int.Parse(Next(args, ref i, token), CultureInfo.InvariantCulture);
                    break;
                case "--demo-frame":
                    options.PublishDemoFrame = true;
                    break;
                case "--tapclock":
                    tapClock ??= new TapClockExportOptions();
                    tapClock.Endpoint = Next(args, ref i, token);
                    break;
                case "--tapclock-dir":
                    tapClock ??= new TapClockExportOptions();
                    tapClock.WatchDirectory = Next(args, ref i, token);
                    break;
                case "--tapclock-fps":
                    tapClock ??= new TapClockExportOptions();
                    tapClock.FramesPerSecond = double.Parse(Next(args, ref i, token), CultureInfo.InvariantCulture);
                    break;
                case "--tapclock-events":
                    notify.TapClockEvents = true;
                    break;
                case "--tapclock-base":
                    notify.TapClockBaseUrl = Next(args, ref i, token);
                    break;
                case "--notify-debounce-ms":
                    notify.DebounceMs = int.Parse(Next(args, ref i, token), CultureInfo.InvariantCulture);
                    break;
                case "--notify-listen":
                    notify.Listen = IPAddress.Parse(Next(args, ref i, token)).ToString();
                    break;
                case "--notify-port":
                    notify.Port = int.Parse(Next(args, ref i, token), CultureInfo.InvariantCulture);
                    break;
                case "--notify-config":
                    notify.ConfigPath = Next(args, ref i, token);
                    break;
                case "--webhook":
                    notify.WebhookUrls.Add(Next(args, ref i, token));
                    break;
                case "--no-notify":
                    notify.DisablePage = true;
                    break;
                case "--lab-key":
                    options.LabContentKey = Convert.FromHexString(Next(args, ref i, token));
                    break;
                case "--media-key-plugin":
                    options.MediaKeyPluginPath = Next(args, ref i, token);
                    break;
                default:
                    throw new ArgumentException("Unknown argument: " + token);
            }
        }

        if (tapClock is not null && string.IsNullOrWhiteSpace(tapClock.Endpoint) && string.IsNullOrWhiteSpace(tapClock.WatchDirectory))
        {
            throw new ArgumentException("--tapclock-fps requires --tapclock or --tapclock-dir.");
        }

        options.TapClock = tapClock;
        if (decodeTimingOn is null)
        {
            var timingVariable = Environment.GetEnvironmentVariable("AIRPLAY_DECODE_TIMING");
            if (!string.IsNullOrWhiteSpace(timingVariable))
            {
                if (!DecodeTiming.TryParse(timingVariable, out var timing))
                {
                    throw new ArgumentException("AIRPLAY_DECODE_TIMING must be off, sample, or every.");
                }

                options.DecodeTiming = timing;
            }
        }

        options.Log ??= ConsoleAirPlayLog.Instance;
        options.EnableInProcessFairPlay();
        parsed.UpdateMode = updateMode ?? HostUpdateMode.Disabled;
        parsed.Options = options;
        parsed.Notify = notify;
        return parsed;
    }

    private static bool CombineDecodeTiming(bool? current, bool next)
    {
        if (current is null || current == next)
        {
            return next;
        }

        throw new ArgumentException("--decode-timing and --no-decode-timing cannot be combined.");
    }

    private static DecodeTimingMode TakeTimingMode(string[] args, ref int index)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
        {
            return DecodeTimingMode.Sample;
        }

        if (!DecodeTiming.TryParse(args[index + 1], out var mode) || mode == DecodeTimingMode.Off)
        {
            throw new ArgumentException("--decode-timing expects sample or every.");
        }

        index++;
        return mode;
    }

    private static HostUpdateMode CombineUpdateMode(HostUpdateMode? current, HostUpdateMode next)
    {
        if (current is null || current == next)
        {
            return next;
        }

        throw new ArgumentException(UpdateModeConflict(current.Value, next));
    }

    private static string UpdateModeConflict(HostUpdateMode left, HostUpdateMode right)
    {
        var hasExplicit = left == HostUpdateMode.Explicit || right == HostUpdateMode.Explicit;
        var hasDisabled = left == HostUpdateMode.Disabled || right == HostUpdateMode.Disabled;
        if (hasExplicit && hasDisabled)
        {
            return "--update and --no-update cannot be combined.";
        }

        if (hasDisabled)
        {
            return "--auto-update and --no-update cannot be combined.";
        }

        return "--update and --auto-update cannot be combined.";
    }

    private static void ParseScreen(string text, AirPlayReceiverOptions options)
    {
        var parts = text.Split('x', 'X');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height))
        {
            throw new ArgumentException("--screen must look like 480x1040.");
        }

        options.DisplayWidth = width;
        options.DisplayHeight = height;
    }

    private static string Next(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException("Missing value for " + name);
        }

        index++;
        return args[index];
    }
}
