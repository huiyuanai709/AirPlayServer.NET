using System.Runtime.InteropServices;
using System.Text;

namespace AirPlayServer.Core.Usb;

/// <summary>
/// Windows catalog for the libusb-win32 filter device. libusb-1.0's WinUSB
/// backend returns <c>LIBUSB_ERROR_IO</c> (-1) or <c>LIBUSB_ERROR_NOT_FOUND</c>
/// (-5) on an Apple <c>usbccgp</c> parent that has a libusb0 UpperFilter, so
/// the QuickTime control transfer never runs. <c>libusb0.dll</c> opens
/// <c>\\.\libusb0-####--0x05ac-0xpppp</c> the same way a native <c>usb_open</c>
/// does. Linux and macOS stay on libusb-1.0. The DLL is loaded at runtime and
/// is not linked into the Native AOT image.
/// </summary>
internal static class LibUsb0Catalog
{
    public static bool TryCreate(IAirPlayLog log, out IUsbDeviceCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(log);
        catalog = null;
        if (!LibUsb0Native.TryLoad(out var api, out var library) || api is null)
        {
            log.Log(AirPlayLogLevel.Warning, UsbDriverHint.LibraryMissing());
            log.Log(AirPlayLogLevel.Warning, UsbDriverHint.Readiness(UsbDriverProbe.Capture(libraryLoaded: false, libraryPath: null)));
            return false;
        }

        catalog = new Catalog(api, log);
        log.Log(
            AirPlayLogLevel.Information,
            "已加载 " + library + "。Windows 通过 libusb0 过滤设备 \\\\.\\libusb0-… 接收有线画面，不用 libusb-1.0 的 WinUSB 打开 Apple 父设备。 Loaded "
            + library + ". Windows opens the libusb0 filter device \\\\.\\libusb0-… for wired screen receive, not the Apple parent through libusb-1.0 WinUSB.");
        var readiness = UsbDriverProbe.Capture(libraryLoaded: true, library);
        log.Log(
            UsbDriverHint.NeedsAttention(readiness) ? AirPlayLogLevel.Warning : AirPlayLogLevel.Information,
            UsbDriverHint.Readiness(readiness));
        return true;
    }

    private sealed class Catalog : IUsbDeviceCatalog
    {
        private readonly LibUsb0Native _api;
        private readonly IAirPlayLog _log;
        private readonly UsbScanLog _scan = new();
        private readonly Dictionary<string, UsbPhone> _openPhones = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _openFiles = new(StringComparer.OrdinalIgnoreCase);

        public Catalog(LibUsb0Native api, IAirPlayLog log)
        {
            _api = api;
            _log = log;
        }

        public IReadOnlyList<UsbPhone> List()
        {
            lock (_api.Gate)
            {
                if (!RefreshIfIdle("usb_find_devices"))
                {
                    return [];
                }

                var found = new List<UsbPhone>();
                var apple = 0;
                foreach (var device in _api.Snapshot())
                {
                    var filename = LibUsb0Native.ReadFilename(device);
                    if (!LibUsb0Layout.TryParseFilterName(filename, out var vendor, out _) || vendor != LibUsbCatalog.AppleVendorId)
                    {
                        continue;
                    }

                    apple++;
                    if (_openFiles.Contains(filename))
                    {
                        if (_openPhones.TryGetValue(filename, out var held))
                        {
                            found.Add(held);
                        }

                        continue;
                    }

                    if (!TryDescribe(device, filename, out var phone))
                    {
                        continue;
                    }

                    found.Add(phone);
                }

                if (apple == 0)
                {
                    _scan.Repeat(_log, AirPlayLogLevel.Warning, "no-apple", UsbDriverHint.Libusb0NoAppleDevice());
                }

                return found;
            }
        }

        public IUsbBulkChannel? TryOpen(UsbPhone phone, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 12 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                string? failure;
                Channel? channel;
                var wait = false;
                lock (_api.Gate)
                {
                    if (!RefreshIfIdle("usb_find_devices"))
                    {
                        return null;
                    }

                    if (!TryGrab(phone.Id, out var grabbed, out failure))
                    {
                        channel = null;
                    }
                    else if (grabbed.QuickTime < 0)
                    {
                        var rc = _api.Control(grabbed.Handle, 0x40, 0x52, 0, 2, 1000);
                        var level = rc < 0 ? AirPlayLogLevel.Warning : AirPlayLogLevel.Information;
                        var detail = rc < 0 ? " " + UsbDriverHint.ForLibusb0(rc, "QuickTime configuration request") : "";
                        _log.Log(
                            level,
                            "已向 " + phone.Id + " 发送 QuickTime USB 配置请求（libusb0 控制传输 0x40/0x52，返回 " + rc
                            + "）。手机可能会重新枚举。 Sent the QuickTime USB configuration request to " + phone.Id
                            + " (libusb0 control transfer 0x40/0x52, result " + rc + "). The phone may re-enumerate."
                            + detail);
                        _api.Close(grabbed.Handle);
                        wait = true;
                        channel = null;
                    }
                    else if (!_api.TryClaimQuickTime(grabbed.Handle, grabbed.QuickTime, grabbed.Interface, grabbed.Inbound, grabbed.Outbound, out var error))
                    {
                        _log.Log(AirPlayLogLevel.Warning, error);
                        _api.Close(grabbed.Handle);
                        return null;
                    }
                    else
                    {
                        _openFiles.Add(grabbed.Filename);
                        _openPhones[grabbed.Filename] = phone;
                        channel = new Channel(_api, grabbed, () => Release(grabbed.Filename));
                    }
                }

                if (channel is not null)
                {
                    return channel;
                }

                if (!wait)
                {
                    _scan.Repeat(
                        _log,
                        AirPlayLogLevel.Warning,
                        "reopen:" + phone.Id + ":" + failure,
                        failure ?? UsbDriverHint.DeviceNotListed(phone.Id));
                    if (attempt == 0)
                    {
                        return null;
                    }
                }

                if (!Sleep(cancellationToken))
                {
                    return null;
                }
            }

            _log.Log(AirPlayLogLevel.Warning, "等待 " + phone.Id + " 出现 QuickTime USB 接口超时。 Timed out waiting for the QuickTime USB interface on " + phone.Id + ".");
            return null;
        }

        private bool RefreshIfIdle(string action)
        {
            // usb_find_devices CreateFile's every \\.\libusb0-N with share mode 0.
            // Doing that while a channel holds the same path makes libusb0 drop the
            // open device. Skip the rescan until the last stream closes.
            if (_openFiles.Count > 0)
            {
                return true;
            }

            var rc = _api.Refresh();
            if (rc >= 0)
            {
                return true;
            }

            _scan.Repeat(_log, AirPlayLogLevel.Warning, "refresh:" + rc, UsbDriverHint.ForLibusb0(rc, action));
            return false;
        }

        private bool TryDescribe(nint device, string filename, out UsbPhone phone)
        {
            phone = null!;
            if (!_api.TryOpen(device, out var handle, out var error))
            {
                _scan.Repeat(_log, AirPlayLogLevel.Warning, "open:" + filename, UsbDriverHint.Libusb0OpenFailed(filename, error));
                return false;
            }

            try
            {
                if (!TryInspect(handle, filename, out var described))
                {
                    return false;
                }

                phone = described.Phone;
                _scan.Once(
                    _log,
                    AirPlayLogLevel.Information,
                    "seen:" + phone.Id,
                    "已用 libusb0 打开 " + filename + "（" + (phone.Product ?? "未命名") + "，" + phone.Id
                    + "）。 Opened libusb0 filter device " + filename + " (" + (phone.Product ?? "unnamed") + ", " + phone.Id
                    + "). QuickTime configuration " + (described.QuickTime >= 0 ? described.QuickTime.ToString() : "not visible yet") + ".");
                return true;
            }
            finally
            {
                _api.Close(handle);
            }
        }

        private bool TryGrab(string serial, out Grabbed grabbed, out string? failure)
        {
            grabbed = default;
            failure = null;
            Grabbed? best = null;
            var sawApple = false;
            foreach (var device in _api.Snapshot())
            {
                var filename = LibUsb0Native.ReadFilename(device);
                if (!LibUsb0Layout.TryParseFilterName(filename, out var vendor, out _) || vendor != LibUsbCatalog.AppleVendorId)
                {
                    continue;
                }

                sawApple = true;
                if (_openFiles.Contains(filename))
                {
                    continue;
                }

                if (!_api.TryOpen(device, out var handle, out var error))
                {
                    failure = UsbDriverHint.Libusb0OpenFailed(filename, error);
                    _scan.Repeat(_log, AirPlayLogLevel.Warning, "open:" + filename, failure);
                    continue;
                }

                if (!TryInspect(handle, filename, out var described) || !UsbSerial.Matches(described.Phone.Id, serial))
                {
                    _api.Close(handle);
                    continue;
                }

                var candidate = new Grabbed(handle, filename, described.Phone, described.Mux, described.QuickTime, described.Interface, described.Inbound, described.Outbound);
                if (best is null || (candidate.QuickTime >= 0 && best.Value.QuickTime < 0))
                {
                    if (best is not null)
                    {
                        _api.Close(best.Value.Handle);
                    }

                    best = candidate;
                    if (candidate.QuickTime >= 0)
                    {
                        break;
                    }
                }
                else
                {
                    _api.Close(handle);
                }
            }

            if (best is null)
            {
                failure ??= sawApple
                    ? UsbDriverHint.DeviceNotListed(serial)
                    : UsbDriverHint.Libusb0NoAppleDevice();
                return false;
            }

            grabbed = best.Value;
            failure = null;
            return true;
        }

        private bool TryInspect(nint handle, string filename, out Inspected described)
        {
            described = default;
            var deviceDescriptor = new byte[18];
            var read = _api.GetDescriptor(handle, 0x01, 0, deviceDescriptor);
            if (read < 18)
            {
                _scan.Repeat(
                    _log,
                    AirPlayLogLevel.Warning,
                    "desc:" + filename + ":" + read,
                    UsbDriverHint.ForLibusb0(read, "GET_DESCRIPTOR device on " + filename));
                return false;
            }

            var vendor = (ushort)(deviceDescriptor[8] | (deviceDescriptor[9] << 8));
            var productId = (ushort)(deviceDescriptor[10] | (deviceDescriptor[11] << 8));
            if (vendor != LibUsbCatalog.AppleVendorId)
            {
                return false;
            }

            var serial = UsbSerial.Correct(_api.ReadString(handle, deviceDescriptor[16]));
            if (serial.Length == 0)
            {
                serial = "usb-" + vendor.ToString("x4") + "-" + productId.ToString("x4") + "-" + filename;
            }

            var product = _api.ReadString(handle, deviceDescriptor[15]);
            var configs = new List<byte[]>();
            var count = Math.Min(deviceDescriptor[17], (byte)8);
            for (byte index = 0; index < count; index++)
            {
                if (_api.TryReadConfig(handle, index, out var blob, out var rc))
                {
                    configs.Add(blob);
                    continue;
                }

                _scan.Repeat(
                    _log,
                    AirPlayLogLevel.Warning,
                    "cfg:" + filename + ":" + index + ":" + rc,
                    UsbDriverHint.ForLibusb0(rc, "GET_DESCRIPTOR configuration " + index + " on " + filename));
            }

            UsbDescriptorMap.Classify(configs, out var mux, out var quickTime, out var iface, out var inbound, out var outbound);
            described = new Inspected(
                new UsbPhone
                {
                    Id = serial,
                    Product = string.IsNullOrWhiteSpace(product) ? null : product.Trim(),
                    QuickTimeActive = quickTime >= 0,
                },
                mux,
                quickTime,
                iface,
                inbound,
                outbound);
            return true;
        }

        private void Release(string filename)
        {
            lock (_api.Gate)
            {
                _openFiles.Remove(filename);
                _openPhones.Remove(filename);
            }
        }

        private static bool Sleep(CancellationToken cancellationToken)
        {
            try
            {
                Task.Delay(500, cancellationToken).GetAwaiter().GetResult();
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private readonly struct Inspected
    {
        public Inspected(UsbPhone phone, int mux, int quickTime, int iface, byte inbound, byte outbound)
        {
            Phone = phone;
            Mux = mux;
            QuickTime = quickTime;
            Interface = iface;
            Inbound = inbound;
            Outbound = outbound;
        }

        public UsbPhone Phone { get; }

        public int Mux { get; }

        public int QuickTime { get; }

        public int Interface { get; }

        public byte Inbound { get; }

        public byte Outbound { get; }
    }

    private readonly struct Grabbed
    {
        public Grabbed(nint handle, string filename, UsbPhone phone, int mux, int quickTime, int iface, byte inbound, byte outbound)
        {
            Handle = handle;
            Filename = filename;
            Phone = phone;
            Mux = mux;
            QuickTime = quickTime;
            Interface = iface;
            Inbound = inbound;
            Outbound = outbound;
        }

        public nint Handle { get; }

        public string Filename { get; }

        public UsbPhone Phone { get; }

        public int Mux { get; }

        public int QuickTime { get; }

        public int Interface { get; }

        public byte Inbound { get; }

        public byte Outbound { get; }
    }

    private sealed class Channel : IUsbBulkChannel
    {
        private readonly LibUsb0Native _api;
        private readonly nint _handle;
        private readonly int _interface;
        private readonly byte _inbound;
        private readonly byte _outbound;
        private readonly int _muxConfig;
        private readonly Action _released;
        private int _disposed;

        public Channel(LibUsb0Native api, Grabbed grabbed, Action released)
        {
            _api = api;
            _handle = grabbed.Handle;
            Id = grabbed.Phone.Id;
            Product = grabbed.Phone.Product;
            _interface = grabbed.Interface;
            _inbound = grabbed.Inbound;
            _outbound = grabbed.Outbound;
            _muxConfig = grabbed.Mux;
            _released = released;
        }

        public string Id { get; }

        public string? Product { get; }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            lock (_api.Gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return _api.BulkRead(_handle, _inbound, buffer, timeoutMs);
            }
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            lock (_api.Gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var offset = 0;
                while (offset < data.Length)
                {
                    var wrote = _api.BulkWrite(_handle, _outbound, data[offset..], 1000);
                    if (wrote <= 0)
                    {
                        throw new IOException("USB bulk write failed (libusb0 " + wrote + ").");
                    }

                    offset += wrote;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (_api.Gate)
            {
                _api.ReleaseInterface(_handle, _interface);
                _api.Control(_handle, 0x40, 0x52, 0, 0, 500);
                if (_muxConfig > 0)
                {
                    _api.SetConfiguration(_handle, _muxConfig);
                }

                _api.Close(_handle);
            }

            _released();
        }
    }
}

internal sealed class LibUsb0Native
{
    private readonly object _gate = new();
    private readonly nint _init;
    private readonly nint _findBusses;
    private readonly nint _findDevices;
    private readonly nint _getBusses;
    private readonly nint _open;
    private readonly nint _close;
    private readonly nint _strerror;
    private readonly nint _string;
    private readonly nint _descriptor;
    private readonly nint _control;
    private readonly nint _setConfiguration;
    private readonly nint _claim;
    private readonly nint _release;
    private readonly nint _clearHalt;
    private readonly nint _bulkRead;
    private readonly nint _bulkWrite;
    private int _ready;

    private LibUsb0Native(
        nint init,
        nint findBusses,
        nint findDevices,
        nint getBusses,
        nint open,
        nint close,
        nint strerror,
        nint text,
        nint descriptor,
        nint control,
        nint setConfiguration,
        nint claim,
        nint release,
        nint clearHalt,
        nint bulkRead,
        nint bulkWrite)
    {
        _init = init;
        _findBusses = findBusses;
        _findDevices = findDevices;
        _getBusses = getBusses;
        _open = open;
        _close = close;
        _strerror = strerror;
        _string = text;
        _descriptor = descriptor;
        _control = control;
        _setConfiguration = setConfiguration;
        _claim = claim;
        _release = release;
        _clearHalt = clearHalt;
        _bulkRead = bulkRead;
        _bulkWrite = bulkWrite;
    }

    public object Gate => _gate;

    public static bool TryLoad(out LibUsb0Native? api, out string library)
    {
        api = null;
        library = "";
        foreach (var name in new[] { "libusb0.dll" })
        {
            var local = Path.Combine(AppContext.BaseDirectory, name);
            if (!NativeLibrary.TryLoad(File.Exists(local) ? local : name, out var handle))
            {
                continue;
            }

            if (!TryBind(handle, out api))
            {
                NativeLibrary.Free(handle);
                continue;
            }

            library = name;
            return true;
        }

        return false;
    }

    public int Refresh()
    {
        EnsureInit();
        int buses;
        int devices;
        unsafe
        {
            buses = ((delegate* unmanaged[Cdecl]<int>)_findBusses)();
            if (buses < 0)
            {
                return buses;
            }

            devices = ((delegate* unmanaged[Cdecl]<int>)_findDevices)();
        }

        return devices;
    }

    public List<nint> Snapshot()
    {
        nint bus;
        unsafe
        {
            bus = ((delegate* unmanaged[Cdecl]<nint>)_getBusses)();
        }

        var devices = new List<nint>();
        var buses = 0;
        while (bus != 0 && buses++ < 32)
        {
            var dev = Marshal.ReadIntPtr(bus, LibUsb0Layout.BusDevices);
            var count = 0;
            while (dev != 0 && count++ < 256)
            {
                devices.Add(dev);
                var next = Marshal.ReadIntPtr(dev, LibUsb0Layout.DeviceNext);
                if (next == dev)
                {
                    break;
                }

                dev = next;
            }

            var nextBus = Marshal.ReadIntPtr(bus, 0);
            if (nextBus == bus)
            {
                break;
            }

            bus = nextBus;
        }

        return devices;
    }

    public static string ReadFilename(nint device)
    {
        if (device == 0)
        {
            return "";
        }

        var buffer = new byte[LibUsb0Layout.FilenameBytes];
        Marshal.Copy(device + LibUsb0Layout.DeviceFilename, buffer, 0, buffer.Length);
        var length = 0;
        while (length < buffer.Length && buffer[length] != 0)
        {
            length++;
        }

        return Encoding.ASCII.GetString(buffer, 0, length);
    }

    public bool TryOpen(nint device, out nint handle, out string error)
    {
        unsafe
        {
            handle = ((delegate* unmanaged[Cdecl]<nint, nint>)_open)(device);
        }

        if (handle != 0)
        {
            error = "";
            return true;
        }

        error = LastError();
        return false;
    }

    public void Close(nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        unsafe
        {
            ((delegate* unmanaged[Cdecl]<nint, int>)_close)(handle);
        }
    }

    public string ReadString(nint handle, int index)
    {
        if (index <= 0)
        {
            return "";
        }

        var buffer = new byte[255];
        int rc;
        unsafe
        {
            fixed (byte* pointer = buffer)
            {
                rc = ((delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, int>)_string)(handle, index, pointer, (nuint)buffer.Length);
            }
        }

        if (rc <= 0)
        {
            return "";
        }

        return Encoding.ASCII.GetString(buffer, 0, Math.Min(rc, buffer.Length - 1)).Trim('\0', ' ');
    }

    public int GetDescriptor(nint handle, byte type, byte index, byte[] buffer)
    {
        unsafe
        {
            fixed (byte* pointer = buffer)
            {
                return ((delegate* unmanaged[Cdecl]<nint, byte, byte, byte*, int, int>)_descriptor)(handle, type, index, pointer, buffer.Length);
            }
        }
    }

    public bool TryReadConfig(nint handle, byte index, out byte[] blob, out int rc)
    {
        blob = [];
        var header = new byte[9];
        rc = GetDescriptor(handle, 0x02, index, header);
        if (rc < 9)
        {
            return false;
        }

        var total = header[2] | (header[3] << 8);
        if (total < 9 || total > 65535)
        {
            blob = [];
            rc = total;
            return false;
        }

        blob = new byte[total];
        rc = GetDescriptor(handle, 0x02, index, blob);
        if (rc < 9)
        {
            blob = [];
            return false;
        }

        if (rc < blob.Length)
        {
            blob = blob[..rc];
        }

        return true;
    }

    public int Control(nint handle, byte requestType, byte request, int value, int index, int timeout)
    {
        unsafe
        {
            var spare = stackalloc byte[1];
            return ((delegate* unmanaged[Cdecl]<nint, int, int, int, int, byte*, int, int, int>)_control)(
                handle, requestType, request, value, index, spare, 0, timeout);
        }
    }

    public int SetConfiguration(nint handle, int configuration)
    {
        unsafe
        {
            return ((delegate* unmanaged[Cdecl]<nint, int, int>)_setConfiguration)(handle, configuration);
        }
    }

    public bool TryClaimQuickTime(nint handle, int quickTime, int iface, byte inbound, byte outbound, out string error)
    {
        error = "";
        if (inbound == 0 || outbound == 0)
        {
            error = "QuickTime USB 配置里没有找到批量端点。 The QuickTime USB configuration has no bulk endpoints.";
            return false;
        }

        var configured = SetConfiguration(handle, quickTime);
        if (configured != 0)
        {
            error = UsbDriverHint.ForLibusb0(configured, "set QuickTime USB configuration " + quickTime);
            return false;
        }

        int claimed;
        unsafe
        {
            claimed = ((delegate* unmanaged[Cdecl]<nint, int, int>)_claim)(handle, iface);
        }

        if (claimed != 0)
        {
            error = UsbDriverHint.ForLibusb0(claimed, "claim QuickTime USB interface " + iface);
            return false;
        }

        ClearHalt(handle, inbound);
        ClearHalt(handle, outbound);
        return true;
    }

    public void ReleaseInterface(nint handle, int iface)
    {
        unsafe
        {
            ((delegate* unmanaged[Cdecl]<nint, int, int>)_release)(handle, iface);
        }
    }

    public int BulkRead(nint handle, byte endpoint, Span<byte> buffer, int timeoutMs)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        int rc;
        unsafe
        {
            fixed (byte* pointer = buffer)
            {
                rc = ((delegate* unmanaged[Cdecl]<nint, int, byte*, int, int, int>)_bulkRead)(handle, endpoint, pointer, buffer.Length, timeoutMs);
            }
        }

        if (rc == -116)
        {
            return 0;
        }

        return rc;
    }

    public int BulkWrite(nint handle, byte endpoint, ReadOnlySpan<byte> data, int timeoutMs)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        int rc;
        unsafe
        {
            fixed (byte* pointer = data)
            {
                rc = ((delegate* unmanaged[Cdecl]<nint, int, byte*, int, int, int>)_bulkWrite)(handle, endpoint, pointer, data.Length, timeoutMs);
            }
        }

        return rc;
    }

    private void ClearHalt(nint handle, byte endpoint)
    {
        unsafe
        {
            ((delegate* unmanaged[Cdecl]<nint, uint, int>)_clearHalt)(handle, endpoint);
        }
    }

    private void EnsureInit()
    {
        if (Interlocked.Exchange(ref _ready, 1) != 0)
        {
            return;
        }

        unsafe
        {
            ((delegate* unmanaged[Cdecl]<void>)_init)();
        }
    }

    private string LastError()
    {
        nint pointer;
        unsafe
        {
            pointer = ((delegate* unmanaged[Cdecl]<nint>)_strerror)();
        }

        if (pointer == 0)
        {
            return "";
        }

        return Marshal.PtrToStringAnsi(pointer) ?? "";
    }

    private static bool TryBind(nint handle, out LibUsb0Native? api)
    {
        api = null;
        if (!Export(handle, "usb_init", out var init)
            || !Export(handle, "usb_find_busses", out var findBusses)
            || !Export(handle, "usb_find_devices", out var findDevices)
            || !Export(handle, "usb_get_busses", out var getBusses)
            || !Export(handle, "usb_open", out var open)
            || !Export(handle, "usb_close", out var close)
            || !Export(handle, "usb_strerror", out var strerror)
            || !Export(handle, "usb_get_string_simple", out var text)
            || !Export(handle, "usb_get_descriptor", out var descriptor)
            || !Export(handle, "usb_control_msg", out var control)
            || !Export(handle, "usb_set_configuration", out var setConfiguration)
            || !Export(handle, "usb_claim_interface", out var claim)
            || !Export(handle, "usb_release_interface", out var release)
            || !Export(handle, "usb_clear_halt", out var clearHalt)
            || !Export(handle, "usb_bulk_read", out var bulkRead)
            || !Export(handle, "usb_bulk_write", out var bulkWrite))
        {
            return false;
        }

        api = new LibUsb0Native(init, findBusses, findDevices, getBusses, open, close, strerror, text, descriptor, control, setConfiguration, claim, release, clearHalt, bulkRead, bulkWrite);
        return true;
    }

    private static bool Export(nint handle, string name, out nint address)
        => NativeLibrary.TryGetExport(handle, name, out address);
}
