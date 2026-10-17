using System.Text;

namespace AirPlayServer.Core.Protocol;

internal enum HttpParseStatus
{
    NeedMore = 0,
    Ok = 1,
    Fatal = 2,
}

internal sealed class Http1Request
{
    public const int MaxHeaderBytes = 8 * 1024;
    public const int MaxBodyBytes = 256 * 1024;

    private Http1Request()
    {
        Protocol = string.Empty;
        Method = string.Empty;
        Target = string.Empty;
        Path = string.Empty;
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Body = [];
    }

    public required string Protocol { get; init; }

    public required string Method { get; init; }

    public required string Target { get; init; }

    public required string Path { get; init; }

    public required Dictionary<string, string> Headers { get; init; }

    public required byte[] Body { get; init; }

    public static HttpParseStatus TryParse(ReadOnlySpan<byte> buffer, out Http1Request? request, out int consumed)
    {
        request = null;
        consumed = 0;
        var headerEnd = buffer.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0)
        {
            return buffer.Length > MaxHeaderBytes ? HttpParseStatus.Fatal : HttpParseStatus.NeedMore;
        }

        if (headerEnd > MaxHeaderBytes)
        {
            return HttpParseStatus.Fatal;
        }

        var headerText = Encoding.ASCII.GetString(buffer[..headerEnd]);
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0)
        {
            return HttpParseStatus.Fatal;
        }

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || requestLine[0].Length == 0 || requestLine[1].Length == 0 || requestLine[2].Length == 0)
        {
            return HttpParseStatus.Fatal;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                return HttpParseStatus.Fatal;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                return HttpParseStatus.Fatal;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Length == 0)
            {
                return HttpParseStatus.Fatal;
            }

            if (headers.TryGetValue(name, out var existing) && name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && existing != value)
            {
                return HttpParseStatus.Fatal;
            }

            headers[name] = value;
        }

        if (headers.ContainsKey("Transfer-Encoding"))
        {
            return HttpParseStatus.Fatal;
        }

        var bodyLength = 0;
        if (headers.TryGetValue("Content-Length", out var contentLength)
            && (!int.TryParse(contentLength, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out bodyLength) || bodyLength < 0 || bodyLength > MaxBodyBytes))
        {
            return HttpParseStatus.Fatal;
        }

        var total = headerEnd + 4 + bodyLength;
        if (buffer.Length < total)
        {
            return HttpParseStatus.NeedMore;
        }

        consumed = total;
        request = new Http1Request
        {
            Protocol = requestLine[2],
            Method = requestLine[0],
            Target = requestLine[1],
            Path = PathOf(requestLine[1]),
            Headers = headers,
            Body = buffer.Slice(headerEnd + 4, bodyLength).ToArray(),
        };
        return HttpParseStatus.Ok;
    }

    private static string PathOf(string target)
    {
        if (target == "*")
        {
            return "*";
        }

        var path = target;
        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
        {
            path = absolute.AbsolutePath;
        }

        var query = path.IndexOf('?');
        return query >= 0 ? path[..query] : path;
    }
}

internal readonly record struct Http1Response(
    int Status,
    string Reason,
    byte[] Body,
    string? ContentType,
    bool Close,
    bool SwitchToReverse,
    IReadOnlyList<(string Name, string Value)> ExtraHeaders)
{
    public static Http1Response Bytes(int status, string? contentType, byte[] body, bool close = false, bool reverse = false, IReadOnlyList<(string Name, string Value)>? extra = null)
        => new(status, ReasonFor(status), body, contentType, close, reverse, extra ?? []);

    public static Http1Response Text(int status, string message, bool close = false)
        => Bytes(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(message), close);

    public static Http1Response Empty(int status, bool close = false, bool reverse = false, IReadOnlyList<(string, string)>? extra = null)
        => Bytes(status, null, [], close, reverse, extra);

    private static string ReasonFor(int status) => status switch
    {
        101 => "Switching Protocols",
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        501 => "Not Implemented",
        _ => "OK",
    };
}
