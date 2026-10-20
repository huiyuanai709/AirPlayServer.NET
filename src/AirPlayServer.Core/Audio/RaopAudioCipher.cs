using System.Security.Cryptography;

namespace AirPlayServer.Core.Audio;

/// <summary>
/// AirPlay mirror audio (RAOP type 96) uses AES-128-CBC, not the video CTR
/// stream. The key is the first 16 bytes of SHA-512(contentKey || ecdhSecret).
/// The IV is the session SETUP <c>eiv</c> and is reset for every packet.
/// A trailing partial block is not encrypted.
/// </summary>
internal static class RaopAudioCipher
{
    public static void DeriveKey(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> ecdhSecret, Span<byte> key)
    {
        if (contentKey.Length != 16 || ecdhSecret.Length != 32 || key.Length != 16)
        {
            throw new ArgumentException("Audio key derivation needs a 16-byte content key, a 32-byte secret, and a 16-byte destination.");
        }

        Span<byte> digest = stackalloc byte[64];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        hash.AppendData(contentKey);
        hash.AppendData(ecdhSecret);
        if (hash.GetHashAndReset(digest) != 64)
        {
            throw new CryptographicException("SHA-512 did not return 64 bytes.");
        }

        digest[..16].CopyTo(key);
    }

    public static Aes Create(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != 16 || iv.Length != 16)
        {
            throw new ArgumentException("AES-128-CBC needs a 16-byte key and IV.");
        }

        var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key.ToArray();
        aes.IV = iv.ToArray();
        return aes;
    }

    /// <summary>
    /// Decrypts the 16-byte-aligned prefix into <paramref name="output"/> and
    /// copies any tail unchanged. <paramref name="output"/> must not overlap
    /// <paramref name="input"/>.
    /// </summary>
    public static void Decrypt(Aes aes, ReadOnlySpan<byte> input, Span<byte> output)
    {
        ArgumentNullException.ThrowIfNull(aes);
        if (output.Length < input.Length)
        {
            throw new ArgumentException("Audio decrypt output is shorter than the payload.", nameof(output));
        }

        var aligned = input.Length & ~15;
        if (aligned > 0)
        {
            var written = aes.DecryptCbc(input[..aligned], aes.IV, output[..aligned], PaddingMode.None);
            if (written != aligned)
            {
                throw new CryptographicException("AES-CBC did not return the aligned payload.");
            }
        }

        if (input.Length > aligned)
        {
            input[aligned..].CopyTo(output[aligned..]);
        }
    }
}
