namespace AirPlayServer.Host.Update;

internal static class ReleaseUrls
{
    public const string Owner = "huiyuanai709";

    public const string Repository = "AirPlayServer.NET";

    public static readonly Uri LatestRelease = new(
        "https://api.github.com/repos/huiyuanai709/AirPlayServer.NET/releases/latest");

    public static bool IsAllowedLatest(Uri uri) =>
        IsHttps(uri)
        && HostEquals(uri, "api.github.com")
        && uri.AbsolutePath.Equals("/repos/huiyuanai709/AirPlayServer.NET/releases/latest", StringComparison.Ordinal);

    public static bool IsAllowedApiAsset(Uri uri)
    {
        if (!IsHttps(uri) || !HostEquals(uri, "api.github.com"))
        {
            return false;
        }

        const string Prefix = "/repos/huiyuanai709/AirPlayServer.NET/releases/assets/";
        if (!uri.AbsolutePath.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var id = uri.AbsolutePath.AsSpan(Prefix.Length);
        if (id.IsEmpty)
        {
            return false;
        }

        foreach (var ch in id)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsAllowedDownload(Uri uri)
    {
        if (!IsHttps(uri) || !HostEquals(uri, "github.com"))
        {
            return false;
        }

        const string Prefix = "/huiyuanai709/AirPlayServer.NET/releases/download/";
        var path = uri.AbsolutePath;
        if (!path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = path.AsSpan(Prefix.Length);
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
        {
            return false;
        }

        return !rest.Contains("..".AsSpan(), StringComparison.Ordinal);
    }

    public static bool IsAllowedRedirect(Uri uri) =>
        IsAllowedLatest(uri) || IsAllowedApiAsset(uri) || IsAllowedDownload(uri) || IsAllowedCdn(uri);

    public static bool IsAllowedCdn(Uri uri)
    {
        if (!IsHttps(uri))
        {
            return false;
        }

        return HostEquals(uri, "release-assets.githubusercontent.com")
            || HostEquals(uri, "objects.githubusercontent.com")
            || HostEquals(uri, "github-releases.githubusercontent.com");
    }

    private static bool IsHttps(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Port == 443;

    private static bool HostEquals(Uri uri, string host) =>
        uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);
}
