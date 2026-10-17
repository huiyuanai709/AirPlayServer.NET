using System.Net;
using System.Net.Sockets;
using AirPlayServer.Core;
using AirPlayServer.Core.Media;
using AirPlayServer.Core.Pairing;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class FairPlayKeysTests
{
    private static readonly string[] ContentKeys =
    [
        "28e7e60d2fee72fd6faa7fe1f5e5d65e",
        "c6091d788099f737648caff98b6e38a1",
        "ce29a8432940a1ab9390c40eb65bfa1c",
        "e784babe63a4e32b043fa960dc98b679",
    ];

    [Fact]
    public void Host_default_shares_one_session_and_keeps_overrides()
    {
        var options = new AirPlayReceiverOptions();
        var keys = options.EnableInProcessFairPlay();
        Assert.NotNull(keys);
        Assert.Same(keys, options.MediaKeyUnwrapper);
        Assert.Same(keys, options.FairPlaySetup);

        var plugin = new AirPlayReceiverOptions { MediaKeyPluginPath = "fairplay.so" };
        Assert.Null(plugin.EnableInProcessFairPlay());
        Assert.Null(plugin.MediaKeyUnwrapper);
        Assert.Null(plugin.FairPlaySetup);

        var custom = new AirPlayReceiverOptions { MediaKeyUnwrapper = new UnavailableMediaKeyUnwrapper() };
        Assert.Null(custom.EnableInProcessFairPlay());
        Assert.IsType<UnavailableMediaKeyUnwrapper>(custom.MediaKeyUnwrapper);
        Assert.Null(custom.FairPlaySetup);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Setup_handshake_and_unwrap_share_one_session(int mode)
    {
        var keys = new FairPlayKeys();
        var setup = new byte[4096];
        Assert.True(keys.TryRespond(SetupRequest(mode), setup, out var setupWritten));
        Assert.Equal(142, setupWritten);
        Assert.Equal((byte)'F', setup[0]);
        Assert.Equal((byte)'P', setup[1]);
        Assert.Equal((byte)'L', setup[2]);
        Assert.Equal((byte)'Y', setup[3]);
        Assert.Equal((byte)mode, setup[13]);

        var handshake = new byte[64];
        Assert.True(keys.TryRespond(Handshake(mode), handshake, out var handshakeWritten));
        Assert.Equal(32, handshakeWritten);
        Assert.Equal(new byte[] { 0x46, 0x50, 0x4c, 0x59, 0x03, 0x01, 0x04, 0x00, 0x00, 0x00, 0x00, 0x14 }, handshake.AsSpan(0, 12).ToArray());

        var content = new byte[16];
        Assert.True(keys.TryUnwrap(Ekey(), content));
        Assert.Equal(ContentKeys[mode], Convert.ToHexString(content).ToLowerInvariant());

        Assert.True(keys.TryRespond(SetupRequest(mode), setup, out _));
        content.AsSpan().Fill(0xAB);
        Assert.False(keys.TryUnwrap(Ekey(), content));
        Assert.Equal(0xAB, content[0]);
    }

    [Fact]
    public async Task Receiver_fp_setup_then_ekey_uses_the_same_session()
    {
        var options = ReceiverOptions();
        var keys = options.EnableInProcessFairPlay();
        Assert.NotNull(keys);
        await using var receiver = await AirPlayReceiver.StartAsync(options);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.AirPlayEndPoint.Port);
        await using var conversation = new HttpConversation(client.GetStream());
        await PairAsync(conversation);

        var setup = await conversation.ExchangeAsync("POST /fp-setup HTTP/1.1", SetupRequest(0));
        Assert.Equal(200, setup.Status);
        Assert.Equal(142, setup.Body.Length);
        Assert.Equal((byte)'Y', setup.Body[3]);

        var handshakeRequest = Handshake(0);
        var handshake = await conversation.ExchangeAsync("POST /fp-setup HTTP/1.1", handshakeRequest);
        Assert.Equal(200, handshake.Status);
        Assert.Equal(32, handshake.Body.Length);
        Assert.Equal(handshakeRequest.AsSpan(144, 20).ToArray(), handshake.Body.AsSpan(12, 20).ToArray());

        var content = new byte[16];
        Assert.True(keys.TryUnwrap(Ekey(), content));
        Assert.Equal(ContentKeys[0], Convert.ToHexString(content).ToLowerInvariant());

        await SetupMirrorAsync(conversation, Ekey());
        Assert.True(receiver.Mirror.MediaKeysReady);
    }

    [Fact]
    public async Task Receiver_drops_a_72_byte_ekey_until_the_handshake()
    {
        var options = ReceiverOptions();
        var keys = options.EnableInProcessFairPlay();
        Assert.NotNull(keys);
        var content = new byte[16];
        content[0] = 0x11;
        Assert.False(keys.TryUnwrap(Ekey(), content));
        Assert.Equal(0x11, content[0]);

        await using var receiver = await AirPlayReceiver.StartAsync(options);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.AirPlayEndPoint.Port);
        await using var conversation = new HttpConversation(client.GetStream());
        await PairAsync(conversation);
        await SetupMirrorAsync(conversation, Ekey());
        Assert.False(receiver.Mirror.MediaKeysReady);
    }

    [Fact]
    public async Task Lab_key_still_arms_the_stream_when_fairplay_is_on()
    {
        var lab = new byte[16];
        lab[3] = 9;
        var options = ReceiverOptions();
        options.LabContentKey = lab;
        Assert.NotNull(options.EnableInProcessFairPlay());
        await using var receiver = await AirPlayReceiver.StartAsync(options);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.AirPlayEndPoint.Port);
        await using var conversation = new HttpConversation(client.GetStream());
        await PairAsync(conversation);
        await SetupMirrorAsync(conversation, Ekey());
        Assert.True(receiver.Mirror.MediaKeysReady);
    }

    private static AirPlayReceiverOptions ReceiverOptions()
    {
        return new AirPlayReceiverOptions
        {
            Name = "FairPlay",
            ListenAddress = IPAddress.Loopback,
            AirPlayPort = 0,
            RaopPort = 0,
            EnableDiscovery = false,
            ShowWindow = false,
            HardwareAddress = [0x02, 0x10, 0x20, 0x30, 0x40, 0x61],
            PairingSeed = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
            Decoder = new UnavailableH264Decoder(),
            Log = NullAirPlayLog.Instance,
        };
    }

    private static async Task PairAsync(HttpConversation conversation)
    {
        using var senderKey = PairingIdentity.Generate();
        using var sender = new PairingSender(senderKey);
        var setup = await conversation.ExchangeAsync(
            "POST /pair-setup HTTP/1.1\r\nCSeq: 1\r\nContent-Type: application/octet-stream",
            sender.CreateSetupRequest());
        sender.AcceptSetup(setup.Body);
        var verify = await conversation.ExchangeAsync(
            "POST /pair-verify HTTP/1.1\r\nCSeq: 2\r\nContent-Type: application/octet-stream",
            sender.CreateVerifyStart());
        await conversation.ExchangeAsync(
            "POST /pair-verify HTTP/1.1\r\nCSeq: 3",
            sender.AcceptVerifyStart(verify.Body));
    }

    private static async Task SetupMirrorAsync(HttpConversation conversation, byte[] ekey)
    {
        var session = new PlistDictionary();
        session.Add("eiv", new PlistData(new byte[16]));
        session.Add("ekey", new PlistData(ekey));
        session.Add("timingPort", new PlistInteger(46001));
        var first = await conversation.ExchangeAsync(
            "SETUP * RTSP/1.0\r\nCSeq: 4\r\nContent-Type: application/x-apple-binary-plist",
            BinaryPropertyList.Write(session));
        Assert.Equal(200, first.Status);
        var stream = new PlistDictionary();
        stream.Add("type", new PlistInteger(110));
        stream.Add("streamConnectionID", new PlistInteger(77));
        var streams = new PlistArray();
        streams.Add(stream);
        var body = new PlistDictionary();
        body.Add("streams", streams);
        var second = await conversation.ExchangeAsync(
            "SETUP * RTSP/1.0\r\nCSeq: 5\r\nContent-Type: application/x-apple-binary-plist",
            BinaryPropertyList.Write(body));
        Assert.Equal(200, second.Status);
    }

    private static byte[] SetupRequest(int mode)
    {
        var request = new byte[16];
        request[4] = 0x03;
        request[14] = (byte)mode;
        return request;
    }

    private static byte[] Handshake(int mode)
    {
        var request = new byte[164];
        for (var i = 0; i < request.Length; i++)
        {
            request[i] = (byte)((i * 17) + 3);
        }

        request[0] = 0x46;
        request[1] = 0x50;
        request[2] = 0x4c;
        request[3] = 0x59;
        request[4] = 0x03;
        request[5] = 0x01;
        request[6] = 0x03;
        request[12] = (byte)mode;
        return request;
    }

    private static byte[] Ekey()
    {
        var ekey = new byte[72];
        for (var i = 0; i < ekey.Length; i++)
        {
            ekey[i] = (byte)(0xA0 + i);
        }

        return ekey;
    }
}
