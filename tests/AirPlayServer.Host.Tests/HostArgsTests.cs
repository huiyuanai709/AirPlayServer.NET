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
        Assert.Contains("绝对像素", HostArgs.HelpText, StringComparison.Ordinal);
        Assert.Contains("点击仍然不发给手机", HostArgs.HelpText, StringComparison.Ordinal);
    }
}
