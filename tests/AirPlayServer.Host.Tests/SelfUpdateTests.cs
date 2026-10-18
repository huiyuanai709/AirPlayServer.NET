using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirPlayServer.Host;
using AirPlayServer.Host.Update;

namespace AirPlayServer.Host.Tests;

public class SelfUpdateTests
{
    [Theory]
    [InlineData("v0.1.3", "v0.1.4", -1)]
    [InlineData("v0.1.3", "v0.1.3", 0)]
    [InlineData("v0.2.0", "v0.1.9", 1)]
    [InlineData("v0.2.0-rc.1", "v0.2.0", -1)]
    [InlineData("v1.0.0", "v0.9.9", 1)]
    [InlineData("0.1.3+sha", "v0.1.3", 0)]
    [InlineData("v0.1.10", "v0.1.9", 1)]
    [InlineData("v0.1.3-rc.1", "v0.1.3-rc.2", -1)]
    [InlineData("v0.1.3-alpha", "v0.1.3-rc", -1)]
    [InlineData("v0.1.3-rc.1", "v0.1.3-rc.1.1", -1)]
    [InlineData("1.0.0.0", "v1.0.0", 0)]
    public void Semver_orders_release_tags(string leftText, string rightText, int expectedSign)
    {
        Assert.True(SemanticVersion.TryParse(leftText, out var left));
        Assert.True(SemanticVersion.TryParse(rightText, out var right));
        Assert.Equal(expectedSign, Math.Sign(left.CompareTo(right)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1")]
    [InlineData("v1.2")]
    [InlineData("not-a-version")]
    [InlineData("v1.2.3.1")]
    [InlineData("v1.2.3-")]
    public void Semver_rejects_incomplete_tags(string text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Fact]
    public void Version_file_reads_the_release_layout()
    {
        const string text = "tag=v0.1.3\nrid=linux-x64\nbinary=AirPlayServer.Host\nlicense=GPL-3.0-only\n";
        Assert.True(VersionFile.TryParse(text, out var info));
        Assert.Equal("v0.1.3", info.Tag);
        Assert.Equal("linux-x64", info.Rid);
        Assert.Equal("AirPlayServer.Host", info.Binary);
        Assert.True(SemanticVersion.TryParse(info.Tag, out var version));
        Assert.Equal(0, version.Major);
        Assert.Equal(1, version.Minor);
        Assert.Equal(3, version.Patch);
        Assert.Null(version.PreRelease);
    }

    [Fact]
    public void Asset_name_selects_the_rid_zip_and_checksum_file()
    {
        var release = new PublishedRelease("v0.1.4", Parsed("v0.1.4"),
        [
            Asset("AirPlayServer.Host-v0.1.4-win-x64.zip"),
            Asset("AirPlayServer.Host-v0.1.4-linux-x64.zip"),
            Asset("AirPlayServer.Host-v0.1.4-linux-x64.pdb"),
            Asset("SHA256SUMS.txt"),
        ]);

        Assert.Equal("AirPlayServer.Host-v0.1.4-linux-x64.zip", ReleaseAssets.ZipFileName("v0.1.4", HostRid.LinuxX64));
        Assert.True(ReleaseAssets.TrySelectZip(release, HostRid.LinuxX64, out var linux));
        Assert.Equal("AirPlayServer.Host-v0.1.4-linux-x64.zip", linux!.Name);
        Assert.True(ReleaseAssets.TrySelectZip(release, HostRid.WinX64, out var windows));
        Assert.Equal("AirPlayServer.Host-v0.1.4-win-x64.zip", windows!.Name);
        Assert.False(ReleaseAssets.TrySelectZip(release, "osx-arm64", out _));
        Assert.True(ReleaseAssets.TrySelectSums(release, out var sums));
        Assert.Equal("SHA256SUMS.txt", sums!.Name);
        Assert.False(PackageFiles.IsInstallable("AirPlayServer.Host.pdb"));
        Assert.False(PackageFiles.IsInstallable("Host.DBG"));
        Assert.True(PackageFiles.IsInstallable("AirPlayServer.Host"));
        Assert.True(PackageFiles.IsInstallable("SomeNative.dll"));
        Assert.False(PackageFiles.IsInstallable("deps.json"));
    }

    [Fact]
    public void Sha256_sums_match_a_known_digest_and_reject_a_mismatch()
    {
        const string hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        var actual = Convert.ToHexStringLower(SHA256.HashData("abc"u8.ToArray()));
        Assert.Equal(hash, actual);

        var sums = hash + "  AirPlayServer.Host-v0.1.4-linux-x64.zip\n"
            + new string('a', 64) + " *other.zip\n"
            + "# comment\n";
        Assert.True(Sha256Sums.TryFind(sums, "AirPlayServer.Host-v0.1.4-linux-x64.zip", out var found));
        Assert.True(Sha256Sums.HexEquals(found, hash.ToUpperInvariant()));
        Assert.True(Sha256Sums.TryFind(sums, "other.zip", out var starred));
        Assert.Equal(new string('a', 64), starred);
        Assert.False(Sha256Sums.HexEquals(found, new string('b', 64)));
        Assert.True(Sha256Sums.TryParseDigest("sha256:" + hash, out var digest));
        Assert.True(Sha256Sums.HexEquals(digest, hash));
        Assert.False(Sha256Sums.TryParseDigest("sha512:" + hash, out _));
    }

    [Fact]
    public void Download_urls_stay_on_this_repository()
    {
        Assert.True(ReleaseUrls.IsAllowedLatest(ReleaseUrls.LatestRelease));
        Assert.True(ReleaseUrls.IsAllowedDownload(new Uri(
            "https://github.com/huiyuanai709/AirPlayServer.NET/releases/download/v0.1.3/AirPlayServer.Host-v0.1.3-linux-x64.zip")));
        Assert.True(ReleaseUrls.IsAllowedApiAsset(new Uri(
            "https://api.github.com/repos/huiyuanai709/AirPlayServer.NET/releases/assets/652974937")));
        Assert.True(ReleaseUrls.IsAllowedRedirect(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/file")));
        Assert.True(ReleaseUrls.IsAllowedRedirect(new Uri("https://objects.githubusercontent.com/github-production-release-asset/file")));

        Assert.False(ReleaseUrls.IsAllowedDownload(new Uri("http://github.com/huiyuanai709/AirPlayServer.NET/releases/download/v0.1.3/a.zip")));
        Assert.False(ReleaseUrls.IsAllowedDownload(new Uri("https://github.com/other/AirPlayServer.NET/releases/download/v0.1.3/a.zip")));
        Assert.False(ReleaseUrls.IsAllowedDownload(new Uri("https://example.com/a.zip")));
        Assert.False(ReleaseUrls.IsAllowedDownload(new Uri("https://user:pass@github.com/huiyuanai709/AirPlayServer.NET/releases/download/v0.1.3/a.zip")));
        Assert.False(ReleaseUrls.IsAllowedDownload(new Uri(
            "https://github.com/huiyuanai709/AirPlayServer.NET/releases/download/v0.1.3/../../other/payload")));
        Assert.False(ReleaseUrls.IsAllowedCdn(new Uri("https://evil.githubusercontent.com.example/file")));
        Assert.False(ReleaseUrls.IsAllowedDownload(new Uri("https://release-assets.githubusercontent.com/file")));
        Assert.True(ReleaseUrls.IsAllowedRedirect(new Uri("https://release-assets.githubusercontent.com/file")));
    }

    [Fact]
    public async Task Redirect_to_an_untrusted_host_is_refused_and_drops_the_token()
    {
        var stub = new ScriptedHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example/payload");
            return response;
        });
        using var handler = new AllowedReleaseRedirectHandler(stub);
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://github.com/huiyuanai709/AirPlayServer.NET/releases/download/v0.1.3/SHA256SUMS.txt");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");

        var thrown = await Record.ExceptionAsync(() => client.SendAsync(request));
        Assert.NotNull(thrown);
        Assert.Contains("evil.example", thrown.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", thrown.ToString(), StringComparison.Ordinal);
        Assert.Single(stub.Uris);
    }

    [Fact]
    public async Task Cdn_redirect_does_not_receive_the_token()
    {
        var stub = new ScriptedHandler(request =>
        {
            if (request.RequestUri!.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://release-assets.githubusercontent.com/asset/sums");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("ok"u8.ToArray()),
            };
        });
        using var handler = new AllowedReleaseRedirectHandler(stub);
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        using var response = await client.GetAsync(
            "https://github.com/huiyuanai709/AirPlayServer.NET/releases/download/v0.1.3/SHA256SUMS.txt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Uris.Count);
        Assert.True(stub.SawAuthorization[0]);
        Assert.False(stub.SawAuthorization[1]);
        Assert.Equal("release-assets.githubusercontent.com", stub.Uris[1].Host);
    }

    [Fact]
    public void Release_json_reads_snake_case_asset_fields()
    {
        const string json = """
            {
              "tag_name": "v0.1.4",
              "draft": false,
              "prerelease": false,
              "assets": [
                {
                  "name": "AirPlayServer.Host-v0.1.4-linux-x64.zip",
                  "browser_download_url": "https://github.com/huiyuanai709/AirPlayServer.NET/releases/download/v0.1.4/AirPlayServer.Host-v0.1.4-linux-x64.zip",
                  "url": "https://api.github.com/repos/huiyuanai709/AirPlayServer.NET/releases/assets/15",
                  "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "size": 12
                }
              ]
            }
            """;
        var document = JsonSerializer.Deserialize(json, GitHubReleaseJsonContext.Default.GitHubReleaseDocument);
        Assert.NotNull(document);
        Assert.Equal("v0.1.4", document.TagName);
        Assert.NotNull(document.Assets);
        Assert.Equal("AirPlayServer.Host-v0.1.4-linux-x64.zip", document.Assets[0].Name);
        Assert.Contains("/releases/download/", document.Assets[0].BrowserDownloadUrl, StringComparison.Ordinal);
        Assert.Contains("/releases/assets/15", document.Assets[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Relaunch_arguments_drop_the_update_flags_and_keep_order()
    {
        var kept = HostUpdate.ArgumentsForRelaunch(
            ["--name", "客厅", "--update", "--screen", "480x1040", "--check-update"]);
        Assert.Equal(["--name", "客厅", "--screen", "480x1040"], kept);
    }

    [Fact]
    public void Windows_helper_waits_for_the_parent_and_does_not_embed_a_token()
    {
        Assert.Contains("Wait-Process", WindowsUpdateHandoff.Script, StringComparison.Ordinal);
        Assert.Contains("AIRPLAYSERVER_JUST_UPDATED", WindowsUpdateHandoff.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_TOKEN", WindowsUpdateHandoff.Script, StringComparison.Ordinal);
        var encoded = WindowsUpdateHandoff.EncodeArguments(["--name", "客厅"]);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        var args = JsonSerializer.Deserialize(json, GitHubReleaseJsonContext.Default.StringArray);
        Assert.NotNull(args);
        Assert.Equal(new[] { "--name", "客厅" }, args);
    }

    [Fact]
    public void Zip_paths_that_escape_the_package_are_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "airplay-zip-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var zip = Path.Combine(root, "bad.zip");
            WriteZip(zip, ("../evil.txt", "nope"));
            var error = Assert.Throws<InvalidDataException>(() => PackageZip.Extract(zip, Path.Combine(root, "out"), "AirPlayServer.Host"));
            Assert.Contains("不安全", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
            Assert.Null(PackageZip.CombineSafe(root, "../evil.txt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Newer_release_replaces_the_install_and_skips_symbols()
    {
        using var fixture = new UpdateFixture("v0.1.0", "v0.1.4", includeSums: true, mismatchSums: false, includeDigest: true);
        var relaunch = new RecordingRelauncher();
        var log = new ListLog();
        var result = await SelfUpdater.ExecuteAsync(fixture.Request(relaunch, log), CancellationToken.None);

        Assert.Equal(UpdateStatus.Restarted, result.Status);
        Assert.Equal("new-binary", File.ReadAllText(fixture.ExecutablePath));
        Assert.Contains("tag=v0.1.4", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "VERSION.txt")), StringComparison.Ordinal);
        Assert.Equal("license-new", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "LICENSE")));
        Assert.False(File.Exists(Path.Combine(fixture.InstallDirectory, "AirPlayServer.Host.pdb")));
        Assert.Equal(fixture.ExecutablePath, relaunch.Executable);
        Assert.Equal(["--name", "客厅"], relaunch.Arguments);
        Assert.Contains(log.Lines, line => line.Contains("SHA256 校验通过", StringComparison.Ordinal));
        if (OperatingSystem.IsLinux())
        {
            Assert.True(File.GetUnixFileMode(fixture.ExecutablePath).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public async Task Hash_mismatch_leaves_the_current_binary_in_place()
    {
        using var fixture = new UpdateFixture("v0.1.0", "v0.1.4", includeSums: true, mismatchSums: true, includeDigest: false);
        var relaunch = new RecordingRelauncher();
        var log = new ListLog();
        var result = await SelfUpdater.ExecuteAsync(fixture.Request(relaunch, log), CancellationToken.None);

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Equal("old-binary", File.ReadAllText(fixture.ExecutablePath));
        Assert.Contains("tag=v0.1.0", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "VERSION.txt")), StringComparison.Ordinal);
        Assert.Null(relaunch.Executable);
        Assert.Contains(log.Lines, line => line.Contains("SHA256 不一致", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_checksum_file_still_installs_and_says_so()
    {
        using var fixture = new UpdateFixture("v0.1.0", "v0.1.4", includeSums: false, mismatchSums: false, includeDigest: false);
        var log = new ListLog();
        var result = await SelfUpdater.ExecuteAsync(fixture.Request(new RecordingRelauncher(), log), CancellationToken.None);

        Assert.Equal(UpdateStatus.Restarted, result.Status);
        Assert.Equal("new-binary", File.ReadAllText(fixture.ExecutablePath));
        Assert.Contains(log.Lines, line => line.Contains("没有 SHA256SUMS.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Current_and_prerelease_tags_do_not_download()
    {
        using var current = new UpdateFixture("v0.1.4", "v0.1.4", includeSums: true, mismatchSums: false, includeDigest: false);
        current.Handler.FailDownloads = true;
        var currentResult = await SelfUpdater.ExecuteAsync(current.Request(new RecordingRelauncher(), new ListLog()), CancellationToken.None);
        Assert.Equal(UpdateStatus.UpToDate, currentResult.Status);
        Assert.Equal("old-binary", File.ReadAllText(current.ExecutablePath));

        using var preview = new UpdateFixture("v0.1.0", "v0.2.0-rc.1", includeSums: false, mismatchSums: false, includeDigest: false, prerelease: true);
        preview.Handler.FailDownloads = true;
        var log = new ListLog();
        var previewResult = await SelfUpdater.ExecuteAsync(preview.Request(new RecordingRelauncher(), log), CancellationToken.None);
        Assert.Equal(UpdateStatus.UpToDate, previewResult.Status);
        Assert.Contains(log.Lines, line => line.Contains("不是稳定版", StringComparison.Ordinal));
        Assert.Equal("old-binary", File.ReadAllText(preview.ExecutablePath));
    }

    [Fact]
    public async Task Missing_release_does_not_throw()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Not Found"}""", Encoding.UTF8, "application/json"),
        });
        var directory = Path.Combine(Path.GetTempPath(), "airplay-update-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "AirPlayServer.Host");
        File.WriteAllText(executable, "old-binary");
        var log = new ListLog();
        try
        {
            var result = await SelfUpdater.ExecuteAsync(new SelfUpdateRequest
            {
                LocalVersion = Parsed("v0.1.0"),
                LocalLabel = "v0.1.0",
                Rid = HostRid.LinuxX64,
                InstallDirectory = directory,
                ExecutablePath = executable,
                RelaunchArguments = [],
                HasVersionFile = true,
                HttpHandler = handler,
                Relauncher = new RecordingRelauncher(),
                Log = log,
            }, CancellationToken.None);

            Assert.Equal(UpdateStatus.Failed, result.Status);
            Assert.Contains(log.Lines, line => line.Contains("404", StringComparison.Ordinal));
            Assert.Contains(log.Lines, line => line.Contains("GITHUB_TOKEN", StringComparison.Ordinal));
            Assert.Equal("old-binary", File.ReadAllText(executable));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Baked_version_constants_are_present()
    {
        Assert.False(string.IsNullOrWhiteSpace(HostVersion.InformationalVersion));
        Assert.False(string.IsNullOrWhiteSpace(HostVersion.Version));
        var agent = HostUpdate.UserAgent();
        Assert.StartsWith("AirPlayServer.Host/", agent, StringComparison.Ordinal);
        using var headers = new HttpRequestMessage();
        headers.Headers.UserAgent.ParseAdd(agent);
    }

    private static SemanticVersion Parsed(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        return version;
    }

    private static ReleaseAsset Asset(string name) =>
        new(name, new Uri("https://github.com/huiyuanai709/AirPlayServer.NET/releases/download/v0.1.4/" + name), null, null, 1);

    private static void WriteZip(string path, params (string Name, string Content)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    private sealed class UpdateFixture : IDisposable
    {
        public UpdateFixture(
            string localTag,
            string remoteTag,
            bool includeSums,
            bool mismatchSums,
            bool includeDigest,
            bool prerelease = false)
        {
            InstallDirectory = Path.Combine(Path.GetTempPath(), "airplay-update-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(InstallDirectory);
            ExecutablePath = Path.Combine(InstallDirectory, "AirPlayServer.Host");
            File.WriteAllText(ExecutablePath, "old-binary");
            File.WriteAllText(Path.Combine(InstallDirectory, "VERSION.txt"), "tag=" + localTag + "\nrid=linux-x64\nbinary=AirPlayServer.Host\n");
            File.WriteAllText(Path.Combine(InstallDirectory, "LICENSE"), "license-old");

            var zipName = ReleaseAssets.ZipFileName(remoteTag, HostRid.LinuxX64);
            var prefix = Path.GetFileNameWithoutExtension(zipName);
            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(archive, prefix + "/VERSION.txt", "tag=" + remoteTag + "\nrid=linux-x64\nbinary=AirPlayServer.Host\nlicense=GPL-3.0-only\n");
                Write(archive, prefix + "/LICENSE", "license-new");
                Write(archive, prefix + "/NOTICE.md", "notice");
                Write(archive, prefix + "/RUN.txt", "run");
                Write(archive, prefix + "/AirPlayServer.Host", "new-binary");
                Write(archive, prefix + "/AirPlayServer.Host.pdb", "symbols");
                Write(archive, prefix + "/libnative.so", "so");
            }

            var zipBytes = zipStream.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
            var sumsHash = mismatchSums ? new string('0', 64) : hash;
            var sumsText = sumsHash + "  " + zipName + "\n";
            var sumsBytes = Encoding.UTF8.GetBytes(sumsText);

            var assetJson = new StringBuilder();
            assetJson.Append("{\"name\":\"").Append(zipName).Append('"');
            assetJson.Append(",\"browser_download_url\":\"https://github.com/huiyuanai709/AirPlayServer.NET/releases/download/")
                .Append(remoteTag).Append('/').Append(zipName).Append('"');
            if (includeDigest)
            {
                assetJson.Append(",\"digest\":\"sha256:").Append(hash).Append('"');
            }

            assetJson.Append(",\"size\":").Append(zipBytes.Length).Append('}');
            if (includeSums)
            {
                assetJson.Append(",{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://github.com/huiyuanai709/AirPlayServer.NET/releases/download/")
                    .Append(remoteTag).Append("/SHA256SUMS.txt\",\"size\":").Append(sumsBytes.Length).Append('}');
            }

            var body = "{\"tag_name\":\"" + remoteTag + "\",\"draft\":false,\"prerelease\":"
                + (prerelease ? "true" : "false") + ",\"assets\":[" + assetJson + "]}";
            Handler = new ScriptedHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                }

                if (Handler is { FailDownloads: true })
                {
                    throw new InvalidOperationException("download was not expected");
                }

                if (path.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(sumsBytes) };
                }

                if (path.EndsWith("/" + zipName, StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            LocalTag = localTag;
        }

        public string InstallDirectory { get; }

        public string ExecutablePath { get; }

        public string LocalTag { get; }

        public ScriptedHandler Handler { get; }

        public SelfUpdateRequest Request(IRelauncher relauncher, IUpdateLog log) => new()
        {
            LocalVersion = Parsed(LocalTag),
            LocalLabel = LocalTag,
            Rid = HostRid.LinuxX64,
            InstallDirectory = InstallDirectory,
            ExecutablePath = ExecutablePath,
            RelaunchArguments = ["--name", "客厅"],
            HasVersionFile = true,
            HttpHandler = Handler,
            Relauncher = relauncher,
            Log = log,
            UserAgent = "AirPlayServer.Host/test",
        };

        public void Dispose()
        {
            if (Directory.Exists(InstallDirectory))
            {
                Directory.Delete(InstallDirectory, recursive: true);
            }
        }

        private static void Write(ZipArchive archive, string name, string content)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _next;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> next) => _next = next;

        public List<Uri> Uris { get; } = [];

        public List<bool> SawAuthorization { get; } = [];

        public bool FailDownloads { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            SawAuthorization.Add(request.Headers.Authorization is not null);
            return Task.FromResult(_next(request));
        }
    }

    private sealed class RecordingRelauncher : IRelauncher
    {
        public string? Executable { get; private set; }

        public IReadOnlyList<string>? Arguments { get; private set; }

        public void Relaunch(string executable, IReadOnlyList<string> arguments)
        {
            Executable = executable;
            Arguments = arguments.ToArray();
        }
    }

    private sealed class ListLog : IUpdateLog
    {
        public List<string> Lines { get; } = [];

        public void Info(string message) => Lines.Add(message);

        public void Error(string message) => Lines.Add(message);
    }
}
