using System.Net;

namespace AirPlayServer.Core.Export;

/// <summary>
/// Watch-directory JPEG names. The source address is the mirror data peer,
/// not the control connection, so two phones do not share one file.
/// </summary>
public static class MirrorExportNames
{
    public const string UnknownFileName = "mirror-unknown.jpg";

    public static string JpegFileName(IPAddress? peer)
    {
        if (peer is null)
        {
            return UnknownFileName;
        }

        return JpegFileName(peer.ToString());
    }

    /// <summary>File name for a non-IP source such as a USB serial. The label is sanitized.</summary>
    public static string JpegFileName(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return UnknownFileName;
        }

        return "mirror-" + Sanitize(label) + ".jpg";
    }

    /// <summary>
    /// Keeps digits, ASCII letters, dots, and dashes. Every other character,
    /// including IPv6 colons and zone-id percent signs, becomes a dash.
    /// </summary>
    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "unknown";
        }

        var chars = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            chars[i] = c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '.' or '-'
                ? c
                : '-';
        }

        return new string(chars);
    }
}
