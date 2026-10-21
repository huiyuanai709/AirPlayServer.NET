using AirPlayServer.Core;
using AirPlayServer.Core.Notify;
using AirPlayServer.Host;
using AirPlayServer.Host.Notify;

namespace AirPlayServer.Host.Tests;

public class HostArgsTests
{
    [Fact]
    public void Host_default_advertises_480x1040_without_a_model()
    {
        var parsed = HostArgs.Parse([]);
        Assert.Equal(HostArgs.DefaultAdvertisedWidth, parsed.Options.DisplayWidth);
        Assert.Equal(HostArgs.DefaultAdvertisedHeight, parsed.Options.DisplayHeight);
        Assert.Equal(480, parsed.Options.DisplayWidth);
        Assert.Equal(1040, parsed.Options.DisplayHeight);
        Assert.Null(parsed.Options.TapClockLogicalScreen);
        Assert.Equal(1920, AirPlayCompatibility.DefaultDisplayWidth);
        Assert.Equal(1080, AirPlayCompatibility.DefaultDisplayHeight);
    }

    [Fact]
    public void Model_sets_uikit_points_and_leaves_the_advertised_screen()
    {
        var parsed = HostArgs.Parse(["--model", "iphone15pro"]);
        Assert.Equal(480, parsed.Options.DisplayWidth);
        Assert.Equal(1040, parsed.Options.DisplayHeight);
        var logical = parsed.Options.TapClockLogicalScreen;
        Assert.NotNull(logical);
        Assert.Equal("iphone-15-pro", logical.Slug);
        Assert.Equal(393, logical.Width);
        Assert.Equal(852, logical.Height);
    }

    [Fact]
    public void Explicit_screen_wins_for_advertise_in_either_order()
    {
        string[][] orders =
        [
            ["--model", "iphone-16-pro-max", "--screen", "1920x1080"],
            ["--screen", "1920x1080", "--model", "iphone-16-pro-max"],
        ];

        foreach (var args in orders)
        {
            var parsed = HostArgs.Parse(args);
            Assert.Equal(1920, parsed.Options.DisplayWidth);
            Assert.Equal(1080, parsed.Options.DisplayHeight);
            var logical = parsed.Options.TapClockLogicalScreen;
            Assert.NotNull(logical);
            Assert.Equal("iphone-16-pro-max", logical.Slug);
            Assert.Equal(440, logical.Width);
            Assert.Equal(956, logical.Height);
        }
    }

    [Fact]
    public void Screen_alone_does_not_invent_a_model()
    {
        var parsed = HostArgs.Parse(["--screen", "393x852"]);
        Assert.Equal(393, parsed.Options.DisplayWidth);
        Assert.Equal(852, parsed.Options.DisplayHeight);
        Assert.Null(parsed.Options.TapClockLogicalScreen);
    }

    [Fact]
    public void Unknown_and_ambiguous_models_fail_before_startup()
    {
        var unknown = Assert.Throws<ArgumentException>(() => HostArgs.Parse(["--model", "nokia3310"]));
        Assert.Contains("未知机型", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("nokia3310", unknown.Message, StringComparison.Ordinal);

        var ambiguous = Assert.Throws<ArgumentException>(() => HostArgs.Parse(["--model", "iphone se"]));
        Assert.Contains("iphone-se-1", ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains("iphone-se-2", ambiguous.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void List_models_is_a_flag_and_help_separates_the_two_sizes()
    {
        var parsed = HostArgs.Parse(["--list-models"]);
        Assert.True(parsed.ListModels);
        Assert.False(parsed.Help);

        Assert.Contains("--model", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("--screen", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("--list-models", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("UIKit", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("/info", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("480x1040", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("TapClock x, y", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("像素 x, y", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("iphone-15-pro-max", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("点击仍然不发给手机", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("--no-update", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("--update", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("--check-update", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("--auto-update", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("不检查更新；用 --update 手动更新", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("--version", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("GITHUB_TOKEN", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("构建: " + HostBuild.Flavor, HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Equal(HostBuild.HasAvaloniaWindow, HostArgs.Parse([]).Options.ShowWindow);
        Assert.False(HostArgs.Parse(["--no-window"]).Options.ShowWindow);
    }

    [Fact]
    public void Update_flags_default_to_disabled()
    {
        Assert.Equal(HostUpdateMode.Disabled, HostArgs.Parse([]).UpdateMode);
        Assert.Equal(HostUpdateMode.Disabled, HostArgs.Parse(["--screen", "1290x2796", "--model", "iphone-15-pro-max", "--h265"]).UpdateMode);
        Assert.Equal(HostUpdateMode.Explicit, HostArgs.Parse(["--update"]).UpdateMode);
        Assert.Equal(HostUpdateMode.Explicit, HostArgs.Parse(["--check-update", "--name", "客厅"]).UpdateMode);
        Assert.Equal(HostUpdateMode.Explicit, HostArgs.Parse(["--update", "--check-update"]).UpdateMode);
        Assert.Equal(HostUpdateMode.Disabled, HostArgs.Parse(["--no-update"]).UpdateMode);
        Assert.Equal(HostUpdateMode.Disabled, HostArgs.Parse(["--no-update", "--name", "客厅", "--h265"]).UpdateMode);
        Assert.Equal("客厅", HostArgs.Parse(["--no-update", "--name", "客厅"]).Options.Name);
        Assert.Equal(HostUpdateMode.Automatic, HostArgs.Parse(["--auto-update"]).UpdateMode);
        Assert.Equal(HostUpdateMode.Automatic, HostArgs.Parse(["--screen", "1290x2796", "--auto-update", "--auto-update"]).UpdateMode);
        Assert.True(HostArgs.Parse(["--version"]).ShowVersion);

        var updateFirst = Assert.Throws<ArgumentException>(() => HostArgs.Parse(["--update", "--no-update"]));
        Assert.Contains("--update", updateFirst.Message, StringComparison.Ordinal);
        Assert.Contains("--no-update", updateFirst.Message, StringComparison.Ordinal);
        var skipFirst = Assert.Throws<ArgumentException>(() => HostArgs.Parse(["--no-update", "--check-update"]));
        Assert.Contains("--no-update", skipFirst.Message, StringComparison.Ordinal);
        var autoWithSkip = Assert.Throws<ArgumentException>(() => HostArgs.Parse(["--auto-update", "--no-update"]));
        Assert.Contains("--auto-update", autoWithSkip.Message, StringComparison.Ordinal);
        Assert.Contains("--no-update", autoWithSkip.Message, StringComparison.Ordinal);
        var autoWithUpdate = Assert.Throws<ArgumentException>(() => HostArgs.Parse(["--update", "--auto-update", "--name", "客厅"]));
        Assert.Contains("--update", autoWithUpdate.Message, StringComparison.Ordinal);
        Assert.Contains("--auto-update", autoWithUpdate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_timing_defaults_off_and_the_flag_selects_sample_or_every()
    {
        var previous = Environment.GetEnvironmentVariable("AIRPLAY_DECODE_TIMING");
        Environment.SetEnvironmentVariable("AIRPLAY_DECODE_TIMING", null);
        try
        {
            Assert.Equal(DecodeTimingMode.Off, HostArgs.Parse([]).Options.DecodeTiming);
            Assert.Equal(DecodeTimingMode.Sample, HostArgs.Parse(["--decode-timing"]).Options.DecodeTiming);
            Assert.Equal(DecodeTimingMode.Sample, HostArgs.Parse(["--decode-timing", "sample", "--h265"]).Options.DecodeTiming);
            Assert.True(HostArgs.Parse(["--decode-timing", "sample", "--h265"]).Options.AcceptHevc);
            Assert.Equal(DecodeTimingMode.Every, HostArgs.Parse(["--decode-timing", "every"]).Options.DecodeTiming);
            Assert.Equal(DecodeTimingMode.Off, HostArgs.Parse(["--no-decode-timing"]).Options.DecodeTiming);
            Assert.Contains("--decode-timing", HostArgs.HelpText, StringComparison.Ordinal);
            Assert.Contains("us", HostArgs.HelpText, StringComparison.Ordinal);
            Assert.Contains("AIRPLAY_DECODE_TIMING", HostArgs.HelpText, StringComparison.Ordinal);

            var bad = Assert.Throws<ArgumentException>(() => HostArgs.Parse(["--decode-timing", "fast"]));
            Assert.Contains("sample", bad.Message, StringComparison.Ordinal);
            var both = Assert.Throws<ArgumentException>(() => HostArgs.Parse(["--decode-timing", "--no-decode-timing"]));
            Assert.Contains("--no-decode-timing", both.Message, StringComparison.Ordinal);

            Environment.SetEnvironmentVariable("AIRPLAY_DECODE_TIMING", "every");
            Assert.Equal(DecodeTimingMode.Every, HostArgs.Parse([]).Options.DecodeTiming);
            Assert.Equal(DecodeTimingMode.Sample, HostArgs.Parse(["--decode-timing"]).Options.DecodeTiming);
            Assert.Equal(DecodeTimingMode.Off, HostArgs.Parse(["--no-decode-timing"]).Options.DecodeTiming);
            Environment.SetEnvironmentVariable("AIRPLAY_DECODE_TIMING", "nope");
            var env = Assert.Throws<ArgumentException>(() => HostArgs.Parse([]));
            Assert.Contains("AIRPLAY_DECODE_TIMING", env.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIRPLAY_DECODE_TIMING", previous);
        }
    }

    [Fact]
    public void H265_is_opt_in_and_leaves_the_advertised_screen()
    {
        Assert.False(HostArgs.Parse([]).Options.AcceptHevc);
        Assert.True(HostArgs.Parse(["--h265"]).Options.AcceptHevc);
        Assert.True(HostArgs.Parse(["--hevc"]).Options.AcceptHevc);

        var parsed = HostArgs.Parse(["--h265", "--screen", "1290x2796", "--model", "iphone-15-pro-max"]);
        Assert.True(parsed.Options.AcceptHevc);
        Assert.Equal(1290, parsed.Options.DisplayWidth);
        Assert.Equal(2796, parsed.Options.DisplayHeight);
        Assert.Equal(480, HostArgs.Parse(["--hevc"]).Options.DisplayWidth);
        Assert.Equal(1040, HostArgs.Parse(["--hevc"]).Options.DisplayHeight);
        Assert.Equal(int.MaxValue, AirPlayReceiverOptions.DefaultMaxMirrors);
        Assert.Equal(int.MaxValue, HostArgs.Parse([]).Options.MaxMirrors);
        HostArgs.Parse([]).Options.Validate();
        Assert.Equal(2, HostArgs.Parse(["--max-mirrors", "2"]).Options.MaxMirrors);
        var twenty = HostArgs.Parse(["--max-mirrors", "20"]);
        Assert.Equal(20, twenty.Options.MaxMirrors);
        twenty.Options.Validate();
        HostArgs.Parse(["--max-mirrors", "100"]).Options.Validate();
        Assert.Contains("--max-mirrors", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("省略则不限制", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("没有硬上限", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.DoesNotContain("默认 4。", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.DoesNotContain("最多 4", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.DoesNotContain("--max-mirrors 4", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.DoesNotContain("1 到 8", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("mirror-192.168.5.20.jpg", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("mirror-unknown.jpg", HostArgs.HelpText, StringComparison.Ordinal);
        var notPositive = Assert.Throws<ArgumentException>(() => HostArgs.Parse(["--max-mirrors", "0"]).Options.Validate());
        Assert.Contains("Max mirrors", notPositive.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => HostArgs.Parse(["--max-mirrors", "-1"]).Options.Validate());
        Assert.Contains("--h265", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("--hevc", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("1290x2796", HostArgs.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void Notify_flags_override_the_config_file_and_can_hide_the_page()
    {
        var directory = Path.Combine(Path.GetTempPath(), "airplay-notify-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "session-notify.json");
            SessionNotifyConfigStore.Save(path, new SessionNotifyConfig { DisconnectDebounceMs = 1000 });
            var parsed = HostArgs.Parse(
            [
                "--notify-config", path,
                "--tapclock-events",
                "--tapclock-base", "http://127.0.0.1:5099",
                "--notify-debounce-ms", "2500",
                "--notify-port", "7101",
                "--notify-listen", "127.0.0.1",
                "--no-notify",
            ]);
            var plan = SessionNotifyService.Load(parsed.Notify);
            Assert.True(plan.Config.TapClockEventsEnabled);
            Assert.Equal("http://127.0.0.1:5099", plan.Config.TapClockBaseUrl);
            Assert.Equal(2500, plan.Config.DisconnectDebounceMs);
            Assert.Equal(7101, plan.Config.ConfigPort);
            Assert.Equal("127.0.0.1", plan.Config.ConfigBindAddress);
            Assert.False(plan.ServePage);
            Assert.Equal(path, plan.Path);

            var defaults = HostArgs.Parse([]);
            Assert.False(defaults.Notify.DisablePage);
            Assert.Null(defaults.Notify.TapClockEvents);
            Assert.Contains("--tapclock-events", HostArgs.HelpText, StringComparison.Ordinal);
            Assert.Contains("/api/integrations/airplay/events", HostArgs.HelpText, StringComparison.Ordinal);
            Assert.Contains("--notify-debounce-ms", HostArgs.HelpText, StringComparison.Ordinal);
            Assert.Contains("--no-notify", HostArgs.HelpText, StringComparison.Ordinal);
            Assert.Contains("不操作控制中心", HostArgs.HelpText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Webhook_flag_enables_the_click_list()
    {
        var directory = Directory.CreateTempSubdirectory("airplay-webhook-");
        try
        {
            var path = Path.Combine(directory.FullName, "session-notify.json");
            File.WriteAllText(path, """{"webhooks_enabled":true,"webhook_urls":["http://127.0.0.1:9/from-file"]}""");
            var parsed = HostArgs.Parse(
            [
                "--notify-config", path,
                "--webhook", "http://127.0.0.1:9/from-cli",
                "--webhook", "http://127.0.0.1:9/from-file",
            ]);
            var plan = SessionNotifyService.Load(parsed.Notify);
            Assert.True(plan.Config.WebhooksEnabled);
            Assert.Equal(
                ["http://127.0.0.1:9/from-file", "http://127.0.0.1:9/from-cli"],
                plan.Config.WebhookUrls);
            Assert.Contains("--webhook", HostArgs.HelpText, StringComparison.Ordinal);
            Assert.Contains("点击仍然不发给手机", HostArgs.HelpText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public void Usb_flags_are_opt_in_and_do_not_name_the_qt_toolkit()
    {
        var idle = HostArgs.Parse([]);
        Assert.False(idle.Options.EnableUsb);
        Assert.False(idle.Options.AllowDualIngress);
        Assert.Null(idle.Options.UsbUdid);
        idle.Options.Validate();

        var usb = HostArgs.Parse(["--usb"]);
        Assert.True(usb.Options.EnableUsb);
        Assert.True(string.IsNullOrEmpty(usb.Options.UsbUdid));

        var filtered = HostArgs.Parse(["--usb-udid", "00008110-001A"]);
        Assert.True(filtered.Options.EnableUsb);
        Assert.Equal("00008110-001A", filtered.Options.UsbUdid);

        var dual = HostArgs.Parse(["--usb", "--allow-dual"]);
        Assert.True(dual.Options.AllowDualIngress);
        dual.Options.Validate();

        Assert.Contains("--usb", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("--usb-udid", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("--allow-dual", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("libusb", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("Zadig", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("不链接 Qt", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.DoesNotContain("Qt Widgets", HostArgs.HelpText, StringComparison.Ordinal);
    }
}
