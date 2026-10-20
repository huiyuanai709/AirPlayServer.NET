using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AirPlayServer.Core;
using AirPlayServer.Core.Audio;
using AirPlayServer.Core.Notify;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class MirrorAudioTests
{
    [Fact]
    public void Audio_key_is_the_sha512_prefix()
    {
        var content = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var secret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var key = new byte[16];
        RaopAudioCipher.DeriveKey(content, secret, key);
        Assert.Equal("210498799841dddb97e4614a4e6854a5", Convert.ToHexString(key).ToLowerInvariant());
    }

    [Fact]
    public void Cbc_resets_the_iv_on_every_packet()
    {
        var key = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        var iv = Enumerable.Range(30, 16).Select(value => (byte)value).ToArray();
        var plain = new byte[32];
        RandomNumberGenerator.Fill(plain);
        using var aes = RaopAudioCipher.Create(key, iv);
        var cipher = aes.EncryptCbc(plain, iv, PaddingMode.None);
        var first = new byte[32];
        var second = new byte[32];
        RaopAudioCipher.Decrypt(aes, cipher, first);
        RaopAudioCipher.Decrypt(aes, cipher, second);
        Assert.Equal(plain, first);
        Assert.Equal(plain, second);
    }

    [Fact]
    public void Volume_matches_the_player_curve_and_the_parameter_text()
    {
        Assert.Equal(128, AirPlayVolume.ToSdl(0));
        Assert.Equal("volume: 0.0\r\n", AirPlayVolume.FormatParameter(0));
        Assert.Equal(0, AirPlayVolume.ToSdl(-30));
        Assert.Equal(0, AirPlayVolume.ToSdl(-144));
        Assert.Equal(64, AirPlayVolume.ToSdl(-15));
        Assert.Equal(-144, AirPlayVolume.ClampDb(-200));
        Assert.Equal(0, AirPlayVolume.ClampDb(3));
        var samples = new short[] { 200, -200, 1000, -1000 };
        AirPlayVolume.Apply(samples, 64);
        Assert.Equal(100, samples[0]);
        Assert.Equal(-100, samples[1]);
        Assert.True(AirPlayVolume.TryParseSetVolume("volume: -11.25\r\n"u8, out var db));
        Assert.Equal(-11.25f, db);
        Assert.Equal(3456, ReadSequence("seq=3456;rtptime=1"));
        Assert.Equal(-1, ReadSequence(null));
    }

    [Fact]
    public void Buffer_orders_gaps_and_ignores_the_sync_marker()
    {
        var buffer = new RaopAudioBuffer();
        using var decoder = new ToneDecoder();
        Assert.Equal(0, buffer.Queue(SyncMarker(), decoder));
        Assert.Equal(RaopDequeue.Empty, buffer.TryDequeue(new short[16], out _, out _, out _));

        Assert.Equal(1, buffer.Queue(Packet(1, 0xAA), decoder));
        Assert.Equal(0, buffer.Queue(Packet(1, 0xAA), decoder));
        Assert.Equal(1, buffer.Queue(Packet(3, 0xAA), decoder));
        Assert.Equal(0, buffer.Queue(Packet(0, 0xAA), decoder));

        var pcm = new short[16];
        Assert.Equal(RaopDequeue.Frame, buffer.TryDequeue(pcm, out var samples, out var channels, out var rate));
        Assert.Equal(4, samples);
        Assert.Equal(2, channels);
        Assert.Equal(44100, rate);
        Assert.Equal(1000, pcm[0]);
        Assert.Equal(RaopDequeue.Gap, buffer.TryDequeue(pcm, out _, out _, out _));
        Assert.Equal(RaopDequeue.Frame, buffer.TryDequeue(pcm, out _, out _, out _));
        Assert.Equal(RaopDequeue.Empty, buffer.TryDequeue(pcm, out _, out _, out _));

        buffer.Flush(20);
        Assert.Equal(RaopDequeue.Empty, buffer.TryDequeue(pcm, out _, out _, out _));
        Assert.Equal(1, buffer.Queue(Packet(20, 0xAA), decoder));
        Assert.Equal(0, buffer.Queue(Packet(19, 0xAA), decoder));
        Assert.Equal(0, buffer.Queue(Packet(3, 0xBB), decoder));
        Assert.Equal(RaopDequeue.Frame, buffer.TryDequeue(pcm, out samples, out _, out _));
        Assert.Equal(4, samples);
    }

    [Fact]
    public void Click_map_letterboxes_and_scales_logical_points()
    {
        var at = new DateTimeOffset(2026, 10, 20, 7, 40, 0, TimeSpan.Zero);
        var mapped = MirrorClickMap.Map(
            MirrorPointerPhase.Down,
            MirrorPointerButton.Left,
            at,
            windowX: 10,
            windowY: 20,
            clientWidth: 100,
            clientHeight: 200,
            frameWidth: 50,
            frameHeight: 100,
            logicalWidth: 430,
            logicalHeight: 932);
        Assert.True(mapped.OnContent);
        Assert.Equal(5, mapped.ContentX);
        Assert.Equal(10, mapped.ContentY);
        Assert.Equal(50, mapped.ContentWidth);
        Assert.Equal(100, mapped.ContentHeight);
        Assert.Equal(5 * 430 / 50, mapped.LogicalX);
        Assert.Equal(10 * 932 / 100, mapped.LogicalY);

        var bar = MirrorClickMap.Map(
            MirrorPointerPhase.Up,
            MirrorPointerButton.Right,
            at,
            windowX: 0,
            windowY: 0,
            clientWidth: 200,
            clientHeight: 100,
            frameWidth: 50,
            frameHeight: 100,
            logicalWidth: 430,
            logicalHeight: 932);
        Assert.False(bar.OnContent);
        Assert.Null(bar.ContentX);
        Assert.Null(bar.ContentY);
        Assert.Null(bar.LogicalX);
        Assert.Equal(430, bar.LogicalWidth);

        var pixels = MirrorClickMap.Map(
            MirrorPointerPhase.Down,
            MirrorPointerButton.Left,
            at,
            10,
            20,
            100,
            200,
            50,
            100,
            0,
            0);
        Assert.True(pixels.OnContent);
        Assert.Null(pixels.LogicalX);
        Assert.Null(pixels.LogicalWidth);
    }

    [Fact]
    public void Ffmpeg_aac_eld_opens_when_the_library_is_present()
    {
        var library = FFmpegLibrary.TryLoad(NullAirPlayLog.Instance);
        if (library is null)
        {
            return;
        }

        Assert.True(FFmpegAacEldDecoder.TryOpen(library, NullAirPlayLog.Instance, out var decoder));
        using (decoder)
        {
            Assert.NotNull(decoder);
            var pcm = new short[8192];
            Assert.False(decoder!.TryDecode([], pcm, out _, out _, out _));
            decoder.TryDecode([0x21, 0x10, 0x04, 0x60], pcm, out var samples, out var channels, out var rate);
            if (samples > 0)
            {
                Assert.InRange(channels, 1, 2);
                Assert.InRange(rate, 8000, 192000);
            }
        }
    }

    [Fact]
    public async Task Audio_socket_accepts_one_datagram_per_session()
    {
        var audio = new MirrorAudio(NullAirPlayLog.Instance, static () => { });
        await using var session = audio;
        Assert.True(audio.TryStart(IPAddress.Loopback, out var data, out var control, out var timing));
        Assert.NotEqual(data, control);
        Assert.NotEqual(data, timing);
        Assert.True(audio.TryStart(IPAddress.Loopback, out var again, out _, out _));
        Assert.Equal(data, again);
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var packet = new byte[16];
        packet[0] = 0x80;
        udp.SendTo(packet, new IPEndPoint(IPAddress.Loopback, data));
        await Task.Delay(50);
        audio.Stop();
        Assert.True(audio.TryStart(IPAddress.Loopback, out var restarted, out _, out _));
        Assert.NotEqual(data, restarted);
    }

    private static int ReadSequence(string? header)
    {
        AirPlayVolume.TryReadFlushSequence(header, out var sequence);
        return sequence;
    }

    private static byte[] Packet(ushort sequence, byte marker)
    {
        var packet = new byte[16];
        packet[0] = 0x80;
        packet[1] = 96;
        packet[2] = (byte)(sequence >> 8);
        packet[3] = (byte)sequence;
        packet[12] = marker;
        return packet;
    }

    private static byte[] SyncMarker()
    {
        var packet = new byte[16];
        packet[0] = 0x80;
        packet[12] = 0x00;
        packet[13] = 0x68;
        packet[14] = 0x34;
        packet[15] = 0x00;
        return packet;
    }

    private sealed class ToneDecoder : IAacEldDecoder
    {
        public bool IsAvailable => true;

        public bool TryDecode(ReadOnlySpan<byte> accessUnit, Span<short> interleaved, out int samples, out int channels, out int sampleRate)
        {
            samples = 0;
            channels = 0;
            sampleRate = 0;
            if (accessUnit.IsEmpty || accessUnit[0] != 0xAA)
            {
                return false;
            }

            samples = 4;
            channels = 2;
            sampleRate = 44100;
            interleaved[0] = 1000;
            interleaved[1] = -1000;
            interleaved[2] = 500;
            interleaved[3] = -500;
            return true;
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}
