namespace AirPlayServer.Host.Update;

internal static class HostUpdate
{
    public const string JustUpdatedVariable = "AIRPLAYSERVER_JUST_UPDATED";

    public const string TokenVariable = "GITHUB_TOKEN";

    public const string TokenFallbackVariable = "GH_TOKEN";

    public static readonly TimeSpan AutomaticMetadataTimeout = TimeSpan.FromSeconds(12);

    public static readonly TimeSpan ExplicitMetadataTimeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan AutomaticDownloadTimeout = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan ExplicitDownloadTimeout = TimeSpan.FromMinutes(5);

    public static bool WasJustUpdated()
    {
        var value = Environment.GetEnvironmentVariable(JustUpdatedVariable);
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        Environment.SetEnvironmentVariable(JustUpdatedVariable, null);
        return true;
    }

    public static string? ReadToken()
    {
        var token = Environment.GetEnvironmentVariable(TokenVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable(TokenFallbackVariable);
        }

        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    public static string UserAgent()
    {
        var cleaned = new string(HostVersion.InformationalVersion
            .Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-')
            .ToArray());
        if (cleaned.Length == 0)
        {
            cleaned = "0";
        }

        return "AirPlayServer.Host/" + cleaned + " (+https://github.com/huiyuanai709/AirPlayServer.NET)";
    }

    public static void PrintVersion()
    {
        Console.WriteLine("AirPlayServer.Host");
        Console.WriteLine("程序内版本: " + HostVersion.InformationalVersion);
        if (TryReadVersionFile(out var info, out _))
        {
            Console.WriteLine("VERSION.txt: " + info.Tag + (info.Rid is null ? "" : "（" + info.Rid + "）"));
            return;
        }

        Console.WriteLine("VERSION.txt: 未找到。自动更新会改用程序内版本。");
    }

    public static void CleanupStaleFiles()
    {
        if (string.IsNullOrEmpty(Environment.ProcessPath))
        {
            return;
        }

        var executable = Environment.ProcessPath;
        var directory = Path.GetDirectoryName(executable);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        foreach (var path in new[]
        {
            executable + ".old",
            executable + ".incoming",
            Path.Combine(directory, "AirPlayServer.Host.old"),
            Path.Combine(directory, "AirPlayServer.Host.exe.old"),
            Path.Combine(directory, "AirPlayServer.Host.incoming"),
            Path.Combine(directory, "AirPlayServer.Host.exe.incoming"),
        })
        {
            TryDelete(path);
        }
    }

    public static string[] ArgumentsForRelaunch(IReadOnlyList<string> args)
    {
        var kept = new List<string>(args.Count);
        foreach (var arg in args)
        {
            if (arg is "--update" or "--check-update")
            {
                continue;
            }

            kept.Add(arg);
        }

        return kept.ToArray();
    }

    public static async Task<int> RunExplicitAsync(string[] args, CancellationToken cancellationToken)
    {
        var log = ConsoleUpdateLog.Instance;
        if (!TryBuildRequest(args, explicitRequest: true, log, out var request, out var error))
        {
            log.Error(UpdateText.Failure(explicitRequest: true, error));
            return 1;
        }

        var result = await SelfUpdater.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Status is UpdateStatus.UpToDate or UpdateStatus.Restarted or UpdateStatus.Cancelled ? 0 : 1;
    }

    public static async Task<bool> TryAutomaticAsync(string[] args, CancellationToken cancellationToken)
    {
        var log = ConsoleUpdateLog.Instance;
        if (!TryBuildRequest(args, explicitRequest: false, log, out var request, out var error))
        {
            log.Error(UpdateText.Failure(explicitRequest: false, error));
            return false;
        }

        try
        {
            var result = await SelfUpdater.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            return result.Status == UpdateStatus.Restarted;
        }
        catch (Exception ex)
        {
            log.Error(UpdateText.Failure(explicitRequest: false, UpdateText.Safe(ex)));
            return false;
        }
    }

    private static bool TryBuildRequest(
        string[] args,
        bool explicitRequest,
        IUpdateLog log,
        out SelfUpdateRequest request,
        out string error)
    {
        request = null!;
        error = "";
        if (!TryResolveInstall(out var install, out error))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(install.VersionFileRid)
            && HostRid.Detect() is { } detected
            && !install.VersionFileRid.Equals(detected, StringComparison.Ordinal))
        {
            log.Info(UpdateText.RidMismatch(install.VersionFileRid, detected));
        }

        var rid = HostRid.Detect();
        if (rid is null)
        {
            error = UpdateText.UnsupportedPlatform();
            return false;
        }

        request = new SelfUpdateRequest
        {
            LocalVersion = install.Version,
            LocalLabel = install.Label,
            Rid = rid,
            InstallDirectory = install.Directory,
            ExecutablePath = install.ExecutablePath,
            RelaunchArguments = ArgumentsForRelaunch(args),
            HasVersionFile = install.HasVersionFile,
            ExplicitRequest = explicitRequest,
            GitHubToken = ReadToken(),
            UserAgent = UserAgent(),
            MetadataTimeout = explicitRequest ? ExplicitMetadataTimeout : AutomaticMetadataTimeout,
            DownloadTimeout = explicitRequest ? ExplicitDownloadTimeout : AutomaticDownloadTimeout,
            Log = log,
        };
        return true;
    }

    private readonly record struct LocalInstall(
        SemanticVersion Version,
        string Label,
        string? VersionFileRid,
        string Directory,
        string ExecutablePath,
        bool HasVersionFile);

    private static bool TryResolveInstall(out LocalInstall install, out string error)
    {
        install = default;
        error = "";
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            error = "找不到当前可执行文件路径。";
            return false;
        }

        var directory = Path.GetDirectoryName(executable);
        if (string.IsNullOrEmpty(directory))
        {
            error = "找不到安装目录。";
            return false;
        }

        if (TryReadVersionFile(out var info, out var versionFilePath) && versionFilePath.StartsWith(directory, StringComparison.Ordinal))
        {
            if (!SemanticVersion.TryParse(info.Tag, out var parsed))
            {
                error = "VERSION.txt 里的 tag 无法解析。";
                return false;
            }

            install = new LocalInstall(parsed, info.Tag, info.Rid, directory, executable, true);
            return true;
        }

        if (SemanticVersion.TryParse(HostVersion.InformationalVersion, out var informational))
        {
            install = new LocalInstall(informational, HostVersion.InformationalVersion, null, directory, executable, false);
            return true;
        }

        if (SemanticVersion.TryParse(HostVersion.Version, out var assemblyVersion))
        {
            install = new LocalInstall(assemblyVersion, HostVersion.Version, null, directory, executable, false);
            return true;
        }

        error = "无法确定本地版本。";
        return false;
    }

    private static bool TryReadVersionFile(out VersionFile info, out string path)
    {
        info = default;
        path = "";
        var executable = Environment.ProcessPath;
        var directory = string.IsNullOrEmpty(executable) ? AppContext.BaseDirectory : Path.GetDirectoryName(executable);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        path = Path.Combine(directory, "VERSION.txt");
        if (!File.Exists(path))
        {
            return false;
        }

        return VersionFile.TryParse(File.ReadAllText(path), out info);
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
