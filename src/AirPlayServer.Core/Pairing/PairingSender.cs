using System.Security.Cryptography;
using AirPlayServer.Core.Crypto;

namespace AirPlayServer.Core.Pairing;

/// <summary>
/// Sender half of transient pairing. The control-server tests drive a real
/// handshake through this type; it is also the shape a future client fixture can reuse.
/// </summary>
public sealed class PairingSender : IDisposable
{
    private readonly PairingIdentity _identity;
    private X25519DiffieHellman? _ephemeral;
    private byte[]? _ourEphemeralPublic;
    private byte[]? _serverLongTermPublic;
    private byte[]? _sharedSecret;
    private bool _disposed;

    public PairingSender(PairingIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
    }

    internal ReadOnlyMemory<byte> SharedSecret => _sharedSecret ?? ReadOnlyMemory<byte>.Empty;

    public byte[] CreateSetupRequest()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _identity.PublicKey.ToArray();
    }

    public void AcceptSetup(ReadOnlySpan<byte> receiverPublicKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (receiverPublicKey.Length != 32)
        {
            throw new PairingProtocolException("pair-setup response must be the 32-byte receiver public key.");
        }

        _serverLongTermPublic = receiverPublicKey.ToArray();
    }

    public byte[] CreateVerifyStart()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ephemeral?.Dispose();
        _ephemeral = X25519DiffieHellman.GenerateKey();
        _ourEphemeralPublic = _ephemeral.ExportPublicKey();

        var message = new byte[68];
        message[0] = 1;
        _ourEphemeralPublic.CopyTo(message.AsSpan(4));
        _identity.PublicKey.Span.CopyTo(message.AsSpan(36));
        return message;
    }

    public byte[] AcceptVerifyStart(ReadOnlySpan<byte> receiverResponse)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ephemeral is null || _ourEphemeralPublic is null || _serverLongTermPublic is null)
        {
            throw new PairingProtocolException("pair-verify requires pair-setup to have completed.");
        }

        if (receiverResponse.Length != 96)
        {
            throw new PairingProtocolException("pair-verify response must be 96 bytes.");
        }

        var serverEphemeral = receiverResponse[..32].ToArray();
        var encryptedSignature = receiverResponse.Slice(32, 64).ToArray();
        var shared = _ephemeral.DeriveRawSecretAgreement(serverEphemeral);
        if (_sharedSecret is not null)
        {
            CryptographicOperations.ZeroMemory(_sharedSecret);
        }

        _sharedSecret = shared.ToArray();

        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[16];
        PairVerifyCrypto.Derive(shared, key, iv);
        using var ctr = new AesCtr(key, iv);
        ctr.Transform(encryptedSignature, encryptedSignature);

        Span<byte> receiverMessage = stackalloc byte[64];
        serverEphemeral.CopyTo(receiverMessage);
        _ourEphemeralPublic.CopyTo(receiverMessage[32..]);
        if (!Ed25519.Verify(_serverLongTermPublic, receiverMessage, encryptedSignature))
        {
            throw new PairingProtocolException("receiver pair-verify signature was rejected.", statusCode: 403);
        }

        Span<byte> senderMessage = stackalloc byte[64];
        _ourEphemeralPublic.CopyTo(senderMessage);
        serverEphemeral.CopyTo(senderMessage[32..]);
        var signature = _identity.Sign(senderMessage);
        ctr.Transform(signature, signature);

        var finish = new byte[68];
        signature.CopyTo(finish.AsSpan(4));
        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(iv);
        return finish;
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
}
