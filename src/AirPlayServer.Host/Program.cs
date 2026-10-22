using System.Globalization;
using System.Net.Sockets;
using AirPlayServer.Core;
using AirPlayServer.Core.Export;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Host;
#if AVALONIA_UI
using AirPlayServer.Host.Display;
#endif
using AirPlayServer.Host.Notify;
using AirPlayServer.Host.Update;

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

if (arguments.ShowVersion)
{
    HostUpdate.PrintVersion();
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
HostSignals.Install(shutdown);

HostUpdate.CleanupStaleFiles();

if (arguments.UpdateMode == HostUpdateMode.Explicit)
{
    return await HostUpdate.RunExplicitAsync(args, shutdown.Token).ConfigureAwait(false);
}

if (arguments.UpdateMode == HostUpdateMode.Automatic && !HostUpdate.WasJustUpdated())
{
    var restarted = await HostUpdate.TryAutomaticAsync(args, shutdown.Token).ConfigureAwait(false);
    if (restarted || shutdown.IsCancellationRequested)
    {
        if (shutdown.IsCancellationRequested)
        {
            Console.WriteLine("已取消。");
        }

        return 0;
    }
}

#if AVALONIA_UI
if (arguments.Options.ShowWindow && OperatingSystem.IsMacOS())
{
    return AvaloniaMirrorDisplay.RunOnMainThread(arguments.Options, () => RunReceiverAsync(arguments, shutdown.Token));
}

if (arguments.Options.ShowWindow)
{
    arguments.Options.OpenMirrorSurface = AvaloniaMirrorDisplay.Open;
}
#endif

try
{
    return await RunReceiverAsync(arguments, shutdown.Token).ConfigureAwait(false);
}
finally
{
#if AVALONIA_UI
    AvaloniaMirrorDisplay.Shutdown();
#endif
}

static async Task<int> RunReceiverAsync(HostArgs arguments, CancellationToken cancellationToken)
{
    SessionNotifyPlan notifyPlan;
    try
    {
        notifyPlan = SessionNotifyService.Load(arguments.Notify);
    }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException)
    {
        Console.Error.WriteLine("通知配置无效: " + ex.Message);
        return 1;
    }

    AirPlayReceiver receiver;
    try
    {
        receiver = await AirPlayReceiver.StartAsync(arguments.Options, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or SocketException)
    {
        Console.Error.WriteLine("启动失败: " + ex.Message);
        return 1;
    }

    SessionNotifyService notify;
    try
    {
        notify = SessionNotifyService.Start(notifyPlan, receiver);
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or SocketException or InvalidDataException)
    {
        await receiver.DisposeAsync().ConfigureAwait(false);
        Console.Error.WriteLine("通知页面启动失败: " + ex.Message);
        return 1;
    }

    await using (receiver)
    await using (notify)
    {
        Console.WriteLine("AirPlayServer.NET 接收端已启动");
        Console.WriteLine("名称: " + receiver.Name);
        Console.WriteLine("设备 ID: " + receiver.DeviceId);
        Console.WriteLine("AirPlay 控制: " + receiver.AirPlayEndPoint);
        Console.WriteLine("RAOP 控制: " + receiver.RaopEndPoint);
        Console.WriteLine(receiver.DiscoveryReceiving
            ? "mDNS: 正在广播并应答 _airplay._tcp / _raop._tcp（UDP 5353）"
            : arguments.Options.EnableDiscovery
                ? "mDNS: UDP 5353 查询端口不可用，改为更频繁的主动通告"
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
        var mirrorLimit = arguments.Options.MaxMirrors == AirPlayReceiverOptions.DefaultMaxMirrors
            ? "不限制路数"
            : "最多 " + arguments.Options.MaxMirrors.ToString(CultureInfo.InvariantCulture) + " 路";
        Console.WriteLine("同时镜像: " + mirrorLimit
            + "。每路独立数据端口、会话和窗口。导出文件为 mirror-<来源IP>.jpg。");
        if (arguments.Options.EnableUsb)
        {
            Console.WriteLine(string.IsNullOrWhiteSpace(arguments.Options.UsbUdid)
                ? "有线镜像: 已开启（QuickTime-over-USB）。和无线同时收。" + (OperatingSystem.IsWindows() ? " 需要 libusb0.dll。" : " 需要 libusb-1.0。")
                : "有线镜像: 只收 UDID " + arguments.Options.UsbUdid + "。和无线同时收。" + (OperatingSystem.IsWindows() ? " 需要 libusb0.dll。" : " 需要 libusb-1.0。"));
            Console.WriteLine("USB 伴音: ASYN EAT! 的明文 LPCM 写到系统默认播放设备。Windows 用 WASAPI，Linux 用 PulseAudio 或 ALSA。打不开设备时只丢声音，画面继续。无线 AAC-ELD 仍走原来的音量。");
            if (OperatingSystem.IsWindows())
            {
                Console.WriteLine("Windows 驱动: 父设备保持 usbccgp，Apple Devices 可以留着，不要用 Zadig 替换父设备。--usb 用 libusb0.dll 打开 \\\\.\\libusb0-…，不用 WinUSB。一键（管理员）双击 windows-usb-setup.cmd，或运行 windows-usb-driver.ps1 -Setup。它下载官方 libusb-1.0.dll 和官方 libusb-win32，把 libusb0.dll 放到程序旁边，只给 Apple 父设备装 UpperFilter，再把官方 zip 的 libusb0.sys 复制到 System32\\drivers。文件不在就是代码 39，手机只能充电。说明见 RUN.txt。");
            }
            if (arguments.Options.AllowDualIngress)
            {
                Console.WriteLine("同一台手机可以同时走 USB 和 AirPlay（--allow-dual）。");
            }
        }
        else
        {
            Console.WriteLine("有线镜像: 未启用。需要时加 --usb。");
        }
#if AVALONIA_UI
        var windowLine = "显示: 当前进程没有打开窗口。";
        if (arguments.Options.ShowWindow && !AvaloniaMirrorDisplay.HasOpenedSurface)
        {
            windowLine = "显示: 窗口没有打开，画面仍可导出。无图形会话时加 --no-window。";
        }
        else if (arguments.Options.ShowWindow)
        {
            windowLine = arguments.Options.TapClockLogicalScreen is { } hoverModel
                ? "显示: 每一路镜像一个窗口（Windows、macOS、Linux），标题是设备名，只画该路最新一帧。鼠标悬停显示 TapClock 屏幕坐标 "
                    + AppleScreens.FormatPoints(hoverModel)
                    + "。窗口里的按下、拖动和抬起不会发给手机。"
                : "显示: 每一路镜像一个窗口（Windows、macOS、Linux），标题是设备名，只画该路最新一帧。未指定 --model，鼠标悬停显示解码帧像素。窗口里的按下、拖动和抬起不会发给手机。";
        }
#else
        var windowLine = "显示: 精简构建，没有 Avalonia 窗口。画面仍可导出。";
#endif

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
            Console.WriteLine("FairPlay：--media-key-plugin 已接上。各路镜像共用这个原生库的握手；没有对应导出时那一项失败关闭。");
        }
        else
        {
            Console.WriteLine("FairPlay：进程内 AirPlayFairPlay 已启用。每一路镜像有自己的 /fp-setup 握手，不和另一台手机共用。");
        }

        if (arguments.Options.LabContentKey is not null)
        {
            Console.WriteLine("内容密钥：SETUP 使用 --lab-key 的 16 字节实验密钥。");
        }
        foreach (var line in notify.StartupLines())
        {
            Console.WriteLine(line);
        }

        Console.WriteLine("H.264 解码器: " + receiver.Mirror.DecoderName + "。点击仍由 TapClock 走 TCP 42123，坐标是板上的 HID 计数。");
        if (receiver.AdvertisesHevc)
        {
            Console.WriteLine("HEVC：已打开。解码器: " + receiver.HevcDecoderName + "。mDNS 和 /info 的 features 置上第 42 位（SupportsScreenMultiCodec）。手机仍可能选择 H.264。");
        }
        else if (arguments.Options.AcceptHevc)
        {
            Console.WriteLine("HEVC：--h265 已忽略。没有可用的解码器，features 第 42 位不会置上，手机继续发送 H.264。");
        }
        Console.WriteLine("按 Ctrl+C 停止。");
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    Console.WriteLine("已停止。");
    return 0;
}
