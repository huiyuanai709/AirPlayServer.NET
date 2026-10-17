using System.Security.Cryptography;
using AirPlayServer.Core.Crypto;
using AirPlayServer.Core.Pairing;

namespace AirPlayServer.Core.Protocol;

/// <summary>
/// Known-answer checks for the handshake primitives. The Native AOT host runs
/// this so the published binary, not only the JIT test host, executes them.
/// </summary>
public static class ProtocolSelfCheck
{
    public static void Run()
    {
        CheckEd25519();
        CheckX25519();
        CheckPairing();
    }

    private static void CheckEd25519()
    {
        var seed = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        var expectedPublic = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var expectedSignature = Convert.FromHexString(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
            "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        if (!publicKey.AsSpan().SequenceEqual(expectedPublic))
        {
            throw new CryptographicException("Ed25519 public key does not match RFC 8032 test 1.");
        }

        var signature = Ed25519.Sign(seed, ReadOnlySpan<byte>.Empty);
        if (!signature.AsSpan().SequenceEqual(expectedSignature) || !Ed25519.Verify(publicKey, ReadOnlySpan<byte>.Empty, signature))
        {
            throw new CryptographicException("Ed25519 signature does not match RFC 8032 test 1.");
        }
    }

    private static void CheckX25519()
    {
        if (!X25519DiffieHellman.IsSupported)
        {
            throw new CryptographicException("X25519 is not supported on this runtime.");
        }

        var alicePrivate = Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bobPrivate = Convert.FromHexString("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        var expectedPublic = Convert.FromHexString("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        var expectedShared = Convert.FromHexString("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");
        using var alice = X25519DiffieHellman.ImportPrivateKey(alicePrivate);
        using var bob = X25519DiffieHellman.ImportPrivateKey(bobPrivate);
        var alicePublic = alice.ExportPublicKey();
        if (!alicePublic.AsSpan().SequenceEqual(expectedPublic))
        {
            throw new CryptographicException("X25519 public key does not match RFC 7748.");
        }

        var shared = alice.DeriveRawSecretAgreement(bob.ExportPublicKey());
        if (!shared.AsSpan().SequenceEqual(expectedShared))
        {
            throw new CryptographicException("X25519 shared secret does not match RFC 7748.");
        }
    }

    private static void CheckPairing()
    {
        var receiverSeed = new byte[32];
        var senderSeed = new byte[32];
        receiverSeed[0] = 1;
        senderSeed[0] = 2;
        using var receiver = PairingIdentity.FromSeed(receiverSeed);
        using var senderIdentity = PairingIdentity.FromSeed(senderSeed);
        using var session = new PairingSession(receiver);
        using var sender = new PairingSender(senderIdentity);
        sender.AcceptSetup(session.PairSetup(sender.CreateSetupRequest()));
        session.PairVerifyFinish(sender.AcceptVerifyStart(session.PairVerifyStart(sender.CreateVerifyStart())));
        if (session.Phase != PairingPhase.Finished)
        {
            throw new CryptographicException("Transient pair-verify did not finish.");
        }
    }
}
