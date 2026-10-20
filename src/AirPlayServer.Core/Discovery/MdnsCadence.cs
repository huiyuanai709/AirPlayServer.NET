namespace AirPlayServer.Core.Discovery;

/// <summary>
/// Announcement timing for the AirPlay mDNS responder.
/// TTL matches Bonjour's <c>kStandardTTL</c> (75 minutes). RFC 6762 says to refresh
/// well before expiry; 80% of this TTL is one hour. Startup is four packets at
/// 0s, 1s, 2s, and 4s. The steady timer stays far inside that hour, and is shorter
/// when UDP 5353 cannot be shared, because the phone then only learns about the
/// receiver from unsolicited announcements.
/// </summary>
internal static class MdnsCadence
{
    public const uint TtlSeconds = 4500;

    public const int TtlRefreshPercent = 80;

    /// <summary>Used when queries are answered. Far below <see cref="TtlRefreshDeadlineSeconds"/>.</summary>
    public const int SteadySeconds = 60;

    /// <summary>
    /// Used when no socket is receiving UDP 5353. A phone that opens Control Center
    /// between announcements would otherwise wait out the full steady interval,
    /// and one dropped Wi-Fi multicast would double that.
    /// </summary>
    public const int QueryBlindSeconds = 10;

    /// <summary>
    /// Quiet time after <see cref="System.Net.NetworkInformation.NetworkChange"/>
    /// before adapters are read again. Windows raises several events for one
    /// Mobile Hotspot flap. An unchanged advertised address produces no packet.
    /// </summary>
    public const int NetworkChangeDebounceMilliseconds = 2000;

    /// <summary>
    /// Steady-loop adapter poll. NetworkChange callbacks only bump a counter;
    /// they do not enumerate NICs or send. A real address change is noticed
    /// on this poll, after the debounce above.
    /// </summary>
    public const int AddressPollMilliseconds = 5000;

    /// <summary>Gaps after the immediate first packet. Timestamps are 0s, 1s, 2s, 4s.</summary>
    public static readonly int[] StartupGapSeconds = [1, 1, 2];

    public static int StartupAnnouncementCount => StartupGapSeconds.Length + 1;

    public static int TtlRefreshDeadlineSeconds => (int)(TtlSeconds * TtlRefreshPercent / 100);

    public static TimeSpan SteadyIntervalFor(bool receivingQueries)
        => TimeSpan.FromSeconds(receivingQueries ? SteadySeconds : QueryBlindSeconds);

    public static TimeSpan NetworkChangeDebounce => TimeSpan.FromMilliseconds(NetworkChangeDebounceMilliseconds);

    public static TimeSpan AddressPollInterval => TimeSpan.FromMilliseconds(AddressPollMilliseconds);

    /// <summary>
    /// True only when the published A record changed. Hotspot-only flaps are
    /// absent from both lists once selection has run, so they return false.
    /// A true result is one mDNS announcement. It does not reset a mirror.
    /// </summary>
    public static bool ShouldRefreshAnnouncement(IReadOnlyList<AdvertisedNic> previous, IReadOnlyList<AdvertisedNic> next)
        => !SameAddresses(previous, next);

    public static IReadOnlyList<TimeSpan> StartupGaps()
    {
        var gaps = new TimeSpan[StartupGapSeconds.Length];
        for (var i = 0; i < StartupGapSeconds.Length; i++)
        {
            gaps[i] = TimeSpan.FromSeconds(StartupGapSeconds[i]);
        }

        return gaps;
    }

    public static int[] StartupTimesSeconds()
    {
        var times = new int[StartupAnnouncementCount];
        var elapsed = 0;
        for (var i = 0; i < StartupGapSeconds.Length; i++)
        {
            elapsed += StartupGapSeconds[i];
            times[i + 1] = elapsed;
        }

        return times;
    }

    public static bool RefreshesBeforeTtl(int intervalSeconds)
        => intervalSeconds > 0 && intervalSeconds <= TtlRefreshDeadlineSeconds;

    public static bool SameAddresses(IReadOnlyList<AdvertisedNic> left, IReadOnlyList<AdvertisedNic> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Address.Equals(right[i].Address))
            {
                return false;
            }
        }

        return true;
    }

    public static string FormatNics(IReadOnlyList<AdvertisedNic> nics)
    {
        if (nics.Count == 0)
        {
            return "(none)";
        }

        var parts = new string[nics.Count];
        for (var i = 0; i < nics.Count; i++)
        {
            var name = string.IsNullOrEmpty(nics[i].Name) ? "nic" : nics[i].Name;
            parts[i] = name + "=" + nics[i].Address;
        }

        return string.Join(", ", parts);
    }

    public static string DescribeStartup(bool receiving, bool canSend, string instanceName, string deviceId, string nics, string? bindFailure)
    {
        if (!canSend)
        {
            var reason = string.IsNullOrWhiteSpace(bindFailure) ? "no socket" : bindFailure;
            return "mDNS 已关闭，UDP 套接字创建失败，无法通告 "
                + instanceName + " MAC/deviceId=" + deviceId + " " + nics + "。原因: " + reason
                + " mDNS is disabled because a UDP socket could not be created for "
                + instanceName + " deviceId=" + deviceId + " " + nics + ". Cause: " + reason;
        }

        var cadenceZh = receiving
            ? $"启动在 0、1、2、4 秒连发，之后每 {SteadySeconds} 秒刷新"
            : $"启动在 0、1、2、4 秒连发，之后每 {QueryBlindSeconds} 秒主动通告（收不到 UDP 5353 查询）";
        var cadenceEn = receiving
            ? $"startup burst at 0/1/2/4s, then every {SteadySeconds}s"
            : $"startup burst at 0/1/2/4s, then every {QueryBlindSeconds}s because UDP 5353 is not receiving";
        var ttlZh = $"TTL {TtlSeconds} 秒，80% 刷新期限 {TtlRefreshDeadlineSeconds} 秒";
        var ttlEn = $"TTL {TtlSeconds}s, 80% refresh deadline {TtlRefreshDeadlineSeconds}s";
        if (receiving)
        {
            return "mDNS 查询套接字已绑定 UDP 5353（SO_REUSEADDR，非 Windows 再加 SO_REUSEPORT），会应答浏览查询。"
                + $" 通告 {instanceName} MAC/deviceId={deviceId} {nics}。{cadenceZh}。{ttlZh}。"
                + " mDNS query socket is bound to UDP 5353 with address reuse and will answer browse queries."
                + $" Advertising {instanceName} deviceId={deviceId} {nics}. {cadenceEn}. {ttlEn}.";
        }

        var cause = string.IsNullOrWhiteSpace(bindFailure) ? "bind failed" : bindFailure;
        return "mDNS 查询套接字 UDP 5353 不可用，手机的浏览查询不会被这个进程应答。"
            + " 绑到其它端口也收不到发往 5353 的查询，所以改为更频繁的主动通告，并从选定网卡送出（IP_MULTICAST_IF）。"
            + $" 通告 {instanceName} MAC/deviceId={deviceId} {nics}。{cadenceZh}。{ttlZh}。原因: {cause}"
            + " mDNS query socket UDP 5353 is unavailable, so browse queries will not be answered."
            + " A socket on any other port does not receive mDNS queries (they are addressed to port 5353)."
            + " Unsolicited announcements will be sent more often, with the multicast interface pinned to the selected NIC."
            + $" Advertising {instanceName} deviceId={deviceId} {nics}. {cadenceEn}. {ttlEn}. Cause: {cause}";
    }

    public static string DescribeAnnouncement(int sequence, string instanceName, string deviceId, string nics, int sourcePort, bool receiving)
    {
        var port = sourcePort == MdnsSockets.Port
            ? sourcePort.ToString()
            : sourcePort + " (not 5353; some iOS builds ignore these)";
        var query = receiving ? "on" : "off";
        return $"mDNS 通告 #{sequence} {instanceName} MAC/deviceId={deviceId} {nics} sourcePort={port} querySocket={query}"
            + $" | announcement #{sequence} {instanceName} deviceId={deviceId} {nics} sourcePort={port} querySocket={query}";
    }

    public static string DescribeAddressChange(string instanceName, string deviceId, string previous, string next)
        => $"mDNS 通告地址变化 {instanceName} MAC/deviceId={deviceId} {previous} -> {next}，只再发一条，不重置镜像。"
            + $" Advertised address changed for {instanceName} deviceId={deviceId}: {previous} -> {next}. Sending one announcement. The mirror session is not reset.";
}
