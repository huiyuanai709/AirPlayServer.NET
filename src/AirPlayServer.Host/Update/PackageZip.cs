using System.IO.Compression;

namespace AirPlayServer.Host.Update;

internal static class PackageZip
{
    public const int MaxEntries = 64;

    public const long MaxUncompressedBytes = 512L * 1024 * 1024;

    public static string Extract(string zipPath, string destination, string binaryName)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(zipPath);
        var files = 0;
        long declared = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            files++;
            if (files > MaxEntries)
            {
                throw new InvalidDataException("压缩包里的文件太多。");
            }

            declared += entry.Length;
            if (declared > MaxUncompressedBytes || entry.Length < 0)
            {
                throw new InvalidDataException("压缩包解开后太大。");
            }

            if (IsUnixSymlink(entry))
            {
                throw new InvalidDataException("压缩包含有符号链接：" + entry.FullName);
            }

            if (PackageFiles.IsDebugSymbol(entry.Name))
            {
                continue;
            }

            var target = CombineSafe(destination, entry.FullName);
            if (target is null)
            {
                throw new InvalidDataException("压缩包路径不安全：" + entry.FullName);
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            ExtractLimited(entry, target);
        }

        return FindPackageDirectory(destination, binaryName);
    }

    private static void ExtractLimited(ZipArchiveEntry entry, string target)
    {
        using var input = entry.Open();
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            written += read;
            if (written > entry.Length || written > MaxUncompressedBytes)
            {
                throw new InvalidDataException("压缩包条目大小异常：" + entry.FullName);
            }

            output.Write(buffer, 0, read);
        }
    }

    private static string FindPackageDirectory(string root, string binaryName)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string? found = null;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!string.Equals(Path.GetFileName(file), binaryName, comparison))
            {
                continue;
            }

            if (found is not null)
            {
                throw new InvalidDataException("压缩包里有多份 " + binaryName + "。");
            }

            found = file;
        }

        if (found is null)
        {
            throw new InvalidDataException("压缩包里没有 " + binaryName + "。");
        }

        return Path.GetDirectoryName(found) ?? throw new InvalidDataException("压缩包里没有 " + binaryName + "。");
    }

    private static bool IsUnixSymlink(ZipArchiveEntry entry)
    {
        var mode = entry.ExternalAttributes >> 16;
        return (mode & 0xF000) == 0xA000;
    }

    internal static string? CombineSafe(string root, string relative)
    {
        relative = relative.Replace('\\', '/').TrimEnd('/');
        if (relative.Length == 0 || relative[0] == '/')
        {
            return null;
        }

        var parts = relative.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or "..")
            {
                return null;
            }
        }

        var combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var fullRoot = Path.GetFullPath(root);
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!combined.StartsWith(prefix, comparison))
        {
            return null;
        }

        return combined;
    }
}
