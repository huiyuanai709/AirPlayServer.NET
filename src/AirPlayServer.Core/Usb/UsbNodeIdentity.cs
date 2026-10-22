namespace AirPlayServer.Core.Usb;

/// <summary>
/// One libusb0 filter node (<c>\\.\libusb0-…</c>) is one phone. The serial
/// string is a label, not a second device. A failed string read keeps the id
/// already published for that node so an unnamed fallback cannot open the
/// same parent beside the serial session.
/// </summary>
internal sealed class UsbNodeIdentity
{
    private readonly Dictionary<string, string> _serialByNode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _idByNode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _enableAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _missingSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _locked = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLocked(string node)
        => _locked.Contains(node);

    public void Lock(string node)
    {
        if (!string.IsNullOrEmpty(node))
        {
            _locked.Add(node);
        }
    }

    public void Unlock(string node)
    {
        if (!string.IsNullOrEmpty(node))
        {
            _locked.Remove(node);
        }
    }

    public string IdFor(string node, string? serialRead)
    {
        var serial = UsbSerial.Correct(serialRead);
        if (serial.Length > 0)
        {
            _serialByNode[node] = serial;
        }
        else if (_serialByNode.TryGetValue(node, out var known))
        {
            serial = known;
        }

        if (_idByNode.TryGetValue(node, out var published))
        {
            return published;
        }

        var id = serial.Length > 0 ? serial : Fallback(node);
        _idByNode[node] = id;
        return id;
    }

    public static string Fallback(string node)
        => "usb-node:" + node;

    /// <summary>
    /// <c>0x40/0x52</c> re-enumerates the phone. Send it only when every
    /// configuration descriptor was read and none of them is QuickTime, and
    /// only once per connection. A short absence (the enable itself) does not
    /// arm another request. A node that stays gone is a replug and may enable once.
    /// </summary>
    public bool TryMarkEnable(string node, bool configsComplete, int quickTime, long now)
    {
        if (!UsbQtOpenPolicy.ShouldSendEnable(configsComplete, quickTime))
        {
            return false;
        }

        if (_enableAt.TryGetValue(node, out var at) && now - at < UsbQtOpenPolicy.EnableCooldownMs)
        {
            return false;
        }

        _enableAt[node] = now;
        return true;
    }

    public void NoteSweep(IEnumerable<string> present, long now)
    {
        var seen = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        var known = new List<string>(_idByNode.Keys);
        foreach (var node in known)
        {
            if (seen.Contains(node) || _locked.Contains(node))
            {
                _missingSince.Remove(node);
                continue;
            }

            if (!_missingSince.TryGetValue(node, out var since))
            {
                _missingSince[node] = now;
                continue;
            }

            if (now - since < UsbQtOpenPolicy.AbsentResetMs)
            {
                continue;
            }

            _missingSince.Remove(node);
            _idByNode.Remove(node);
            _enableAt.Remove(node);
            _serialByNode.Remove(node);
        }
    }
}

/// <summary>
/// Decisions and log lines for one QuickTime open. The catalog calls these so
/// a second identity cannot enable the same phone, and a black window can be
/// told apart from a phone that never opened.
/// </summary>
internal static class UsbQtOpenPolicy
{
    public const int EnableCooldownMs = 12_000;

    public const int AbsentResetMs = 3_000;

    public const int SilentWarnMs = 8_000;

    public static bool ShouldSendEnable(bool configsComplete, int quickTime)
        => configsComplete && quickTime < 0;

    public static bool ShouldWarnSilent(int videoFrames, long openedAt, long now)
        => videoFrames == 0 && now - openedAt >= SilentWarnMs;

    public static string Incomplete(string node)
        => "QuickTime USB descriptors were not fully readable on " + node
           + ". No 0x40/0x52 was sent, so the phone is not re-enumerated and is not asked to trust this computer again. "
           + "配置描述符没有读全，没有发送 0x40/0x52，不会因此要求重新信任。";

    public static string EnableSuppressed(string node)
        => "QuickTime USB enable was already sent to " + node
           + ". Waiting for the class 0xFF subclass 0x2A interface instead of sending 0x40/0x52 again. "
           + "已经发过 QuickTime 配置请求，等待接口出现，不再重复发送。";

    public static string AlreadyActive(int configuration, string node)
        => "QuickTime USB configuration " + configuration + " is already active on " + node
           + ". Leaving it there so the phone is not re-enumerated. "
           + "配置已经是 QuickTime，不再切换，避免手机重新枚举。";

    public static string ConfigStuck(int actual, int wanted, string node)
        => "libusb0 left " + node + " on configuration " + actual + " after SET_CONFIGURATION " + wanted
           + ". The bulk pipe was not claimed and the mirror window was not opened. No further 0x40/0x52 was sent. "
           + "切换配置没有生效，没有打开镜像窗口，也没有再发 0x40/0x52。";

    public static string Waiting(string id)
        => "USB mirror " + id + " is claimed and waiting for the phone's first QuickTime packet. The window stays black until a frame is decoded. "
           + "USB 已认领，正在等手机的第一包 QuickTime。解码出画面之前窗口是黑的。";

    public static string FirstRead(string id, int count, string hex)
        => "USB mirror " + id + " received " + count + " bytes (" + hex + "). "
           + "镜像已收到第一段数据。";

    public static string Silent(string id, int reads)
        => reads <= 0
            ? "USB mirror " + id + " is open but no QuickTime packets have arrived. The window stays black until H.264 is decoded. This is not a failed open. No 0x40/0x52 was sent while waiting. "
              + "镜像已经打开，但还没有 QuickTime 数据包。窗口是黑的是因为还没有解码出画面，不是没有打开。等待期间没有发送 0x40/0x52。"
            : "USB mirror " + id + " received " + reads + " USB reads but no H.264 frame has been decoded. The window stays black. This is not a failed open. "
              + "已经读到 USB 数据，但没有解码出 H.264。窗口是黑的。不是没有打开。";

    public static string ReadFailed(string id, int code, int reads)
        => "USB bulk read on " + id + " failed (" + code + ") after " + reads
           + " reads. The mirror is closing. This is a read error after a successful open, not a missing device. "
           + "USB 批量读取失败。镜像已经打开过，这是读取错误。";

    public static string HexPreview(ReadOnlySpan<byte> data)
    {
        var n = Math.Min(data.Length, 16);
        return n == 0 ? "" : Convert.ToHexString(data[..n]);
    }
}
