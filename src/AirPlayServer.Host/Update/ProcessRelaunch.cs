using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AirPlayServer.Host.Update;

internal interface IRelauncher
{
    void Relaunch(string executable, IReadOnlyList<string> arguments);
}

internal sealed class ProcessRelauncher : IRelauncher
{
    public static ProcessRelauncher Instance { get; } = new();

    public void Relaunch(string executable, IReadOnlyList<string> arguments)
    {
        if (OperatingSystem.IsLinux())
        {
            // Native AOT does not call setenv. Exec passes the marker through execve.
            Environment.SetEnvironmentVariable(HostUpdate.JustUpdatedVariable, "1");
            try
            {
                LinuxProcessImage.Exec(executable, arguments);
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or EntryPointNotFoundException)
            {
                Console.Error.WriteLine("更新：exec 失败，改为启动新进程。 " + UpdateText.Safe(ex));
            }
        }

        Spawn(executable, arguments);
    }

    internal static void Spawn(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[HostUpdate.JustUpdatedVariable] = "1";
        if (Process.Start(start) is null)
        {
            throw new IOException("未能启动新进程。");
        }
    }
}

internal static class WindowsUpdateHandoff
{
    internal const string Script = """
        param(
          [Parameter(Mandatory = $true)][int]$ParentId,
          [Parameter(Mandatory = $true)][string]$PackageDir,
          [Parameter(Mandatory = $true)][string]$InstallDir,
          [Parameter(Mandatory = $true)][string]$Executable,
          [Parameter(Mandatory = $true)][string]$CleanupDir
        )
        $ErrorActionPreference = 'Stop'
        try { Wait-Process -Id $ParentId -Timeout 180 -ErrorAction SilentlyContinue } catch {}
        Start-Sleep -Seconds 1
        Get-ChildItem -LiteralPath $PackageDir -File | ForEach-Object {
          Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $InstallDir $_.Name) -Force
        }
        $staged = Join-Path $PackageDir 'AirPlayServer.Host.exe'
        if (Test-Path -LiteralPath $staged) {
          Copy-Item -LiteralPath $staged -Destination $Executable -Force
        }
        $argList = @()
        if ($env:AIRPLAYSERVER_UPDATE_ARGS) {
          $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:AIRPLAYSERVER_UPDATE_ARGS))
          $parsed = ConvertFrom-Json $json
          if ($null -ne $parsed) { $argList = @($parsed) }
        }
        Remove-Item Env:AIRPLAYSERVER_UPDATE_ARGS -ErrorAction SilentlyContinue
        $env:AIRPLAYSERVER_JUST_UPDATED = '1'
        Start-Process -FilePath $Executable -WorkingDirectory $InstallDir -ArgumentList $argList
        Remove-Item -LiteralPath $CleanupDir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;

    public static void Start(
        string packageDirectory,
        string installDirectory,
        string executablePath,
        string cleanupDirectory,
        IReadOnlyList<string> arguments)
    {
        if (IsInside(cleanupDirectory, installDirectory) || IsInside(installDirectory, cleanupDirectory))
        {
            throw new IOException("临时目录和安装目录重叠。");
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), "airplayserver-update-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".ps1");
        File.WriteAllText(scriptPath, Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
        {
            throw new IOException("找不到 powershell.exe，无法在退出后替换正在运行的程序。");
        }

        var start = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(scriptPath);
        start.ArgumentList.Add("-ParentId");
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-PackageDir");
        start.ArgumentList.Add(packageDirectory);
        start.ArgumentList.Add("-InstallDir");
        start.ArgumentList.Add(installDirectory);
        start.ArgumentList.Add("-Executable");
        start.ArgumentList.Add(executablePath);
        start.ArgumentList.Add("-CleanupDir");
        start.ArgumentList.Add(cleanupDirectory);
        start.Environment["AIRPLAYSERVER_UPDATE_ARGS"] = EncodeArguments(arguments);
        if (Process.Start(start) is null)
        {
            throw new IOException("未能启动更新辅助脚本。");
        }
    }

    internal static string EncodeArguments(IReadOnlyList<string> arguments)
    {
        var json = JsonSerializer.Serialize(arguments.ToArray(), GitHubReleaseJsonContext.Default.StringArray);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static bool IsInside(string parent, string child)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullChild = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullChild.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }
}
