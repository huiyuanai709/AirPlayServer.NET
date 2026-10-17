using System.Security.Cryptography;

namespace AirPlayServer.Core.Crypto;

/// <summary>
/// Derives the pair-verify AES-128-CTR key and counter block:
/// SHA-512(label || X25519 shared secret), truncated to 16 bytes.
/// </summary>
internal static class PairVerifyCrypto
{
    private static ReadOnlySpan<byte> KeyLabel => "Pair-Verify-AES-Key"u8;
    private static ReadOnlySpan<byte> IvLabel => "Pair-Verify-AES-IV"u8;

    public static void Derive(ReadOnlySpan<byte> sharedSecret, Span<byte> key, Span<byte> iv)
    {
        if (sharedSecret.Length != 32 || key.Length != 16 || iv.Length != 16)
        {
            throw new ArgumentException("Pair-verify expects a 32-byte shared secret and 16-byte key material.");
        }

        DeriveLabel(KeyLabel, sharedSecret, key);
        DeriveLabel(IvLabel, sharedSecret, iv);
    }

    private static void DeriveLabel(ReadOnlySpan<byte> label, ReadOnlySpan<byte> sharedSecret, Span<byte> destination)
    {
        Span<byte> hash = stackalloc byte[64];
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        incremental.AppendData(label);
        incremental.AppendData(sharedSecret);
        incremental.TryGetHashAndReset(hash, out _);
        hash[..16].CopyTo(destination);
        CryptographicOperations.ZeroMemory(hash);
    }
}
