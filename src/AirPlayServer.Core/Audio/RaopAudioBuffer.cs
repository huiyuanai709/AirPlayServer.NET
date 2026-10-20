using System.Buffers;
using System.Buffers.Binary;

namespace AirPlayServer.Core.Audio;

internal enum RaopDequeue
{
    Empty = 0,
    Gap = 1,
    Frame = 2,
}

/// <summary>
/// Reorders one mirror's AAC-ELD RTP packets the way upstream
/// <c>raop_buffer</c> does, then decodes each one to interleaved S16 PCM.
/// Sequence numbers are 16-bit and wrap. A gap is not filled with silence:
/// the caller skips it. Playback does not wait for a retransmission, matching
/// the current upstream receive loop.
/// </summary>
internal sealed class RaopAudioBuffer
{
    public const int Capacity = 64;

    public const int MaxPacketBytes = 32768;

    public const int MaxPcmSamples = 2048 * 2;

    private readonly Entry[] _entries = new Entry[Capacity];
    private bool _empty = true;
    private ushort _first;
    private ushort _last;

    public RaopAudioBuffer()
    {
        for (var i = 0; i < _entries.Length; i++)
        {
            _entries[i].Pcm = new short[MaxPcmSamples];
        }
    }

    public int Queue(ReadOnlySpan<byte> packet, IAacEldDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        if (packet.Length < 12 || packet.Length > MaxPacketBytes)
        {
            return -1;
        }

        // Sender sync marker. It is not an access unit.
        if (packet.Length == 16
            && packet[12] == 0x00
            && packet[13] == 0x68
            && packet[14] == 0x34
            && packet[15] == 0x00)
        {
            return 0;
        }

        var seq = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        if (!_empty && SeqCmp(seq, _first) < 0)
        {
            return 0;
        }

        if (!_empty && SeqCmp(seq, (ushort)(_first + Capacity)) >= 0)
        {
            Flush(seq);
        }

        ref var entry = ref _entries[seq % Capacity];
        if (entry.Available && entry.Seq == seq)
        {
            return 0;
        }

        entry.Available = true;
        entry.Seq = seq;
        entry.Timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4));
        entry.Samples = 0;
        entry.Channels = 0;
        entry.SampleRate = 0;

        var payload = packet[12..];
        var plain = ArrayPool<byte>.Shared.Rent(payload.Length);
        try
        {
            if (entry.Decrypt is null)
            {
                payload.CopyTo(plain);
            }
            else
            {
                entry.Decrypt.Decrypt(payload, plain.AsSpan(0, payload.Length));
            }

            if (!decoder.TryDecode(plain.AsSpan(0, payload.Length), entry.Pcm, out var samples, out var channels, out var rate)
                || samples <= 0
                || channels is < 1 or > 2
                || rate is < 8000 or > 192000
                || samples > MaxPcmSamples
                || samples % channels != 0)
            {
                entry.Available = false;
            }
            else
            {
                entry.Samples = samples;
                entry.Channels = channels;
                entry.SampleRate = rate;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plain, clearArray: true);
        }

        if (_empty)
        {
            _first = seq;
            _last = seq;
            _empty = false;
        }
        else if (SeqCmp(seq, _last) > 0)
        {
            _last = seq;
        }

        return 1;
    }

    /// <summary>
    /// Attaches the packet cipher used by the next <see cref="Queue"/> calls.
    /// The buffer does not own it.
    /// </summary>
    public void SetDecryptor(IRaopAudioDecryptor? decryptor)
    {
        for (var i = 0; i < _entries.Length; i++)
        {
            _entries[i].Decrypt = decryptor;
        }
    }

    public RaopDequeue TryDequeue(Span<short> destination, out int samples, out int channels, out int sampleRate)
    {
        samples = 0;
        channels = 0;
        sampleRate = 0;
        if (_empty || SeqCmp(_last, _first) < 0)
        {
            return RaopDequeue.Empty;
        }

        ref var entry = ref _entries[_first % Capacity];
        _first++;
        if (!entry.Available || entry.Samples <= 0)
        {
            entry.Available = false;
            return RaopDequeue.Gap;
        }

        entry.Available = false;
        if (destination.Length < entry.Samples)
        {
            entry.Samples = 0;
            return RaopDequeue.Gap;
        }

        entry.Pcm.AsSpan(0, entry.Samples).CopyTo(destination);
        samples = entry.Samples;
        channels = entry.Channels;
        sampleRate = entry.SampleRate;
        entry.Samples = 0;
        return RaopDequeue.Frame;
    }

    public void Flush(int nextSequence)
    {
        for (var i = 0; i < _entries.Length; i++)
        {
            _entries[i].Available = false;
            _entries[i].Samples = 0;
        }

        if (nextSequence is < 0 or > 0xffff)
        {
            _empty = true;
            return;
        }

        _first = (ushort)nextSequence;
        _last = (ushort)(nextSequence - 1);
        _empty = false;
    }

    private static int SeqCmp(ushort left, ushort right) => (short)(left - right);

    private struct Entry
    {
        public bool Available;
        public ushort Seq;
        public uint Timestamp;
        public int Samples;
        public int Channels;
        public int SampleRate;
        public short[] Pcm;
        public IRaopAudioDecryptor? Decrypt;
    }
}

internal interface IRaopAudioDecryptor
{
    void Decrypt(ReadOnlySpan<byte> ciphertext, Span<byte> plaintext);
}

internal sealed class AesCbcAudioDecryptor : IRaopAudioDecryptor
{
    private readonly System.Security.Cryptography.Aes _aes;

    public AesCbcAudioDecryptor(System.Security.Cryptography.Aes aes)
    {
        ArgumentNullException.ThrowIfNull(aes);
        _aes = aes;
    }

    public void Decrypt(ReadOnlySpan<byte> ciphertext, Span<byte> plaintext)
        => RaopAudioCipher.Decrypt(_aes, ciphertext, plaintext);
}
