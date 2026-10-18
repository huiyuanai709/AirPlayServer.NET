using System.Globalization;
using System.Net;
using AirPlayServer.Core;
using AirPlayServer.Core.Export;

namespace AirPlayServer.Host;

internal sealed class HostArgs
{
    public bool Help { get; private set; }

    public bool ListModels { get; private set; }

    public bool SelfCheck { get; private set; }

    public AirPlayReceiverOptions Options { get; private set; } = new();

    public const int DefaultAdvertisedWidth = 480;

    public const int DefaultAdvertisedHeight = 1040;

    public static string HelpText => """
        AirPlayServer.NET

        用法:
          AirPlayServer.Host [--name 名称] [--listen 0.0.0.0] [--airplay-port 7001] [--raop-port 5001]
                             [--screen 480x1040] [--model iphone-15-pro] [--list-models]
                             [--no-discovery] [--no-window] [--demo-frame]
                             [--tapclock 主机:42125] [--tapclock-dir 目录] [--tapclock-fps 2]
                             [--lab-key 32位十六进制] [--media-key-plugin 路径] [--seed 64位十六进制]
          AirPlayServer.Host --self-check
          AirPlayServer.Host --help

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
        Windows 窗口里，鼠标停在画面上时，标题和左上角会显示该点在帧里的绝对像素。
        窗口把画面拉伸到客户区，坐标按这个拉伸换算并限制在帧内。悬停像素是解码帧上的点，不是 --model 的 UIKit 点，也不是 --screen 的广告分辨率。点击仍然不发给手机。
        """;

    public static HostArgs Parse(string[] args)
    {
        var parsed = new HostArgs();
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
                case "--self-check":
                    parsed.SelfCheck = true;
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
        parsed.Options = options;
        return parsed;
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
