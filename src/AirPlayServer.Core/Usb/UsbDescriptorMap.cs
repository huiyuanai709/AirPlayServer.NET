namespace AirPlayServer.Core.Usb;

/// <summary>
/// 64-bit pack(1) offsets from libusb-win32 1.4.0.2 <c>lusb0_usb.h</c>.
/// <c>usb_bus.devices</c> and <c>usb_device.filename</c> sit at these
/// offsets with or without the trailing pointer padding, because they follow
/// two pointers and a 512-byte char array. The host reads the filename and
/// then standard USB descriptors. It does not walk libusb0's config structs.
/// </summary>
internal static class LibUsb0Layout
{
    public const int BusDevices = 528;

    public const int DeviceNext = 0;

    public const int DeviceFilename = 16;

    public const int FilenameBytes = 512;

    public static bool TryParseFilterName(string? filename, out ushort vendorId, out ushort productId)
    {
        vendorId = 0;
        productId = 0;
        if (string.IsNullOrEmpty(filename))
        {
            return false;
        }

        var mark = filename.LastIndexOf("--0x", StringComparison.OrdinalIgnoreCase);
        if (mark < 0)
        {
            return false;
        }

        var rest = filename.AsSpan(mark + 4);
        var split = rest.IndexOf("-0x", StringComparison.OrdinalIgnoreCase);
        if (split != 4 || rest.Length != split + 3 + 4)
        {
            return false;
        }

        if (!ushort.TryParse(rest[..4], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out vendorId))
        {
            return false;
        }

        return ushort.TryParse(rest[(split + 3)..], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out productId);
    }
}

internal readonly struct UsbConfigView
{
    public UsbConfigView(int value, bool mux, bool quickTime, int iface, byte inbound, byte outbound)
    {
        Value = value;
        Mux = mux;
        QuickTime = quickTime;
        Interface = iface;
        Inbound = inbound;
        Outbound = outbound;
    }

    public int Value { get; }

    public bool Mux { get; }

    public bool QuickTime { get; }

    public int Interface { get; }

    public byte Inbound { get; }

    public byte Outbound { get; }
}

/// <summary>
/// Walks a raw USB configuration descriptor. QuickTime is class <c>0xFF</c>
/// subclass <c>0x2A</c>. Mux is class <c>0xFF</c> subclass <c>0xFE</c>.
/// The configuration passed to <c>SET_CONFIGURATION</c> is that descriptor's
/// <c>bConfigurationValue</c>. It is not a fixed number: on iPhone 15 Pro Max
/// (<c>05ac:12a8</c>) value 4 is interface <c>FF/FD/01</c>, and the screen is
/// value 5, interface 2, <c>FF/2A/FF</c>, bulk IN <c>0x86</c> OUT <c>0x05</c>.
/// </summary>
internal static class UsbDescriptorMap
{
    public static bool TryReadConfig(ReadOnlySpan<byte> data, out UsbConfigView view)
    {
        view = default;
        if (data.Length < 9 || data[0] < 9 || data[1] != 0x02)
        {
            return false;
        }

        var declared = data[2] | (data[3] << 8);
        var length = data.Length;
        if (declared >= 9 && declared < length)
        {
            length = declared;
        }

        var mux = false;
        var quickTime = false;
        var iface = 0;
        byte inbound = 0;
        byte outbound = 0;
        var capture = false;
        var offset = data[0];
        while (offset + 1 < length)
        {
            var len = data[offset];
            if (len < 2 || offset + len > length)
            {
                break;
            }

            var type = data[offset + 1];
            if (type == 0x04 && len >= 9)
            {
                var klass = data[offset + 5];
                var sub = data[offset + 6];
                if (klass == 0xFF && sub == 0xFE)
                {
                    mux = true;
                }

                capture = klass == 0xFF && sub == 0x2A;
                if (capture)
                {
                    quickTime = true;
                    iface = data[offset + 2];
                    inbound = 0;
                    outbound = 0;
                }
            }
            else if (type == 0x05 && len >= 7 && capture)
            {
                var address = data[offset + 2];
                var attributes = data[offset + 3];
                if ((attributes & 0x03) == 0x02)
                {
                    if ((address & 0x80) != 0)
                    {
                        if (inbound == 0)
                        {
                            inbound = address;
                        }
                    }
                    else if (outbound == 0)
                    {
                        outbound = address;
                    }
                }
            }

            offset += len;
        }

        view = new UsbConfigView(data[5], mux, quickTime, iface, inbound, outbound);
        return true;
    }

    public static void Classify(IReadOnlyList<byte[]> configs, out int mux, out int quickTime, out int iface, out byte inbound, out byte outbound)
    {
        mux = -1;
        quickTime = -1;
        iface = 0;
        inbound = 0;
        outbound = 0;
        if (configs is null)
        {
            return;
        }

        foreach (var config in configs)
        {
            if (config is null || !TryReadConfig(config, out var view))
            {
                continue;
            }

            if (view.Mux)
            {
                mux = view.Value;
            }

            if (!view.QuickTime)
            {
                continue;
            }

            quickTime = view.Value;
            iface = view.Interface;
            inbound = view.Inbound;
            outbound = view.Outbound;
        }
    }

    public static string Chosen(int configuration, int iface, byte inbound, byte outbound)
        => "QuickTime USB 接口在配置值 " + configuration + " 接口 " + iface
           + "（类 0xFF 子类 0x2A），批量 IN 0x" + inbound.ToString("x2") + " OUT 0x" + outbound.ToString("x2")
           + "。这个配置号来自含该接口的描述符，不是固定数字。 QuickTime USB interface is configuration value "
           + configuration + " interface " + iface + " (class 0xFF subclass 0x2A), bulk IN 0x"
           + inbound.ToString("x2") + " OUT 0x" + outbound.ToString("x2")
           + ". That value comes from the descriptor that contains this interface, not a fixed configuration number.";
}
