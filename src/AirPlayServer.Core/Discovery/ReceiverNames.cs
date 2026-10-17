using System.Text;

namespace AirPlayServer.Core.Discovery;

internal static class ReceiverNames
{
    public static string InstanceLabel(string displayName)
    {
        var trimmed = displayName.Trim();
        if (trimmed.Length == 0)
        {
            trimmed = "AirPlay";
        }

        // 12 hex digits, '@', and the instance must fit in one 63-byte DNS label.
        return TruncateUtf8(trimmed, 50);
    }

    public static string HostLabel(string displayName)
    {
        var builder = new StringBuilder(displayName.Length);
        foreach (var character in displayName.Trim().ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            {
                builder.Append(character);
            }
            else if (character is ' ' or '_' or '.')
            {
                builder.Append('-');
            }
        }

        var label = builder.ToString().Trim('-');
        while (label.Contains("--", StringComparison.Ordinal))
        {
            label = label.Replace("--", "-", StringComparison.Ordinal);
        }

        if (label.Length == 0)
        {
            label = "airplay-receiver";
        }

        if (label.Length > 63)
        {
            label = label[..63].Trim('-');
        }

        return label;
    }

    public static string RaopInstance(ReadOnlySpan<byte> hardwareAddress, string instanceLabel)
        => Convert.ToHexString(hardwareAddress) + "@" + instanceLabel;

    public static string FormatDeviceId(ReadOnlySpan<byte> hardwareAddress)
    {
        Span<char> chars = stackalloc char[(hardwareAddress.Length * 3) - 1];
        var position = 0;
        for (var i = 0; i < hardwareAddress.Length; i++)
        {
            if (i > 0)
            {
                chars[position++] = ':';
            }

            chars[position++] = ToHex(hardwareAddress[i] >> 4);
            chars[position++] = ToHex(hardwareAddress[i] & 0x0F);
        }

        return new string(chars);
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        if (utf8.Length <= maxBytes)
        {
            return value;
        }

        var length = maxBytes;
        while (length > 0 && (utf8[length] & 0xC0) == 0x80)
        {
            length--;
        }

        return Encoding.UTF8.GetString(utf8, 0, length);
    }

    private static char ToHex(int value) => (char)(value < 10 ? '0' + value : 'a' + (value - 10));
}
