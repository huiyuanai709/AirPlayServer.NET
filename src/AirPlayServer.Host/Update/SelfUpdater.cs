using System.Text.Json;

namespace AirPlayServer.Host.Update;

internal enum UpdateStatus
{
    UpToDate,
    Restarted,
    Failed,
    Cancelled,
}

internal readonly record struct SelfUpdateResult(UpdateStatus Status, string Detail);

internal sealed class SelfUpdateRequest
{
    public required SemanticVersion LocalVersion { get; init; }

    public required string LocalLabel { get; init; }

    public required string Rid { get; init; }

    public required string InstallDirectory { get; init; }

    public required string ExecutablePath { get; init; }

    public required IReadOnlyList<string> RelaunchArguments { get; init; }

    public bool HasVersionFile { get; init; }

    public bool ExplicitRequest { get; init; }

    public HttpMessageHandler? HttpHandler { get; init; }

    public string? GitHubToken { get; init; }

    public IRelauncher? Relauncher { get; init; }

    public IUpdateLog? Log { get; init; }

    public string UserAgent { get; init; } = "AirPlayServer.Host/test";

    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(3);
}

internal static class SelfUpdater
{
    public static async Task<SelfUpdateResult> ExecuteAsync(SelfUpdateRequest request, CancellationToken cancellationToken)
    {
        var log = request.Log ?? NullUpdateLog.Instance;
        var relauncher = request.Relauncher ?? ProcessRelauncher.Instance;
        if (!request.HasVersionFile)
        {
            log.Info(UpdateText.MissingVersionFile(request.LocalLabel));
        }

        log.Info(UpdateText.Checking(request.Rid));
        var handoff = false;
        string? tempRoot = null;
        try
        {
            using var http = GitHubReleaseClient.Create(request.HttpHandler, request.GitHubToken, request.UserAgent);
            ReleaseQuery query;
            using (var metadata = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                metadata.CancelAfter(request.MetadataTimeout);
                query = await http.GetLatestAsync(metadata.Token).ConfigureAwait(false);
            }

            switch (query.Status)
            {
                case ReleaseQueryStatus.NoStableRelease:
                    log.Info(UpdateText.NoStable);
                    return new SelfUpdateResult(UpdateStatus.UpToDate, query.Detail);
                case ReleaseQueryStatus.NotFound:
                    return Fail(log, request, UpdateText.NotFound);
                case ReleaseQueryStatus.Unauthorized:
                    return Fail(log, request, UpdateText.Unauthorized);
                case ReleaseQueryStatus.RateLimited:
                    return Fail(log, request, UpdateText.RateLimited);
                case ReleaseQueryStatus.Failed:
                    return Fail(log, request, string.IsNullOrWhiteSpace(query.Detail) ? "无法读取 Release。" : query.Detail);
                case ReleaseQueryStatus.Found when query.Release is null:
                    return Fail(log, request, "无法读取 Release。");
            }

            var release = query.Release!;
            log.Info(UpdateText.Current(request.LocalLabel, release.Tag));
            if (release.Version.CompareTo(request.LocalVersion) <= 0)
            {
                log.Info(UpdateText.UpToDate);
                return new SelfUpdateResult(UpdateStatus.UpToDate, release.Tag);
            }

            if (!ReleaseAssets.TrySelectZip(release, request.Rid, out var zip) || zip is null)
            {
                return Fail(log, request, UpdateText.NoAsset(ReleaseAssets.ZipFileName(release.Tag, request.Rid)));
            }

            PackageSwap.EnsureWritable(request.InstallDirectory);

            var temp = Directory.CreateTempSubdirectory("airplayserver-update-");
            tempRoot = temp.FullName;
            using var download = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            download.CancelAfter(request.DownloadTimeout);

            string? expectedHash = null;
            if (ReleaseAssets.TrySelectSums(release, out var sums) && sums is not null)
            {
                var sumsPath = Path.Combine(temp.FullName, ReleaseAssets.SumsFileName);
                var sumsHash = await http.DownloadAsync(sums, sumsPath, GitHubReleaseClient.MaxSumsBytes, download.Token).ConfigureAwait(false);
                if (sums.Sha256Digest is not null && !Sha256Sums.HexEquals(sumsHash, sums.Sha256Digest))
                {
                    return Fail(log, request, UpdateText.DigestMismatch);
                }

                var sumsText = File.ReadAllText(sumsPath);
                if (!Sha256Sums.TryFind(sumsText, zip.Name, out var found))
                {
                    return Fail(log, request, "SHA256SUMS.txt 里没有 " + zip.Name + "。");
                }

                expectedHash = found;
            }
            else if (zip.Sha256Digest is null)
            {
                log.Info(UpdateText.SumsMissing);
            }

            log.Info(UpdateText.Downloading(zip.Name));
            var zipPath = Path.Combine(temp.FullName, zip.Name);
            var actualHash = await http.DownloadAsync(zip, zipPath, GitHubReleaseClient.MaxDownloadBytes, download.Token).ConfigureAwait(false);
            if (expectedHash is not null && !Sha256Sums.HexEquals(actualHash, expectedHash))
            {
                return Fail(log, request, UpdateText.HashMismatch);
            }

            if (zip.Sha256Digest is not null && !Sha256Sums.HexEquals(actualHash, zip.Sha256Digest))
            {
                return Fail(log, request, UpdateText.DigestMismatch);
            }

            if (expectedHash is not null || zip.Sha256Digest is not null)
            {
                log.Info(UpdateText.HashOk);
            }

            var extractRoot = Path.Combine(temp.FullName, "pkg");
            var packageDirectory = PackageZip.Extract(zipPath, extractRoot, ReleaseAssets.BinaryName(request.Rid));
            log.Info(UpdateText.Replacing(release.Tag));
            try
            {
                PackageSwap.Apply(packageDirectory, request.InstallDirectory, request.ExecutablePath, ReleaseAssets.BinaryName(request.Rid));
            }
            catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
            {
                log.Info(UpdateText.Deferred);
                WindowsUpdateHandoff.Start(
                    packageDirectory,
                    request.InstallDirectory,
                    request.ExecutablePath,
                    temp.FullName,
                    request.RelaunchArguments);
                handoff = true;
                return new SelfUpdateResult(UpdateStatus.Restarted, release.Tag);
            }

            log.Info(UpdateText.Restarting);
            try
            {
                relauncher.Relaunch(request.ExecutablePath, request.RelaunchArguments);
            }
            catch (Exception ex)
            {
                return Fail(log, request, UpdateText.RestartFailed() + " " + UpdateText.Safe(ex));
            }

            return new SelfUpdateResult(UpdateStatus.Restarted, release.Tag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.Info(UpdateText.Cancelled);
            return new SelfUpdateResult(UpdateStatus.Cancelled, "已取消");
        }
        catch (OperationCanceledException)
        {
            return Fail(log, request, "等待超时");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Fail(log, request, UpdateText.Safe(ex));
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or InvalidDataException or JsonException)
        {
            return Fail(log, request, UpdateText.Safe(ex));
        }
        finally
        {
            if (!handoff && tempRoot is not null)
            {
                TryDeleteDirectory(tempRoot);
            }
        }
    }

    private static SelfUpdateResult Fail(IUpdateLog log, SelfUpdateRequest request, string reason)
    {
        log.Error(reason.StartsWith("更新：", StringComparison.Ordinal)
            ? reason
            : UpdateText.Failure(request.ExplicitRequest, reason));
        return new SelfUpdateResult(UpdateStatus.Failed, reason);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
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
