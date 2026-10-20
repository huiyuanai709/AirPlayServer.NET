using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace AirPlayServer.Core.Audio;

/// <summary>
/// One mirror session's AAC-ELD companion audio. Sockets, the RTP buffer, the
/// decoder, and the playback device belong to this session. Another mirror
/// does not share them. Stopping the sockets keeps the content key so a later
/// type-96 SETUP can start again. A full session teardown calls
/// <see cref="ClearKeys"/>.
/// </summary>
internal sealed class MirrorAudio : IAsyncDisposable
{
    private readonly IAirPlayLog _log;
    private readonly Action _packetAccepted;
    private readonly object _state = new();
    private readonly object _gate = new();
    private readonly object _outputGate = new();
    private readonly RaopAudioBuffer _buffer = new();
    private CancellationTokenSource _run = new();
    private Socket? _data;
    private Socket? _control;
    private Socket? _timing;
    private Task? _dataLoop;
    private Task? _controlLoop;
    private Task? _timingLoop;
    private Task? _playLoop;
    private IPAddress? _timingPeer;
    private int _timingRemotePort;
    private Aes? _aes;
    private IAacEldDecoder? _decoder;
    private IPcmOutput? _output;
    private float _volumeDb;
    private int _sdlVolume = AirPlayVolume.ToSdl(0);
    private bool _paused;
    private bool _hasKeys;
    private int _running;
    private int _disposed;
    private int _loggedMissingKeys;
    private int _loggedFrame;
    private int _loggedWrite;

    public MirrorAudio(IAirPlayLog log, Action packetAccepted)
    {
        _log = log;
        _packetAccepted = packetAccepted;
    }

    public float VolumeDb
    {
        get
        {
            lock (_gate)
            {
                return _volumeDb;
            }
        }
    }

    public void SetVolumeDb(float db)
    {
        var clamped = AirPlayVolume.ClampDb(db);
        lock (_gate)
        {
            _volumeDb = clamped;
            _sdlVolume = AirPlayVolume.ToSdl(clamped);
        }
    }

    public void SetTimingTarget(IPAddress? peer, int port)
    {
        lock (_gate)
        {
            _timingPeer = peer;
            _timingRemotePort = port is > 0 and <= 65535 ? port : 0;
        }
    }

    public void SetKeys(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> ecdhSecret, ReadOnlySpan<byte> iv)
    {
        Span<byte> key = stackalloc byte[16];
        RaopAudioCipher.DeriveKey(contentKey, ecdhSecret, key);
        var aes = RaopAudioCipher.Create(key, iv);
        CryptographicOperations.ZeroMemory(key);
        Aes? previous;
        lock (_gate)
        {
            previous = _aes;
            _aes = aes;
            _hasKeys = true;
            _buffer.SetDecryptor(new AesCbcAudioDecryptor(aes));
        }

        previous?.Dispose();
    }

    public void ClearKeys()
    {
        Aes? previous;
        lock (_gate)
        {
            previous = _aes;
            _aes = null;
            _hasKeys = false;
            _buffer.SetDecryptor(null);
        }

        previous?.Dispose();
    }

    /// <summary>
    /// Binds the data, control, and timing UDP sockets. A second call while
    /// they are open returns the same ports.
    /// </summary>
    public bool TryStart(IPAddress listen, out int dataPort, out int controlPort, out int timingPort)
    {
        ArgumentNullException.ThrowIfNull(listen);
        lock (_state)
        {
            if (_running != 0 && _data is not null && _control is not null && _timing is not null)
            {
                dataPort = Port(_data);
                controlPort = Port(_control);
                timingPort = Port(_timing);
                return true;
            }
        }

        Socket? data = null;
        Socket? control = null;
        Socket? timing = null;
        try
        {
            data = BindUdp(listen);
            control = BindUdp(listen);
            timing = BindUdp(listen);
            lock (_state)
            {
                if (_disposed != 0)
                {
                    throw new ObjectDisposedException(nameof(MirrorAudio));
                }

                if (_running != 0 && _data is not null && _control is not null && _timing is not null)
                {
                    dataPort = Port(_data);
                    controlPort = Port(_control);
                    timingPort = Port(_timing);
                    Close(data);
                    Close(control);
                    Close(timing);
                    return true;
                }

                _data = data;
                _control = control;
                _timing = timing;
                var token = _run.Token;
                _running = 1;
                _dataLoop = Task.Run(() => ReceiveLoop(data, controlPacket: false, token));
                _controlLoop = Task.Run(() => ReceiveLoop(control, controlPacket: true, token));
                _timingLoop = Task.Run(() => TimingLoop(token));
                _playLoop = Task.Run(() => PlayLoop(token));
                dataPort = Port(data);
                controlPort = Port(control);
                timingPort = Port(timing);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            Close(data);
            Close(control);
            Close(timing);
            _log.Log(AirPlayLogLevel.Warning, "音频端口没有绑上: " + ex.Message + " Audio sockets were not opened: " + ex.Message);
            dataPort = 0;
            controlPort = 0;
            timingPort = 0;
            return false;
        }

        _log.Log(
            AirPlayLogLevel.Information,
            "镜像伴音已打开。data=" + dataPort + " control=" + controlPort + " timing=" + timingPort
            + ". Mirror audio is open.");
        return true;
    }

    /// <summary>Closes the sockets and drops queued PCM. The cipher key stays.</summary>
    public void Stop()
    {
        CancellationTokenSource cts;
        Socket? data;
        Socket? control;
        Socket? timing;
        Task? dataLoop;
        Task? controlLoop;
        Task? timingLoop;
        Task? playLoop;
        lock (_state)
        {
            if (_running == 0)
            {
                return;
            }

            _running = 0;
            cts = _run;
            data = _data;
            control = _control;
            timing = _timing;
            dataLoop = _dataLoop;
            controlLoop = _controlLoop;
            timingLoop = _timingLoop;
            playLoop = _playLoop;
            _data = null;
            _control = null;
            _timing = null;
            _dataLoop = null;
            _controlLoop = null;
            _timingLoop = null;
            _playLoop = null;
            _run = new CancellationTokenSource();
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        Close(data);
        Close(control);
        Close(timing);
        Wait(dataLoop);
        Wait(controlLoop);
        Wait(timingLoop);
        Wait(playLoop);
        cts.Dispose();
        lock (_gate)
        {
            _buffer.Flush(-1);
        }

        lock (_outputGate)
        {
            _output?.Flush();
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            _buffer.Flush(-1);
        }

        lock (_outputGate)
        {
            _output?.Flush();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
        }
    }

    public void Flush(int nextSequence)
    {
        IAacEldDecoder? decoder;
        lock (_gate)
        {
            _buffer.Flush(nextSequence);
            _paused = false;
            decoder = _decoder;
        }

        decoder?.Flush();
        lock (_outputGate)
        {
            _output?.Flush();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
        IPcmOutput? output;
        IAacEldDecoder? decoder;
        Aes? aes;
        lock (_outputGate)
        {
            output = _output;
            _output = null;
        }

        lock (_gate)
        {
            decoder = _decoder;
            _decoder = null;
            aes = _aes;
            _aes = null;
            _buffer.SetDecryptor(null);
        }

        output?.Dispose();
        decoder?.Dispose();
        aes?.Dispose();
        _run.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task ReceiveLoop(Socket socket, bool controlPacket, CancellationToken cancellationToken)
    {
        var buffer = new byte[RaopAudioBuffer.MaxPacketBytes];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            int count;
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);
                count = result.ReceivedBytes;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            if (count <= 0)
            {
                continue;
            }

            if (controlPacket)
            {
                if (count > 4 && (buffer[1] & ~0x80) == 0x56)
                {
                    Queue(buffer.AsSpan(4, count - 4), countAccepted: false);
                }

                continue;
            }

            if (count > 12)
            {
                Queue(buffer.AsSpan(0, count), countAccepted: true);
            }
        }
    }

    private void Queue(ReadOnlySpan<byte> packet, bool countAccepted)
    {
        if (countAccepted)
        {
            _packetAccepted();
        }

        lock (_gate)
        {
            if (!_hasKeys && Interlocked.Exchange(ref _loggedMissingKeys, 1) == 0)
            {
                _log.Log(AirPlayLogLevel.Warning, "音频包先于内容密钥到达，这一包解不开。An audio packet arrived before the content key and was not decrypted.");
            }

            _decoder ??= AacEldDecoders.Create(_log);
            _buffer.Queue(packet, _decoder);
        }
    }

    private async Task PlayLoop(CancellationToken cancellationToken)
    {
        var pcm = new short[RaopAudioBuffer.MaxPcmSamples];
        while (!cancellationToken.IsCancellationRequested)
        {
            RaopDequeue kind;
            int samples;
            int channels;
            int rate;
            int volume;
            bool paused;
            lock (_gate)
            {
                kind = _buffer.TryDequeue(pcm, out samples, out channels, out rate);
                volume = _sdlVolume;
                paused = _paused;
            }

            if (kind == RaopDequeue.Empty)
            {
                try
                {
                    await Task.Delay(2, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            if (kind != RaopDequeue.Frame || paused || samples <= 0)
            {
                continue;
            }

            var slice = pcm.AsSpan(0, samples);
            AirPlayVolume.Apply(slice, volume);
            try
            {
                WriteOutput(slice, channels, rate);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or SocketException)
            {
                if (Interlocked.Exchange(ref _loggedWrite, 1) == 0)
                {
                    _log.Log(AirPlayLogLevel.Warning, "音频没有写进播放设备: " + ex.Message + " Audio was not written to the playback device: " + ex.Message);
                }
            }

            if (Interlocked.Exchange(ref _loggedFrame, 1) == 0)
            {
                _log.Log(
                    AirPlayLogLevel.Information,
                    "镜像伴音已解码 " + rate + " Hz " + channels + " ch。Mirror audio decoded.");
            }
        }
    }

    private void WriteOutput(ReadOnlySpan<short> samples, int channels, int rate)
    {
        lock (_outputGate)
        {
            if (_disposed != 0)
            {
                return;
            }

            _output ??= SystemPcmOutput.Create(_log);
            _output.Write(samples, channels, rate);
        }
    }

    private async Task TimingLoop(CancellationToken cancellationToken)
    {
        var probe = new byte[32];
        probe[0] = 0x80;
        probe[1] = 0xD2;
        probe[3] = 0x07;
        while (!cancellationToken.IsCancellationRequested)
        {
            IPAddress? peer;
            int port;
            lock (_gate)
            {
                peer = _timingPeer;
                port = _timingRemotePort;
            }

            Socket? socket;
            lock (_state)
            {
                socket = _timing;
            }

            if (peer is not null && port > 0 && socket is not null)
            {
                WriteTimestamp(probe.AsSpan(24), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L);
                try
                {
                    await socket.SendToAsync(probe, SocketFlags.None, new IPEndPoint(peer, port), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
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

    private static int Port(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

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

    private static void Close(Socket? socket)
    {
        if (socket is null)
        {
            return;
        }

        try
        {
            socket.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void Wait(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            task.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
        {
        }
    }
}
