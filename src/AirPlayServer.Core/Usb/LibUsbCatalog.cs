using System.Runtime.InteropServices;
using System.Text;

namespace AirPlayServer.Core.Usb;

/// <summary>
/// Userspace libusb-1.0 catalog. The shared library is loaded at runtime, the
/// same way FFmpeg is, so a Native AOT build does not link libusb. 64-bit
/// Linux, macOS, and Windows are the layouts this walker understands.
/// </summary>
internal static class LibUsbCatalog
{
    public const int AppleVendorId = 0x05AC;

    public static bool TryCreate(IAirPlayLog log, out IUsbDeviceCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(log);
        catalog = null;
        if (nint.Size != 8)
        {
            log.Log(AirPlayLogLevel.Warning, "有线镜像需要 64 位 libusb。这个进程不是 64 位，USB 不启用。 Wired mirroring needs 64-bit libusb. This process is not 64-bit, so USB stays off.");
            return false;
        }

        if (!LibUsbNative.TryLoad(out var api, out var library) || api is null)
        {
            log.Log(AirPlayLogLevel.Warning, UsbDriverHint.LibraryMissing());
            log.Log(AirPlayLogLevel.Warning, UsbDriverHint.Readiness(UsbDriverProbe.Capture(libraryLoaded: false, libraryPath: null)));
            return false;
        }

        catalog = new Catalog(api, log);
        log.Log(AirPlayLogLevel.Information, "已加载 " + library + "，用来接收有线画面。 Loaded " + library + " for wired screen receive.");
        var readiness = UsbDriverProbe.Capture(libraryLoaded: true, library);
        log.Log(
            UsbDriverHint.NeedsAttention(readiness) ? AirPlayLogLevel.Warning : AirPlayLogLevel.Information,
            UsbDriverHint.Readiness(readiness));
        return true;
    }

    private sealed class Catalog : IUsbDeviceCatalog
    {
        private readonly LibUsbNative _api;
        private readonly IAirPlayLog _log;
        private readonly object _logGate = new();
        private readonly Dictionary<string, long> _loggedAt = new(StringComparer.Ordinal);

        public Catalog(LibUsbNative api, IAirPlayLog log)
        {
            _api = api;
            _log = log;
        }

        public IReadOnlyList<UsbPhone> List()
        {
            var found = new List<UsbPhone>();
            if (!_api.TryOpenContext(out var ctx))
            {
                LogLimited(AirPlayLogLevel.Warning, "context", UsbMirrorCandidate.ContextFailed());
                return found;
            }

            try
            {
                var devices = _api.DeviceList(ctx);
                var appleSeen = 0;
                var mtpOnly = new Dictionary<string, UsbMirrorCandidate.Scan>(StringComparer.OrdinalIgnoreCase);
                var vendorSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    for (var i = 0; i < devices.Length; i++)
                    {
                        if (!TryDescribe(devices[i], ref appleSeen, out var phone, out var scan, out var mtpCandidate))
                        {
                            continue;
                        }

                        AddDistinct(found, phone);
                        if (!mtpCandidate)
                        {
                            mtpOnly.Remove(phone.Id);
                            vendorSeen.Add(phone.Id);
                            continue;
                        }

                        if (!vendorSeen.Contains(phone.Id))
                        {
                            mtpOnly[phone.Id] = scan;
                        }
                    }
                }
                finally
                {
                    for (var i = 0; i < devices.Length; i++)
                    {
                        _api.Unref(devices[i]);
                    }
                }

                foreach (var phone in found)
                {
                    if (!mtpOnly.TryGetValue(phone.Id, out var scan))
                    {
                        continue;
                    }

                    LogLimited(AirPlayLogLevel.Information, "mtp:" + phone.Id, UsbMirrorCandidate.MtpEnable(scan, phone.Id));
                }

                if (found.Count == 0)
                {
                    LogLimited(
                        AirPlayLogLevel.Warning,
                        "empty:" + devices.Length + ":" + appleSeen,
                        UsbMirrorCandidate.EmptyList(devices.Length, appleSeen));
                }
            }
            finally
            {
                _api.Exit(ctx);
            }

            return found;
        }

        public IUsbBulkChannel? TryOpen(UsbPhone phone, CancellationToken cancellationToken)
        {
            if (!TryActivate(phone, cancellationToken, out var opened))
            {
                return null;
            }

            return opened;
        }

        private bool TryDescribe(nint device, ref int appleSeen, out UsbPhone phone, out UsbMirrorCandidate.Scan scan, out bool mtpCandidate)
        {
            phone = null!;
            scan = default;
            mtpCandidate = false;
            if (!_api.TryDeviceDescriptor(device, out var descriptor))
            {
                return false;
            }

            if (descriptor.VendorId != AppleVendorId)
            {
                return false;
            }

            appleSeen++;
            var inspection = _api.Inspect(device, descriptor.Configurations);
            scan = new UsbMirrorCandidate.Scan(
                descriptor.ProductId,
                _api.BusNumber(device),
                _api.DeviceAddress(device),
                descriptor.Configurations,
                inspection.ConfigsRead,
                inspection.Interfaces,
                inspection.HasMuxOrQuickTime);
            if (!inspection.HasMuxOrQuickTime && !UsbMirrorCandidate.ShouldAttemptQuickTimeEnable(scan))
            {
                LogLimited(
                    AirPlayLogLevel.Warning,
                    "classify:" + scan.ProductId.ToString("x4") + ":" + scan.Bus + ":" + scan.Address + ":" + scan.ConfigsRead + ":" + inspection.Interfaces.Count,
                    UsbMirrorCandidate.ClassifySkipped(scan));
                return false;
            }

            if (!_api.TryOpen(device, out var handle, out var opened))
            {
                LogLimited(
                    AirPlayLogLevel.Warning,
                    "open:" + scan.ProductId.ToString("x4") + ":" + scan.Bus + ":" + scan.Address + ":" + opened,
                    UsbMirrorCandidate.OpenFailed(scan, opened));
                return false;
            }

            try
            {
                var serial = UsbSerial.Correct(_api.ReadString(handle, descriptor.SerialIndex));
                if (serial.Length == 0)
                {
                    serial = "usb-" + descriptor.VendorId.ToString("x4") + "-" + descriptor.ProductId.ToString("x4") + "-" + scan.Bus + "-" + scan.Address;
                }

                mtpCandidate = !inspection.HasMuxOrQuickTime;
                var product = _api.ReadString(handle, descriptor.ProductIndex);
                phone = new UsbPhone
                {
                    Id = serial,
                    Product = string.IsNullOrWhiteSpace(product) ? null : product.Trim(),
                    QuickTimeActive = inspection.QuickTimeValue >= 0,
                };
                return true;
            }
            finally
            {
                _api.Close(handle);
            }
        }

        private bool TryActivate(UsbPhone phone, CancellationToken cancellationToken, out Channel? channel)
        {
            channel = null;
            for (var attempt = 0; attempt < 12 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                if (!_api.TryOpenContext(out var ctx))
                {
                    return false;
                }

                if (!_api.TryOpenSerial(ctx, phone.Id, out var device, out var handle, out var descriptor, out var openError))
                {
                    _api.Exit(ctx);
                    if (attempt == 0)
                    {
                        LogLimited(
                            AirPlayLogLevel.Warning,
                            "reopen:" + phone.Id + ":" + openError,
                            UsbMirrorCandidate.ReopenFailed(phone.Id, openError));
                        return false;
                    }

                    if (!Sleep(cancellationToken))
                    {
                        return false;
                    }

                    continue;
                }

                _api.TryClassify(device, descriptor.Configurations, out var mux, out var quickTime);
                if (quickTime < 0)
                {
                    var rc = _api.Control(handle, 0x40, 0x52, 0, 2, 1000);
                    var level = rc < 0 ? AirPlayLogLevel.Warning : AirPlayLogLevel.Information;
                    var detail = rc < 0 ? " " + UsbDriverHint.ForLibusb(rc, "QuickTime configuration request") : "";
                    _log.Log(
                        level,
                        "已向 " + phone.Id + " 发送 QuickTime USB 配置请求（控制传输 0x40/0x52，返回 " + rc
                        + "）。手机可能会重新枚举。 Sent the QuickTime USB configuration request to " + phone.Id
                        + " (control transfer 0x40/0x52, result " + rc + "). The phone may re-enumerate."
                        + detail);
                    _api.Close(handle);
                    _api.Exit(ctx);
                    if (!Sleep(cancellationToken))
                    {
                        return false;
                    }

                    continue;
                }

                if (!_api.TryClaimQuickTime(handle, device, descriptor.Configurations, quickTime, out var iface, out var inbound, out var outbound, out var error))
                {
                    _log.Log(AirPlayLogLevel.Warning, error);
                    _api.Close(handle);
                    _api.Exit(ctx);
                    return null == channel && false;
                }

                channel = new Channel(_api, ctx, handle, phone, iface, inbound, outbound, mux);
                return true;
            }

            _log.Log(AirPlayLogLevel.Warning, "等待 " + phone.Id + " 出现 QuickTime USB 接口超时。 Timed out waiting for the QuickTime USB interface on " + phone.Id + ".");
            return false;
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

        private void LogLimited(AirPlayLogLevel level, string key, string message)
        {
            var now = Environment.TickCount64;
            lock (_logGate)
            {
                if (_loggedAt.TryGetValue(key, out var at) && now - at < UsbMirrorCandidate.ScanLogIntervalMs)
                {
                    return;
                }

                _loggedAt[key] = now;
                if (_loggedAt.Count > 48)
                {
                    var stale = new List<string>();
                    foreach (var pair in _loggedAt)
                    {
                        if (now - pair.Value >= UsbMirrorCandidate.ScanLogIntervalMs && !string.Equals(pair.Key, key, StringComparison.Ordinal))
                        {
                            stale.Add(pair.Key);
                        }
                    }

                    foreach (var item in stale)
                    {
                        _loggedAt.Remove(item);
                    }
                }
            }

            _log.Log(level, message);
        }

        private static void AddDistinct(List<UsbPhone> found, UsbPhone phone)
        {
            for (var i = 0; i < found.Count; i++)
            {
                if (!string.Equals(found[i].Id, phone.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (phone.QuickTimeActive && !found[i].QuickTimeActive)
                {
                    found[i] = phone;
                }

                return;
            }

            found.Add(phone);
        }
    }

    private sealed class Channel : IUsbBulkChannel
    {
        private readonly LibUsbNative _api;
        private readonly nint _context;
        private readonly nint _handle;
        private readonly int _interface;
        private readonly byte _inbound;
        private readonly byte _outbound;
        private readonly int _muxConfig;
        private int _disposed;

        public Channel(LibUsbNative api, nint context, nint handle, UsbPhone phone, int iface, byte inbound, byte outbound, int muxConfig)
        {
            _api = api;
            _context = context;
            _handle = handle;
            Id = phone.Id;
            Product = phone.Product;
            _interface = iface;
            _inbound = inbound;
            _outbound = outbound;
            _muxConfig = muxConfig;
        }

        public string Id { get; }

        public string? Product { get; }

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _api.Bulk(_handle, _inbound, buffer, timeoutMs);
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var offset = 0;
            while (offset < data.Length)
            {
                var wrote = _api.BulkWrite(_handle, _outbound, data[offset..], 1000);
                if (wrote <= 0)
                {
                    throw new IOException("USB bulk write failed (" + wrote + ").");
                }

                offset += wrote;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _api.ReleaseInterface(_handle, _interface);
            _api.Control(_handle, 0x40, 0x52, 0, 0, 500);
            if (_muxConfig > 0)
            {
                _api.SetConfiguration(_handle, _muxConfig);
            }

            _api.Close(_handle);
            _api.Exit(_context);
        }
    }
}

internal sealed class UsbInspection
{
    public UsbInspection(int muxValue, int quickTimeValue, int configsRead, IReadOnlyList<UsbMirrorCandidate.Alt> interfaces)
    {
        MuxValue = muxValue;
        QuickTimeValue = quickTimeValue;
        ConfigsRead = configsRead;
        Interfaces = interfaces;
    }

    public int MuxValue { get; }

    public int QuickTimeValue { get; }

    public int ConfigsRead { get; }

    public IReadOnlyList<UsbMirrorCandidate.Alt> Interfaces { get; }

    public bool HasMuxOrQuickTime => MuxValue >= 0 || QuickTimeValue >= 0;
}

internal sealed class LibUsbNative
{
    private readonly nint _init;
    private readonly nint _exit;
    private readonly nint _getDeviceList;
    private readonly nint _freeDeviceList;
    private readonly nint _getDeviceDescriptor;
    private readonly nint _getConfigDescriptor;
    private readonly nint _freeConfigDescriptor;
    private readonly nint _open;
    private readonly nint _close;
    private readonly nint _getString;
    private readonly nint _control;
    private readonly nint _setConfiguration;
    private readonly nint _claim;
    private readonly nint _release;
    private readonly nint _bulk;
    private readonly nint _clearHalt;
    private readonly nint _detach;
    private readonly nint _bus;
    private readonly nint _address;
    private readonly nint _unref;

    private LibUsbNative(
        nint init,
        nint exit,
        nint getDeviceList,
        nint freeDeviceList,
        nint getDeviceDescriptor,
        nint getConfigDescriptor,
        nint freeConfigDescriptor,
        nint open,
        nint close,
        nint getString,
        nint control,
        nint setConfiguration,
        nint claim,
        nint release,
        nint bulk,
        nint clearHalt,
        nint detach,
        nint bus,
        nint address,
        nint unref)
    {
        _init = init;
        _exit = exit;
        _getDeviceList = getDeviceList;
        _freeDeviceList = freeDeviceList;
        _getDeviceDescriptor = getDeviceDescriptor;
        _getConfigDescriptor = getConfigDescriptor;
        _freeConfigDescriptor = freeConfigDescriptor;
        _open = open;
        _close = close;
        _getString = getString;
        _control = control;
        _setConfiguration = setConfiguration;
        _claim = claim;
        _release = release;
        _bulk = bulk;
        _clearHalt = clearHalt;
        _detach = detach;
        _bus = bus;
        _address = address;
        _unref = unref;
    }

    public static bool TryLoad(out LibUsbNative? api, out string library)
    {
        api = null;
        library = "";
        foreach (var name in Candidates())
        {
            var local = Path.Combine(AppContext.BaseDirectory, name);
            if (!TryLoadHandle(File.Exists(local) ? local : name, out var handle))
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

    public bool TryOpenContext(out nint context)
    {
        unsafe
        {
            nint local = 0;
            var rc = ((delegate* unmanaged[Cdecl]<nint*, int>)_init)(&local);
            context = local;
            return rc == 0 && context != 0;
        }
    }

    public void Exit(nint context)
    {
        if (context == 0)
        {
            return;
        }

        unsafe
        {
            ((delegate* unmanaged[Cdecl]<nint, void>)_exit)(context);
        }
    }

    public nint[] DeviceList(nint context)
    {
        unsafe
        {
            nint list = 0;
            var count = ((delegate* unmanaged[Cdecl]<nint, nint*, nint>)_getDeviceList)(context, &list);
            if (count <= 0 || list == 0)
            {
                if (list != 0)
                {
                    ((delegate* unmanaged[Cdecl]<nint, int, void>)_freeDeviceList)(list, 1);
                }

                return [];
            }

            var devices = new nint[(int)count];
            for (var i = 0; i < devices.Length; i++)
            {
                devices[i] = Marshal.ReadIntPtr(list, i * nint.Size);
            }

            ((delegate* unmanaged[Cdecl]<nint, int, void>)_freeDeviceList)(list, 0);
            return devices;
        }
    }

    public void Unref(nint device)
    {
        if (device == 0)
        {
            return;
        }

        unsafe
        {
            ((delegate* unmanaged[Cdecl]<nint, void>)_unref)(device);
        }
    }

    public bool TryDeviceDescriptor(nint device, out DeviceIds descriptor)
    {
        Span<byte> raw = stackalloc byte[18];
        int rc;
        unsafe
        {
            fixed (byte* pointer = raw)
            {
                rc = ((delegate* unmanaged[Cdecl]<nint, byte*, int>)_getDeviceDescriptor)(device, pointer);
            }
        }

        descriptor = default;
        if (rc != 0)
        {
            return false;
        }

        descriptor = new DeviceIds(
            BinaryReadU16(raw, 8),
            BinaryReadU16(raw, 10),
            raw[15],
            raw[16],
            raw[17]);
        return true;
    }

    public bool TryClassify(nint device, int configurations, out int muxValue, out int quickTimeValue)
    {
        var inspection = Inspect(device, configurations);
        muxValue = inspection.MuxValue;
        quickTimeValue = inspection.QuickTimeValue;
        return inspection.HasMuxOrQuickTime;
    }

    public UsbInspection Inspect(nint device, int configurations)
    {
        var interfaces = new List<UsbMirrorCandidate.Alt>(4);
        var muxValue = -1;
        var quickTimeValue = -1;
        var configsRead = 0;
        for (var index = 0; index < configurations; index++)
        {
            if (!TryConfig(device, index, out var value, out var mux, out var quickTime, out _, out _, out _, interfaces))
            {
                continue;
            }

            configsRead++;
            if (mux)
            {
                muxValue = value;
            }

            if (quickTime)
            {
                quickTimeValue = value;
            }
        }

        return new UsbInspection(muxValue, quickTimeValue, configsRead, interfaces);
    }

    public bool TryOpen(nint device, out nint handle)
        => TryOpen(device, out handle, out _);

    public bool TryOpen(nint device, out nint handle, out int result)
    {
        unsafe
        {
            nint local = 0;
            result = ((delegate* unmanaged[Cdecl]<nint, nint*, int>)_open)(device, &local);
            handle = local;
            return result == 0 && handle != 0;
        }
    }

    public bool TryOpenSerial(nint context, string serial, out nint device, out nint handle, out DeviceIds descriptor, out int openError)
    {
        device = 0;
        handle = 0;
        descriptor = default;
        openError = 0;
        var list = DeviceList(context);
        nint bestDevice = 0;
        nint bestHandle = 0;
        var bestDescriptor = default(DeviceIds);
        var bestRank = -1;
        try
        {
            foreach (var candidate in list)
            {
                if (!TryDeviceDescriptor(candidate, out var described) || described.VendorId != LibUsbCatalog.AppleVendorId)
                {
                    continue;
                }

                if (!TryOpen(candidate, out var opened, out var rc))
                {
                    openError = rc;
                    continue;
                }

                try
                {
                    var id = UsbSerial.Correct(ReadString(opened, described.SerialIndex));
                    if (!string.Equals(id, serial, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var inspection = Inspect(candidate, described.Configurations);
                    var rank = UsbMirrorCandidate.NodeRank(inspection.QuickTimeValue >= 0, inspection.MuxValue >= 0);
                    if (rank <= bestRank)
                    {
                        continue;
                    }

                    if (bestHandle != 0)
                    {
                        Close(bestHandle);
                    }

                    bestDevice = candidate;
                    bestHandle = opened;
                    bestDescriptor = described;
                    bestRank = rank;
                    opened = 0;
                    if (rank == 2)
                    {
                        break;
                    }
                }
                finally
                {
                    if (opened != 0)
                    {
                        Close(opened);
                    }
                }
            }

            if (bestHandle == 0)
            {
                return false;
            }

            device = bestDevice;
            handle = bestHandle;
            descriptor = bestDescriptor;
            bestHandle = 0;
            return true;
        }
        finally
        {
            if (bestHandle != 0)
            {
                Close(bestHandle);
            }

            foreach (var candidate in list)
            {
                Unref(candidate);
            }
        }
    }

    public void Close(nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        unsafe
        {
            ((delegate* unmanaged[Cdecl]<nint, void>)_close)(handle);
        }
    }

    public string ReadString(nint handle, byte index)
    {
        if (index == 0)
        {
            return "";
        }

        var buffer = new byte[256];
        int rc;
        unsafe
        {
            fixed (byte* pointer = buffer)
            {
                rc = ((delegate* unmanaged[Cdecl]<nint, byte, byte*, int, int>)_getString)(handle, index, pointer, buffer.Length);
            }
        }

        if (rc <= 0)
        {
            return "";
        }

        return Encoding.ASCII.GetString(buffer, 0, Math.Min(rc, buffer.Length)).Trim('\0', ' ');
    }

    public int Control(nint handle, byte requestType, byte request, ushort value, ushort index, uint timeout)
    {
        unsafe
        {
            return ((delegate* unmanaged[Cdecl]<nint, byte, byte, ushort, ushort, nint, ushort, uint, int>)_control)(
                handle, requestType, request, value, index, 0, 0, timeout);
        }
    }

    public int BusNumber(nint device)
    {
        unsafe
        {
            return ((delegate* unmanaged[Cdecl]<nint, int>)_bus)(device);
        }
    }

    public int DeviceAddress(nint device)
    {
        unsafe
        {
            return ((delegate* unmanaged[Cdecl]<nint, int>)_address)(device);
        }
    }

    public bool TryClaimQuickTime(nint handle, nint device, int configurations, int quickTimeValue, out int iface, out byte inbound, out byte outbound, out string error)
    {
        iface = 0;
        inbound = 0;
        outbound = 0;
        error = "";
        if (_detach != 0)
        {
            unsafe
            {
                ((delegate* unmanaged[Cdecl]<nint, int, int>)_detach)(handle, 1);
            }
        }

        var configured = SetConfiguration(handle, quickTimeValue);
        if (configured != 0)
        {
            error = UsbDriverHint.ForLibusb(configured, "set QuickTime USB configuration " + quickTimeValue);
            return false;
        }

        for (var index = 0; index < configurations; index++)
        {
            if (!TryConfig(device, index, out var value, out _, out var quickTime, out iface, out inbound, out outbound, null) || !quickTime || value != quickTimeValue)
            {
                continue;
            }

            int rc;
            unsafe
            {
                rc = ((delegate* unmanaged[Cdecl]<nint, int, int>)_claim)(handle, iface);
            }

            if (rc != 0)
            {
                error = UsbDriverHint.ForLibusb(rc, "claim QuickTime USB interface " + iface);
                return false;
            }

            ClearHalt(handle, inbound);
            ClearHalt(handle, outbound);
            return inbound != 0 && outbound != 0;
        }

        error = "QuickTime USB 配置里没有找到批量端点。 The QuickTime USB configuration has no bulk endpoints.";
        return false;
    }

    public int SetConfiguration(nint handle, int configuration)
    {
        unsafe
        {
            return ((delegate* unmanaged[Cdecl]<nint, int, int>)_setConfiguration)(handle, configuration);
        }
    }

    public void ReleaseInterface(nint handle, int iface)
    {
        unsafe
        {
            ((delegate* unmanaged[Cdecl]<nint, int, int>)_release)(handle, iface);
        }
    }

    public int Bulk(nint handle, byte endpoint, Span<byte> buffer, int timeoutMs)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        int transferred;
        int rc;
        unsafe
        {
            fixed (byte* pointer = buffer)
            {
                var actual = 0;
                rc = ((delegate* unmanaged[Cdecl]<nint, byte, byte*, int, int*, uint, int>)_bulk)(
                    handle, endpoint, pointer, buffer.Length, &actual, (uint)timeoutMs);
                transferred = actual;
            }
        }

        if (rc == -7)
        {
            return 0;
        }

        if (rc < 0)
        {
            return rc;
        }

        return transferred;
    }

    public int BulkWrite(nint handle, byte endpoint, ReadOnlySpan<byte> data, int timeoutMs)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        var buffer = data.ToArray();
        int transferred;
        int rc;
        unsafe
        {
            fixed (byte* pointer = buffer)
            {
                var actual = 0;
                rc = ((delegate* unmanaged[Cdecl]<nint, byte, byte*, int, int*, uint, int>)_bulk)(
                    handle, endpoint, pointer, buffer.Length, &actual, (uint)timeoutMs);
                transferred = actual;
            }
        }

        return rc < 0 ? rc : transferred;
    }

    private void ClearHalt(nint handle, byte endpoint)
    {
        unsafe
        {
            ((delegate* unmanaged[Cdecl]<nint, byte, int>)_clearHalt)(handle, endpoint);
        }
    }

    private bool TryConfig(nint device, int index, out int value, out bool mux, out bool quickTime, out int iface, out byte inbound, out byte outbound, List<UsbMirrorCandidate.Alt>? interfaces)
    {
        value = 0;
        mux = false;
        quickTime = false;
        iface = 0;
        inbound = 0;
        outbound = 0;
        unsafe
        {
            nint config = 0;
            var rc = ((delegate* unmanaged[Cdecl]<nint, byte, nint*, int>)_getConfigDescriptor)(device, (byte)index, &config);
            if (rc != 0 || config == 0)
            {
                return false;
            }

            try
            {
                var interfaceCount = Marshal.ReadByte(config, 4);
                value = Marshal.ReadByte(config, 5);
                var table = Marshal.ReadIntPtr(config, 16);
                for (var i = 0; i < interfaceCount; i++)
                {
                    var entry = table + (i * 16);
                    var alt = Marshal.ReadIntPtr(entry, 0);
                    var altCount = Marshal.ReadInt32(entry, 8);
                    if (alt == 0 || altCount < 1)
                    {
                        continue;
                    }

                    var number = Marshal.ReadByte(alt, 2);
                    var endpointCount = Marshal.ReadByte(alt, 4);
                    var klass = Marshal.ReadByte(alt, 5);
                    var sub = Marshal.ReadByte(alt, 6);
                    var protocol = Marshal.ReadByte(alt, 7);
                    interfaces?.Add(new UsbMirrorCandidate.Alt(klass, sub, protocol));
                    if (klass != 0xFF)
                    {
                        continue;
                    }

                    if (sub == 0xFE)
                    {
                        mux = true;
                    }

                    if (sub != 0x2A)
                    {
                        continue;
                    }

                    quickTime = true;
                    iface = number;
                    var endpoints = Marshal.ReadIntPtr(alt, 16);
                    for (var endpointIndex = 0; endpointIndex < endpointCount; endpointIndex++)
                    {
                        var endpoint = endpoints + (endpointIndex * 32);
                        var address = Marshal.ReadByte(endpoint, 2);
                        var attributes = Marshal.ReadByte(endpoint, 3);
                        if ((attributes & 0x03) != 0x02)
                        {
                            continue;
                        }

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
            }
            finally
            {
                ((delegate* unmanaged[Cdecl]<nint, void>)_freeConfigDescriptor)(config);
            }
        }

        return true;
    }

    private static ushort BinaryReadU16(ReadOnlySpan<byte> data, int offset)
        => (ushort)(data[offset] | (data[offset + 1] << 8));

    private static bool TryBind(nint handle, out LibUsbNative? api)
    {
        api = null;
        if (!Export(handle, "libusb_init", out var init)
            || !Export(handle, "libusb_exit", out var exit)
            || !Export(handle, "libusb_get_device_list", out var list)
            || !Export(handle, "libusb_free_device_list", out var freeList)
            || !Export(handle, "libusb_get_device_descriptor", out var descriptor)
            || !Export(handle, "libusb_get_config_descriptor", out var config)
            || !Export(handle, "libusb_free_config_descriptor", out var freeConfig)
            || !Export(handle, "libusb_open", out var open)
            || !Export(handle, "libusb_close", out var close)
            || !Export(handle, "libusb_get_string_descriptor_ascii", out var text)
            || !Export(handle, "libusb_control_transfer", out var control)
            || !Export(handle, "libusb_set_configuration", out var setConfiguration)
            || !Export(handle, "libusb_claim_interface", out var claim)
            || !Export(handle, "libusb_release_interface", out var release)
            || !Export(handle, "libusb_bulk_transfer", out var bulk)
            || !Export(handle, "libusb_clear_halt", out var clearHalt)
            || !Export(handle, "libusb_get_bus_number", out var bus)
            || !Export(handle, "libusb_get_device_address", out var address)
            || !Export(handle, "libusb_unref_device", out var unref))
        {
            return false;
        }

        Export(handle, "libusb_set_auto_detach_kernel_driver", out var detach);
        api = new LibUsbNative(init, exit, list, freeList, descriptor, config, freeConfig, open, close, text, control, setConfiguration, claim, release, bulk, clearHalt, detach, bus, address, unref);
        return true;
    }

    private static bool Export(nint handle, string name, out nint address)
        => NativeLibrary.TryGetExport(handle, name, out address);

    private static bool TryLoadHandle(string name, out nint handle)
        => NativeLibrary.TryLoad(name, out handle);

    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "libusb-1.0.dll";
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "libusb-1.0.0.dylib";
            yield return "libusb-1.0.dylib";
            yield break;
        }

        yield return "libusb-1.0.so.0";
        yield return "libusb-1.0.so";
    }

    internal readonly struct DeviceIds
    {
        public DeviceIds(ushort vendorId, ushort productId, byte productIndex, byte serialIndex, byte configurations)
        {
            VendorId = vendorId;
            ProductId = productId;
            ProductIndex = productIndex;
            SerialIndex = serialIndex;
            Configurations = configurations;
        }

        public ushort VendorId { get; }

        public ushort ProductId { get; }

        public byte ProductIndex { get; }

        public byte SerialIndex { get; }

        public byte Configurations { get; }
    }
}
