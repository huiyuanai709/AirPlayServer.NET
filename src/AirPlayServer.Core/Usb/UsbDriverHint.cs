namespace AirPlayServer.Core.Usb;

/// <summary>
/// One Apple USB parent as Windows enumerated it. The function driver should
/// stay <c>usbccgp</c> (or Apple's own <c>usbaapl64</c>). A libusb-win32
/// capture path is an UpperFilter on that instance, not a replacement of it.
/// </summary>
internal sealed class UsbParentBinding
{
    public required string InstanceId { get; init; }

    public required string Service { get; init; }

    public bool Libusb0UpperFilter { get; init; }

    public bool KeepsAppleParent =>
        EqualsService("usbccgp")
        || EqualsService("usbaapl64")
        || EqualsService("usbaapl")
        || EqualsService("appleusb");

    public bool ReplacedParent =>
        EqualsService("winusb")
        || EqualsService("libusbk")
        || EqualsService("libusb0");

    private bool EqualsService(string name)
        => string.Equals(Service, name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Read-only facts the host can print before it tries to claim a phone.
/// Populated by <see cref="UsbDriverProbe"/> on Windows and left empty elsewhere.
/// </summary>
internal sealed class UsbDriverSnapshot
{
    public bool IsWindows { get; init; }

    public bool LibraryLoaded { get; init; }

    public string? LibraryPath { get; init; }

    public bool AppleMobileDeviceService { get; init; }

    public bool AppleSupportFiles { get; init; }

    /// <summary>
    /// Microsoft Store Apple Devices or iTunes. Those packages do not register
    /// the classic Apple Mobile Device Service. Filter drivers and the Store
    /// package name still mean the Apple stack is installed.
    /// </summary>
    public bool AppleStoreStack { get; init; }

    /// <summary>
    /// <c>HKLM\SYSTEM\CurrentControlSet\Services\libusb0</c> exists.
    /// install-filter creates this service with ImagePath <c>system32\drivers\libusb0.sys</c>
    /// and does not copy the file.
    /// </summary>
    public bool Libusb0ServicePresent { get; init; }

    /// <summary>
    /// <c>%SystemRoot%\System32\drivers\libusb0.sys</c> is on disk.
    /// A registered UpperFilter or service without this file is Code 39.
    /// </summary>
    public bool Libusb0DriverFilePresent { get; init; }

    public string? ProbeError { get; init; }

    public IReadOnlyList<UsbParentBinding> Parents { get; init; } = [];

    public bool AppleStackPresent => AppleMobileDeviceService || AppleSupportFiles || AppleStoreStack;
}

/// <summary>
/// Next-step text for a missing libusb library or a failed claim. The host
/// never installs a driver. Windows users run <c>windows-usb-driver.ps1</c>.
/// </summary>
internal static class UsbDriverHint
{
    public const string ScriptName = "windows-usb-driver.ps1";

    public static bool NeedsAttention(UsbDriverSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.LibraryLoaded)
        {
            return true;
        }

        if (!snapshot.IsWindows)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(snapshot.ProbeError))
        {
            return true;
        }

        if (!snapshot.AppleStackPresent)
        {
            return true;
        }

        if (DriverRegisteredWithoutFile(snapshot))
        {
            return true;
        }

        foreach (var parent in snapshot.Parents)
        {
            if (parent.ReplacedParent || (parent.KeepsAppleParent && !parent.Libusb0UpperFilter))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// UpperFilter or the libusb0 service is registered, but the kernel file it loads is absent.
    /// </summary>
    public static bool DriverRegisteredWithoutFile(UsbDriverSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsWindows || snapshot.Libusb0DriverFilePresent)
        {
            return false;
        }

        if (snapshot.Libusb0ServicePresent)
        {
            return true;
        }

        foreach (var parent in snapshot.Parents)
        {
            if (parent.Libusb0UpperFilter)
            {
                return true;
            }
        }

        return false;
    }

    public static string LibraryMissing()
        => "libusb-1.0 was not found next to the host or on the system library path. "
           + "Wired mirroring stays off. Wireless AirPlay is unchanged. "
           + "Windows: copy libusb-1.0.dll beside the host, or run " + ScriptName + " -Setup (or -FetchLibusb -HostDir <folder>). "
           + "Linux: libusb-1.0.so.0 (apt install libusb-1.0-0). macOS: libusb-1.0.0.dylib (brew install libusb). "
           + "没有找到 libusb-1.0。有线镜像不启用，无线 AirPlay 不受影响。Windows 把 libusb-1.0.dll 放在程序旁边，或运行 " + ScriptName + " -FetchLibusb。";

    public static string ForLibusb(int rc, string action)
    {
        var what = string.IsNullOrWhiteSpace(action) ? "USB request" : action.Trim();
        var code = "libusb " + rc + " during " + what + ". ";
        var next = rc switch
        {
            -12 => "NOT_SUPPORTED. WinUSB cannot change the USB configuration, so it cannot open the QuickTime interface (class 0xFF subclass 0x2A). "
                   + "Keep the Apple parent on usbccgp and install a libusb-win32 UpperFilter on that device instance only. "
                   + "Run " + ScriptName + " -Setup, or double-click windows-usb-setup.cmd. "
                   + "Do not run Zadig on the Apple composite parent. ",
            -3 => "ACCESS. The device is open in another driver, or this process cannot open it. "
                  + "Leave the Apple parent on usbccgp. Do not run Zadig on the Apple composite parent. "
                  + "Install the libusb-win32 UpperFilter for this phone and replug it. ",
            -6 => "BUSY. The interface is already claimed. Close other USB tools, keep the Apple parent on usbccgp, "
                  + "and do not run Zadig on the Apple composite parent. Replug after the libusb-win32 UpperFilter is installed. ",
            -4 or -5 => "The phone is not on the bus. Unlock it, tap Trust This Computer, and replug. Wireless AirPlay is unchanged. ",
            -1 => "IO (LIBUSB_ERROR_IO). libusb-1.0 listed the Apple device and libusb_open failed. The phone is not missing, and the device list was not empty. "
                  + "Do not uninstall Apple Devices. Wireless AirPlay is unchanged. ",
            _ => "Wired USB did not open. Wireless AirPlay is unchanged. ",
        };
        return code + next
               + "See RUN.txt and " + ScriptName + ". "
               + "libusb " + rc + "。" + Chinese(rc);
    }

    public static string Readiness(UsbDriverSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsWindows)
        {
            var loaded = snapshot.LibraryLoaded
                ? "libusb loaded" + (string.IsNullOrEmpty(snapshot.LibraryPath) ? "" : " from " + snapshot.LibraryPath) + ". "
                : "libusb is not loaded. ";
            return loaded
                   + "Linux and macOS use the system libusb-1.0 shared library. No extra USB driver is required. "
                   + "Linux：系统里的 libusb-1.0.so.0。macOS：libusb-1.0.0.dylib。不需要额外驱动。";
        }

        var lines = new List<string>();
        if (!string.IsNullOrEmpty(snapshot.ProbeError))
        {
            lines.Add("Could not read the Windows USB registry: " + snapshot.ProbeError + ". Run " + ScriptName + " -Check from an elevated prompt if this persists.");
        }

        if (!snapshot.LibraryLoaded)
        {
            lines.Add(LibraryMissing());
        }
        else if (!string.IsNullOrEmpty(snapshot.LibraryPath))
        {
            lines.Add("libusb loaded from " + snapshot.LibraryPath + ".");
        }

        if (!snapshot.AppleStackPresent)
        {
            lines.Add(
                "Apple Mobile Device Support was not found. Install Apple Devices from the Microsoft Store, or Apple Mobile Device Support from Apple's desktop package. "
                + "This host does not download Apple software. Do not replace the Apple parent with Zadig. "
                + "没有找到 Apple Mobile Device Support。本程序不下载 Apple 的安装包，也不要用 Zadig 替换父设备。");
        }
        else if (snapshot.AppleStoreStack && !snapshot.AppleMobileDeviceService)
        {
            lines.Add(
                "Microsoft Store Apple Devices is installed. The classic Apple Mobile Device Service is not registered, and Common Files\\Apple\\Mobile Device Support may be absent. That is normal for AppleInc.AppleDevices. AppleLowerFilter, AppleKmdfFilter, AppleMobileDeviceProcess.exe, or AMDS32\\MobileDevice.dll count as the Apple stack. Do not uninstall Apple Devices. "
                + "已安装商店版 Apple Devices。没有传统的 Apple Mobile Device Service 是正常的。不要卸载 Apple Devices。");
        }
        else if (!snapshot.AppleMobileDeviceService)
        {
            lines.Add("Apple support files are present, but the Apple Mobile Device Service is not registered. Repair that install before expecting a wired mirror. Apple 文件在，但 Apple Mobile Device Service 没有注册。");
        }

        if (snapshot.Parents.Count == 0)
        {
            lines.Add("No Apple USB parent is plugged in. When a phone is connected, its parent service must stay usbccgp. Do not run Zadig on the Apple composite parent. 当前没有插着 Apple USB 父设备。插上之后应保持 usbccgp，不要用 Zadig。");
        }

        if (DriverRegisteredWithoutFile(snapshot))
        {
            lines.Add(
                "libusb0 is registered (UpperFilter and/or the libusb0 kernel service) but Windows\\System32\\drivers\\libusb0.sys is missing. "
                + "Device Manager shows Code 39 (CM_PROB_DRIVER_FAILED_LOAD): the Apple parent fails to load, the phone only charges, and the Trust dialog does not appear. "
                + "install-filter.exe only creates the service and UpperFilter. Re-run " + ScriptName + " -Setup or windows-usb-setup.cmd so it copies bin\\amd64\\libusb0.sys from the official libusb-win32 zip and checks the file is there. "
                + "Copying bin\\amd64\\libusb0.sys from that zip into System32\\drivers also repairs it. Do not use Zadig. "
                + "已注册 libusb0，但 System32\\drivers\\libusb0.sys 不在。设备管理器代码 39，手机只能充电。请重新运行 -Setup，不要用 Zadig。");
        }

        foreach (var parent in snapshot.Parents)
        {
            if (parent.ReplacedParent)
            {
                lines.Add(
                    "Apple USB parent " + parent.InstanceId + " is bound to " + parent.Service
                    + ". That replaces usbccgp. Restore Apple Mobile Device Support so the parent service is usbccgp, then install a libusb-win32 UpperFilter on that instance only. "
                    + "Do not use Zadig on the Apple composite parent. "
                    + "父设备被换成了 " + parent.Service + "。先恢复 usbccgp，再只给这一实例加 libusb0 UpperFilter。");
                continue;
            }

            if (parent.KeepsAppleParent && parent.Libusb0UpperFilter && !snapshot.Libusb0DriverFilePresent)
            {
                lines.Add(
                    "Apple USB parent " + parent.InstanceId + " is " + parent.Service
                    + " with a libusb0 UpperFilter, but libusb0.sys is not in System32\\drivers, so that filter cannot load (Code 39). "
                    + "Re-run " + ScriptName + " -Setup. "
                    + "父设备有 libusb0 UpperFilter，但缺少 libusb0.sys，驱动加载失败（代码 39）。请重新运行 -Setup。");
                continue;
            }

            if (parent.KeepsAppleParent && parent.Libusb0UpperFilter)
            {
                lines.Add(
                    "Apple USB parent " + parent.InstanceId + " is " + parent.Service + " with a libusb0 UpperFilter, and libusb0.sys is installed. The host claims only the QuickTime interface. "
                    + "Unlock the phone and tap Trust if the mirror does not start. "
                    + "父设备是 " + parent.Service + "，已有 libusb0 UpperFilter，libusb0.sys 也在。程序只认领 QuickTime 接口。");
                continue;
            }

            if (parent.KeepsAppleParent)
            {
                lines.Add(
                    "Apple USB parent " + parent.InstanceId + " is " + parent.Service + ", which is the right function driver. "
                    + "libusb still needs a libusb-win32 UpperFilter on this instance before it can switch to the QuickTime configuration. "
                    + "Do not bind WinUSB to the parent. Run " + ScriptName + " -Setup, or -InstallFilter -FilterZip <official libusb-win32 zip>, then unplug and replug. "
                    + "父设备驱动正确，但还没有 libusb0 UpperFilter。不要把父设备绑成 WinUSB。");
                continue;
            }

            lines.Add(
                "Apple USB parent " + parent.InstanceId + " uses service " + parent.Service
                + ". Leave it on the Apple or usbccgp driver. Do not run Zadig on the Apple composite parent. "
                + "这个父设备的服务是 " + parent.Service + "。不要用 Zadig 替换它。");
        }

        return string.Join(" ", lines);
    }

    private static string Chinese(int rc)
        => rc switch
        {
            -12 => "WinUSB 改不了 USB 配置。父设备保持 usbccgp，只给该实例装 libusb-win32 UpperFilter。不要用 Zadig 改 Apple 复合父设备。",
            -3 => "设备被别的驱动占用或当前进程打不开。父设备保持 usbccgp，装好过滤驱动后重新插拔。不要用 Zadig 改父设备。",
            -6 => "接口正忙。关掉其他 USB 工具，父设备保持 usbccgp，装好过滤驱动后重新插拔。不要用 Zadig 改父设备。",
            -4 or -5 => "手机不在总线上。解锁并信任这台电脑，然后重新插拔。无线 AirPlay 不受影响。",
            -1 => "LIBUSB_ERROR_IO。libusb-1.0 已经列出这台 Apple 设备，但 libusb_open 失败。手机还在，列表也不是空的。不要卸载 Apple Devices。无线 AirPlay 不受影响。",
            _ => "有线 USB 没有打开。无线 AirPlay 不受影响。",
        };
}
