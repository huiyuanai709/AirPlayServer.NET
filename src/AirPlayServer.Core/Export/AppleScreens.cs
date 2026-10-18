// Adapted from TapClock host/src/TapClock.Core/AppleScreens.cs (MIT).
// Copyright (c) 2026 TapClock contributors
// https://github.com/huiyuanai709/TapClock
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AirPlayServer.Core.Export;

/// <summary>
/// Portrait UIKit points for shipping iPhones and iPads (through iPhone 18 and iPad Pro M5).
/// One entry per distinct logical size; marketing names that share those points are aliases.
/// Points are the default (Standard) display, not physical pixels and not Display Zoom.
/// Foldables are omitted. This catalog is TapClock click alignment. It is not the AirPlay
/// <c>/info</c> advertised mirror size (<c>--screen</c>).
/// </summary>
public static partial class AppleScreens
{
    /// <summary>Same bounds as TapClock <c>BoardLimits.ScreenMin</c> / <c>ScreenMax</c>.</summary>
    private const int ScreenMin = 2;

    private const int ScreenMax = 10000;

    public sealed record Model(string Slug, int Width, int Height, string Title);

    private sealed record Entry(Model Model, string[] Aliases);

    private static readonly Entry[] Table =
    [
        EntryOf("iphone-16-pro-max", 440, 956, "iPhone 16 Pro Max、17 Pro Max、18 Pro Max",
            "iphone-17-pro-max", "iphone-18-pro-max"),
        EntryOf("iphone-16-pro", 402, 874, "iPhone 16 Pro、17、17 Pro、18 Pro",
            "iphone-17", "iphone-17-pro", "iphone-18-pro"),
        EntryOf("iphone-air", 420, 912, "iPhone Air"),
        EntryOf("iphone-15-pro-max", 430, 932, "iPhone 14 Pro Max、15 Plus、15 Pro Max、16 Plus",
            "iphone-14-pro-max", "iphone-15-plus", "iphone-16-plus"),
        EntryOf("iphone-15-pro", 393, 852, "iPhone 14 Pro、15、15 Pro、16",
            "iphone-14-pro", "iphone-15", "iphone-16"),
        EntryOf("iphone-14-plus", 428, 926, "iPhone 12 Pro Max、13 Pro Max、14 Plus",
            "iphone-12-pro-max", "iphone-13-pro-max"),
        EntryOf("iphone-14", 390, 844, "iPhone 12、12 Pro、13、13 Pro、14、16e、17e",
            "iphone-12", "iphone-12-pro", "iphone-13", "iphone-13-pro", "iphone-16e", "iphone-17e"),
        EntryOf("iphone-11", 414, 896, "iPhone XR、XS Max、11、11 Pro Max",
            "iphone-xr", "iphone-xs-max", "iphone-11-pro-max"),
        EntryOf("iphone-x", 375, 812, "iPhone X、XS、11 Pro、12 mini、13 mini",
            "iphone-xs", "iphone-11-pro", "iphone-12-mini", "iphone-13-mini", "iphone-mini"),
        EntryOf("iphone-8-plus", 414, 736, "iPhone 6 Plus、6s Plus、7 Plus、8 Plus",
            "iphone-6-plus", "iphone-6s-plus", "iphone-7-plus", "iphone-6+", "iphone-8+"),
        EntryOf("iphone-se-2", 375, 667, "iPhone SE（第 2 / 3 代）、6、6s、7、8",
            "iphone-se-3", "iphone-se-2nd", "iphone-se-3rd", "iphone-se-2020", "iphone-se-2022",
            "iphone-6", "iphone-6s", "iphone-7", "iphone-8"),
        EntryOf("iphone-se-1", 320, 568, "iPhone SE（第 1 代）、5、5s、5c",
            "iphone-se-1st", "iphone-se-2016", "iphone-5", "iphone-5s", "iphone-5c"),

        EntryOf("ipad-pro-13", 1032, 1376, "iPad Pro 13 英寸（M4 / M5）",
            "ipad-pro-13-m4", "ipad-pro-13-m5", "ipad-pro-m4-13", "ipad-pro-m5-13"),
        EntryOf("ipad-pro-11-m4", 834, 1210, "iPad Pro 11 英寸（M4 / M5）",
            "ipad-pro-11-m5", "ipad-pro-m4-11", "ipad-pro-m5-11"),
        EntryOf("ipad-pro-12-9", 1024, 1366, "iPad Pro 12.9 英寸（第 1–6 代）、iPad Air 13 英寸",
            "ipad-pro-12.9", "ipad-air-13", "ipad-air-13-m2", "ipad-air-13-m3", "ipad-air-13-m4"),
        EntryOf("ipad-pro-11-1", 834, 1194, "iPad Pro 11 英寸（第 1–4 代，2018–2022）",
            "ipad-pro-11-2", "ipad-pro-11-3", "ipad-pro-11-4",
            "ipad-pro-11-2018", "ipad-pro-11-2020", "ipad-pro-11-2021", "ipad-pro-11-2022"),
        EntryOf("ipad-10-9", 820, 1180, "iPad 第 10 / 11 代、Air 第 4 / 5 代、Air 11 英寸",
            "ipad-10", "ipad-11", "ipad-10th", "ipad-11th", "ipad-10.9",
            "ipad-air-4", "ipad-air-5", "ipad-air-11", "ipad-air-11-m2", "ipad-air-11-m3", "ipad-air-11-m4"),
        EntryOf("ipad-mini-6", 744, 1133, "iPad mini 第 6 / 7 代",
            "ipad-mini-7", "ipad-mini-6th", "ipad-mini-7th"),
        EntryOf("ipad-10-5", 834, 1112, "iPad Pro 10.5 英寸、iPad Air（第 3 代）",
            "ipad-pro-10-5", "ipad-pro-10.5", "ipad-air-3"),
        EntryOf("ipad-10-2", 810, 1080, "iPad 第 7 / 8 / 9 代（10.2 英寸）",
            "ipad-7", "ipad-8", "ipad-9", "ipad-7th", "ipad-8th", "ipad-9th", "ipad-10.2"),
        EntryOf("ipad-9-7", 768, 1024, "iPad 9.7 英寸（第 1–6 代）、mini 第 1–5 代、Air 第 1–2 代、Pro 9.7 英寸",
            "ipad-9.7", "ipad-1", "ipad-2", "ipad-3", "ipad-4", "ipad-5", "ipad-6",
            "ipad-mini-1", "ipad-mini-2", "ipad-mini-3", "ipad-mini-4", "ipad-mini-5",
            "ipad-air-1", "ipad-air-2", "ipad-pro-9-7", "ipad-pro-9.7"),
    ];

    private static readonly (string Input, string Message)[] AmbiguousInputs =
    [
        ("iphone", "「iphone」不是具体机型。例如 iphone-15-pro、iphone-16-pro-max、iphone-se-2。列表：--list-models。"),
        ("iphone se", "iPhone SE 第 1 代是 320×568（iphone-se-1），第 2 / 3 代是 375×667（iphone-se-2）。"),
        ("iphone pro", "请写完整型号，例如 iphone-15-pro 或 iphone-16-pro。各代 Pro 的逻辑点不同。"),
        ("iphone pro max", "请写完整型号，例如 iphone-15-pro-max 或 iphone-16-pro-max。各代 Pro Max 的逻辑点不同。"),
        ("iphone plus", "请写完整型号，例如 iphone-8-plus、iphone-14-plus 或 iphone-16-plus。各代 Plus 的逻辑点不同。"),
        ("ipad", "「ipad」对应多套逻辑点。例如 ipad-10-9、ipad-pro-11-m4、ipad-pro-13。列表：--list-models。"),
        ("ipad air", "iPad Air 有多套逻辑点：第 1–2 代 ipad-9-7（768×1024），第 3 代 ipad-10-5（834×1112），11 英寸 / 第 4–5 代 ipad-10-9（820×1180），13 英寸 ipad-air-13（1024×1366）。"),
        ("ipad pro", "iPad Pro 有多套逻辑点。例如 ipad-pro-11-1（834×1194）、ipad-pro-11-m4（834×1210）、ipad-pro-12-9（1024×1366）、ipad-pro-13（1032×1376）。"),
        ("ipad pro 11", "11 英寸 iPad Pro 有两套竖屏逻辑点：ipad-pro-11-1 是 834×1194（2018–2022），ipad-pro-11-m4 是 834×1210（M4 / M5）。"),
        ("ipad mini", "iPad mini 第 1–5 代是 768×1024（ipad-mini-5，同 ipad-9-7），第 6–7 代是 744×1133（ipad-mini-6）。"),
        ("ipad air 6", "iPad Air 第 6 代分 11 英寸和 13 英寸：ipad-air-11（820×1180）或 ipad-air-13（1024×1366）。"),
        ("ipad air 7", "iPad Air 第 7 代分 11 英寸和 13 英寸：ipad-air-11（820×1180）或 ipad-air-13（1024×1366）。"),
        ("ipad air 8", "iPad Air 第 8 代分 11 英寸和 13 英寸：ipad-air-11（820×1180）或 ipad-air-13（1024×1366）。"),
        ("ipad air m2", "iPad Air（M2）分 11 英寸和 13 英寸：ipad-air-11（820×1180）或 ipad-air-13（1024×1366）。"),
        ("ipad air m3", "iPad Air（M3）分 11 英寸和 13 英寸：ipad-air-11（820×1180）或 ipad-air-13（1024×1366）。"),
        ("ipad air m4", "iPad Air（M4）分 11 英寸和 13 英寸：ipad-air-11（820×1180）或 ipad-air-13（1024×1366）。"),
        ("ipad pro 7", "iPad Pro 第 7 代（M4）分 11 英寸和 13 英寸：ipad-pro-11-m4（834×1210）或 ipad-pro-13（1032×1376）。"),
        ("ipad pro 8", "iPad Pro 第 8 代（M5）分 11 英寸和 13 英寸：ipad-pro-11-m4（834×1210）或 ipad-pro-13（1032×1376）。"),
        ("ipad pro m4", "iPad Pro（M4）分 11 英寸和 13 英寸：ipad-pro-11-m4（834×1210）或 ipad-pro-13（1032×1376）。"),
        ("ipad pro m5", "iPad Pro（M5）分 11 英寸和 13 英寸：ipad-pro-11-m4（834×1210）或 ipad-pro-13（1032×1376）。"),
    ];

    private static readonly Dictionary<string, Model> ByKey;
    private static readonly Dictionary<string, string> Ambiguous;

    static AppleScreens()
    {
        var sizes = new HashSet<(int Width, int Height)>();
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Table)
        {
            if (entry.Model.Width > entry.Model.Height)
            {
                throw new InvalidOperationException($"{entry.Model.Slug} 不是竖屏。");
            }

            if (entry.Model.Width < ScreenMin || entry.Model.Height < ScreenMin ||
                entry.Model.Width > ScreenMax || entry.Model.Height > ScreenMax)
            {
                throw new InvalidOperationException($"{entry.Model.Slug} 超出 SCREEN 范围。");
            }

            if (!slugs.Add(entry.Model.Slug) || !sizes.Add((entry.Model.Width, entry.Model.Height)))
            {
                throw new InvalidOperationException($"重复的机型尺寸：{entry.Model.Slug}。");
            }
        }

        Ambiguous = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (input, message) in AmbiguousInputs)
        {
            var key = Normalize(input);
            if (!Ambiguous.TryAdd(key, message))
            {
                throw new InvalidOperationException($"含糊机型键重复：{key}。");
            }
        }

        ByKey = new Dictionary<string, Model>(StringComparer.Ordinal);
        foreach (var entry in Table)
        {
            Register(entry.Model.Slug, entry.Model);
            foreach (var alias in entry.Aliases)
            {
                Register(alias, entry.Model);
            }
        }

        All = Table.Select(entry => entry.Model).ToArray();
    }

    public static IReadOnlyList<Model> All { get; }

    public static Model Resolve(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("请提供机型名称。列表：--list-models。");
        }

        var key = Normalize(text);
        if (Ambiguous.TryGetValue(key, out var ambiguous))
        {
            throw new ArgumentException(ambiguous);
        }

        if (ByKey.TryGetValue(key, out var model))
        {
            return model;
        }

        var shown = text.Trim();
        throw new ArgumentException(
            $"未知机型「{shown}」。表内是竖屏 UIKit 逻辑点，不是物理像素。用 --list-models 查看。");
    }

    public static bool TryResolve(string? text, out Model model)
    {
        model = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var key = Normalize(text);
        if (Ambiguous.ContainsKey(key) || !ByKey.TryGetValue(key, out var found))
        {
            return false;
        }

        model = found;
        return true;
    }

    /// <summary>
    /// A model name becomes <c>WxH</c>. An existing <c>390x844</c> value is returned trimmed.
    /// Ambiguous names throw. Callers that mean the AirPlay advertisement use <c>--screen</c> instead.
    /// </summary>
    public static string Coerce(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var key = Normalize(text);
        if (Ambiguous.TryGetValue(key, out var ambiguous))
        {
            throw new ArgumentException(ambiguous);
        }

        if (ByKey.TryGetValue(key, out var model))
        {
            return FormatWxH(model);
        }

        if (LooksLikeWxH(text))
        {
            return text.Trim();
        }

        throw new ArgumentException(
            $"未知机型「{text.Trim()}」。表内是竖屏 UIKit 逻辑点，不是物理像素。用 --list-models 查看，或直接写宽高。");
    }

    private static bool LooksLikeWxH(string text)
    {
        var parts = text.Trim().Split('x', 'X');
        return parts.Length == 2 &&
               int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
               int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    public static string FormatWxH(Model model) =>
        $"{model.Width.ToString(CultureInfo.InvariantCulture)}x{model.Height.ToString(CultureInfo.InvariantCulture)}";

    public static string FormatPoints(Model model) =>
        $"{model.Width.ToString(CultureInfo.InvariantCulture)}×{model.Height.ToString(CultureInfo.InvariantCulture)}";

    public static string FormatCatalog()
    {
        var text = new StringBuilder();
        text.AppendLine("机型 → 竖屏 UIKit 逻辑点（point）。不是物理像素，也不是 @2x / @3x。");
        text.AppendLine("同一行共用一套尺寸，只保留一个 slug。这是标准显示的默认值。");
        text.AppendLine("Display Zoom、横屏、台前调度（Stage Manager）和分屏会改变有效尺寸。");
        text.AppendLine("USB HID 读不到手机型号，不能自动探测。折叠屏不在表里。");
        text.AppendLine("这些点只给 TapClock 点击对齐。AirPlay /info 的宣告分辨率是 --screen，两者互不影响。");
        text.AppendLine();
        text.AppendLine("用法：AirPlayServer.Host --model iphone-15-pro --screen 480x1040 --tapclock 127.0.0.1:42125");
        text.AppendLine("      AirPlayServer.Host --list-models");
        text.AppendLine();
        foreach (var model in All)
        {
            text.Append(model.Slug.PadRight(20));
            text.Append(model.Width.ToString(CultureInfo.InvariantCulture).PadLeft(4));
            text.Append('×');
            text.Append(model.Height.ToString(CultureInfo.InvariantCulture).PadRight(5));
            text.Append("  ");
            text.AppendLine(model.Title);
        }

        return text.ToString();
    }

    public static string Normalize(string text)
    {
        var lower = text.Trim().ToLowerInvariant().Replace("+", "plus", StringComparison.Ordinal);
        lower = OrdinalSuffix().Replace(lower, "$1");
        lower = lower.Replace("generation", "", StringComparison.Ordinal);
        lower = lower.Replace("inches", "", StringComparison.Ordinal);
        lower = lower.Replace("inch", "", StringComparison.Ordinal);
        lower = lower.Replace("英寸", "", StringComparison.Ordinal);
        lower = lower.Replace("gen", "", StringComparison.Ordinal);
        var slug = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                slug.Append(ch);
            }
        }

        return slug.ToString();
    }

    [GeneratedRegex(@"(\d+)(st|nd|rd|th)\b", RegexOptions.CultureInvariant)]
    private static partial Regex OrdinalSuffix();

    private static void Register(string alias, Model model)
    {
        var key = Normalize(alias);
        if (key.Length == 0)
        {
            throw new InvalidOperationException($"{model.Slug} 有空别名。");
        }

        if (Ambiguous.ContainsKey(key))
        {
            throw new InvalidOperationException($"别名「{alias}」（{model.Slug}）和含糊机型冲突。");
        }

        if (ByKey.TryGetValue(key, out var existing))
        {
            if (existing.Slug != model.Slug)
            {
                throw new InvalidOperationException($"别名「{alias}」同时指向 {existing.Slug} 和 {model.Slug}。");
            }

            return;
        }

        ByKey.Add(key, model);
    }

    private static Entry EntryOf(string slug, int width, int height, string title, params string[] aliases) =>
        new(new Model(slug, width, height, title), aliases);
}
