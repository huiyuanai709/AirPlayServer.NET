using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlayServer.Core;
using AirPlayServer.Core.Crypto;
using AirPlayServer.Core.Export;
using AirPlayServer.Core.Media;
using AirPlayServer.Core.Pairing;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Tests;

public sealed class H264AndMediaKeyTests
{
    [Fact]
    public void Sps_parser_reads_baseline_and_high_profile_dimensions()
    {
        Assert.True(H264Sequence.TryGetDimensions(BaselineSps(176, 144), out var width, out var height));
        Assert.Equal(176, width);
        Assert.Equal(144, height);

        Assert.True(H264Sequence.TryGetDimensions(HighProfileSps(), out width, out height));
        Assert.Equal(320, width);
        Assert.Equal(180, height);
        Assert.True(H264Sequence.TryGetPictureSize(HighProfileSps(), out var visibleWidth, out var visibleHeight, out var codedWidth, out var codedHeight));
        Assert.Equal(320, visibleWidth);
        Assert.Equal(180, visibleHeight);
        Assert.Equal(320, codedWidth);
        Assert.Equal(192, codedHeight);
        Assert.False(H264Sequence.TryGetDimensions([0x67, 0x42], out _, out _));
        Assert.True(H264Sequence.TryGetDimensions(BaselineAccessUnitSps, out width, out height));
        Assert.Equal(16, width);
        Assert.Equal(16, height);
        Assert.True(H264Sequence.TryGetDimensions(MediaFoundationSps, out width, out height));
        Assert.Equal(48, width);
        Assert.Equal(48, height);
    }

    [Fact]
    public void Sps_vui_keeps_full_range_and_lets_an_unspecified_matrix_stay_bt709()
    {
        // AirPlay spec sample (iPad). ffmpeg trace_headers: 864x656 coded, crop bottom 4,
        // video_full_range_flag 1, matrix_coefficients 2 (unspecified).
        byte[] apple =
        [
            0x67, 0x64, 0xC0, 0x28, 0xAC, 0x56, 0x20, 0x0D, 0x81, 0x4F, 0xE5, 0x9B, 0x81, 0x01, 0x01, 0x01, 0x01,
        ];
        Assert.True(H264Sequence.TryGetPictureSize(apple, out var width, out var height, out var codedWidth, out var codedHeight, out var appleColor));
        Assert.Equal(864, width);
        Assert.Equal(648, height);
        Assert.Equal(864, codedWidth);
        Assert.Equal(656, codedHeight);
        Assert.True(appleColor.HasRange);
        Assert.True(appleColor.FullRange);
        Assert.True(appleColor.HasMatrix);
        Assert.Equal(2, appleColor.MatrixCoefficients);
        Assert.Equal(YuvColorSpace.AirPlayMirror, YuvColorSpace.Resolve(appleColor, 2, 2));

        Assert.True(H264Sequence.TryGetColorSignals(MediaFoundationSps, out var plain));
        Assert.False(plain.HasMatrix);
        Assert.False(plain.HasRange);
        Assert.Equal(YuvColorSpace.AirPlayMirror, YuvColorSpace.Resolve(plain, null, null));
        Assert.Equal(YuvColorSpace.AirPlayMirror, YuvColorSpace.Resolve(default, 0, 0));
        Assert.Equal(new YuvColorSpace(YuvMatrix.Bt709, YuvRange.Limited), YuvColorSpace.Resolve(default, 1, 2));
        Assert.Equal(new YuvColorSpace(YuvMatrix.Bt601, YuvRange.Full), YuvColorSpace.Resolve(default, 2, 1));
        Assert.Equal(YuvRange.Full, YuvColorSpace.Resolve(default, null, 4).Range);

        var full709 = BaselineSpsWithColor(fullRange: true, matrix: 1, includeMatrix: true, extendedSar: false);
        Assert.True(H264Sequence.TryGetPictureSize(full709, out width, out height, out _, out _, out var full));
        Assert.Equal(16, width);
        Assert.Equal(16, height);
        Assert.True(full.FullRange);
        Assert.Equal(1, full.MatrixCoefficients);
        Assert.Equal(new YuvColorSpace(YuvMatrix.Bt709, YuvRange.Full), YuvColorSpace.Resolve(full, 2, 2));

        var limited601 = BaselineSpsWithColor(fullRange: false, matrix: 6, includeMatrix: true, extendedSar: false);
        Assert.True(H264Sequence.TryGetColorSignals(limited601, out var limited));
        Assert.False(limited.FullRange);
        Assert.Equal(6, limited.MatrixCoefficients);
        Assert.Equal(new YuvColorSpace(YuvMatrix.Bt601, YuvRange.Limited), YuvColorSpace.Resolve(limited, 1, 1));

        var rangeOnly = BaselineSpsWithColor(fullRange: true, matrix: 0, includeMatrix: false, extendedSar: true);
        Assert.True(H264Sequence.TryGetColorSignals(rangeOnly, out var range));
        Assert.True(range.HasRange);
        Assert.True(range.FullRange);
        Assert.False(range.HasMatrix);
        Assert.Equal(new YuvColorSpace(YuvMatrix.Bt601, YuvRange.Full), YuvColorSpace.Resolve(range, 2, null));
        Assert.Contains("sequence parameter set", YuvColorSpace.DescribeChoice(YuvColorSpace.Resolve(full, null, null), full, 2, 2), StringComparison.Ordinal);
        Assert.Contains("media type", YuvColorSpace.DescribeChoice(YuvColorSpace.Resolve(default, 2, 1), default, 2, 1), StringComparison.Ordinal);
        Assert.Contains("AirPlay default", YuvColorSpace.DescribeChoice(YuvColorSpace.AirPlayMirror, default, null, null), StringComparison.Ordinal);
    }

    // 16x16 baseline SPS/PPS. Useful for the parser; the Microsoft H.264 decoder rejects it (minimum 48x48).
    private static readonly byte[] BaselineAccessUnitSps = [0x67, 0x42, 0xC0, 0x0A, 0xDD, 0xEC, 0x04, 0x40, 0x00, 0x00, 0x03, 0x00, 0x40, 0x00, 0x00, 0x0F, 0x03, 0xC4, 0x89, 0xE0];

    private static readonly byte[] BaselineAccessUnitPps = [0x68, 0xCE, 0x0F, 0x2C, 0x80];

    // 48x48 constrained-baseline Annex-B: SPS, PPS, and one black IDR. The x264 user-data SEI is omitted.
    // 48x48 is the smallest frame the Media Foundation H.264 decoder accepts.
    private static readonly byte[] MediaFoundationSps = [0x67, 0x42, 0xC0, 0x0A, 0xD9, 0x0D, 0xEC, 0x04, 0x40, 0x00, 0x00, 0x03, 0x00, 0x40, 0x00, 0x00, 0x0F, 0x03, 0xC4, 0x89, 0x92];

    private static readonly byte[] MediaFoundationPps = [0x68, 0xCB, 0x83, 0xCB, 0x20];

    private static readonly byte[] MediaFoundationAccessUnit =
    [
        0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0xC0, 0x0A, 0xD9, 0x0D, 0xEC, 0x04, 0x40, 0x00, 0x00, 0x03, 0x00, 0x40, 0x00, 0x00, 0x0F, 0x03, 0xC4, 0x89, 0x92,
        0x00, 0x00, 0x00, 0x01, 0x68, 0xCB, 0x83, 0xCB, 0x20,
        0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x09, 0xF2, 0x62, 0x80, 0x00, 0xA1, 0x6C, 0x9C, 0x9D, 0x75, 0xD7, 0x5D, 0x75, 0xE0,
    ];

    [Fact]
    public void Emulation_prevention_bytes_are_removed_before_the_rbsp()
    {
        var destination = new byte[8];
        var written = H264Sequence.RemoveEmulationPrevention([0x67, 0x00, 0x00, 0x03, 0x01, 0x00, 0x00, 0x03], destination);
        Assert.Equal(6, written);
        Assert.Equal(new byte[] { 0x67, 0x00, 0x00, 0x01, 0x00, 0x00 }, destination[..written]);
        Assert.Equal(-1, H264Sequence.RemoveEmulationPrevention([1, 2, 3], new byte[2]));
    }

    [Fact]
    public void Annex_b_parameter_sets_prefix_both_nals_with_start_codes()
    {
        var needed = 8 + BaselineAccessUnitSps.Length + BaselineAccessUnitPps.Length;
        var header = new byte[needed];
        Assert.Equal(needed, H264Sequence.WriteAnnexBParameterSets(BaselineAccessUnitSps, BaselineAccessUnitPps, header));
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, header[..4]);
        Assert.Equal(BaselineAccessUnitSps, header.AsSpan(4, BaselineAccessUnitSps.Length).ToArray());
        var picture = 8 + BaselineAccessUnitSps.Length;
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, header.AsSpan(picture - 4, 4).ToArray());
        Assert.Equal(BaselineAccessUnitPps, header.AsSpan(picture).ToArray());
        Assert.Equal(-1, H264Sequence.WriteAnnexBParameterSets(BaselineAccessUnitSps, BaselineAccessUnitPps, new byte[needed - 1]));
    }

    [Fact]
    public void Platform_decoder_is_media_foundation_only_on_windows()
    {
        var decoder = H264Decoders.CreatePlatformDefault();
        using var owned = decoder as IDisposable;
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(decoder.Name is "unavailable" or "ffmpeg-h264");
            Assert.Equal(decoder.Name == "ffmpeg-h264", decoder.IsAvailable);
            return;
        }

        Assert.StartsWith("media-foundation", decoder.Name, StringComparison.Ordinal);
        Assert.True(decoder.IsAvailable);
        Assert.True(H264Sequence.TryGetDimensions(MediaFoundationSps, out var parsedWidth, out var parsedHeight));
        Assert.Equal(48, parsedWidth);
        Assert.Equal(48, parsedHeight);
        decoder.Configure(MediaFoundationSps, MediaFoundationPps);
        // Hardware may need a second access unit before it produces a picture, or
        // it may hand the same unit to the software decoder. Either way one IDR
        // must become a frame without staying on a transform that has stalled.
        var produced = decoder.TryDecode(MediaFoundationAccessUnit, true, out var frame);
        if (!produced)
        {
            produced = decoder.TryDecode(MediaFoundationAccessUnit, true, out frame);
        }

        Assert.True(produced);
        Assert.True(H264Decoders.NegotiatedOutput(decoder));
        Assert.Equal(48, frame.Width);
        Assert.Equal(48, frame.Height);
        Assert.Equal(48 * 48 * 4, frame.Bgra.Length);
        Assert.Equal(255, frame.Bgra.Span[3]);
    }

    [Fact]
    public async Task Decrypted_access_unit_publishes_bgra_and_tapclock_jpeg()
    {
        var frames = new FrameSlot();
        var decoder = new PaintingDecoder();
        await using var mirror = MirrorRuntime.Start(IPAddress.Loopback, 9, frames, decoder, null, ReadOnlySpan<byte>.Empty, NullAirPlayLog.Instance);
        var content = new byte[16];
        content[0] = 11;
        var secret = new byte[32];
        secret[1] = 4;
        mirror.ArmForStream(content, secret, 77);

        var directory = Path.Combine(Path.GetTempPath(), "airplay-decoded-" + Guid.NewGuid().ToString("N"));
        var export = new TapClockExportOptions { WatchDirectory = directory, FramesPerSecond = 20 };
        await using var exporter = TapClockExporter.Start(export, frames, NullAirPlayLog.Instance, CancellationToken.None);

        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[16];
        StreamCipher.Derive(content, secret, 77, key, iv);
        var access = new byte[] { 0, 0, 0, 1, 0x65 };
        var encrypted = new byte[access.Length];
        using (var ctr = new AesCtr(key, iv))
        {
            ctr.Transform(access, encrypted);
        }

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, mirror.Activity.DataPort);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Packet(0, 0x10, encrypted));
        Assert.True(await exporter.WaitForDeliveryAsync(1, TimeSpan.FromSeconds(2)));
        Assert.True(frames.TryRead(out var width, out var height, out var stride, out var pixels, out var generation));
        Assert.Equal(8, width);
        Assert.Equal(8, height);
        Assert.Equal(32, stride);
        Assert.True(generation > 0);
        Assert.Equal(0x22, pixels[2]);
        Assert.Equal(255, pixels[3]);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65 }, decoder.LastAnnexB);
        var jpeg = await File.ReadAllBytesAsync(Path.Combine(directory, "mirror.jpg"));
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);
        Assert.False(File.Exists(Path.Combine(directory, "mirror.jpg.part")));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Unwrapper_and_fairplay_hook_arm_the_stream_or_stay_closed()
    {
        var content = new byte[16];
        content[5] = 0x3C;
        var ekey = new byte[72];
        content.CopyTo(ekey.AsSpan(56));
        await using var receiver = await StartReceiverAsync(new TailUnwrapper(), new ScriptedFairPlay { Body = [9, 8, 7, 6] }, null);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.AirPlayEndPoint.Port);
        await using var conversation = new HttpConversation(client.GetStream());
        await PairAsync(conversation);

        var fairPlay = await conversation.ExchangeAsync("POST /fp-setup HTTP/1.1", [1, 2, 3, 4]);
        Assert.Equal(200, fairPlay.Status);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, fairPlay.Body);

        await SetupMirrorAsync(conversation, ekey);
        Assert.True(receiver.Mirror.MediaKeysReady);

        await using var declined = await StartReceiverAsync(new TailUnwrapper(), new ScriptedFairPlay(), null);
        using var other = new TcpClient();
        await other.ConnectAsync(IPAddress.Loopback, declined.AirPlayEndPoint.Port);
        await using var otherConversation = new HttpConversation(other.GetStream());
        var declinedSetup = await otherConversation.ExchangeAsync("POST /fp-setup HTTP/1.1", [1]);
        Assert.Equal(501, declinedSetup.Status);
        Assert.Contains("FairPlay", Encoding.UTF8.GetString(declinedSetup.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_plugin_unwraps_when_a_compiler_can_build_it()
    {
        var missing = Path.Combine(Path.GetTempPath(), "missing-plugin-" + Guid.NewGuid().ToString("N") + ".so");
        Assert.False(NativeMediaKeyModule.TryLoad(missing, out _, out var missingError));
        Assert.Contains("not found", missingError, StringComparison.OrdinalIgnoreCase);

        var compiler = FindCompiler();
        if (compiler is null)
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Fail("clang or gcc is required to build the media-key plugin.");
            }

            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "airplay-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var library = CompilePlugin(compiler, directory);
            var content = new byte[16];
            content[2] = 0x5A;
            var ekey = new byte[72];
            content.CopyTo(ekey.AsSpan(56));
            await using var receiver = await StartReceiverAsync(null, null, library);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, receiver.AirPlayEndPoint.Port);
            await using var conversation = new HttpConversation(client.GetStream());
            await PairAsync(conversation);
            var fairPlay = await conversation.ExchangeAsync("POST /fp-setup HTTP/1.1", [9]);
            Assert.Equal(200, fairPlay.Status);
            Assert.Equal(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }, fairPlay.Body);
            await SetupMirrorAsync(conversation, ekey);
            Assert.True(receiver.Mirror.MediaKeysReady);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<AirPlayReceiver> StartReceiverAsync(IMediaKeyUnwrapper? unwrapper, IFairPlaySetup? fairPlay, string? pluginPath)
    {
        return await AirPlayReceiver.StartAsync(new AirPlayReceiverOptions
        {
            Name = "Keys",
            ListenAddress = IPAddress.Loopback,
            AirPlayPort = 0,
            RaopPort = 0,
            EnableDiscovery = false,
            ShowWindow = false,
            HardwareAddress = [0x02, 0x10, 0x20, 0x30, 0x40, 0x51],
            PairingSeed = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
            MediaKeyUnwrapper = unwrapper,
            FairPlaySetup = fairPlay,
            MediaKeyPluginPath = pluginPath,
            Decoder = new UnavailableH264Decoder(),
            Log = NullAirPlayLog.Instance,
        });
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

    private static byte[] Packet(byte type, byte flags, byte[] payload)
    {
        var packet = new byte[128 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(packet, 0);
        packet[4] = type;
        packet[5] = flags;
        payload.CopyTo(packet, 128);
        return packet;
    }

    private static byte[] BaselineSps(int width, int height)
    {
        var writer = new BitWriter();
        writer.WriteBits(0x67, 8);
        writer.WriteBits(66, 8);
        writer.WriteBits(0, 8);
        writer.WriteBits(30, 8);
        writer.WriteUe(0);
        writer.WriteUe(0);
        writer.WriteUe(0);
        writer.WriteUe(0);
        writer.WriteUe(1);
        writer.WriteBit(0);
        writer.WriteUe((uint)(width / 16 - 1));
        writer.WriteUe((uint)(height / 16 - 1));
        writer.WriteBit(1);
        writer.WriteBit(1);
        writer.WriteBit(0);
        return writer.Finish();
    }

    private static byte[] BaselineSpsWithColor(bool fullRange, int matrix, bool includeMatrix, bool extendedSar)
    {
        var writer = new BitWriter();
        writer.WriteBits(0x67, 8);
        writer.WriteBits(66, 8);
        writer.WriteBits(0, 8);
        writer.WriteBits(30, 8);
        writer.WriteUe(0);
        writer.WriteUe(0);
        writer.WriteUe(0);
        writer.WriteUe(0);
        writer.WriteUe(1);
        writer.WriteBit(0);
        writer.WriteUe(0);
        writer.WriteUe(0);
        writer.WriteBit(1);
        writer.WriteBit(1);
        writer.WriteBit(0);
        writer.WriteBit(1);
        writer.WriteBit(extendedSar ? 1 : 0);
        if (extendedSar)
        {
            writer.WriteBits(255, 8);
            writer.WriteBits(1, 16);
            writer.WriteBits(1, 16);
        }

        writer.WriteBit(0);
        writer.WriteBit(1);
        writer.WriteBits(5, 3);
        writer.WriteBit(fullRange ? 1 : 0);
        writer.WriteBit(includeMatrix ? 1 : 0);
        if (includeMatrix)
        {
            writer.WriteBits(1, 8);
            writer.WriteBits(1, 8);
            writer.WriteBits((uint)matrix, 8);
        }

        return writer.Finish();
    }

    private static byte[] HighProfileSps()
    {
        var writer = new BitWriter();
        writer.WriteBits(0x67, 8);
        writer.WriteBits(100, 8);
        writer.WriteBits(0, 8);
        writer.WriteBits(31, 8);
        writer.WriteUe(0);
        writer.WriteUe(1);
        writer.WriteUe(0);
        writer.WriteUe(0);
        writer.WriteBit(0);
        writer.WriteBit(1);
        writer.WriteBit(1);
        for (var i = 0; i < 16; i++)
        {
            writer.WriteUe(0);
        }

        for (var i = 0; i < 7; i++)
        {
            writer.WriteBit(0);
        }

        writer.WriteUe(0);
        writer.WriteUe(0);
        writer.WriteUe(0);
        writer.WriteUe(1);
        writer.WriteBit(0);
        writer.WriteUe(19);
        writer.WriteUe(11);
        writer.WriteBit(1);
        writer.WriteBit(1);
        writer.WriteBit(1);
        writer.WriteUe(0);
        writer.WriteUe(0);
        writer.WriteUe(0);
        writer.WriteUe(6);
        return writer.Finish();
    }

    private static string? FindCompiler()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var name in new[] { "clang", "gcc" })
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string CompilePlugin(string compiler, string directory)
    {
        var source = Path.Combine(directory, "airplay_plugin.c");
        var output = Path.Combine(directory, OperatingSystem.IsWindows() ? "airplay_plugin.dll" : "airplay_plugin.so");
        File.WriteAllText(source, """
            #include <stdint.h>
            #if defined(_WIN32)
            #define AIRPLAY_EXPORT __declspec(dllexport)
            #else
            #define AIRPLAY_EXPORT __attribute__((visibility("default")))
            #endif
            AIRPLAY_EXPORT
            int AirPlay_TryUnwrapContentKey(const uint8_t* wrapped, int wrappedLength, uint8_t* contentKey)
            {
                if (!wrapped || !contentKey || wrappedLength < 16) return 0;
                const uint8_t* src = wrapped + (wrappedLength - 16);
                for (int i = 0; i < 16; i++) contentKey[i] = src[i];
                return 1;
            }
            AIRPLAY_EXPORT
            int AirPlay_FairPlaySetup(const uint8_t* request, int requestLength, uint8_t* response, int responseCapacity, int* written)
            {
                (void)request;
                (void)requestLength;
                if (!response || !written || responseCapacity < 4) return 0;
                response[0] = 0xA1; response[1] = 0xB2; response[2] = 0xC3; response[3] = 0xD4;
                *written = 4;
                return 1;
            }
            """);
        var start = new ProcessStartInfo(compiler)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-shared");
        // clang's x86_64-pc-windows-msvc target rejects -fPIC. PE DLLs do not use it.
        if (!OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-fPIC");
        }
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add(source);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(20000));
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }

        return output;
    }

    private sealed class PaintingDecoder : IH264Decoder
    {
        public byte[] LastAnnexB { get; private set; } = [];

        public bool IsAvailable => true;

        public string Name => "painting";

        public void Configure(ReadOnlySpan<byte> sequenceParameterSet, ReadOnlySpan<byte> pictureParameterSet)
        {
        }

        public void Reset()
        {
        }

        public bool TryDecode(ReadOnlySpan<byte> annexB, bool keyFrame, out DecodedFrame frame)
        {
            LastAnnexB = annexB.ToArray();
            var pixels = new byte[8 * 8 * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 2] = 0x22;
                pixels[i + 3] = 255;
            }

            frame = new DecodedFrame(8, 8, pixels, 32);
            return true;
        }
    }

    private sealed class TailUnwrapper : IMediaKeyUnwrapper
    {
        public bool TryUnwrap(ReadOnlySpan<byte> wrappedKey, Span<byte> contentKey)
        {
            if (wrappedKey.Length < 16 || contentKey.Length < 16)
            {
                return false;
            }

            wrappedKey[^16..].CopyTo(contentKey);
            return true;
        }
    }

    private sealed class ScriptedFairPlay : IFairPlaySetup
    {
        public byte[]? Body { get; init; }

        public bool TryRespond(ReadOnlySpan<byte> request, Span<byte> response, out int written)
        {
            written = 0;
            if (Body is null || Body.Length > response.Length)
            {
                return false;
            }

            Body.CopyTo(response);
            written = Body.Length;
            return true;
        }
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bit;
        private byte _current;

        public void WriteBit(int bit)
        {
            _current = (byte)((_current << 1) | (bit & 1));
            _bit++;
            if (_bit == 8)
            {
                _bytes.Add(_current);
                _bit = 0;
                _current = 0;
            }
        }

        public void WriteBits(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                WriteBit((int)((value >> i) & 1));
            }
        }

        public void WriteUe(uint value)
        {
            var code = value + 1;
            var bits = 0;
            for (var probe = code; probe > 0; probe >>= 1)
            {
                bits++;
            }

            WriteBits(0, bits - 1);
            WriteBits(code, bits);
        }

        public byte[] Finish()
        {
            if (_bit != 0)
            {
                _current <<= 8 - _bit;
                _bytes.Add(_current);
            }

            return [.. _bytes];
        }
    }
}
