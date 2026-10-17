using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AirPlayServer.Core.Crypto;

namespace AirPlayServer.Core.Media;

/// <summary>
/// Turns a FairPlay-wrapped content key (the 72-byte SETUP ekey) into the 16-byte
/// AES key the mirror stream uses. This repository does not ship an implementation.
/// PlayFair is GPL-3.0 and is not vendored. Return false to keep encrypted frames dropped.
/// </summary>
public interface IMediaKeyUnwrapper
{
    /// <summary>
    /// Unwrap a 72-byte FairPlay ekey into 16 content-key bytes.
    /// Return false to keep encrypted frames dropped. Do not call PlayFair from here.
    /// </summary>
    bool TryUnwrap(ReadOnlySpan<byte> wrappedKey, Span<byte> contentKey);
}

/// <summary>
/// Optional answer for <c>POST /fp-setup</c>. Return false, or write nothing, and the
/// control server stays on HTTP 501. This process does not implement FairPlay.
/// </summary>
public interface IFairPlaySetup
{
    bool TryRespond(ReadOnlySpan<byte> request, Span<byte> response, out int written);
}

internal sealed class UnavailableMediaKeyUnwrapper : IMediaKeyUnwrapper
{
    public bool TryUnwrap(ReadOnlySpan<byte> wrappedKey, Span<byte> contentKey) => false;
}

/// <summary>
/// AirPlay mirror stream AES-128-CTR key and IV.
/// SHA-512(contentKey || ecdhSecret) supplies the 16-byte material mixed into
/// SHA-512("AirPlayStreamKey" + decimal streamConnectionID || material) and the
/// matching "AirPlayStreamIV" label. The decimal form has no leading zeros.
/// </summary>
internal static class StreamCipher
{
    public static void Derive(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> ecdhSecret, ulong streamConnectionId, Span<byte> key, Span<byte> iv)
    {
        if (contentKey.Length != 16 || ecdhSecret.Length != 32 || key.Length != 16 || iv.Length != 16)
        {
            throw new ArgumentException("Mirror stream derivation needs a 16-byte content key and a 32-byte X25519 secret.");
        }

        Span<byte> mixed = stackalloc byte[64];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        hash.AppendData(contentKey);
        hash.AppendData(ecdhSecret);
        if (hash.GetHashAndReset(mixed) != 64)
        {
            throw new CryptographicException("SHA-512 did not return 64 bytes.");
        }

        var id = streamConnectionId.ToString(CultureInfo.InvariantCulture);
        FoldLabel(hash, "AirPlayStreamKey" + id, mixed[..16], key);
        FoldLabel(hash, "AirPlayStreamIV" + id, mixed[..16], iv);
    }

    private static void FoldLabel(IncrementalHash hash, string label, ReadOnlySpan<byte> material, Span<byte> destination)
    {
        hash.AppendData(Encoding.ASCII.GetBytes(label));
        hash.AppendData(material);
        Span<byte> digest = stackalloc byte[64];
        if (hash.GetHashAndReset(digest) != 64)
        {
            throw new CryptographicException("SHA-512 did not return 64 bytes.");
        }

        digest[..16].CopyTo(destination);
    }
}

/// <summary>
/// One mirror's content key and the CTR transform that continues across payloads.
/// A payload that arrives before the key exists desynchronizes the counter; later
/// packets stay dropped until the next stream bind.
/// </summary>
internal sealed class MirrorDecryptor : IDisposable
{
    private readonly object _gate = new();
    private byte[]? _contentKey;
    private byte[]? _ecdh;
    private AesCtr? _ctr;
    private bool _desynced;

    public bool MediaKeysReady
    {
        get
        {
            lock (_gate)
            {
                return _ctr is not null && !_desynced;
            }
        }
    }

    public void SetMaterial(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> ecdhSecret)
    {
        lock (_gate)
        {
            _ctr?.Dispose();
            _ctr = null;
            _desynced = false;
            _contentKey = contentKey.Length == 16 ? contentKey.ToArray() : null;
            _ecdh = ecdhSecret.Length == 32 ? ecdhSecret.ToArray() : null;
        }
    }

    public bool TryBind(ulong streamConnectionId)
    {
        lock (_gate)
        {
            _ctr?.Dispose();
            _ctr = null;
            _desynced = false;
            if (_contentKey is null || _ecdh is null)
            {
                return false;
            }

            Span<byte> key = stackalloc byte[16];
            Span<byte> iv = stackalloc byte[16];
            StreamCipher.Derive(_contentKey, _ecdh, streamConnectionId, key, iv);
            _ctr = new AesCtr(key, iv);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(iv);
            return true;
        }
    }

    public bool TryDecrypt(ReadOnlySpan<byte> input, Span<byte> output)
    {
        lock (_gate)
        {
            if (_ctr is null || _desynced)
            {
                _desynced = true;
                return false;
            }

            _ctr.Transform(input, output);
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _ctr?.Dispose();
            _ctr = null;
            _desynced = false;
            if (_contentKey is not null)
            {
                CryptographicOperations.ZeroMemory(_contentKey);
            }

            _contentKey = null;
            _ecdh = null;
        }
    }

    public void Dispose() => Reset();
}
