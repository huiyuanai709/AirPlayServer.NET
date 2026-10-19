using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AirPlayServer.Core.Pairing;
using AirPlayServer.Core.Protocol;
using AirPlayServer.Core.Video;

namespace AirPlayServer.Core.Media;

/// <summary>
/// Screen-mirror control and the TCP data connection. Video is not UDP RTP:
/// each packet is a 128-byte header plus a length-prefixed payload. Audio SETUP
/// (type 96) stays unimplemented. A 72-byte ekey unwraps when <see cref="IMediaKeyUnwrapper"/> succeeds.
/// </summary>
internal sealed class MirrorRuntime : IAsyncDisposable
{
    public const int MaxPayloadBytes = 8 * 1024 * 1024;

    private readonly Socket _data;
    private readonly Socket _timing;
    private readonly FrameSlot _frames;
    private readonly IH264Decoder _decoder;
    private readonly IHevcDecoder? _hevc;
    private readonly bool _acceptHevc;
    private readonly IMediaKeyUnwrapper _unwrapper;
    private readonly byte[]? _labKey;
    private readonly IAirPlayLog _log;
    private readonly MirrorDecryptor _decryptor = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private readonly object _media = new();
    private readonly Queue<PendingCipher> _pending = new();
    private readonly Task _acceptLoop;
    private readonly Task _timingLoop;
    private Socket? _client;
    private IPAddress? _peer;
    private int _remoteTimingPort;
    private byte[]? _sequence;
    private byte[]? _picture;
    private byte[]? _vps;
    private MirrorVideoCodec _codec;
    private byte[]? _wrappedEkey;
    private byte[]? _heldKeyframe;
    private ulong _streamId;
    private ulong _armedStreamId;
    private bool _streamIdKnown;
    private bool _hasArmedStream;
    private int _pendingBytes;
    private int _loggedDecoder;
    private int _consecutiveDecodeMisses;
    private int _loggedEmptyCodec;
    private int _loggedHevcUnavailable;
    private int _loggedHold;
    private int _loggedOverflow;
    private int _loggedHeldKeyframe;
    private int _loggedDecoded;
    private int _loggedBadAvcc;
    private int _consecutiveBadAvcc;
    private bool _prependParameterSets;
    private bool _hevcRejected;
    private bool _senderPaused;
    private bool _discardHeld;
    private CancellationTokenSource _clientStop = new();
    private int _disposed;

    private const int BadAccessUnitLimit = 4;

    /// <summary>
    /// Ciphertext kept while FairPlay or the stream key is still missing.
    /// Tests shrink this so an overflow can be forced without an 8 MiB buffer.
    /// </summary>
    internal int PendingEncryptedByteLimit { get; set; } = 8 * 1024 * 1024;

    private MirrorRuntime(
        Socket data,
        Socket timing,
        int eventPort,
        FrameSlot frames,
        IH264Decoder decoder,
        IMediaKeyUnwrapper unwrapper,
        byte[]? labKey,
        IAirPlayLog log,
        bool acceptHevc,
        IHevcDecoder? hevcDecoder)
    {
        _data = data;
        _timing = timing;
        _frames = frames;
        _decoder = decoder;
        _hevc = hevcDecoder;
        _acceptHevc = acceptHevc;
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
        IAirPlayLog log,
        bool acceptHevc = false,
        IHevcDecoder? hevcDecoder = null)
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
                log,
                acceptHevc,
                hevcDecoder);
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
        _clientStop.Dispose();
        if (_decoder is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (_hevc is IDisposable hevcDisposable && !ReferenceEquals(hevcDisposable, _decoder))
        {
            hevcDisposable.Dispose();
        }

        _cancellation.Dispose();
    }

    internal void ArmForStream(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> ecdhSecret, ulong streamConnectionId)
    {
        lock (_media)
        {
            ClearWrappedEkey();
            _decryptor.SetMaterial(contentKey, ecdhSecret);
            _streamId = streamConnectionId;
            _streamIdKnown = true;
            var ready = _decryptor.TryBind(streamConnectionId);
            Activity.SetKeysReady(ready);
            if (ready)
            {
                OnArmedLocked(streamConnectionId);
            }
        }
    }

    internal void NotePairingSecret(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != 32)
        {
            return;
        }

        lock (_media)
        {
            if (!_decryptor.NoteSecret(secret))
            {
                return;
            }

            TryFinishArmLocked();
        }
    }

    internal void RetryPendingFairPlay()
    {
        lock (_media)
        {
            TryFinishArmLocked();
        }
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
            var kind = ClassifyTeardown(request);
            if (kind == TeardownKind.AudioOnly)
            {
                _log.Log(AirPlayLogLevel.Information, "Audio teardown ignored. Mirror video stays armed.");
                return Http1Response.Empty(200);
            }

            lock (_media)
            {
                if (kind == TeardownKind.VideoOnly)
                {
                    // The phone locks by tearing down type 110 (sometimes together
                    // with type 96) and keeping the RTSP session. The content key
                    // and the last SPS/PPS stay. Ciphertext still queued from the
                    // old counter is dropped instead of being replayed on the next
                    // stream id, which quarantined that stream after four bad AVCCs.
                    _decryptor.UnbindStream();
                    _streamIdKnown = false;
                    _hasArmedStream = false;
                    _senderPaused = false;
                    _discardHeld = true;
                    _prependParameterSets = ParameterSetsReadyLocked();
                    _heldKeyframe = null;
                    ClearPendingLocked();
                    ClearStreamHealthLocked();
                    Activity.SetKeysReady(false);
                }
                else
                {
                    _decryptor.Reset();
                    Activity.SetKeysReady(false);
                    Activity.SetPendingEncrypted(0);
                    ClearPendingLocked();
                    _heldKeyframe = null;
                    ClearWrappedEkey();
                    _streamIdKnown = false;
                    _hasArmedStream = false;
                    _senderPaused = false;
                    _prependParameterSets = false;
                    _hevcRejected = false;
                    ClearCodecLocked();
                    ClearStreamHealthLocked();
                }

                ResetDecodersLocked();
            }

            DropClient();
            _log.Log(AirPlayLogLevel.Information, kind == TeardownKind.VideoOnly
                ? "Mirror video teardown. Session key kept for the next SETUP."
                : "Mirror teardown.");
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
        byte[]? wrapped = null;
        if (_labKey is not null)
        {
            contentKey = _labKey;
            _log.Log(AirPlayLogLevel.Information, "Using the configured 16-byte lab content key. FairPlay was not unwrapped.");
        }
        else if (ekey.Length == 16)
        {
            contentKey = ekey.ToArray();
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
                wrapped = ekey.ToArray();
                _log.Log(AirPlayLogLevel.Warning, "FairPlay ekey (72 bytes) is not unwrapped yet. Encrypted frames are held until POST /fp-setup finishes on this FairPlay session.");
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

        lock (_media)
        {
            if (contentKey is null && _decryptor.HasContentKey)
            {
                // Unlock can repeat the session plist before /fp-setup has a new
                // key message. Replacing the working key with an empty one left
                // the next type-110 SETUP unable to arm, so the picture stayed
                // on the last frame. A later handshake can still unwrap once
                // the slot is empty; this duplicate does not clear it.
                if (secret.Length == 32)
                {
                    _decryptor.NoteSecret(secret);
                }

                _log.Log(AirPlayLogLevel.Information, "SETUP did not supply a new content key. The session key already in use is kept.");
                return Http1Response.Empty(200);
            }

            ClearWrappedEkey();
            _wrappedEkey = wrapped;
            ClearPendingLocked();
            _heldKeyframe = null;
            _prependParameterSets = false;
            _hevcRejected = false;
            ClearCodecLocked();
            _senderPaused = false;
            _hasArmedStream = false;
            _streamIdKnown = false;
            ClearStreamHealthLocked();
            ResetDecodersLocked();
            if (contentKey is null)
            {
                _decryptor.SetMaterial([], secret, keepStoredSecret: true);
            }
            else
            {
                _decryptor.SetMaterial(contentKey, secret, keepStoredSecret: true);
            }

            Activity.SetKeysReady(false);
        }

        DropClient();
        var name = Find(root, "name") is PlistString sender ? sender.Value : "unknown";
        _log.Log(AirPlayLogLevel.Information, "Mirror session setup from " + name + ". Video CTR starts at the next type-110 SETUP.");
        return Http1Response.Empty(200);
    }

    private Http1Response HandleMirrorSetup(PlistDictionary stream)
    {
        // streamConnectionID is a uint64. The high bit is set on some connects;
        // reading it as a signed plist integer and rejecting the negative value
        // derived the stream key for id 0, so every payload failed the AVCC check.
        var streamId = Find(stream, "streamConnectionID") is PlistInteger id ? unchecked((ulong)id.Value) : 0UL;
        bool ready;
        bool dropStaleClient;
        lock (_media)
        {
            _streamId = streamId;
            _streamIdKnown = true;
            ready = _decryptor.Restart(streamId);
            Activity.SetKeysReady(ready);
            dropStaleClient = ready && OnArmedLocked(streamId);
        }

        if (dropStaleClient)
        {
            DropClient();
        }

        _log.Log(ready ? AirPlayLogLevel.Information : AirPlayLogLevel.Warning,
            ready
                ? "Mirror stream " + streamId + " is armed. data=" + Activity.DataPort + " timing=" + Activity.TimingPort
                : "Mirror stream " + streamId + " is not armed yet. Type 0 payloads are held until the content key and pair-verify secret are ready. data=" + Activity.DataPort);

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
            CancellationToken clientToken;
            lock (_gate)
            {
                if (_clientStop.IsCancellationRequested)
                {
                    _clientStop.Dispose();
                    _clientStop = new CancellationTokenSource();
                }

                _client = client;
                clientToken = _clientStop.Token;
            }

            _log.Log(AirPlayLogLevel.Information, "Mirror data client connected.");
            try
            {
                await ReadClientAsync(client, clientToken, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or EndOfStreamException or IOException)
            {
            }
            catch (Exception ex)
            {
                _log.Log(AirPlayLogLevel.Warning, "Mirror data connection stopped: " + ex.Message);
            }

            _log.Log(AirPlayLogLevel.Information, "Mirror data client disconnected.");
        }
    }

    private async Task ReadClientAsync(Socket client, CancellationToken clientToken, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(clientToken, cancellationToken);
        var readToken = linked.Token;
        var header = new byte[128];
        while (!readToken.IsCancellationRequested)
        {
            await ReadExactAsync(client, header.AsMemory(0, 4), readToken).ConfigureAwait(false);
            if ((header[0] == (byte)'P' && header[1] == (byte)'O' && header[2] == (byte)'S' && header[3] == (byte)'T')
                || (header[0] == (byte)'G' && header[1] == (byte)'E' && header[2] == (byte)'T'))
            {
                _log.Log(AirPlayLogLevel.Information, "Mirror data connection sent HTTP. Closing it.");
                return;
            }

            await ReadExactAsync(client, header.AsMemory(4, 124), readToken).ConfigureAwait(false);
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
                if (parsed.PayloadType == 1 && !_acceptHevc && Interlocked.Exchange(ref _loggedEmptyCodec, 1) == 0)
                {
                    _log.Log(AirPlayLogLevel.Warning, "Mirror codec packet was empty. That usually means the sender wanted a codec other than H.264 while Features bit 42 is off. Start the host with --h265 to advertise SupportsScreenMultiCodec.");
                }

                continue;
            }

            var rented = ArrayPool<byte>.Shared.Rent(parsed.PayloadSize);
            try
            {
                await ReadExactAsync(client, rented.AsMemory(0, parsed.PayloadSize), readToken).ConfigureAwait(false);
                try
                {
                    if (parsed.PayloadType == 0)
                    {
                        HandleEncrypted(rented.AsSpan(0, parsed.PayloadSize), parsed.IsKeyframe);
                    }
                    else if (parsed.PayloadType == 1)
                    {
                        HandleCodec(rented.AsSpan(0, parsed.PayloadSize), parsed);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Log(AirPlayLogLevel.Warning, "Mirror payload was not decoded: " + ex.Message);
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
        lock (_media)
        {
            if (_decryptor.LostAlignment)
            {
                Activity.DropEncrypted();
                return;
            }

            if (_senderPaused)
            {
                // Video resumed without a 0x16 codec packet. Drop pre-lock references
                // so the next IDR does not blend into the frozen picture.
                _senderPaused = false;
                _prependParameterSets = true;
                ResetDecodersLocked();
            }

            if (!_decryptor.MediaKeysReady && !TryFinishArmLocked())
            {
                HoldEncryptedLocked(payload, keyFrame);
                return;
            }

            DrainPendingLocked();
            ConsumeEncryptedLocked(payload, keyFrame);
        }
    }

    private void ConsumeEncryptedLocked(ReadOnlySpan<byte> payload, bool keyFrame)
    {
        var plain = ArrayPool<byte>.Shared.Rent(payload.Length);
        try
        {
            if (!_decryptor.TryDecrypt(payload, plain.AsSpan(0, payload.Length)))
            {
                Activity.DropEncrypted();
                _log.Log(AirPlayLogLevel.Warning, "Encrypted mirror payload could not be decrypted. DroppedEncrypted=" + Activity.DroppedEncryptedPayloads + ", DecodedFrames=" + Activity.DecodedFrames + ".");
                return;
            }

            var accessUnit = plain.AsSpan(0, payload.Length);
            if (!AvccAnnexB.TryConvertInPlace(accessUnit))
            {
                NoteInvalidAccessUnitLocked();
                return;
            }

            _consecutiveBadAvcc = 0;
            Activity.AcceptAccessUnit();
            if (_hevcRejected && _codec != MirrorVideoCodec.Hevc)
            {
                if (Interlocked.Exchange(ref _loggedHevcUnavailable, 1) == 0)
                {
                    _log.Log(AirPlayLogLevel.Warning, "HEVC access unit was not decoded. The sender offered HEVC and this receiver did not accept it.");
                }

                return;
            }

            if (PresentAnnexLocked(accessUnit, keyFrame))
            {
                return;
            }

            if (keyFrame && !ParameterSetsReadyLocked())
            {
                _heldKeyframe = accessUnit.ToArray();
                if (Interlocked.Exchange(ref _loggedHeldKeyframe, 1) == 0)
                {
                    _log.Log(AirPlayLogLevel.Information, "Keyframe arrived before SPS/PPS and is held until codec data arrives.");
                }

                return;
            }

            NoteDecodeMissLocked();
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

        if (CodecConfig.LooksLikeHevc(payload))
        {
            HandleHevcCodec(payload, header);
            return;
        }

        if (!CodecConfig.TryParse(payload, out var sequence, out var picture))
        {
            Activity.Reject();
            _log.Log(AirPlayLogLevel.Warning, "Mirror codec payload was rejected.");
            return;
        }

        lock (_media)
        {
            var resume = NoteSenderPauseLocked(header);
            if (_codec == MirrorVideoCodec.Hevc)
            {
                _log.Log(AirPlayLogLevel.Information, "Mirror codec switched from HEVC to H.264.");
                _hevc?.Reset();
            }

            _hevcRejected = false;
            _codec = MirrorVideoCodec.H264;
            _vps = null;
            var changed = _sequence is null
                || _picture is null
                || !_sequence.AsSpan().SequenceEqual(sequence)
                || !_picture.AsSpan().SequenceEqual(picture);
            _sequence = sequence;
            _picture = picture;
            // Prefix only the next keyframe. Doing it on every 0x10 packet feeds a
            // second copy of the parameter sets into the open GOP and the picture ghosts.
            _prependParameterSets = true;
            // Record the configuration before the decoder callback. A waiter resumed
            // from Configure must already observe CodecConfigurations.
            Activity.ConfigureCodec();
            if (changed)
            {
                _decoder.Configure(sequence, picture);
            }
            else if (resume)
            {
                _decoder.Reset();
            }

            PresentHeldKeyframeLocked();
        }
    }

    private void HandleHevcCodec(ReadOnlySpan<byte> payload, MirrorHeader header)
    {
        if (!_acceptHevc)
        {
            Activity.Reject();
            lock (_media)
            {
                _hevcRejected = true;
                _heldKeyframe = null;
            }

            _log.Log(AirPlayLogLevel.Warning, "Mirror sender offered HEVC (hvc1) but HEVC is off. Start the host with --h265 to advertise Features bit 42 (SupportsScreenMultiCodec).");
            return;
        }

        if (_hevc is null || !_hevc.IsAvailable)
        {
            Activity.Reject();
            lock (_media)
            {
                _hevcRejected = true;
                _heldKeyframe = null;
            }

            _log.Log(AirPlayLogLevel.Warning, "HEVC was requested (--h265) but no HEVC decoder is available on this machine (Media Foundation CLSID_CMSH265DecoderMFT or FFmpeg libavcodec). This platform will not decode HEVC frames.");
            return;
        }

        if (!CodecConfig.TryParseHevc(payload, out var video, out var sequence, out var picture))
        {
            Activity.Reject();
            lock (_media)
            {
                _heldKeyframe = null;
            }

            _log.Log(AirPlayLogLevel.Warning, "Mirror HEVC codec payload was rejected.");
            return;
        }

        lock (_media)
        {
            var resume = NoteSenderPauseLocked(header);
            if (_codec == MirrorVideoCodec.H264)
            {
                _log.Log(AirPlayLogLevel.Information, "Mirror codec switched from H.264 to HEVC.");
                _decoder.Reset();
            }

            _hevcRejected = false;
            _codec = MirrorVideoCodec.Hevc;
            var changed = _vps is null
                || _sequence is null
                || _picture is null
                || !_vps.AsSpan().SequenceEqual(video)
                || !_sequence.AsSpan().SequenceEqual(sequence)
                || !_picture.AsSpan().SequenceEqual(picture);
            _vps = video;
            _sequence = sequence;
            _picture = picture;
            _prependParameterSets = true;
            Activity.ConfigureCodec();
            if (changed)
            {
                _hevc.Configure(video, sequence, picture);
            }
            else if (resume)
            {
                _hevc.Reset();
            }

            PresentHeldKeyframeLocked();
        }
    }

    private bool NoteSenderPauseLocked(MirrorHeader header)
    {
        var resume = header.SenderResumed || (_senderPaused && !header.SenderPaused);
        if (header.SenderPaused)
        {
            _senderPaused = true;
        }
        else if (header.SenderResumed)
        {
            _senderPaused = false;
        }

        return resume;
    }

    private void PresentHeldKeyframeLocked()
    {
        if (_heldKeyframe is null)
        {
            return;
        }

        var held = _heldKeyframe;
        _heldKeyframe = null;
        if (!PresentAnnexLocked(held, keyFrame: true))
        {
            NoteDecodeMissLocked();
        }
    }

    private void NoteDecodeMissLocked()
    {
        // One access unit with no picture is normal hardware latency. Logging
        // it immediately looks like the stuck-black failure the phone just hit.
        _consecutiveDecodeMisses++;
        if (_consecutiveDecodeMisses >= HardwareDecodeStall.MissesWithNoPicture)
        {
            LogDecodeMissLocked();
        }
    }

    private void LogDecodeMissLocked()
    {
        if (Interlocked.Exchange(ref _loggedDecoder, 1) != 0)
        {
            return;
        }

        if (_codec == MirrorVideoCodec.Hevc)
        {
            if (_hevc is null || !_hevc.IsAvailable)
            {
                _log.Log(AirPlayLogLevel.Warning, "HEVC was requested (--h265) but no HEVC decoder is available on this machine (Media Foundation CLSID_CMSH265DecoderMFT or FFmpeg libavcodec). This platform will not decode HEVC frames.");
                return;
            }

            _log.Log(AirPlayLogLevel.Information, "HEVC decoder \"" + _hevc.Name + "\" did not return a picture. Ciphertext is not shown as the phone screen. DecodedFrames=" + Activity.DecodedFrames + ".");
            return;
        }

        _log.Log(AirPlayLogLevel.Information, "H.264 decoder \"" + _decoder.Name + "\" did not return a picture. Ciphertext is not shown as the phone screen. DecodedFrames=" + Activity.DecodedFrames + ".");
    }

    private bool PresentAnnexLocked(ReadOnlySpan<byte> annexB, bool keyFrame)
    {
        if (_codec == MirrorVideoCodec.Hevc)
        {
            return PresentHevcLocked(annexB, keyFrame);
        }

        var presented = annexB;
        byte[]? prefixed = null;
        if (_prependParameterSets && keyFrame && _sequence is not null && _picture is not null)
        {
            _prependParameterSets = false;
            if (!AvccAnnexB.ContainsNalType(annexB, 7))
            {
                prefixed = PrefixParameterSets(_sequence, _picture, annexB);
                presented = prefixed;
            }
        }

        if (!_decoder.TryDecode(presented, keyFrame, out var frame))
        {
            return false;
        }

        PublishDecodedLocked(frame);
        return true;
    }

    private bool PresentHevcLocked(ReadOnlySpan<byte> annexB, bool keyFrame)
    {
        if (_hevc is null)
        {
            return false;
        }

        var presented = annexB;
        if (_prependParameterSets && keyFrame && _vps is not null && _sequence is not null && _picture is not null)
        {
            _prependParameterSets = false;
            if (!HevcSequence.ContainsNalType(annexB, 32)
                && !HevcSequence.ContainsNalType(annexB, 33)
                && !HevcSequence.ContainsNalType(annexB, 34))
            {
                presented = PrefixHevcParameterSets(_vps, _sequence, _picture, annexB);
            }
        }

        if (!_hevc.TryDecode(presented, keyFrame, out var frame))
        {
            return false;
        }

        PublishDecodedLocked(frame);
        return true;
    }

    private void PublishDecodedLocked(DecodedFrame frame)
    {
        _frames.Publish(frame.Width, frame.Height, frame.Bgra.Span, frame.Stride);
        _consecutiveDecodeMisses = 0;
        Activity.DecodeFrame();
        if (Interlocked.Exchange(ref _loggedDecoded, 1) == 0)
        {
            _log.Log(AirPlayLogLevel.Information, "Decoded mirror frame " + frame.Width + "x" + frame.Height + " published for the window and TapClock.");
        }
    }

    private bool TryFinishArmLocked()
    {
        if (_decryptor.MediaKeysReady)
        {
            return true;
        }

        if (_wrappedEkey is not null && !_decryptor.HasContentKey)
        {
            var unwrapped = new byte[16];
            if (_unwrapper.TryUnwrap(_wrappedEkey, unwrapped))
            {
                _decryptor.SetContentKey(unwrapped);
                CryptographicOperations.ZeroMemory(unwrapped);
                ClearWrappedEkey();
                _log.Log(AirPlayLogLevel.Information, "FairPlay handshake unwrapped the 72-byte ekey.");
            }
        }

        if (!_streamIdKnown)
        {
            Activity.SetKeysReady(false);
            return false;
        }

        var ready = _decryptor.TryBind(_streamId);
        Activity.SetKeysReady(ready);
        if (ready)
        {
            _log.Log(AirPlayLogLevel.Information, "Mirror stream " + _streamId + " is armed after the content key became ready.");
            OnArmedLocked(_streamId);
        }

        return ready;
    }

    /// <summary>
    /// Returns true when the data socket must be dropped. A new stream id has a new
    /// CTR key; bytes still queued on the old socket would walk that counter and
    /// every later payload would fail the AVCC check.
    /// </summary>
    private bool OnArmedLocked(ulong streamId)
    {
        var switched = _hasArmedStream && _armedStreamId != streamId;
        var restarted = _hasArmedStream && _armedStreamId == streamId;
        var discard = _discardHeld;
        if (discard || switched || restarted)
        {
            if (discard && _pending.Count > 0)
            {
                _log.Log(AirPlayLogLevel.Information, "Discarded " + _pending.Count + " encrypted payload(s) held across the video teardown.");
            }

            ClearPendingLocked();
            _heldKeyframe = null;
            _discardHeld = false;
            if (discard && ParameterSetsReadyLocked())
            {
                // The phone often does not send parameter sets again after unlock.
                // Keeping the last sets lets the next keyframe decode.
                _prependParameterSets = true;
            }

            ResetDecodersLocked();
        }
        else
        {
            DrainPendingLocked();
        }

        ClearStreamHealthLocked();
        _armedStreamId = streamId;
        _hasArmedStream = true;
        return switched;
    }

    private void NoteInvalidAccessUnitLocked()
    {
        Activity.Reject();
        if (_decryptor.LostAlignment)
        {
            return;
        }

        _consecutiveBadAvcc++;
        if (Interlocked.Exchange(ref _loggedBadAvcc, 1) == 0)
        {
            _log.Log(AirPlayLogLevel.Warning, "Dropped a mirror payload that was not AVCC.");
        }

        if (_consecutiveBadAvcc < BadAccessUnitLimit)
        {
            return;
        }

        _decryptor.FailStream();
        Activity.SetKeysReady(false);
        _log.Log(AirPlayLogLevel.Warning, "Mirror payloads are not AVCC. This stream stays dropped until the next SETUP.");
    }

    private void ClearStreamHealthLocked()
    {
        _consecutiveBadAvcc = 0;
        _loggedBadAvcc = 0;
    }

    private void DrainPendingLocked()
    {
        var replayed = 0;
        while (_pending.Count > 0 && _decryptor.MediaKeysReady)
        {
            var item = _pending.Dequeue();
            _pendingBytes -= item.Payload.Length;
            if (_pendingBytes < 0)
            {
                _pendingBytes = 0;
            }

            replayed++;
            ConsumeEncryptedLocked(item.Payload, item.KeyFrame);
        }

        Activity.SetPendingEncrypted(_pending.Count);
        if (replayed > 0)
        {
            _log.Log(AirPlayLogLevel.Information, "Replayed " + replayed + " held encrypted mirror payload(s).");
        }
    }

    private void HoldEncryptedLocked(ReadOnlySpan<byte> payload, bool keyFrame)
    {
        var limit = Math.Max(1, PendingEncryptedByteLimit);
        if (_decryptor.LostAlignment || _pendingBytes > limit - payload.Length)
        {
            _decryptor.FailStream();
            ClearPendingLocked();
            Activity.DropEncrypted();
            Activity.SetKeysReady(false);
            if (Interlocked.Exchange(ref _loggedOverflow, 1) == 0)
            {
                _log.Log(AirPlayLogLevel.Warning, "Encrypted mirror data before the content key overflowed the hold buffer. This stream stays dropped until the phone starts a new mirror session.");
            }

            return;
        }

        _pending.Enqueue(new PendingCipher(payload.ToArray(), keyFrame));
        _pendingBytes += payload.Length;
        Activity.SetPendingEncrypted(_pending.Count);
        if (Interlocked.Exchange(ref _loggedHold, 1) == 0)
        {
            _log.Log(AirPlayLogLevel.Warning, "Encrypted mirror payload is held until the content key is ready. It is not counted as dropped.");
        }
    }

    private void ClearPendingLocked()
    {
        _pending.Clear();
        _pendingBytes = 0;
        Activity.SetPendingEncrypted(0);
    }

    private void ClearWrappedEkey()
    {
        if (_wrappedEkey is not null)
        {
            CryptographicOperations.ZeroMemory(_wrappedEkey);
            _wrappedEkey = null;
        }
    }

    private readonly record struct PendingCipher(byte[] Payload, bool KeyFrame);

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

    private static byte[] PrefixHevcParameterSets(byte[] video, byte[] sequence, byte[] picture, ReadOnlySpan<byte> accessUnit)
    {
        var output = new byte[12 + video.Length + sequence.Length + picture.Length + accessUnit.Length];
        var offset = WriteStartCode(output.AsSpan(0), video);
        offset += WriteStartCode(output.AsSpan(offset), sequence);
        offset += WriteStartCode(output.AsSpan(offset), picture);
        accessUnit.CopyTo(output.AsSpan(offset));
        return output;
    }

    private bool ParameterSetsReadyLocked()
        => _codec == MirrorVideoCodec.Hevc
            ? _vps is not null && _sequence is not null && _picture is not null
            : _sequence is not null && _picture is not null;

    private void ClearCodecLocked()
    {
        _vps = null;
        _sequence = null;
        _picture = null;
        _codec = MirrorVideoCodec.Unknown;
    }

    private void ResetDecodersLocked()
    {
        _decoder.Reset();
        _hevc?.Reset();
    }

    private static byte[] PrefixParameterSets(byte[] sequence, byte[] picture, ReadOnlySpan<byte> accessUnit)
    {
        var output = new byte[8 + sequence.Length + picture.Length + accessUnit.Length];
        WriteStartCode(output.AsSpan(0), sequence);
        WriteStartCode(output.AsSpan(4 + sequence.Length), picture);
        accessUnit.CopyTo(output.AsSpan(8 + sequence.Length + picture.Length));
        return output;
    }

    private static int WriteStartCode(Span<byte> destination, ReadOnlySpan<byte> nal)
    {
        destination[0] = 0;
        destination[1] = 0;
        destination[2] = 0;
        destination[3] = 1;
        nal.CopyTo(destination[4..]);
        return 4 + nal.Length;
    }

    private void DropClient()
    {
        Socket? client;
        CancellationTokenSource? stop = null;
        lock (_gate)
        {
            client = _client;
            _client = null;
            if (client is not null && !_clientStop.IsCancellationRequested)
            {
                // Dispose alone does not always finish a pending ReceiveAsync
                // on Windows, so the accept loop never saw the post-unlock socket.
                stop = _clientStop;
            }
        }

        if (stop is not null)
        {
            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (client is null)
        {
            return;
        }

        try
        {
            client.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or PlatformNotSupportedException)
        {
        }

        client.Dispose();
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

    private enum TeardownKind
    {
        Session = 0,
        VideoOnly = 1,
        AudioOnly = 2,
    }

    private static TeardownKind ClassifyTeardown(Http1Request request)
    {
        if (request.Body.Length == 0)
        {
            return TeardownKind.Session;
        }

        try
        {
            if (BinaryPropertyList.Read(request.Body) is not PlistDictionary root
                || Find(root, "streams") is not PlistArray streams
                || streams.Items.Count == 0)
            {
                return TeardownKind.Session;
            }

            var video = false;
            var audio = false;
            foreach (var item in streams.Items)
            {
                if (item is not PlistDictionary stream)
                {
                    continue;
                }

                var type = Find(stream, "type") is PlistInteger value ? value.Value : 0;
                if (type == 110)
                {
                    video = true;
                }
                else if (type == 96)
                {
                    audio = true;
                }
            }

            if (video)
            {
                // Type 110 keeps the FairPlay session even when the same plist
                // also tears down audio. Treating that as a full session reset
                // erased the content key and the unlock SETUP could not decrypt.
                return TeardownKind.VideoOnly;
            }

            if (audio)
            {
                return TeardownKind.AudioOnly;
            }

            return TeardownKind.Session;
        }
        catch (FormatException)
        {
            return TeardownKind.Session;
        }
    }

    private static Socket BindTcp(IPAddress listen)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.NoDelay = true;
            socket.Bind(new IPEndPoint(listen, 0));
            socket.Listen(8);
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
