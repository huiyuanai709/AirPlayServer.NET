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
    private readonly HashSet<string> _pipeRebuild = new(StringComparer.OrdinalIgnoreCase);

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

    public long EnableAt(string node)
        => !string.IsNullOrEmpty(node) && _enableAt.TryGetValue(node, out var at) ? at : 0;

    /// <summary>
    /// <c>usb_set_configuration</c> ran on a handle that was opened on the previous
    /// configuration. The next open must claim a fresh pipe table.
    /// </summary>
    public void MarkPipeRebuild(string node)
    {
        if (!string.IsNullOrEmpty(node))
        {
            _pipeRebuild.Add(node);
        }
    }

    public bool TakePipeRebuild(string node)
        => !string.IsNullOrEmpty(node) && _pipeRebuild.Remove(node);

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
            _pipeRebuild.Remove(node);
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

    /// <summary>Empty bulk reads before the one fallback PING. The phone speaks first.</summary>
    public const int HandshakeQuietMs = 1_000;

    /// <summary>
    /// After <c>0x40/0x52</c>, wait this long for the phone to select the QuickTime
    /// configuration itself. iPhoneMirror uses the same five seconds.
    /// </summary>
    public const int PhoneConfigurationSettleMs = 5_000;

    public const int OpenAttempts = 30;

    public const byte HandshakeRequestType = 0x40;

    public const byte HandshakeRequest = 0x40;

    public const int HandshakeValue = 0x6400;

    public const int HandshakeIndex = 0x6400;

    /// <summary>
    /// libusb0 <c>EINVAL</c>. Claim returns this when the filter pipe table is still
    /// configuration 0, and also when the interface is not part of the active configuration.
    /// </summary>
    public const int Libusb0InvalidConfiguration = -22;

    /// <summary>libusb0 <c>EIO</c>. A timed-out bulk read is <c>-116</c>, not this.</summary>
    public const int Libusb0Eio = -5;

    public const int BulkIoRetries = 3;

    /// <summary>Returned when <c>libusb0.dll</c> has no <c>usb_set_altinterface</c> export. Not an errno.</summary>
    public const int MissingAltExport = -1000;

    public static bool ShouldSendEnable(bool configsComplete, int quickTime)
        => configsComplete && quickTime < 0;

    public static bool ShouldRetryBulkIo(int code, int attempt)
        => code == Libusb0Eio && attempt < BulkIoRetries;

    public static bool PingWriteOk(int wrote)
        => wrote == 16;

    public static bool ShouldKickHandshake(int payloadReads, bool alreadyKicked, long quietMs)
        => payloadReads == 0 && !alreadyKicked && quietMs >= HandshakeQuietMs;

    public static bool ShouldSendHandshakeControl(bool enabledThisSession)
        => enabledThisSession;

    /// <summary>The bus reports the QuickTime configuration value, not merely a descriptor that contains it.</summary>
    public static bool ConfigurationIsActive(int active, int quickTime)
        => quickTime > 0 && active == quickTime;

    /// <summary>
    /// A class 0xFF/0x2A descriptor can stay visible after iOS returns to the mux configuration.
    /// Claiming that interface while GET_CONFIGURATION disagrees is EINVAL, not an empty pipe table.
    /// </summary>
    public static bool ShouldSelectBeforeClaim(int active, int quickTime)
        => quickTime > 0 && !ConfigurationIsActive(active, quickTime);

    /// <summary>
    /// A QuickTime descriptor read while other configurations return EIO is the
    /// phone mid-reenumeration. Do not select a configuration or claim from it.
    /// </summary>
    public static bool ShouldIgnorePartialQuickTime(bool configsComplete, int quickTime)
        => !configsComplete && quickTime >= 0;

    /// <summary>
    /// The phone may still be switching into QuickTime after this session's enable.
    /// Forcing <c>usb_set_configuration</c> during that window reports configuration 5
    /// without starting the screen session.
    /// </summary>
    public static bool ShouldWaitForPhoneConfiguration(long enabledAt, long now, int active, int quickTime)
        => enabledAt > 0
           && now >= enabledAt
           && now - enabledAt < PhoneConfigurationSettleMs
           && ShouldSelectBeforeClaim(active, quickTime);

    /// <summary>
    /// libusb-win32 returns 0 from <c>usb_set_configuration</c> without a bus transfer when the
    /// configuration cached at <c>usb_open</c> already equals the request. The phone can still be elsewhere.
    /// </summary>
    public static bool SetConfigurationWasIgnored(int before, int requested, int setResult, int after)
        => setResult == 0 && before > 0 && before != requested && after == before;

    public static bool ClaimNeedsPipeTable(int claimResult)
        => claimResult == Libusb0InvalidConfiguration;

    /// <summary>
    /// One <c>usb_set_configuration</c> rebuilds the filter pipe table only after GET_CONFIGURATION
    /// already equals the QuickTime value. The same EINVAL while the phone is still on another configuration
    /// is not that case.
    /// </summary>
    public static bool ShouldInitializePipeTable(int active, int quickTime, int claimResult)
        => ConfigurationIsActive(active, quickTime) && ClaimNeedsPipeTable(claimResult);

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

    public static string ConfigurationObserved(int active, int quickTime, string node)
    {
        var action = ConfigurationIsActive(active, quickTime)
            ? "Active configuration matches the QuickTime descriptor, so the interface can be claimed. usb_set_configuration runs only if that claim reports an uninitialized pipe table. "
            : "The interface is not claimed while those values differ. usb_set_configuration(" + quickTime
              + ") runs first, and claim waits until GET_CONFIGURATION returns " + quickTime + ". ";
        return "libusb0 GET_CONFIGURATION on " + node + " returned " + active
               + " before claim. The QuickTime descriptor value is " + quickTime + ". "
               + action
               + "认领前的当前配置。活动配置还不是 QuickTime 配置时，先切换并确认，再认领接口。";
    }

    public static string SelectedBeforeClaim(int active, int quickTime, int code)
        => "usb_set_configuration(" + quickTime + ") returned " + code
           + " before claim because GET_CONFIGURATION was " + active
           + ". The QuickTime descriptor value is " + quickTime
           + ". The interface stays unclaimed until GET_CONFIGURATION returns " + quickTime + ". "
           + "活动配置还不是 QuickTime 配置，先切换，确认之后才认领接口。";

    public static string ConfigurationRead(int active, int quickTime, string node)
        => "libusb0 GET_CONFIGURATION on " + node + " returned " + active
           + " after usb_set_configuration(" + quickTime + "). "
           + "切换之后重新读取的当前配置。";

    public static string ConfigurationConfirmed(int active, int quickTime, string node)
        => "libusb0 GET_CONFIGURATION on " + node + " returned " + active
           + ", matching QuickTime configuration " + quickTime
           + ". Claiming the QuickTime interface now. The bulk IN read starts after claim and waits for the phone's PING. "
           + "当前配置已经是 QuickTime 配置，现在认领接口并开始读，等手机先发 PING。";

    public static string CacheIgnored(int active, int quickTime)
        => "usb_set_configuration(" + quickTime + ") returned 0 but GET_CONFIGURATION is still " + active
           + ". libusb0 skips the bus transfer when the configuration cached at open is already " + quickTime
           + ". Selecting live configuration " + active + " and then " + quickTime
           + " so the phone leaves configuration " + active + ". "
           + "过滤驱动缓存让切换没有发到总线上。先对齐当前配置，再选 QuickTime 配置。";

    public static string LiveConfiguration(int active, int code)
        => "usb_set_configuration(" + active + ") returned " + code
           + ". This aligns the libusb0 cache with the configuration the phone is using before the QuickTime configuration is selected. "
           + "先把过滤驱动缓存对齐到手机正在使用的配置。";

    public static string PartialQuickTime(string node, int quickTime)
        => "QuickTime configuration " + quickTime + " was visible on " + node
           + " while other configuration descriptors failed. Waiting for re-enumeration to finish. No usb_set_configuration and no interface claim. "
           + "描述符还没读全。即使看到 QuickTime 配置，也不切换、不认领。";

    public static string WaitingForPhoneConfiguration(int active, int quickTime, long elapsedMs)
        => "GET_CONFIGURATION is still " + active + ", " + elapsedMs
           + " ms after 0x40/0x52. Waiting for the phone to select QuickTime configuration " + quickTime
           + ". The interface is not claimed and usb_set_configuration is not called during this wait. "
           + "配置请求之后等待手机自己进入 QuickTime 配置。这段时间不切换、不认领。";

    public static string PipeRebuild(int quickTime, int active)
        => "GET_CONFIGURATION returned " + active + " after usb_set_configuration(" + quickTime
           + "). This handle was opened on the previous configuration, so it is closed without a claim. The next open rebuilds the libusb0 pipe table, claims the interface, selects alternate 0, and clears halt before the bulk IN read. "
           + "配置已经切换。这个句柄是在旧配置上打开的，先关掉。下次打开再建管道、认领接口、选择备用设置并清除 halt，然后才开始读。";

    public static string InterfaceStarted(int iface, int alternate, int code, byte inbound, byte outbound)
        => "QuickTime interface " + iface + " alternate " + alternate + " SET_INTERFACE returned " + code
           + ". Cleared halt on bulk IN 0x" + inbound.ToString("X2") + " and OUT 0x" + outbound.ToString("X2")
           + ". The bulk IN read starts now and waits for the phone's PING. No OUT packet is sent before that read. "
           + "已选择接口并清除 halt。开始读，等手机先发 PING。读之前不写 OUT。";

    public static string ClaimedWithoutSetConfiguration(int iface, int active, int quickTime)
        => "Claimed QuickTime interface " + iface + " without usb_set_configuration. Active configuration "
           + active + ", descriptor " + quickTime
           + ". The bulk IN read starts now and waits for the phone's PING. No 0x40/0x52 was sent. "
           + "已认领接口，没有调用 usb_set_configuration。开始读，等手机先发 PING。";

    public static string InitializedConfiguration(int quickTime, int code)
        => "usb_set_configuration(" + quickTime + ") returned " + code
           + " after claim failed with an uninitialized pipe table. GET_CONFIGURATION was already " + quickTime + ". "
           + "活动配置已经是 QuickTime 配置。认领失败后才调用 usb_set_configuration 建立管道。";

    public static string AltSelected(int iface, int alternate, int code)
        => code == MissingAltExport
            ? "libusb0.dll has no usb_set_altinterface export, so interface " + iface
              + " alternate " + alternate + " was not selected. "
              + "没有 usb_set_altinterface，备用设置没有选上。"
            : "QuickTime USB interface " + iface + " alternate setting " + alternate + " returned " + code + ". "
              + "已选择备用设置。";

    public static string AltUnchanged(int iface)
        => "QuickTime interface " + iface + " alternate setting is 0. libusb0 was not asked to SELECT_INTERFACE. The bulk read starts on the claimed setting. "
           + "备用设置已是 0，没有调用 usb_set_altinterface，直接开始读。";

    public static string HandshakeKick(int code)
        => "QuickTime handshake control 0x40/0x40 wValue 0x6400 wIndex 0x6400 returned " + code
           + " after the first empty bulk IN. This starts the endpoint on a configuration this session just enabled. It is not 0x40/0x52. "
           + "第一次空读之后发送握手控制传输。这不是 0x40/0x52。";

    public static string HostPing(byte endpoint, int result)
        => "Sent one QuickTime PING on OUT 0x" + endpoint.ToString("X2")
           + " after an empty bulk IN (libusb0 write result " + result
           + "). The phone speaks first; this is the one-second fallback, not a PING sent before the read. This is not 0x40/0x52. "
           + "空读一秒后才向 OUT 发送一包 PING。手机先说话。这不是 0x40/0x52。";

    public static string BulkRetry(byte endpoint, int code, int attempt)
        => "USB bulk IN 0x" + endpoint.ToString("X2") + " returned " + code
           + " (attempt " + attempt + " of " + BulkIoRetries
           + "). Cleared the halt and will read again. No 0x40/0x52 was sent. "
           + "批量读取失败，已清除 halt 并重试，没有发送 0x40/0x52。";

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
