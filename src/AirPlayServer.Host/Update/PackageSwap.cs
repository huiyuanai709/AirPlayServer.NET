namespace AirPlayServer.Host.Update;

internal static class PackageSwap
{
    public static void Apply(string packageDirectory, string installDirectory, string executablePath, string binaryName)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var names = Directory.GetFiles(packageDirectory)
            .Select(Path.GetFileName)
            .Where(name => name is not null && PackageFiles.IsInstallable(name))
            .Select(name => name!)
            .OrderBy(name => name.Equals("VERSION.txt", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var sawBinary = false;
        foreach (var name in names)
        {
            var source = Path.Combine(packageDirectory, name);
            var destination = Path.Combine(installDirectory, name);
            if (name.Equals(binaryName, comparison))
            {
                ReplaceBinary(source, destination);
                sawBinary = true;
            }
            else
            {
                ReplaceRegular(source, destination);
            }
        }

        if (!sawBinary)
        {
            throw new InvalidDataException("压缩包里没有 " + binaryName + "。");
        }

        var runningName = Path.GetFileName(executablePath);
        if (!runningName.Equals(binaryName, comparison))
        {
            ReplaceBinary(Path.Combine(packageDirectory, binaryName), executablePath);
        }
    }

    public static void EnsureWritable(string directory)
    {
        var probe = Path.Combine(directory, ".airplayserver-update-probe");
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException(UpdateText.Permission(directory), ex);
        }
    }

    private static void ReplaceRegular(string source, string destination)
    {
        var incoming = destination + ".incoming";
        File.Copy(source, incoming, overwrite: true);
        File.Move(incoming, destination, overwrite: true);
    }

    /// <summary>
    /// The running image cannot be overwritten in place. Move it aside, then
    /// put the new file at the original path. Linux keeps the old inode for
    /// this process; Windows allows the rename and blocks a direct write.
    /// </summary>
    private static void ReplaceBinary(string source, string destination)
    {
        var incoming = destination + ".incoming";
        File.Copy(source, incoming, overwrite: true);
        MakeExecutable(incoming);

        var backup = destination + ".old";
        TryDelete(backup);
        if (File.Exists(destination))
        {
            File.Move(destination, backup);
        }

        try
        {
            File.Move(incoming, destination, overwrite: true);
        }
        catch
        {
            if (!File.Exists(destination) && File.Exists(backup))
            {
                try
                {
                    File.Move(backup, destination);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            TryDelete(incoming);
            throw;
        }

        MakeExecutable(destination);
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        const UnixFileMode Mode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(path, Mode);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
