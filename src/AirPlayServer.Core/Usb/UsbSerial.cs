namespace AirPlayServer.Core.Usb;

/// <summary>
/// USB serial numbers used as the stable mirror id. A 24-character serial is
/// the form some phones expose; the matching UDID inserts a dash after the
/// eighth character. A 40-character serial is already the UDID.
/// </summary>
internal static class UsbSerial
{
    public static string Correct(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return "";
        }

        var trimmed = serial.Trim().Trim('\0');
        if (trimmed.Length == 24 && trimmed.IndexOf('-') < 0)
        {
            return trimmed[..8] + "-" + trimmed[8..];
        }

        return trimmed;
    }

    public static bool Matches(string? candidate, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var left = Correct(candidate);
        var right = Correct(filter);
        return left.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
