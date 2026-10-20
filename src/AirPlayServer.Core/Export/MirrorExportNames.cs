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

        return "mirror-" + Sanitize(peer.ToString()) + ".jpg";
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
