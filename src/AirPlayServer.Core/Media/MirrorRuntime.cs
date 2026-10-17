using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlayServer.Core.Pairing;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Media;

/// <summary>
/// Screen-mirror control and the TCP data connection. Video is not UDP RTP:
/// each packet is a 128-byte header plus a length-prefixed payload. Audio SETUP
/// (type 96) stays unimplemented. FairPlay unwrap stays outside this process.
/// </summary>
internal sealed class MirrorRuntime : IAsyncDisposable
{
    public const int MaxPayloadBytes = 8 * 1024 * 1024;

    private readonly Socket _data;
    private readonly Socket _timing;
    private readonly FrameSlot _frames;
    private readonly IH264Decoder _decoder;
    private readonly IMediaKeyUnwrapper _unwrapper;
    private readonly byte[]? _labKey;
    private readonly IAirPlayLog _log;
    private readonly MirrorDecryptor _decryptor = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private readonly Task _acceptLoop;
    private readonly Task _timingLoop;
    private Socket? _client;
    private IPAddress? _peer;
    private int _remoteTimingPort;
    private byte[]? _sequence;
    private byte[]? _picture;
    private int _loggedDecoder;
    private int _disposed;

    private MirrorRuntime(
        Socket data,
        Socket timing,
        int eventPort,
        FrameSlot frames,
        IH264Decoder decoder,
        IMediaKeyUnwrapper unwrapper,
        byte[]? labKey,
        IAirPlayLog log)
    {
        _data = data;
        _timing = timing;
        _frames = frames;
        _decoder = decoder;
        _unwrapper = unwrapper;
        _labKey = labKey;
        _log = log;
        Activity = new MirrorActivity
        {
            DataPort = ((IPEndPoint)data.LocalEndPoint!).Port,
            TimingPort = ((IPEndPoint)timing.LocalEndPoint!).Port,
            EventPort = eventPort,
            DecoderName = decoder.Name,
        };
        _acceptLoop = Task.Run(AcceptLoop);
        _timingLoop = Task.Run(TimingLoop);
    }

    public MirrorActivity Activity { get; }

    public static MirrorRuntime Start(
        IPAddress listen,
        int eventPort,
        FrameSlot frames,
        IH264Decoder decoder,
        IMediaKeyUnwrapper? unwrapper,
        ReadOnlySpan<byte> labKey,
        IAirPlayLog log)
    {
        Socket? data = null;
        Socket? timing = null;
        try
        {
            data = BindTcp(listen);
            timing = BindUdp(listen);
            return new MirrorRuntime(
                data,
                timing,
                eventPort,
                frames,
                decoder,
                unwrapper ?? new UnavailableMediaKeyUnwrapper(),
                labKey.Length == 0 ? null : labKey.ToArray(),
                log);
        }
        catch
        {
            data?.Dispose();
            timing?.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cancellation.CancelAsync().ConfigureAwait(false);
        DropClient();
        _data.Dispose();
        _timing.Dispose();
        try
        {
            await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await _timingLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _decryptor.Dispose();
        _cancellation.Dispose();
    }

    internal void ArmForStream(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> ecdhSecret, ulong streamConnectionId)
    {
        _decryptor.SetMaterial(contentKey, ecdhSecret);
        var ready = _decryptor.TryBind(streamConnectionId);
        Activity.SetKeysReady(ready);
    }

    internal Http1Response HandleMedia(Http1Request request, PairingSession session, EndPoint? remote)
    {
        var method = request.Method;
        if (method.Equals("ANNOUNCE", StringComparison.OrdinalIgnoreCase))
        {
            _log.Log(AirPlayLogLevel.Information, "ANNOUNCE accepted (" + request.Body.Length + " bytes). SDP is not required for screen mirror.");
            return Http1Response.Empty(200);
        }

        if (method.Equals("SETUP", StringComparison.OrdinalIgnoreCase))
        {
            return HandleSetup(request, session, remote);
        }

        if (method.Equals("RECORD", StringComparison.OrdinalIgnoreCase))
        {
            return Http1Response.Empty(200, extra:
            [
                ("Audio-Latency", "11025"),
                ("Audio-Jack-Status", "connected; type=analog"),
            ]);
        }

        if (method.Equals("GET_PARAMETER", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Headers.TryGetValue("Content-Type", out var type)
                && type.StartsWith("text/parameters", StringComparison.OrdinalIgnoreCase)
                && Encoding.ASCII.GetString(request.Body).Contains("volume", StringComparison.Ordinal))
            {
                return Http1Response.Bytes(200, "text/parameters", "volume: 0.0\r\n"u8.ToArray());
            }

            return Http1Response.Empty(200);
        }

        if (method.Equals("SET_PARAMETER", StringComparison.OrdinalIgnoreCase)
            || method.Equals("PAUSE", StringComparison.OrdinalIgnoreCase)
            || method.Equals("FLUSH", StringComparison.OrdinalIgnoreCase))
        {
            return Http1Response.Empty(200);
        }

        if (method.Equals("TEARDOWN", StringComparison.OrdinalIgnoreCase))
        {
            _decryptor.Reset();
            Activity.SetKeysReady(false);
            lock (_gate)
            {
                _sequence = null;
                _picture = null;
            }

            DropClient();
            _log.Log(AirPlayLogLevel.Information, "Mirror teardown.");
            return Http1Response.Empty(200);
        }

        return Http1Response.Text(501, "Media method is not implemented.");
    }

    private Http1Response HandleSetup(Http1Request request, PairingSession session, EndPoint? remote)
    {
        if (request.Body.Length == 0)
        {
            return Http1Response.Text(400, "SETUP body is empty.");
        }

        PlistNode rootNode;
        try
        {
            rootNode = BinaryPropertyList.Read(request.Body);
        }
        catch (FormatException ex)
        {
            return Http1Response.Text(400, "SETUP plist was rejected: " + ex.Message);
        }

        if (rootNode is not PlistDictionary root)
        {
            return Http1Response.Text(400, "SETUP body is not a dictionary.");
        }

        if (remote is IPEndPoint endpoint && endpoint.Address.AddressFamily == AddressFamily.InterNetwork)
        {
            lock (_gate)
            {
                _peer = endpoint.Address;
            }
        }

        if (Find(root, "eiv") is PlistData)
        {
            return HandleSessionSetup(root, session);
        }

        if (Find(root, "streams") is PlistArray streams && streams.Items.Count > 0 && streams.Items[0] is PlistDictionary stream)
        {
            var streamType = Find(stream, "type") is PlistInteger type ? type.Value : 0;
            if (streamType == 110)
            {
                return HandleMirrorSetup(stream);
            }

            if (streamType == 96 || Find(stream, "audioFormat") is not null)
            {
                return Http1Response.Text(501, "Audio streaming is not implemented.");
            }

            return Http1Response.Text(501, "Mirror stream type " + streamType + " is not implemented.");
        }

        return Http1Response.Text(400, "SETUP did not contain a session or a stream.");
    }

    private Http1Response HandleSessionSetup(PlistDictionary root, PairingSession session)
    {
        if (Find(root, "eiv") is not PlistData eiv || eiv.Value.Length != 16)
        {
            return Http1Response.Text(400, "SETUP eiv must be 16 bytes.");
        }

        if (Find(root, "timingPort") is PlistInteger timing)
        {
            lock (_gate)
            {
                _remoteTimingPort = timing.Value is > 0 and <= 65535 ? (int)timing.Value : 0;
            }
        }

        var ekey = Find(root, "ekey") is PlistData keyData ? keyData.Value : [];
        byte[]? contentKey = null;
        if (_labKey is not null)
        {
            contentKey = _labKey;
            _log.Log(AirPlayLogLevel.Information, "Using the configured 16-byte lab content key. FairPlay was not unwrapped.");
        }
        else if (ekey.Length == 16)
        {
            contentKey = ekey;
            _log.Log(AirPlayLogLevel.Information, "SETUP ekey is already 16 bytes and is used as a raw content key.");
        }
        else if (ekey.Length == 72)
        {
            var unwrapped = new byte[16];
            if (_unwrapper.TryUnwrap(ekey, unwrapped))
            {
                contentKey = unwrapped;
                _log.Log(AirPlayLogLevel.Information, "Media-key unwrapper returned a 16-byte content key.");
            }
            else
            {
                _log.Log(AirPlayLogLevel.Warning, "FairPlay ekey (72 bytes) was not unwrapped. Encrypted mirror frames will be dropped. POST /fp-setup stays unimplemented. Supply a 16-byte lab content key or an IMediaKeyUnwrapper; PlayFair is not included.");
            }
        }
        else if (ekey.Length > 0)
        {
            _log.Log(AirPlayLogLevel.Warning, "SETUP ekey length " + ekey.Length + " is not 16 or 72. Encrypted frames will be dropped.");
        }

        var secret = session.SharedSecret.Span;
        if (contentKey is not null && secret.Length != 32)
        {
            _log.Log(AirPlayLogLevel.Warning, "Pair-verify X25519 secret is missing, so the mirror stream key cannot be derived yet.");
        }

        if (contentKey is null)
        {
            _decryptor.SetMaterial([], secret);
        }
        else
        {
            _decryptor.SetMaterial(contentKey, secret);
        }
        Activity.SetKeysReady(false);
        var name = Find(root, "name") is PlistString sender ? sender.Value : "unknown";
        _log.Log(AirPlayLogLevel.Information, "Mirror session setup from " + name + ". Video CTR starts at the next type-110 SETUP.");
        return Http1Response.Empty(200);
    }

    private Http1Response HandleMirrorSetup(PlistDictionary stream)
    {
        var streamId = Find(stream, "streamConnectionID") is PlistInteger id && id.Value >= 0 ? (ulong)id.Value : 0UL;
        var ready = _decryptor.TryBind(streamId);
        Activity.SetKeysReady(ready);
        _log.Log(ready ? AirPlayLogLevel.Information : AirPlayLogLevel.Warning,
            ready
                ? "Mirror stream " + streamId + " is armed. data=" + Activity.DataPort + " timing=" + Activity.TimingPort
                : "Mirror stream " + streamId + " has no content key. Type 0 payloads will be dropped. data=" + Activity.DataPort);

        var inner = new PlistDictionary();
        inner.Add("dataPort", new PlistInteger(Activity.DataPort));
        inner.Add("type", new PlistInteger(110));
        var streams = new PlistArray();
        streams.Add(inner);
        var response = new PlistDictionary();
        response.Add("eventPort", new PlistInteger(Activity.EventPort));
        response.Add("timingPort", new PlistInteger(Activity.TimingPort));
        response.Add("streams", streams);
        return Http1Response.Bytes(200, "application/x-apple-binary-plist", BinaryPropertyList.Write(response));
    }

    private async Task AcceptLoop()
    {
        var cancellationToken = _cancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _data.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }

            client.NoDelay = true;
            DropClient();
            lock (_gate)
            {
                _client = client;
            }

            _log.Log(AirPlayLogLevel.Information, "Mirror data client connected.");
            try
            {
                await ReadClientAsync(client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or EndOfStreamException or IOException)
            {
            }

            _log.Log(AirPlayLogLevel.Information, "Mirror data client disconnected.");
        }
    }

    private async Task ReadClientAsync(Socket client, CancellationToken cancellationToken)
    {
        var header = new byte[128];
        while (!cancellationToken.IsCancellationRequested)
        {
            await ReadExactAsync(client, header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
            if ((header[0] == (byte)'P' && header[1] == (byte)'O' && header[2] == (byte)'S' && header[3] == (byte)'T')
                || (header[0] == (byte)'G' && header[1] == (byte)'E' && header[2] == (byte)'T'))
            {
                _log.Log(AirPlayLogLevel.Information, "Mirror data connection sent HTTP. Closing it.");
                return;
            }

            await ReadExactAsync(client, header.AsMemory(4, 124), cancellationToken).ConfigureAwait(false);
            if (!MirrorHeader.TryParse(header, out var parsed))
            {
                return;
            }

            if (parsed.PayloadSize < 0 || parsed.PayloadSize > MaxPayloadBytes)
            {
                _log.Log(AirPlayLogLevel.Warning, "Mirror payload size " + parsed.PayloadSize + " was rejected.");
                return;
            }

            if (parsed.SenderPaused)
            {
                _log.Log(AirPlayLogLevel.Information, "Mirror sender paused.");
            }
            else if (parsed.SenderResumed)
            {
                _log.Log(AirPlayLogLevel.Information, "Mirror sender resumed.");
            }

            if (parsed.PayloadSize == 0)
            {
                continue;
            }

            var rented = ArrayPool<byte>.Shared.Rent(parsed.PayloadSize);
            try
            {
                await ReadExactAsync(client, rented.AsMemory(0, parsed.PayloadSize), cancellationToken).ConfigureAwait(false);
                if (parsed.PayloadType == 0)
                {
                    HandleEncrypted(rented.AsSpan(0, parsed.PayloadSize), parsed.IsKeyframe);
                }
                else if (parsed.PayloadType == 1)
                {
                    HandleCodec(rented.AsSpan(0, parsed.PayloadSize), parsed);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    private void HandleEncrypted(Span<byte> payload, bool keyFrame)
    {
        var plain = ArrayPool<byte>.Shared.Rent(payload.Length);
        try
        {
            if (!_decryptor.TryDecrypt(payload, plain.AsSpan(0, payload.Length)))
            {
                Activity.DropEncrypted();
                return;
            }

            var accessUnit = plain.AsSpan(0, payload.Length);
            if (!AvccAnnexB.TryConvertInPlace(accessUnit))
            {
                Activity.Reject();
                _log.Log(AirPlayLogLevel.Warning, "Dropped a mirror payload that was not AVCC.");
                return;
            }

            Activity.AcceptAccessUnit();
            byte[]? sequence;
            byte[]? picture;
            lock (_gate)
            {
                sequence = _sequence;
                picture = _picture;
            }

            ReadOnlySpan<byte> presented = accessUnit;
            if (keyFrame && sequence is not null && picture is not null)
            {
                presented = PrefixParameterSets(sequence, picture, accessUnit);
            }

            if (_decoder.TryDecode(presented, keyFrame, out var frame))
            {
                _frames.Publish(frame.Width, frame.Height, frame.Bgra.Span, frame.Stride);
            }
            else if (Interlocked.Exchange(ref _loggedDecoder, 1) == 0)
            {
                _log.Log(AirPlayLogLevel.Information, "H.264 decoder \"" + _decoder.Name + "\" did not return a picture. Ciphertext is not shown as the phone screen.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plain, clearArray: true);
        }
    }

    private void HandleCodec(ReadOnlySpan<byte> payload, MirrorHeader header)
    {
        if (float.IsFinite(header.Width) && float.IsFinite(header.Height) && header.Width > 1 && header.Height > 1)
        {
            _log.Log(AirPlayLogLevel.Information, "Mirror geometry " + header.Width + "x" + header.Height + " source " + header.SourceWidth + "x" + header.SourceHeight);
        }

        if (!CodecConfig.TryParse(payload, out var sequence, out var picture))
        {
            Activity.Reject();
            _log.Log(AirPlayLogLevel.Warning, "Mirror codec payload was rejected.");
            return;
        }

        lock (_gate)
        {
            _sequence = sequence;
            _picture = picture;
        }

        // Record the configuration before the decoder callback. A waiter resumed
        // from Configure must already observe CodecConfigurations.
        Activity.ConfigureCodec();
        _decoder.Configure(sequence, picture);
    }

    private async Task TimingLoop()
    {
        var cancellationToken = _cancellation.Token;
        var probe = new byte[48];
        probe[0] = 35;
        var reply = new byte[128];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                IPAddress? peer;
                int port;
                lock (_gate)
                {
                    peer = _peer;
                    port = _remoteTimingPort;
                }

                if (peer is not null && port > 0)
                {
                    WriteTimestamp(probe.AsSpan(40), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L);
                    await _timing.SendToAsync(probe, new IPEndPoint(peer, port), cancellationToken).ConfigureAwait(false);
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    wait.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        await _timing.ReceiveAsync(reply, wait.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException)
            {
                break;
            }
        }
    }

    private static void WriteTimestamp(Span<byte> destination, long microseconds)
    {
        var seconds = ((ulong)(microseconds / 1_000_000L)) + 2_208_988_800UL;
        var fraction = ((ulong)(microseconds % 1_000_000L)) * 0x1_0000_0000UL / 1_000_000UL;
        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)seconds);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], (uint)fraction);
    }

    private static byte[] PrefixParameterSets(byte[] sequence, byte[] picture, ReadOnlySpan<byte> accessUnit)
    {
        var output = new byte[8 + sequence.Length + picture.Length + accessUnit.Length];
        WriteStartCode(output.AsSpan(0), sequence);
        WriteStartCode(output.AsSpan(4 + sequence.Length), picture);
        accessUnit.CopyTo(output.AsSpan(8 + sequence.Length + picture.Length));
        return output;
    }

    private static void WriteStartCode(Span<byte> destination, ReadOnlySpan<byte> nal)
    {
        destination[0] = 0;
        destination[1] = 0;
        destination[2] = 0;
        destination[3] = 1;
        nal.CopyTo(destination[4..]);
    }

    private void DropClient()
    {
        Socket? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
        }

        client?.Dispose();
    }

    private static async Task ReadExactAsync(Socket socket, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await socket.ReceiveAsync(buffer[read..], SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            read += count;
        }
    }

    private static PlistNode? Find(PlistDictionary dictionary, string key)
    {
        foreach (var (name, value) in dictionary.Items)
        {
            if (name == key)
            {
                return value;
            }
        }

        return null;
    }

    private static Socket BindTcp(IPAddress listen)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.NoDelay = true;
            socket.Bind(new IPEndPoint(listen, 0));
            socket.Listen(2);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static Socket BindUdp(IPAddress listen)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(listen, 0));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
