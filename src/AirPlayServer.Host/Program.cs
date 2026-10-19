using System.Globalization;
using AirPlayServer.Core;
using AirPlayServer.Core.Export;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Host;

HostArgs arguments;
try
{
    arguments = HostArgs.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(HostArgs.HelpText);
    return 2;
}

if (arguments.Help)
{
    Console.WriteLine(HostArgs.HelpText);
    return 0;
}

if (arguments.ListModels)
{
    Console.WriteLine(AppleScreens.FormatCatalog());
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
    Console.WriteLine("宣告画面: " + arguments.Options.DisplayWidth + "x" + arguments.Options.DisplayHeight
        + "（AirPlay /info 广告分辨率。点击对齐的 UIKit 点见下一行）");
    if (arguments.Options.TapClockLogicalScreen is { } logical)
    {
        Console.WriteLine("TapClock 逻辑点: " + logical.Slug + " " + AppleScreens.FormatPoints(logical)
            + "（竖屏 UIKit 点，点击对齐）。宣告画面仍是上面的 "
            + arguments.Options.DisplayWidth + "x" + arguments.Options.DisplayHeight + "。");
    }
    else
    {
        Console.WriteLine("TapClock 逻辑点: 未指定。--model 选择点击对齐的 UIKit 点，宣告画面保持 "
            + arguments.Options.DisplayWidth + "x" + arguments.Options.DisplayHeight + "。");
    }
    Console.WriteLine("镜像数据端口: " + receiver.Mirror.DataPort + "，计时端口: " + receiver.Mirror.TimingPort);
    var windowLine = "显示: 当前进程没有打开窗口。";
    if (arguments.Options.ShowWindow && OperatingSystem.IsWindows())
    {
        windowLine = arguments.Options.TapClockLogicalScreen is { } hoverModel
            ? "显示: Windows 窗口只画最新一帧。鼠标悬停显示 TapClock 屏幕坐标 "
                + AppleScreens.FormatPoints(hoverModel)
                + "。窗口里的点击不会发给手机。"
            : "显示: Windows 窗口只画最新一帧。未指定 --model，鼠标悬停显示解码帧像素。窗口里的点击不会发给手机。";
    }

    Console.WriteLine(windowLine);
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
    if (arguments.Options.AcceptHevc)
    {
        Console.WriteLine("HEVC：已打开。mDNS 和 /info 的 features 置上第 42 位（SupportsScreenMultiCodec）。手机仍可能选择 H.264。");
    }
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
