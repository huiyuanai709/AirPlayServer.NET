namespace AirPlayServer.Core.Media;

/// <summary>
/// Observable mirror-receive counters.
/// <see cref="DroppedEncryptedPayloads"/> counts ciphertext that was discarded.
/// Payloads held until FairPlay or SPS/PPS arrive are <see cref="PendingEncryptedPayloads"/>,
/// not drops. <see cref="DecodedFrames"/> counts pictures actually published to the frame slot.
/// <see cref="AcceptedAccessUnits"/> counts decrypted AVCC units, including ones that did not
/// decode yet.
/// </summary>
public sealed class MirrorActivity
{
    private int _keysReady;
    private int _dropped;
    private int _accepted;
    private int _codec;
    private int _rejected;
    private int _decoded;
    private int _pending;

    public int DataPort { get; internal set; }

    public int TimingPort { get; internal set; }

    public int EventPort { get; internal set; }

    public string DecoderName { get; internal set; } = "unavailable";

    public bool MediaKeysReady => Volatile.Read(ref _keysReady) != 0;

    public int DroppedEncryptedPayloads => Volatile.Read(ref _dropped);

    public int AcceptedAccessUnits => Volatile.Read(ref _accepted);

    public int CodecConfigurations => Volatile.Read(ref _codec);

    public int RejectedPayloads => Volatile.Read(ref _rejected);

    public int DecodedFrames => Volatile.Read(ref _decoded);

    public int PendingEncryptedPayloads => Volatile.Read(ref _pending);

    internal void SetKeysReady(bool ready) => Volatile.Write(ref _keysReady, ready ? 1 : 0);

    internal void DropEncrypted() => Interlocked.Increment(ref _dropped);

    internal void AcceptAccessUnit() => Interlocked.Increment(ref _accepted);

    internal void ConfigureCodec() => Interlocked.Increment(ref _codec);

    internal void Reject() => Interlocked.Increment(ref _rejected);

    internal void DecodeFrame() => Interlocked.Increment(ref _decoded);

    internal void SetPendingEncrypted(int count) => Volatile.Write(ref _pending, count);
}
