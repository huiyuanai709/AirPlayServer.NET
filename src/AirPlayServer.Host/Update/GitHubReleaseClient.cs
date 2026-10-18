using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirPlayServer.Host.Update;

internal sealed record PublishedRelease(string Tag, SemanticVersion Version, IReadOnlyList<ReleaseAsset> Assets);

internal sealed record ReleaseAsset(string Name, Uri? BrowserUrl, Uri? ApiUrl, string? Sha256Digest, long Size)
{
    public IReadOnlyList<Uri> Candidates(bool hasToken)
    {
        var list = new List<Uri>(2);
        if (hasToken)
        {
            Add(ApiUrl);
            Add(BrowserUrl);
        }
        else
        {
            Add(BrowserUrl);
            Add(ApiUrl);
        }

        return list;

        void Add(Uri? uri)
        {
            if (uri is not null && !list.Contains(uri))
            {
                list.Add(uri);
            }
        }
    }
}

internal enum ReleaseQueryStatus
{
    Found,
    NoStableRelease,
    NotFound,
    Unauthorized,
    RateLimited,
    Failed,
}

internal readonly record struct ReleaseQuery(ReleaseQueryStatus Status, PublishedRelease? Release, string Detail);

internal sealed class GitHubReleaseDocument
{
    public string? TagName { get; set; }

    public bool Draft { get; set; }

    public bool Prerelease { get; set; }

    public List<GitHubAssetDocument>? Assets { get; set; }
}

internal sealed class GitHubAssetDocument
{
    public string? Name { get; set; }

    public string? BrowserDownloadUrl { get; set; }

    public string? Url { get; set; }

    public string? Digest { get; set; }

    public long Size { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitHubReleaseDocument))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class GitHubReleaseJsonContext : JsonSerializerContext;

internal sealed class GitHubReleaseClient : IDisposable
{
    public const long MaxDownloadBytes = 256L * 1024 * 1024;

    public const long MaxSumsBytes = 1024 * 1024;

    private readonly HttpClient _http;

    private readonly bool _hasToken;

    private GitHubReleaseClient(HttpClient http, bool hasToken)
    {
        _http = http;
        _hasToken = hasToken;
    }

    public static GitHubReleaseClient Create(HttpMessageHandler? handler, string? token, string userAgent)
    {
        var inner = handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var http = new HttpClient(new AllowedReleaseRedirectHandler(inner), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        var hasToken = !string.IsNullOrWhiteSpace(token);
        if (hasToken)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Trim());
        }

        return new GitHubReleaseClient(http, hasToken);
    }

    public async Task<ReleaseQuery> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseUrls.LatestRelease);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, 64 * 1024, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (body.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            {
                return new ReleaseQuery(ReleaseQueryStatus.RateLimited, null, "");
            }

            return new ReleaseQuery(ReleaseQueryStatus.Unauthorized, null, "");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ReleaseQuery(ReleaseQueryStatus.NotFound, null, "");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new ReleaseQuery(ReleaseQueryStatus.Failed, null, "HTTP " + (int)response.StatusCode);
        }

        GitHubReleaseDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(body, GitHubReleaseJsonContext.Default.GitHubReleaseDocument);
        }
        catch (JsonException ex)
        {
            return new ReleaseQuery(ReleaseQueryStatus.Failed, null, UpdateText.Safe(ex));
        }

        if (document is null || string.IsNullOrWhiteSpace(document.TagName))
        {
            return new ReleaseQuery(ReleaseQueryStatus.Failed, null, "Release JSON 没有 tag_name。");
        }

        if (document.Draft || document.Prerelease)
        {
            return new ReleaseQuery(ReleaseQueryStatus.NoStableRelease, null, document.TagName);
        }

        if (!SemanticVersion.TryParse(document.TagName, out var version) || version.PreRelease is not null)
        {
            return new ReleaseQuery(ReleaseQueryStatus.NoStableRelease, null, document.TagName);
        }

        var assets = new List<ReleaseAsset>();
        if (document.Assets is not null)
        {
            foreach (var item in document.Assets)
            {
                if (string.IsNullOrWhiteSpace(item.Name) || !IsSafeFileName(item.Name))
                {
                    continue;
                }

                Uri? browser = null;
                if (Uri.TryCreate(item.BrowserDownloadUrl, UriKind.Absolute, out var browserUri)
                    && ReleaseUrls.IsAllowedDownload(browserUri))
                {
                    browser = browserUri;
                }

                Uri? api = null;
                if (Uri.TryCreate(item.Url, UriKind.Absolute, out var apiUri) && ReleaseUrls.IsAllowedApiAsset(apiUri))
                {
                    api = apiUri;
                }

                string? digest = null;
                if (!string.IsNullOrWhiteSpace(item.Digest)
                    && item.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Sha256Sums.TryParseDigest(item.Digest, out var parsed))
                    {
                        return new ReleaseQuery(ReleaseQueryStatus.Failed, null, item.Name + " 的 sha256 摘要无法解析。");
                    }

                    digest = parsed;
                }

                if (browser is null && api is null)
                {
                    continue;
                }

                assets.Add(new ReleaseAsset(item.Name, browser, api, digest, item.Size));
            }
        }

        return new ReleaseQuery(
            ReleaseQueryStatus.Found,
            new PublishedRelease(document.TagName.Trim(), version, assets),
            "");
    }

    public async Task<string> DownloadAsync(ReleaseAsset asset, string destination, long maxBytes, CancellationToken cancellationToken)
    {
        if (asset.Size > maxBytes)
        {
            throw new IOException("发布资源过大：" + asset.Name);
        }

        var candidates = asset.Candidates(_hasToken);
        if (candidates.Count == 0)
        {
            throw new IOException("发布资源没有可用的下载地址：" + asset.Name);
        }

        Exception? last = null;
        foreach (var candidate in candidates)
        {
            try
            {
                return await DownloadOnceAsync(candidate, destination, maxBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                last = ex;
            }
        }

        throw last ?? new IOException("下载失败：" + asset.Name);
    }

    public void Dispose() => _http.Dispose();

    private async Task<string> DownloadOnceAsync(Uri uri, string destination, long maxBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                "下载失败，HTTP " + (int)response.StatusCode + "。",
                null,
                response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is long advertised && advertised > maxBytes)
        {
            throw new IOException("发布资源过大。");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maxBytes)
                {
                    throw new IOException("发布资源过大。");
                }

                hasher.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (total == 0)
            {
                throw new IOException("下载到的文件是空的。");
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, int maxChars, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (text.Length <= maxChars)
        {
            return text;
        }

        return text[..maxChars];
    }

    private static bool IsSafeFileName(string name)
    {
        if (name.Length is 0 or > 180)
        {
            return false;
        }

        if (name is "." or ".." || name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in name)
        {
            if (ch is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Follows GitHub release redirects only onto this repository and GitHub's
/// own asset hosts. The Authorization header stays on api.github.com and
/// github.com so a CDN hop cannot see the token.
/// </summary>
internal sealed class AllowedReleaseRedirectHandler : DelegatingHandler
{
    public AllowedReleaseRedirectHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !IsAllowedHop(request.RequestUri, redirect: false))
        {
            throw new InvalidOperationException("拒绝的更新地址。");
        }

        var current = request;
        for (var hop = 0; ; hop++)
        {
            var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308))
            {
                return response;
            }

            if (hop == 4)
            {
                response.Dispose();
                throw new InvalidOperationException("更新下载的重定向次数过多。");
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new InvalidOperationException("更新下载的重定向没有 Location。");
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
            if (!IsAllowedHop(next, redirect: true))
            {
                throw new InvalidOperationException("拒绝的重定向地址：" + next.Host);
            }

            var previous = current;
            current = CloneForRedirect(previous, next);
            if (!ReferenceEquals(previous, request))
            {
                previous.Dispose();
            }
        }
    }

    private static bool IsAllowedHop(Uri uri, bool redirect) =>
        redirect ? ReleaseUrls.IsAllowedRedirect(uri) : ReleaseUrls.IsAllowedLatest(uri) || ReleaseUrls.IsAllowedApiAsset(uri) || ReleaseUrls.IsAllowedDownload(uri);

    private static HttpRequestMessage CloneForRedirect(HttpRequestMessage original, Uri next)
    {
        var clone = new HttpRequestMessage(HttpMethod.Get, next);
        if (original.Headers.UserAgent.Count > 0)
        {
            foreach (var agent in original.Headers.UserAgent)
            {
                clone.Headers.UserAgent.Add(agent);
            }
        }

        if (original.Headers.Accept.Count > 0)
        {
            foreach (var accept in original.Headers.Accept)
            {
                clone.Headers.Accept.Add(accept);
            }
        }

        if (original.Headers.TryGetValues("X-GitHub-Api-Version", out var apiVersion))
        {
            clone.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", apiVersion);
        }

        var keepAuthorization = next.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            || next.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
        if (keepAuthorization && original.Headers.Authorization is { } authorization)
        {
            clone.Headers.Authorization = CopyAuthorization(authorization);
        }

        return clone;
    }

    private static AuthenticationHeaderValue CopyAuthorization(AuthenticationHeaderValue authorization) =>
        authorization.Parameter is null
            ? new AuthenticationHeaderValue(authorization.Scheme)
            : new AuthenticationHeaderValue(authorization.Scheme, authorization.Parameter);
}
