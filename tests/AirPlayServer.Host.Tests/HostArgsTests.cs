using AirPlayServer.Core;
using AirPlayServer.Host;

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
        Assert.Contains("--h265", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("--hevc", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("1290x2796", HostArgs.HelpText, StringComparison.Ordinal);
    }
}
