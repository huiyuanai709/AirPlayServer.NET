using System.Globalization;
using System.Net;
using AirPlayServer.Core;
using AirPlayServer.Core.Export;

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

    public const int DefaultAdvertisedWidth = 480;

    public const int DefaultAdvertisedHeight = 1040;

    public static string HelpText => """
        AirPlayServer.NET

        用法:
          AirPlayServer.Host [--name 名称] [--listen 0.0.0.0] [--airplay-port 7001] [--raop-port 5001]
                             [--screen 480x1040] [--model iphone-15-pro] [--list-models] [--h265]
                             [--no-discovery] [--no-window] [--demo-frame]
                             [--tapclock 主机:42125] [--tapclock-dir 目录] [--tapclock-fps 2]
                             [--lab-key 32位十六进制] [--media-key-plugin 路径] [--seed 64位十六进制]
                             [--no-update] [--auto-update]
          AirPlayServer.Host --self-check
          AirPlayServer.Host --update
          AirPlayServer.Host --check-update
          AirPlayServer.Host --version
          AirPlayServer.Host --help

        --listen 默认 0.0.0.0。控制端口仍监听全部 IPv4。mDNS 通告会选一张手机能到达的局域网地址，
        跳过 Hyper-V vEthernet、WSL、Docker、Clash TAP、VPN 和 169.254 链路本地。
        只剩这些地址时才用第一张非回环网卡。写成具体地址（例如 --listen 192.168.5.15）时，监听和通告都用它。

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
                         它不是 UIKit 逻辑点，也不是 Windows 窗口里的点击坐标。
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
        --tapclock       把最新解码帧按 ATBF type 3 JPEG 推到 host:42125。不发送 H.264。
        --tapclock-dir   备用：把 JPEG 写进该目录的临时文件，再替换 mirror.jpg。
                         目录不可写或 mirror.jpg 被占用时，日志会写出完整路径。
                         同时指定了 --tapclock 时，目录失败不会拆掉 TCP 42125。
        --tapclock-fps   导出节拍，默认约 2，上限 30。
        --lab-key        实验用 16 字节内容密钥。SETUP 用这把密钥。/fp-setup 仍由进程内 FairPlay 回答。
                         pair-verify 完成后，加密的 type 0 会解密。
        --media-key-plugin
                         可选的原生 DLL 或 .so，用来代替进程内 FairPlay。FairPlay 不再依赖它。
                         导出 AirPlay_TryUnwrapContentKey，可选 AirPlay_FairPlaySetup。
                         Native AOT 不能在运行时加载托管程序集，所以这里只接受原生库。
        --demo-frame     推送一张合成画面，用来检查窗口和 TapClock。那不是手机屏幕。
        点击不在本进程发送。TapClock 的点击仍走 TCP 42123（ATB/1），坐标按 --model 的 UIKit 点对齐。
        Windows 窗口里，鼠标停在画面上时，标题和左上角显示 TapClock 绝对屏幕坐标（标签「TapClock x, y」）。
        窗口把画面拉伸到客户区。点先换成解码帧像素（整数截断，画面外的黑边不算），再按 TapClock 的算法换成屏幕点：screen = 帧像素 * --model 宽高 / 帧宽高（整数除法，X 和 Y 分开算）。
        例如 --model iphone-15-pro-max 是 430×932。480×1040 的帧上，像素 (323, 850) 读成 TapClock 289, 761，和 TapClock 日志里的「屏幕坐标」是同一套点。
        没写 --model 时，图像尺寸就是屏幕，标签退回「像素 x, y」。悬停不使用 --screen 的广告分辨率。点击仍然不发给手机。
        默认不检查更新；用 --update 手动更新。普通启动不访问 GitHub。
        版本优先读可执行文件旁边的 VERSION.txt；没有这个文件时用编译进去的版本。
        有较新的 vX.Y.Z 时，下载 win-x64 或 linux-x64 的 zip。Release 里有 SHA256SUMS.txt 就先校验，再替换本目录里的程序并重新启动。
        检查有超时，不会一直卡住。--update 失败时退出，不启动接收端。--auto-update 失败时只记日志，继续跑当前程序。
        --no-update      不检查更新。这是默认行为，可以和其他参数一起写。
        --update         检查并安装更新，然后重新启动。已经是最新则退出，不启动接收端。
        --check-update   与 --update 相同。
        --auto-update    这次启动检查更新。失败时继续跑当前程序。不能和 --update 或 --no-update 一起用。
        --version        打印程序内版本和 VERSION.txt，然后退出。不访问网络。
        公开的 Release 不需要令牌。私有仓库请设置环境变量 GITHUB_TOKEN 或 GH_TOKEN。
        """;

    public static HostArgs Parse(string[] args)
    {
        var parsed = new HostArgs();
        HostUpdateMode? updateMode = null;
        var options = new AirPlayReceiverOptions
        {
            DisplayWidth = DefaultAdvertisedWidth,
            DisplayHeight = DefaultAdvertisedHeight,
            ShowWindow = true,
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
        options.Log ??= ConsoleAirPlayLog.Instance;
        options.EnableInProcessFairPlay();
        parsed.UpdateMode = updateMode ?? HostUpdateMode.Disabled;
        parsed.Options = options;
        return parsed;
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
