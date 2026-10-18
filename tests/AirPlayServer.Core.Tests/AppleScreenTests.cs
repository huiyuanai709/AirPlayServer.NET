using System.Net;
using System.Net.Sockets;
using AirPlayServer.Core.Export;
using AirPlayServer.Core.Protocol;

namespace AirPlayServer.Core.Tests;

public class AppleScreenTests
{
    [Theory]
    [InlineData("iphone15pro", "iphone-15-pro", 393, 852)]
    [InlineData("iphone-15-pro", "iphone-15-pro", 393, 852)]
    [InlineData("iPhone 15 Pro", "iphone-15-pro", 393, 852)]
    [InlineData("IPHONE 15 PRO", "iphone-15-pro", 393, 852)]
    [InlineData("iphone16", "iphone-15-pro", 393, 852)]
    [InlineData("iphone-14-pro", "iphone-15-pro", 393, 852)]
    [InlineData("iphone15promax", "iphone-15-pro-max", 430, 932)]
    [InlineData("iphone-15-pro-max", "iphone-15-pro-max", 430, 932)]
    [InlineData("iPhone 16 Pro", "iphone-16-pro", 402, 874)]
    [InlineData("iphone16promax", "iphone-16-pro-max", 440, 956)]
    [InlineData("iphone-17-pro-max", "iphone-16-pro-max", 440, 956)]
    [InlineData("iphone-18-pro", "iphone-16-pro", 402, 874)]
    [InlineData("iphone-14", "iphone-14", 390, 844)]
    [InlineData("iphone14plus", "iphone-14-plus", 428, 926)]
    [InlineData("iphone 16 plus", "iphone-15-pro-max", 430, 932)]
    [InlineData("iphone se 3", "iphone-se-2", 375, 667)]
    [InlineData("iphone-se-1", "iphone-se-1", 320, 568)]
    [InlineData("iPhone SE (1st generation)", "iphone-se-1", 320, 568)]
    [InlineData("iphone air", "iphone-air", 420, 912)]
    [InlineData("iphone 8+", "iphone-8-plus", 414, 736)]
    [InlineData("ipad-pro-11-m4", "ipad-pro-11-m4", 834, 1210)]
    [InlineData("iPad Pro 13-inch (M4)", "ipad-pro-13", 1032, 1376)]
    [InlineData("ipad-mini-6", "ipad-mini-6", 744, 1133)]
    [InlineData("ipad pro 12.9", "ipad-pro-12-9", 1024, 1366)]
    [InlineData("ipad air 11", "ipad-10-9", 820, 1180)]
    public void ResolvesAliasesToPortraitPoints(string input, string slug, int width, int height)
    {
        var model = AppleScreens.Resolve(input);
        Assert.Equal(slug, model.Slug);
        Assert.Equal(width, model.Width);
        Assert.Equal(height, model.Height);
        Assert.True(model.Width <= model.Height);
        Assert.True(AppleScreens.TryResolve(input, out var resolved));
        Assert.Equal(slug, resolved.Slug);
    }

    [Fact]
    public void FlagshipsAreLogicalPointsNotPhysicalPixels()
    {
        var pro = AppleScreens.Resolve("iphone15pro");
        Assert.Equal(393, pro.Width);
        Assert.Equal(852, pro.Height);
        Assert.NotEqual(1179, pro.Width);
        Assert.NotEqual(2556, pro.Height);

        Assert.Equal((402, 874), Size("iphone-16-pro"));
        Assert.Equal((440, 956), Size("iphone 16 pro max"));
        Assert.Equal((390, 844), Size("iphone14"));
        Assert.Equal((320, 568), Size("iphone5s"));
        Assert.Equal((834, 1194), Size("ipad-pro-11-1"));
        Assert.Equal((834, 1210), Size("ipad pro 11 m5"));
        Assert.Equal((1032, 1376), Size("ipadpro13"));
        Assert.Equal((744, 1133), Size("ipadmini7"));
    }

    [Fact]
    public void UnknownModelMessageIsChinese()
    {
        var ex = Assert.Throws<ArgumentException>(() => AppleScreens.Resolve("nokia3310"));
        Assert.Contains("未知机型", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nokia3310", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--list-models", ex.Message, StringComparison.Ordinal);
        Assert.False(AppleScreens.TryResolve("nokia3310", out _));
        Assert.False(AppleScreens.TryResolve(null, out _));
        Assert.False(AppleScreens.TryResolve("   ", out _));
    }

    [Fact]
    public void AmbiguousNamesExplainTheSplit()
    {
        var se = Assert.Throws<ArgumentException>(() => AppleScreens.Resolve("iphone se"));
        Assert.Contains("iphone-se-1", se.Message, StringComparison.Ordinal);
        Assert.Contains("iphone-se-2", se.Message, StringComparison.Ordinal);
        Assert.False(AppleScreens.TryResolve("iphone se", out _));

        var ipad11 = Assert.Throws<ArgumentException>(() => AppleScreens.Resolve("iPad Pro 11-inch"));
        Assert.Contains("834×1194", ipad11.Message, StringComparison.Ordinal);
        Assert.Contains("834×1210", ipad11.Message, StringComparison.Ordinal);
        Assert.Contains("ipad-pro-11-m4", ipad11.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogHasOneSlugPerPortraitSize()
    {
        Assert.Equal(AppleScreens.All.Count, AppleScreens.All.Select(model => model.Slug).Distinct().Count());
        Assert.Equal(
            AppleScreens.All.Count,
            AppleScreens.All.Select(model => (model.Width, model.Height)).Distinct().Count());
        Assert.All(AppleScreens.All, model => Assert.True(model.Width <= model.Height));
        Assert.Contains(AppleScreens.All, model => model.Slug == "iphone-15-pro" && model.Width == 393);
        var catalog = AppleScreens.FormatCatalog();
        Assert.Contains("不能自动探测", catalog, StringComparison.Ordinal);
        Assert.Contains("Display Zoom", catalog, StringComparison.Ordinal);
        Assert.Contains("iphone-15-pro", catalog, StringComparison.Ordinal);
        Assert.Contains("--screen", catalog, StringComparison.Ordinal);
        foreach (var model in AppleScreens.All)
        {
            Assert.Contains(model.Slug, catalog, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CoerceLeavesWxHAndRewritesModel()
    {
        Assert.Equal("390x844", AppleScreens.Coerce("390x844"));
        Assert.Equal("393x852", AppleScreens.Coerce("  iphone15pro  "));
        var ambiguous = Assert.Throws<ArgumentException>(() => AppleScreens.Coerce("ipad"));
        Assert.Contains("ipad-pro-13", ambiguous.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Library_display_default_stays_1080p_when_a_model_is_stored()
    {
        var options = new AirPlayReceiverOptions
        {
            TapClockLogicalScreen = AppleScreens.Resolve("iphone-15-pro"),
        };

        Assert.Equal(AirPlayCompatibility.DefaultDisplayWidth, options.DisplayWidth);
        Assert.Equal(AirPlayCompatibility.DefaultDisplayHeight, options.DisplayHeight);
        Assert.Equal(1920, options.DisplayWidth);
        Assert.Equal(1080, options.DisplayHeight);
        Assert.Equal(393, options.TapClockLogicalScreen.Width);
        Assert.Equal(852, options.TapClockLogicalScreen.Height);
    }

    [Fact]
    public async Task Advertised_info_keeps_screen_size_when_model_is_selected()
    {
        var model = AppleScreens.Resolve("iphone-15-pro");
        var options = new AirPlayReceiverOptions
        {
            Name = "Model",
            ListenAddress = IPAddress.Loopback,
            AirPlayPort = 0,
            RaopPort = 0,
            EnableDiscovery = false,
            ShowWindow = false,
            HardwareAddress = [0x02, 0x11, 0x22, 0x33, 0x44, 0x66],
            DisplayWidth = 480,
            DisplayHeight = 1040,
            TapClockLogicalScreen = model,
            Log = NullAirPlayLog.Instance,
        };

        Assert.Equal(480, options.DisplayWidth);
        Assert.Equal(1040, options.DisplayHeight);
        Assert.NotEqual(model.Width, options.DisplayWidth);
        Assert.NotEqual(model.Height, options.DisplayHeight);

        await using var receiver = await AirPlayReceiver.StartAsync(options);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.AirPlayEndPoint.Port);
        await using var conversation = new HttpConversation(client.GetStream());
        var binary = await conversation.ExchangeAsync("POST /info RTSP/1.0\r\nCSeq: 1\r\nContent-Length: 0\r\n\r\n");
        Assert.Equal(200, binary.Status);
        var plist = Assert.IsType<PlistDictionary>(BinaryPropertyList.Read(binary.Body));
        var display = Assert.IsType<PlistDictionary>(Assert.IsType<PlistArray>(plist.Require("displays")).Items[0]);
        Assert.Equal(480, Assert.IsType<PlistInteger>(display.Require("width")).Value);
        Assert.Equal(1040, Assert.IsType<PlistInteger>(display.Require("height")).Value);
        Assert.Equal(480, Assert.IsType<PlistInteger>(display.Require("widthPixels")).Value);
        Assert.Equal(1040, Assert.IsType<PlistInteger>(display.Require("heightPixels")).Value);
        Assert.DoesNotContain(display.Items, item => item.Value is PlistInteger number && number.Value is 393 or 852);
        Assert.Equal(AirPlayCompatibility.Model, Assert.IsType<PlistString>(plist.Require("model")).Value);
    }

    private static (int Width, int Height) Size(string name)
    {
        var model = AppleScreens.Resolve(name);
        return (model.Width, model.Height);
    }
}
