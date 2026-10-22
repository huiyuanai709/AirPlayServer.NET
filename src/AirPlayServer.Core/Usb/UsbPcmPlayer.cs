using AirPlayServer.Core.Audio;

namespace AirPlayServer.Core.Usb;

/// <summary>
/// Plays one USB phone's clear LPCM on the process default device.
/// The USB read loop only enqueues. A playback task owns the device so a
/// full WASAPI or ALSA buffer cannot stall FEED video. Wireless AirPlay
/// keeps its own <see cref="MirrorAudio"/> stream, decoder, and volume.
/// USB has no SET_PARAMETER, so these samples stay at unity gain.
/// </summary>
internal sealed class UsbPcmPlayer : IDisposable
{
    public const int QueueCapacity = 32;

    private readonly IAirPlayLog _log;
    private readonly Func<int, int, IPcmOutput> _open;
    private readonly object _gate = new();
    private readonly Queue<QtPcmChunk> _queue = new();
    private IPcmOutput? _output;
    private Task? _loop;
    private int _channels;
    private int _rate;
    private int _dropped;
    private int _loggedDrop;
    private int _loggedWrite;
    private bool _disposed;

    public UsbPcmPlayer(IAirPlayLog log, Func<int, int, IPcmOutput>? open = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _open = open ?? ((rate, channels) => SystemPcmOutput.Create(log, rate, channels, usb: true));
    }

    public int Dropped => Volatile.Read(ref _dropped);

    public void Submit(QtPcmChunk chunk)
    {
        if (chunk.Samples is null || chunk.Samples.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            while (_queue.Count >= QueueCapacity)
            {
                _queue.Dequeue();
                Interlocked.Increment(ref _dropped);
            }

            _queue.Enqueue(chunk);
            _loop ??= Task.Factory.StartNew(
                Loop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Monitor.Pulse(_gate);
        }

        if (Volatile.Read(ref _dropped) > 0 && Interlocked.Exchange(ref _loggedDrop, 1) == 0)
        {
            _log.Log(
                AirPlayLogLevel.Warning,
                "USB 伴音排队超过播放速度，丢掉较早的 PCM，画面继续。 USB LPCM overran the playback queue. Older PCM was dropped. Video continues.");
        }
    }

    public void Dispose()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loop = _loop;
            Monitor.PulseAll(_gate);
        }

        if (loop is not null)
        {
            try
            {
                loop.Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
            {
            }
        }

        _output?.Dispose();
        _output = null;
    }

    private void Loop()
    {
        while (true)
        {
            QtPcmChunk chunk;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_disposed)
                {
                    Monitor.Wait(_gate);
                }

                if (_queue.Count == 0)
                {
                    return;
                }

                chunk = _queue.Dequeue();
                if (_disposed)
                {
                    return;
                }
            }

            try
            {
                EnsureOutput(chunk.Channels, chunk.SampleRate);
                _output?.Write(chunk.Samples, chunk.Channels, chunk.SampleRate);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                if (Interlocked.Exchange(ref _loggedWrite, 1) == 0)
                {
                    _log.Log(
                        AirPlayLogLevel.Warning,
                        "USB 伴音没有写进播放设备: " + ex.Message + " USB LPCM was not written to the playback device: " + ex.Message);
                }
            }
        }
    }

    private void EnsureOutput(int channels, int rate)
    {
        if (_output is not null && _channels == channels && _rate == rate)
        {
            return;
        }

        _output?.Dispose();
        _output = null;
        _channels = channels;
        _rate = rate;
        _output = _open(rate, channels);
    }
}
