namespace AirPlayServer.Host.Update;

internal readonly record struct VersionFile(string Tag, string? Rid, string? Binary)
{
    public static bool TryParse(string? text, out VersionFile info)
    {
        info = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string? tag = null;
        string? rid = null;
        string? binary = null;
        var span = text.AsSpan().TrimStart('\uFEFF');
        while (!span.IsEmpty)
        {
            var lineEnd = span.IndexOf('\n');
            var line = (lineEnd >= 0 ? span[..lineEnd] : span).Trim();
            span = lineEnd >= 0 ? span[(lineEnd + 1)..] : default;
            if (line.IsEmpty || line[0] == '#')
            {
                continue;
            }

            var split = line.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            var key = line[..split].Trim();
            var value = line[(split + 1)..].Trim().ToString();
            if (value.Length == 0)
            {
                continue;
            }

            if (key.Equals("tag", StringComparison.OrdinalIgnoreCase))
            {
                tag = value;
            }
            else if (key.Equals("rid", StringComparison.OrdinalIgnoreCase))
            {
                rid = value;
            }
            else if (key.Equals("binary", StringComparison.OrdinalIgnoreCase))
            {
                binary = value;
            }
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        info = new VersionFile(tag, rid, binary);
        return true;
    }
}
