using System.Security.Cryptography;
using AirPlayServer.Core.Crypto;

namespace AirPlayServer.Core.Pairing;

/// <summary>
/// Long-term Ed25519 identity for one receiver process. The seed stays in
/// memory and is wiped on dispose. Nothing here is written to disk.
/// </summary>
public sealed class PairingIdentity : IDisposable
{
    private readonly byte[] _seed;
    private readonly byte[] _publicKey;
    private bool _disposed;

    private PairingIdentity(byte[] seed, byte[] publicKey)
    {
        _seed = seed;
        _publicKey = publicKey;
    }

    public ReadOnlyMemory<byte> PublicKey => _publicKey;

    public string PublicKeyBase64 => Convert.ToBase64String(_publicKey);

    public static PairingIdentity Generate()
    {
        var seed = new byte[Ed25519.SeedSize];
        RandomNumberGenerator.Fill(seed);
        return FromSeed(seed);
    }

    public static PairingIdentity FromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != Ed25519.SeedSize)
        {
            throw new ArgumentException("Pairing seed must be 32 bytes.", nameof(seed));
        }

        var copy = seed.ToArray();
        var publicKey = Ed25519.PublicKeyFromSeed(copy);
        return new PairingIdentity(copy, publicKey);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_seed);
        _disposed = true;
    }

    internal byte[] Sign(ReadOnlySpan<byte> message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Ed25519.Sign(_seed, message);
    }
}
