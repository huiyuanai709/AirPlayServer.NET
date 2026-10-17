using System.Security.Cryptography;
using AirPlayServer.Core.Crypto;

namespace AirPlayServer.Core.Pairing;

/// <summary>
/// One control connection's transient pair-setup / pair-verify state.
/// Pair-verify may be the first message: senders that already learned <c>pk</c>
/// from mDNS do not always call pair-setup. The X25519 shared secret is kept
/// for a future encrypted control channel. It is not a FairPlay media key.
/// </summary>
public sealed class PairingSession : IDisposable
{
    private readonly PairingIdentity _identity;
    private X25519DiffieHellman? _ephemeral;
    private byte[]? _ourEphemeralPublic;
    private byte[]? _theirEphemeralPublic;
    private byte[]? _theirEd25519Public;
    private byte[]? _sharedSecret;
    private bool _disposed;

    public PairingSession(PairingIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
    }

    public PairingPhase Phase { get; private set; } = PairingPhase.Initial;

    public ReadOnlyMemory<byte> PeerPublicKey => _theirEd25519Public ?? ReadOnlyMemory<byte>.Empty;

    internal ReadOnlyMemory<byte> SharedSecret => _sharedSecret ?? ReadOnlyMemory<byte>.Empty;

    public byte[] PairSetup(ReadOnlySpan<byte> clientMaterial)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (clientMaterial.Length != 32)
        {
            throw new PairingProtocolException("pair-setup body must be 32 bytes.");
        }

        Phase = PairingPhase.Setup;
        return _identity.PublicKey.ToArray();
    }

    public byte[] PairVerifyStart(ReadOnlySpan<byte> message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Phase == PairingPhase.Finished)
        {
            throw new PairingProtocolException("pair-verify already finished on this connection.", closeConnection: true);
        }

        if (message.Length != 68 || message[0] != 1)
        {
            throw new PairingProtocolException("pair-verify start must be 68 bytes with type 1.");
        }

        var theirEphemeral = message.Slice(4, 32).ToArray();
        var theirIdentity = message.Slice(36, 32).ToArray();

        _ephemeral?.Dispose();
        _ephemeral = X25519DiffieHellman.GenerateKey();
        var ourEphemeral = _ephemeral.ExportPublicKey();
        var shared = _ephemeral.DeriveRawSecretAgreement(theirEphemeral);

        Span<byte> signed = stackalloc byte[64];
        ourEphemeral.CopyTo(signed);
        theirEphemeral.CopyTo(signed[32..]);
        var signature = _identity.Sign(signed);
        EncryptSignature(shared, signature, skip: 0);

        var response = new byte[96];
        ourEphemeral.CopyTo(response);
        signature.CopyTo(response.AsSpan(32));

        _ourEphemeralPublic = ourEphemeral;
        _theirEphemeralPublic = theirEphemeral;
        _theirEd25519Public = theirIdentity;
        ReplaceSharedSecret(shared);
        Phase = PairingPhase.Handshake;
        return response;
    }

    public void PairVerifyFinish(ReadOnlySpan<byte> message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Phase != PairingPhase.Handshake || _sharedSecret is null || _ourEphemeralPublic is null || _theirEphemeralPublic is null || _theirEd25519Public is null)
        {
            throw new PairingProtocolException("pair-verify finish requires an open handshake.", closeConnection: true);
        }

        if (message.Length != 68 || message[0] != 0)
        {
            throw new PairingProtocolException("pair-verify finish must be 68 bytes with type 0.", closeConnection: true);
        }

        var signature = message.Slice(4, 64).ToArray();
        // The first 64 bytes of keystream already masked the receiver signature.
        EncryptSignature(_sharedSecret, signature, skip: 64);

        Span<byte> signed = stackalloc byte[64];
        _theirEphemeralPublic.CopyTo(signed);
        _ourEphemeralPublic.CopyTo(signed[32..]);
        if (!Ed25519.Verify(_theirEd25519Public, signed, signature))
        {
            throw new PairingProtocolException("pair-verify signature was rejected.", statusCode: 403, closeConnection: true);
        }

        Phase = PairingPhase.Finished;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _ephemeral?.Dispose();
        if (_sharedSecret is not null)
        {
            CryptographicOperations.ZeroMemory(_sharedSecret);
        }

        _disposed = true;
    }

    private void ReplaceSharedSecret(byte[] shared)
    {
        if (_sharedSecret is not null)
        {
            CryptographicOperations.ZeroMemory(_sharedSecret);
        }

        _sharedSecret = shared;
    }

    private static void EncryptSignature(ReadOnlySpan<byte> sharedSecret, byte[] signature, int skip)
    {
        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[16];
        PairVerifyCrypto.Derive(sharedSecret, key, iv);
        using var ctr = new AesCtr(key, iv);
        if (skip > 0)
        {
            ctr.Skip(skip);
        }

        ctr.Transform(signature, signature);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(iv);
    }
}
