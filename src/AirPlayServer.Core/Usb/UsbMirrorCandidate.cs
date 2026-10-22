namespace AirPlayServer.Core.Usb;

/// <summary>
/// Decides which Apple devices can take the QuickTime enable path, and the
/// bilingual lines <see cref="LibUsbCatalog"/> writes when a scan finds nothing
/// to open. Listing stays quiet for non-Apple devices.
/// </summary>
internal static class UsbMirrorCandidate
{
    /// <summary>Same skip line at most this often while <c>--usb</c> is polling.</summary>
    public const int ScanLogIntervalMs = 30_000;

    /// <summary>
    /// Normal-mode iPhone, iPad, and iPod product ids. DFU is <c>0x1227</c> and
    /// recovery is <c>0x1280</c>–<c>0x1283</c>; those must not be sent the
    /// QuickTime mode switch.
    /// </summary>
    public const ushort NormalModeProductMin = 0x1290;

    public const ushort NormalModeProductMax = 0x12AF;

    internal readonly record struct Alt(byte Class, byte SubClass, byte Protocol);

    internal readonly record struct Scan(
        ushort ProductId,
        int Bus,
        int Address,
        int Configurations,
        int ConfigsRead,
        IReadOnlyList<Alt> Interfaces,
        bool SawMuxOrQuickTime);

    public static bool IsMux(byte klass, byte subClass)
        => klass == 0xFF && subClass == 0xFE;

    public static bool IsQuickTime(byte klass, byte subClass)
        => klass == 0xFF && subClass == 0x2A;

    public static bool IsPtp(byte klass, byte subClass)
        => klass == 0x06 && subClass == 0x01;

    public static bool IsNormalModeIos(ushort productId)
        => productId >= NormalModeProductMin && productId <= NormalModeProductMax;

    /// <summary>
    /// Prefer a libusb node that already exposes QuickTime, then mux, then any
    /// other match. Windows may list the PTP function and the composite parent
    /// as separate devices with the same serial.
    /// </summary>
    public static int NodeRank(bool quickTime, bool mux)
        => quickTime ? 2 : mux ? 1 : 0;

    /// <summary>
    /// True when the device does not already show mux or QuickTime, but it is
    /// still an iOS phone stuck on the PTP/MTP configuration (or another
    /// normal-mode configuration that has not grown the hidden interface).
    /// The existing activate path then sends control transfer <c>0x40/0x52</c>.
    /// A device that already has mux or QuickTime must stay on that path.
    /// </summary>
    public static bool ShouldAttemptQuickTimeEnable(Scan scan)
    {
        if (scan.SawMuxOrQuickTime || scan.Interfaces.Count == 0)
        {
            return false;
        }

        var ptp = false;
        foreach (var alt in scan.Interfaces)
        {
            if (IsMux(alt.Class, alt.SubClass) || IsQuickTime(alt.Class, alt.SubClass))
            {
                return false;
            }

            if (IsPtp(alt.Class, alt.SubClass))
            {
                ptp = true;
            }
        }

        return ptp || IsNormalModeIos(scan.ProductId);
    }

    public static string ClassifySkipped(Scan scan)
    {
        var place = Place(scan);
        var summary = InterfaceSummary(scan.Interfaces);
        return "Apple USB 设备 " + place + " 已看见，但没有 mux（类 0xFF 子类 0xFE）也没有 QuickTime（类 0xFF 子类 0x2A）。"
               + "配置 " + scan.ConfigsRead + "/" + scan.Configurations + "，接口 " + summary + "。这一台不进入有线镜像。"
               + " Apple USB device " + place + " was seen, but it has neither mux (class 0xFF subclass 0xFE) nor QuickTime (class 0xFF subclass 0x2A). "
               + "Configurations read " + scan.ConfigsRead + "/" + scan.Configurations + ", interfaces " + summary + ". It was not opened for wired mirror.";
    }

    public static string OpenFailed(Scan scan, int result)
    {
        var place = Place(scan);
        var summary = InterfaceSummary(scan.Interfaces);
        var kind = scan.SawMuxOrQuickTime
            ? "描述符里已经有 mux 或 QuickTime，但 libusb 打不开。"
            : "描述符里还没有 QuickTime 接口，libusb 也打不开，所以 0x40/0x52 没有发出。";
        var kindEnglish = scan.SawMuxOrQuickTime
            ? "Mux or QuickTime is in the descriptors, but libusb could not open the device."
            : "QuickTime is not in the descriptors, and libusb could not open the device, so 0x40/0x52 was not sent.";
        return "Apple USB 设备 " + place + " 打开失败，libusb " + result + "。接口 " + summary + "。" + kind
               + " Apple USB device " + place + " did not open, libusb " + result + ". Interfaces " + summary + ". " + kindEnglish + " "
               + UsbDriverHint.ForLibusb(result, "open Apple USB device " + place);
    }

    public static string MtpEnable(Scan scan, string serial)
    {
        var place = Place(scan);
        var summary = InterfaceSummary(scan.Interfaces);
        return "Apple USB 设备 " + place + "（" + serial + "）当前接口是 " + summary
               + "，配置 " + scan.ConfigsRead + "/" + scan.Configurations
               + "，没有 QuickTime（类 0xFF 子类 0x2A）。Windows 里显示为 MTP/存储并不等于已经在镜像。"
               + "将发送控制传输 0x40/0x52 尝试打开 QuickTime 配置。这不是控制中心的隔空播放。"
               + " Apple USB device " + place + " (" + serial + ") exposes interfaces " + summary
               + ", configurations " + scan.ConfigsRead + "/" + scan.Configurations
               + ", and has no QuickTime interface (class 0xFF subclass 0x2A). MTP or storage in Windows is not a mirror. "
               + "Control transfer 0x40/0x52 will be sent to enable the QuickTime configuration. This is not Control Center AirPlay.";
    }

    public static string EmptyList(int devices, int apple)
    {
        if (devices <= 0)
        {
            return "--usb 已开启，但 libusb 设备列表是空的（0 台）。手机在资源管理器里显示为存储设备，只说明 MTP 在工作，libusb 还没有看到可切换的 Apple 设备。"
                   + " Wired --usb is on, but the libusb device list is empty (0 devices). A phone shown as storage only means MTP is up. libusb does not yet see an Apple device it can switch.";
        }

        if (apple <= 0)
        {
            return "--usb 已开启，libusb 看到 " + devices + " 台设备，其中没有 Apple VID 0x05AC。资源管理器里的存储设备还不是 QuickTime 镜像。"
                   + " Wired --usb is on. libusb sees " + devices + " devices and none with Apple VID 0x05AC. A storage device in Explorer is not a QuickTime mirror.";
        }

        return "--usb 已开启，这一轮没有可镜像的手机（libusb 设备 " + devices + " 台，Apple VID 0x05AC " + apple + " 台）。上面的跳过原因说明了为什么没有发送 QuickTime 配置请求。"
               + " Wired --usb is on, and this scan found no mirrorable phone (libusb devices " + devices + ", Apple VID 0x05AC " + apple + "). The skip lines above are why the QuickTime configuration request was not sent.";
    }

    public static string ContextFailed()
        => "--usb 已开启，但 libusb 初始化失败，USB 设备列表是空的。 Wired --usb is on, but libusb_init failed, so the USB device list is empty.";

    public static string ReopenFailed(string serial, int openError)
    {
        var detail = openError < 0 ? " " + UsbDriverHint.ForLibusb(openError, "reopen " + serial) : "";
        return "没有再次打开 " + serial + "，QuickTime 配置请求没有发出。"
               + " Could not open " + serial + " again, so the QuickTime configuration request was not sent."
               + detail;
    }

    private static string Place(Scan scan)
        => "05ac:" + scan.ProductId.ToString("x4") + " bus " + scan.Bus + " address " + scan.Address;

    private static string InterfaceSummary(IReadOnlyList<Alt> interfaces)
    {
        if (interfaces.Count == 0)
        {
            return "none";
        }

        var parts = new string[interfaces.Count];
        for (var i = 0; i < interfaces.Count; i++)
        {
            var alt = interfaces[i];
            parts[i] = alt.Class.ToString("x2") + "/" + alt.SubClass.ToString("x2") + "/" + alt.Protocol.ToString("x2");
        }

        return string.Join(", ", parts);
    }
}
