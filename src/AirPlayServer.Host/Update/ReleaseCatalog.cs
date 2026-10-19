namespace AirPlayServer.Host.Update;

internal static class HostRid
{
    public const string WinX64 = "win-x64";

    public const string LinuxX64 = "linux-x64";

    public static string? Detect()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.X64)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return WinX64;
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxX64;
        }

        return null;
    }
}

internal static class ReleaseAssets
{
    public const string SumsFileName = "SHA256SUMS.txt";

    public static string ZipFileName(string tag, string rid) =>
        "AirPlayServer.Host-" + tag + "-" + rid + ".zip";

    public static string BinaryName(string rid) => rid switch
    {
        HostRid.WinX64 => "AirPlayServer.Host.exe",
        HostRid.LinuxX64 => "AirPlayServer.Host",
        _ => throw new ArgumentOutOfRangeException(nameof(rid), rid, "unsupported rid"),
    };

    public static bool TrySelectZip(PublishedRelease release, string rid, out ReleaseAsset? asset)
    {
        var name = ZipFileName(release.Tag, rid);
        foreach (var candidate in release.Assets)
        {
            if (candidate.Name.Equals(name, StringComparison.Ordinal))
            {
                asset = candidate;
                return true;
            }
        }

        asset = null;
        return false;
    }

    public static bool TrySelectSums(PublishedRelease release, out ReleaseAsset? asset)
    {
        foreach (var candidate in release.Assets)
        {
            if (candidate.Name.Equals(SumsFileName, StringComparison.Ordinal))
            {
                asset = candidate;
                return true;
            }
        }

        asset = null;
        return false;
    }
}

internal static class PackageFiles
{
    public static bool IsDebugSymbol(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dbg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dwo", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".debug", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sym", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dsym", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInstallable(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName is "." or "..")
        {
            return false;
        }

        if (fileName.Contains('/', StringComparison.Ordinal) || fileName.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        if (IsDebugSymbol(fileName))
        {
            return false;
        }

        if (fileName is "VERSION.txt" or "LICENSE" or "NOTICE.md" or "RUN.txt" or "airplayserver.service"
            or "AirPlayServer.Host" or "AirPlayServer.Host.exe")
        {
            return true;
        }

        var extension = Path.GetExtension(fileName);
        return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".so", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class Sha256Sums
{
    public static bool TryFind(string sums, string fileName, out string hashHex)
    {
        hashHex = "";
        if (string.IsNullOrEmpty(sums) || string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        string? suffixMatch = null;
        var remaining = sums.AsSpan();
        while (!remaining.IsEmpty)
        {
            var lineEnd = remaining.IndexOf('\n');
            var line = (lineEnd >= 0 ? remaining[..lineEnd] : remaining).Trim();
            remaining = lineEnd >= 0 ? remaining[(lineEnd + 1)..] : default;
            if (line.IsEmpty || line[0] == '#')
            {
                continue;
            }

            var split = 0;
            while (split < line.Length && !char.IsWhiteSpace(line[split]))
            {
                split++;
            }

            if (split != 64 || !IsHex(line[..split]))
            {
                continue;
            }

            var name = line[split..].Trim();
            if (!name.IsEmpty && name[0] == '*')
            {
                name = name[1..].Trim();
            }

            if (name.IsEmpty)
            {
                continue;
            }

            var hash = line[..split].ToString();
            if (name.Equals(fileName.AsSpan(), StringComparison.Ordinal))
            {
                hashHex = hash;
                return true;
            }

            if (suffixMatch is null && EndsWithFileName(name, fileName))
            {
                suffixMatch = hash;
            }
        }

        if (suffixMatch is null)
        {
            return false;
        }

        hashHex = suffixMatch;
        return true;
    }

    public static bool TryParseDigest(string? digest, out string hashHex)
    {
        hashHex = "";
        if (string.IsNullOrWhiteSpace(digest))
        {
            return false;
        }

        const string Prefix = "sha256:";
        if (!digest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = digest.AsSpan(Prefix.Length).Trim();
        if (value.Length != 64 || !IsHex(value))
        {
            return false;
        }

        hashHex = value.ToString().ToLowerInvariant();
        return true;
    }

    public static bool HexEquals(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        if (left.Length != 64 || right.Length != 64)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < 64; i++)
        {
            diff |= NormalizeHex(left[i]) ^ NormalizeHex(right[i]);
        }

        return diff == 0;
    }

    public static bool IsHex(ReadOnlySpan<char> text)
    {
        if (text.Length != 64)
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (ch is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EndsWithFileName(ReadOnlySpan<char> path, string fileName)
    {
        var name = fileName.AsSpan();
        return path.Length > name.Length
            && path[path.Length - name.Length - 1] is '/' or '\\'
            && path.EndsWith(name, StringComparison.Ordinal);
    }

    private static int NormalizeHex(char ch) => ch is >= 'A' and <= 'F' ? ch + 32 : ch;
}
