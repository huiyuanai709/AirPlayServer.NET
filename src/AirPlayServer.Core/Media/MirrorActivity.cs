namespace AirPlayServer.Core.Media;

/// <summary>
/// Observable mirror-receive counters. Encrypted payloads are dropped, not decoded,
/// until a 16-byte content key and the pair-verify X25519 secret are both present.
/// </summary>
public sealed class MirrorActivity
{
    private int _keysReady;
    private int _dropped;
    private int _accepted;
    private int _codec;
    private int _rejected;

    public int DataPort { get; internal set; }

    public int TimingPort { get; internal set; }

    public int EventPort { get; internal set; }

    public string DecoderName { get; internal set; } = "unavailable";

    public bool MediaKeysReady => Volatile.Read(ref _keysReady) != 0;

    public int DroppedEncryptedPayloads => Volatile.Read(ref _dropped);

    public int AcceptedAccessUnits => Volatile.Read(ref _accepted);

    public int CodecConfigurations => Volatile.Read(ref _codec);

    public int RejectedPayloads => Volatile.Read(ref _rejected);

    internal void SetKeysReady(bool ready) => Volatile.Write(ref _keysReady, ready ? 1 : 0);

    internal void DropEncrypted() => Interlocked.Increment(ref _dropped);

    internal void AcceptAccessUnit() => Interlocked.Increment(ref _accepted);

    internal void ConfigureCodec() => Interlocked.Increment(ref _codec);

    internal void Reject() => Interlocked.Increment(ref _rejected);
}
