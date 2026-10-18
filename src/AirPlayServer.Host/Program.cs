using System.Globalization;
using System.Net;
using AirPlayServer.Core;
using AirPlayServer.Core.Export;
using AirPlayServer.Core.Protocol;

Args arguments;
try
{
    arguments = Args.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Args.HelpText);
    return 2;
}

if (arguments.Help)
{
    Console.WriteLine(Args.HelpText);
    return 0;
}

if (arguments.SelfCheck)
{
    ProtocolSelfCheck.Run();
    ExportSelfCheck.Run();
    Console.WriteLine("self-check ok");
    return 0;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

AirPlayReceiver receiver;
try
{
    receiver = await AirPlayReceiver.StartAsync(arguments.Options, shutdown.Token).ConfigureAwait(false);
}
catch (Exception ex) when (ex is ArgumentException or IOException or System.Net.Sockets.SocketException)
{
    Console.Error.WriteLine("启动失败: " + ex.Message);
    return 1;
}

await using (receiver)
{
    Console.WriteLine("AirPlayServer.NET 接收端已启动");
    Console.WriteLine("名称: " + receiver.Name);
    Console.WriteLine("设备 ID: " + receiver.DeviceId);
    Console.WriteLine("AirPlay 控制: " + receiver.AirPlayEndPoint);
    Console.WriteLine("RAOP 控制: " + receiver.RaopEndPoint);
    Console.WriteLine(receiver.DiscoveryReceiving
        ? "mDNS: 正在广播并应答 _airplay._tcp / _raop._tcp"
        : arguments.Options.EnableDiscovery
            ? "mDNS: 查询端口不可用，仅尝试发送通告"
            : "mDNS: 已关闭");
    Console.WriteLine("宣告画面: " + arguments.Options.DisplayWidth + "x" + arguments.Options.DisplayHeight);
    Console.WriteLine("镜像数据端口: " + receiver.Mirror.DataPort + "，计时端口: " + receiver.Mirror.TimingPort);
    Console.WriteLine(arguments.Options.ShowWindow && OperatingSystem.IsWindows()
        ? "显示: Windows 窗口只画最新一帧。鼠标悬停显示画面像素坐标。窗口里的点击不会发给手机。"
        : "显示: 当前进程没有打开窗口。");
    if (arguments.Options.TapClock is { Enabled: true } tapClock)
    {
        Console.WriteLine("TapClock: "
            + (string.IsNullOrWhiteSpace(tapClock.Endpoint) ? "不推 TCP" : "ATBF JPEG -> " + tapClock.Endpoint)
            + (string.IsNullOrWhiteSpace(tapClock.WatchDirectory) ? "" : "，目录 " + tapClock.WatchDirectory)
            + "，约 " + tapClock.FramesPerSecond.ToString(CultureInfo.InvariantCulture) + " fps");
    }
    else
    {
        Console.WriteLine("TapClock: 未启用。需要 --tapclock host:42125 或 --tapclock-dir。");
    }

    if (!string.IsNullOrWhiteSpace(arguments.Options.MediaKeyPluginPath))
    {
        Console.WriteLine("FairPlay：--media-key-plugin 已接上。解包和 /fp-setup 走这个原生库；没有对应导出时那一项失败关闭。");
    }
    else
    {
        Console.WriteLine("FairPlay：进程内 AirPlayFairPlay 已启用。/fp-setup 和 72 字节 ekey 共用同一次握手。");
    }

    if (arguments.Options.LabContentKey is not null)
    {
        Console.WriteLine("内容密钥：SETUP 使用 --lab-key 的 16 字节实验密钥。");
    }
    Console.WriteLine("H.264 解码器: " + receiver.Mirror.DecoderName + "。点击仍由 TapClock 走 TCP 42123，坐标是板上的 HID 计数。");
    Console.WriteLine("按 Ctrl+C 停止。");
    try
    {
        await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
    }
}

Console.WriteLine("已停止。");
return 0;

internal sealed class Args
{
    public bool Help { get; private set; }

    public bool SelfCheck { get; private set; }

    public AirPlayReceiverOptions Options { get; private set; } = new();

    public static string HelpText => """
        AirPlayServer.NET

        用法:
          AirPlayServer.Host [--name 名称] [--listen 0.0.0.0] [--airplay-port 7001] [--raop-port 5001]
                             [--screen 480x1040] [--no-discovery] [--no-window] [--demo-frame]
                             [--tapclock 主机:42125] [--tapclock-dir 目录] [--tapclock-fps 2]
                             [--lab-key 32位十六进制] [--media-key-plugin 路径] [--seed 64位十六进制]
          AirPlayServer.Host --self-check
          AirPlayServer.Host --help

        FairPlay 默认开启。POST /fp-setup 和 72 字节 ekey 共用同一次进程内握手。
        TapClock 仍然用 --tapclock、--tapclock-dir、--tapclock-fps。
        Windows 上让手机画面进 TapClock 的顺序：
          1. 先启动 TapClock，让它监听 ATBF（TCP 42125）。点击仍走 TapClock 自己的 TCP 42123。
          2. 再启动本宿主，FairPlay 默认开着，不要加 --demo-frame：
             AirPlayServer.Host --screen 480x1040 --tapclock 127.0.0.1:42125
          3. iPhone 发现这台电脑并开始镜像。解码出的最新帧会推给 TapClock。
        握手或 SPS/PPS 比第一帧晚到时，主机会先保住数据，就绪后再解码并推送。
        连不上 TapClock 时，控制台会写出失败原因并继续重试。
        --self-check     核对 X25519、Ed25519、一次 pair-verify，以及一张 JPEG/ATBF。
        --seed           仅存在于内存的 32 字节身份种子，不会写入磁盘。
        --screen         向手机宣告的逻辑分辨率。和 TapClock 配合时常用 480x1040，
                         用来对齐 AssistiveTouch 的 HID 校准。这不是 Windows 窗口里的点击坐标。
        --tapclock       把最新解码帧按 ATBF type 3 JPEG 推到 host:42125。不发送 H.264。
        --tapclock-dir   备用：把 mirror.jpg.part 写完后改名为 mirror.jpg。
        --tapclock-fps   导出节拍，默认约 2，上限 30。
        --lab-key        实验用 16 字节内容密钥。SETUP 用这把密钥。/fp-setup 仍由进程内 FairPlay 回答。
                         pair-verify 完成后，加密的 type 0 会解密。
        --media-key-plugin
                         可选的原生 DLL 或 .so，用来代替进程内 FairPlay。FairPlay 不再依赖它。
                         导出 AirPlay_TryUnwrapContentKey，可选 AirPlay_FairPlaySetup。
                         Native AOT 不能在运行时加载托管程序集，所以这里只接受原生库。
        --demo-frame     推送一张合成画面，用来检查窗口和 TapClock。那不是手机屏幕。
        点击不在本进程发送。TapClock 的点击仍走 TCP 42123（ATB/1）。
        Windows 窗口里，鼠标停在画面上时，标题和左上角会显示该点在帧里的绝对像素。
        窗口把画面拉伸到客户区，坐标按这个拉伸换算并限制在帧内。点击仍然不发给手机。
        """;

    public static Args Parse(string[] args)
    {
        var parsed = new Args();
        var options = new AirPlayReceiverOptions
        {
            DisplayWidth = 480,
            DisplayHeight = 1040,
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
