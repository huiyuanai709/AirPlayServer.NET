using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlayServer.Core;
using AirPlayServer.Core.Pairing;
using AirPlayServer.Core.Protocol;

namespace AirPlayServer.Core.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Binary_plist_round_trips_nested_receiver_info()
    {
        var root = new PlistDictionary();
        root.Add("name", new PlistString("客厅"));
        root.Add("features", new PlistInteger(AirPlayCompatibility.FeaturesLow));
        root.Add("ready", new PlistBool(false));
        root.Add("pk", new PlistData([1, 2, 3, 4]));
        root.Add("refreshRate", new PlistReal(1d / 60d));
        var displays = new PlistArray();
        var display = new PlistDictionary();
        display.Add("width", new PlistInteger(1920));
        displays.Add(display);
        root.Add("displays", displays);

        var parsed = BinaryPropertyList.Read(BinaryPropertyList.Write(root));
        var dictionary = Assert.IsType<PlistDictionary>(parsed);
        Assert.Equal("客厅", Assert.IsType<PlistString>(dictionary.Require("name")).Value);
        Assert.Equal(AirPlayCompatibility.FeaturesLow, Assert.IsType<PlistInteger>(dictionary.Require("features")).Value);
        Assert.False(Assert.IsType<PlistBool>(dictionary.Require("ready")).Value);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Assert.IsType<PlistData>(dictionary.Require("pk")).Value);
        Assert.Equal(1d / 60d, Assert.IsType<PlistReal>(dictionary.Require("refreshRate")).Value);
        var width = Assert.IsType<PlistDictionary>(Assert.IsType<PlistArray>(dictionary.Require("displays")).Items[0]);
        Assert.Equal(1920, Assert.IsType<PlistInteger>(width.Require("width")).Value);
    }

    [Fact]
    public void Http_parser_reads_one_request_and_leaves_the_next()
    {
        var raw = Encoding.ASCII.GetBytes("POST /pair-setup HTTP/1.1\r\nCSeq: 7\r\nContent-Length: 4\r\n\r\npingNEXT");
        var status = Http1Request.TryParse(raw, out var request, out var consumed);

        Assert.Equal(HttpParseStatus.Ok, status);
        Assert.NotNull(request);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/pair-setup", request.Path);
        Assert.Equal("7", request.Headers["CSeq"]);
        Assert.Equal("ping"u8.ToArray(), request.Body);
        Assert.Equal("NEXT", Encoding.ASCII.GetString(raw.AsSpan(consumed)));
    }

    [Fact]
    public void Http_parser_waits_for_the_body()
    {
        var raw = "GET /server-info RTSP/1.0\r\nContent-Length: 2\r\n\r\n"u8;
        Assert.Equal(HttpParseStatus.NeedMore, Http1Request.TryParse(raw[..^1], out _, out _));
        Assert.Equal(HttpParseStatus.Fatal, Http1Request.TryParse("GET /\r\n\r\n"u8, out _, out _));
    }

    [Fact]
    public async Task Control_connection_completes_pairing_and_refuses_fairplay()
    {
        var seed = new byte[32];
        seed[31] = 9;
        await using var receiver = await AirPlayReceiver.StartAsync(new AirPlayReceiverOptions
        {
            Name = "Test Receiver",
            ListenAddress = IPAddress.Loopback,
            AirPlayPort = 0,
            RaopPort = 0,
            EnableDiscovery = false,
            HardwareAddress = [0x02, 0x11, 0x22, 0x33, 0x44, 0x55],
            PairingSeed = seed,
            Log = NullAirPlayLog.Instance,
        });

        string? peer = null;
        receiver.SessionEstablished += (_, args) => peer = args.PeerPublicKeyBase64;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.AirPlayEndPoint.Port);
        await using var conversation = new HttpConversation(client.GetStream());

        using var senderKey = PairingIdentity.Generate();
        using var sender = new PairingSender(senderKey);
        var setup = await conversation.ExchangeAsync(
            "POST /pair-setup HTTP/1.1\r\nCSeq: 1\r\nContent-Type: application/octet-stream",
            sender.CreateSetupRequest());
        Assert.Equal(200, setup.Status);
        Assert.Equal("1", setup.Headers["CSeq"]);
        sender.AcceptSetup(setup.Body);

        var verify = await conversation.ExchangeAsync(
            "POST /pair-verify HTTP/1.1\r\nCSeq: 2\r\nContent-Type: application/octet-stream",
            sender.CreateVerifyStart());
        Assert.Equal(200, verify.Status);
        Assert.Equal(96, verify.Body.Length);

        var finish = await conversation.ExchangeAsync(
            "POST /pair-verify HTTP/1.1\r\nCSeq: 3",
            sender.AcceptVerifyStart(verify.Body));
        Assert.Equal(200, finish.Status);
        Assert.Equal(senderKey.PublicKeyBase64, peer);

        var info = await conversation.ExchangeAsync("GET /server-info HTTP/1.1\r\nCSeq: 4");
        var xml = Encoding.UTF8.GetString(info.Body);
        Assert.Equal(200, info.Status);
        Assert.Contains("02:11:22:33:44:55", xml, StringComparison.Ordinal);
        Assert.Contains(AirPlayCompatibility.Model, xml, StringComparison.Ordinal);

        var binary = await conversation.ExchangeAsync("POST /info RTSP/1.0\r\nCSeq: 5\r\nContent-Length: 0\r\n\r\n");
        Assert.Equal("5", binary.Headers["CSeq"]);
        var plist = Assert.IsType<PlistDictionary>(BinaryPropertyList.Read(binary.Body));
        Assert.Equal("02:11:22:33:44:55", Assert.IsType<PlistString>(plist.Require("deviceID")).Value);
        Assert.Equal(receiver.PublicKey.ToArray(), Assert.IsType<PlistData>(plist.Require("pk")).Value);

        var fairPlay = await conversation.ExchangeAsync(
            "POST /fp-setup HTTP/1.1\r\nContent-Type: application/octet-stream",
            new byte[16]);
        var fairPlayText = Encoding.UTF8.GetString(fairPlay.Body);
        Assert.Equal(501, fairPlay.Status);
        Assert.Contains("FairPlay", fairPlayText, StringComparison.Ordinal);
        Assert.InRange(fairPlay.Body.Length, 1, 80);

        var play = await conversation.ExchangeAsync("POST /play HTTP/1.1", "nope"u8.ToArray());
        Assert.Equal(501, play.Status);

        var options = await conversation.ExchangeAsync("OPTIONS * RTSP/1.0\r\nCSeq: 9\r\n\r\n");
        Assert.Equal(200, options.Status);
        Assert.Contains("POST", options.Headers["Public"], StringComparison.Ordinal);
    }
}

internal sealed class HttpConversation : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _scratch = new byte[2048];
    private byte[] _pending = [];

    public HttpConversation(Stream stream) => _stream = stream;

    public async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> ExchangeAsync(string head, byte[]? body = null)
    {
        var text = head;
        if (!text.Contains("Content-Length:", StringComparison.OrdinalIgnoreCase))
        {
            text = text.TrimEnd('\r', '\n') + "\r\nContent-Length: " + (body?.Length ?? 0) + "\r\n";
        }

        if (!text.EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            text += "\r\n";
        }

        await _stream.WriteAsync(Encoding.ASCII.GetBytes(text));
        if (body is { Length: > 0 })
        {
            await _stream.WriteAsync(body);
        }

        await _stream.FlushAsync();
        return await ReadAsync();
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> ReadAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var buffer = new MemoryStream();
        if (_pending.Length > 0)
        {
            buffer.Write(_pending);
            _pending = [];
        }

        while (true)
        {
            var data = buffer.ToArray();
            var headerEnd = data.AsSpan().IndexOf("\r\n\r\n"u8);
            if (headerEnd >= 0)
            {
                var header = Encoding.ASCII.GetString(data, 0, headerEnd);
                var lines = header.Split("\r\n");
                var status = int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < lines.Length; i++)
                {
                    var colon = lines[i].IndexOf(':');
                    headers[lines[i][..colon]] = lines[i][(colon + 1)..].Trim();
                }

                var length = int.Parse(headers["Content-Length"], System.Globalization.CultureInfo.InvariantCulture);
                var total = headerEnd + 4 + length;
                while (buffer.Length < total)
                {
                    var read = await _stream.ReadAsync(_scratch, timeout.Token);
                    if (read == 0)
                    {
                        throw new IOException("The control connection closed before the response finished.");
                    }

                    buffer.Write(_scratch, 0, read);
                }

                var all = buffer.ToArray();
                if (all.Length > total)
                {
                    _pending = all[total..];
                }

                return (status, headers, all.AsSpan(headerEnd + 4, length).ToArray());
            }

            var count = await _stream.ReadAsync(_scratch, timeout.Token);
            if (count == 0)
            {
                throw new IOException("The control connection closed before a response header.");
            }

            buffer.Write(_scratch, 0, count);
        }
    }
}
