namespace AirPlayServer.Host.Update;

internal static class UpdateText
{
    public static string Checking(string rid) =>
        "更新：正在检查 GitHub Release（huiyuanai709/AirPlayServer.NET，" + rid + "）…";

    public static string MissingVersionFile(string label) =>
        "更新：未找到 VERSION.txt，使用程序内版本 " + label + "。";

    public static string Current(string local, string remote) =>
        "更新：当前版本 " + local + "，最新稳定版 " + remote + "。";

    public const string UpToDate = "更新：已是最新稳定版，无需替换。";

    public static string Downloading(string name) =>
        "更新：发现较新版本，正在下载 " + name + "…";

    public const string HashOk = "更新：SHA256 校验通过。";

    public const string SumsMissing = "更新：该 Release 没有 SHA256SUMS.txt，已跳过校验文件并继续。";

    public const string HashMismatch = "更新：SHA256 不一致，已取消替换。";

    public const string DigestMismatch = "更新：GitHub 资源摘要不一致，已取消替换。";

    public static string Replacing(string tag) =>
        "更新：正在替换为 " + tag + "…";

    public const string Restarting = "更新：替换完成，正在用原来的参数重新启动…";

    public const string Deferred =
        "更新：正在运行的程序无法直接覆盖。退出后会替换文件并重新启动。";

    public const string Cancelled = "更新：已取消。";

    public const string NoStable = "更新：最新结果不是稳定版，已忽略。";

    public const string NotFound =
        "更新：没有找到最新稳定版 Release（HTTP 404）。公开仓库应能直接访问；私有仓库请设置 GITHUB_TOKEN 或 GH_TOKEN。";

    public const string Unauthorized =
        "更新：GitHub 拒绝了这个请求。公开仓库不需要令牌；私有仓库请设置 GITHUB_TOKEN 或 GH_TOKEN（只需读取 Release）。";

    public const string RateLimited =
        "更新：GitHub API 速率已用完。可以设置 GITHUB_TOKEN 提高限额，这次先继续跑当前版本。";

    public static string RidMismatch(string fileRid, string detected) =>
        "更新：VERSION.txt 的平台是 " + fileRid + "，当前进程是 " + detected + "。将下载 " + detected + "。";

    public static string UnsupportedPlatform() =>
        "更新：当前平台没有 win-x64 / linux-x64 发布包，已跳过。";

    public static string NoAsset(string name) =>
        "更新：Release 里没有 " + name + "，已取消。";

    public static string Permission(string directory) =>
        "更新：没有写入权限（" + directory + "）。";

    public static string RestartFailed() =>
        "更新：文件已经写入磁盘，但重新启动失败。请手动再开一次。";

    public static string Failure(bool explicitRequest, string reason) =>
        explicitRequest
            ? "更新：失败（" + reason + "）。"
            : "更新：失败（" + reason + "）。继续运行当前版本。";

    public static string Safe(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Contains("Bearer", StringComparison.OrdinalIgnoreCase)
            || message.Contains("GITHUB_TOKEN", StringComparison.Ordinal)
            || message.Contains("ghp_", StringComparison.Ordinal)
            || message.Contains("ghs_", StringComparison.Ordinal)
            || message.Contains("github_pat_", StringComparison.Ordinal))
        {
            return "请求被拒绝。";
        }

        if (message.Length > 240)
        {
            message = message[..240];
        }

        return message.Length == 0 ? exception.GetType().Name : message;
    }
}

internal interface IUpdateLog
{
    void Info(string message);

    void Error(string message);
}

internal sealed class ConsoleUpdateLog : IUpdateLog
{
    public static ConsoleUpdateLog Instance { get; } = new();

    public void Info(string message) => Console.WriteLine(message);

    public void Error(string message) => Console.Error.WriteLine(message);
}

internal sealed class NullUpdateLog : IUpdateLog
{
    public static NullUpdateLog Instance { get; } = new();

    public void Info(string message)
    {
    }

    public void Error(string message)
    {
    }
}
