using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AirPlayServer.Core.Crypto;

namespace AirPlayServer.Core.Media;

/// <summary>
/// Turns a FairPlay-wrapped content key (the 72-byte SETUP ekey) into the 16-byte
/// AES key the mirror stream uses. The host default is <see cref="FairPlayKeys"/>.
/// Return false to keep encrypted frames dropped.
/// </summary>
public interface IMediaKeyUnwrapper
{
    /// <summary>
    /// Unwrap a 72-byte FairPlay ekey into 16 content-key bytes.
    /// Return false to keep encrypted frames dropped.
    /// </summary>
    bool TryUnwrap(ReadOnlySpan<byte> wrappedKey, Span<byte> contentKey);
}

/// <summary>
/// Answer for <c>POST /fp-setup</c>. Return false, or write nothing, and the
/// control server stays on HTTP 501. Use the same instance that unwraps the ekey
/// so the handshake survives until SETUP.
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
/// Ciphertext that arrives before the key is held by the mirror runtime and decrypted
/// in order once the key exists. Bytes that are discarded before that point set
/// <see cref="LostAlignment"/>; the same session cannot invent a new counter.
/// A later session <see cref="SetMaterial"/> or <see cref="Reset"/> starts clean.
/// </summary>
internal sealed class MirrorDecryptor : IDisposable
{
    private readonly object _gate = new();
    private byte[]? _contentKey;
    private byte[]? _ecdh;
    private AesCtr? _ctr;
    private bool _lostAlignment;

    public bool MediaKeysReady
    {
        get
        {
            lock (_gate)
            {
                return _ctr is not null && !_lostAlignment;
            }
        }
    }

    public bool LostAlignment
    {
        get
        {
            lock (_gate)
            {
                return _lostAlignment;
            }
        }
    }

    public bool HasContentKey
    {
        get
        {
            lock (_gate)
            {
                return _contentKey is not null;
            }
        }
    }

    public void SetMaterial(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> ecdhSecret, bool keepStoredSecret = false)
    {
        lock (_gate)
        {
            Replace(contentKey, ecdhSecret, resetAlignment: true, keepStoredSecret);
        }
    }

    /// <summary>
    /// Store a content key unlocked after a late FairPlay handshake.
    /// Does not clear <see cref="LostAlignment"/> and does not restart CTR.
    /// </summary>
    public void SetContentKey(ReadOnlySpan<byte> contentKey)
    {
        if (contentKey.Length != 16)
        {
            return;
        }

        lock (_gate)
        {
            if (_contentKey is not null && _contentKey.AsSpan().SequenceEqual(contentKey))
            {
                // A repeated /fp-setup must not walk the live counter back to zero.
                return;
            }

            _ctr?.Dispose();
            _ctr = null;
            if (_contentKey is not null)
            {
                CryptographicOperations.ZeroMemory(_contentKey);
            }

            _contentKey = contentKey.ToArray();
        }
    }

    /// <summary>
    /// Remember a pair-verify secret that showed up after session SETUP.
    /// Returns false when the stored secret is already these bytes.
    /// </summary>
    public bool NoteSecret(ReadOnlySpan<byte> ecdhSecret)
    {
        if (ecdhSecret.Length != 32)
        {
            return false;
        }

        lock (_gate)
        {
            if (_ecdh is not null && _ecdh.AsSpan().SequenceEqual(ecdhSecret))
            {
                return false;
            }

            _ctr?.Dispose();
            _ctr = null;
            _ecdh = ecdhSecret.ToArray();
            return true;
        }
    }

    public bool TryBind(ulong streamConnectionId) => Bind(streamConnectionId, clearLostAlignment: false);

    /// <summary>
    /// Start the CTR counter at zero for a type-110 SETUP. The sender does the
    /// same in its stream-key init. This is the recovery from a quarantined
    /// keystream; <see cref="TryBind"/> will not invent that counter by itself.
    /// </summary>
    public bool Restart(ulong streamConnectionId) => Bind(streamConnectionId, clearLostAlignment: true);

    /// <summary>
    /// Drop the live counter after a video-only teardown. The content key and
    /// pair-verify secret stay so the next type-110 SETUP can arm without a new ekey.
    /// </summary>
    public void UnbindStream()
    {
        lock (_gate)
        {
            _ctr?.Dispose();
            _ctr = null;
            _lostAlignment = false;
        }
    }

    private bool Bind(ulong streamConnectionId, bool clearLostAlignment)
    {
        lock (_gate)
        {
            if (_lostAlignment && !clearLostAlignment)
            {
                _ctr?.Dispose();
                _ctr = null;
                return false;
            }

            if (clearLostAlignment)
            {
                _lostAlignment = false;
            }

            _ctr?.Dispose();
            _ctr = null;
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

    public void FailStream()
    {
        lock (_gate)
        {
            _lostAlignment = true;
            _ctr?.Dispose();
            _ctr = null;
        }
    }

    public bool TryDecrypt(ReadOnlySpan<byte> input, Span<byte> output)
    {
        lock (_gate)
        {
            if (_ctr is null || _lostAlignment)
            {
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
            Replace([], [], resetAlignment: true, keepStoredSecret: false);
        }
    }

    public void Dispose() => Reset();

    private void Replace(ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> ecdhSecret, bool resetAlignment, bool keepStoredSecret)
    {
        _ctr?.Dispose();
        _ctr = null;
        if (resetAlignment)
        {
            _lostAlignment = false;
        }

        if (_contentKey is not null)
        {
            CryptographicOperations.ZeroMemory(_contentKey);
        }

        _contentKey = contentKey.Length == 16 ? contentKey.ToArray() : null;
        if (ecdhSecret.Length == 32)
        {
            _ecdh = ecdhSecret.ToArray();
        }
        else if (!keepStoredSecret)
        {
            _ecdh = null;
        }
    }
}
