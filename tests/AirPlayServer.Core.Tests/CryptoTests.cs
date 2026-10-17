using System.Security.Cryptography;
using AirPlayServer.Core.Crypto;

namespace AirPlayServer.Core.Tests;

public sealed class CryptoTests
{
    [Fact]
    public void Ed25519_matches_rfc8032_empty_message()
    {
        var seed = Hex.Parse("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        var expectedPublic = Hex.Parse("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var expectedSignature = Hex.Parse(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
            "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");

        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var signature = Ed25519.Sign(seed, ReadOnlySpan<byte>.Empty);

        Assert.Equal(expectedPublic, publicKey);
        Assert.Equal(expectedSignature, signature);
        Assert.True(Ed25519.Verify(publicKey, ReadOnlySpan<byte>.Empty, signature));
    }

    [Fact]
    public void Ed25519_matches_rfc8032_one_byte_message()
    {
        var seed = Hex.Parse("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");
        var expectedPublic = Hex.Parse("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
        var expectedSignature = Hex.Parse(
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da" +
            "085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");
        var message = new byte[] { 0x72 };

        var publicKey = Ed25519.PublicKeyFromSeed(seed);
        var signature = Ed25519.Sign(seed, message);

        Assert.Equal(expectedPublic, publicKey);
        Assert.Equal(expectedSignature, signature);
        Assert.True(Ed25519.Verify(publicKey, message, signature));
        signature[0] ^= 1;
        Assert.False(Ed25519.Verify(publicKey, message, signature));
    }

    [Fact]
    public void X25519_matches_rfc7748()
    {
        Assert.True(X25519DiffieHellman.IsSupported);
        var alicePrivate = Hex.Parse("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bobPrivate = Hex.Parse("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        var expectedPublic = Hex.Parse("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        var expectedShared = Hex.Parse("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

        using var alice = X25519DiffieHellman.ImportPrivateKey(alicePrivate);
        using var bob = X25519DiffieHellman.ImportPrivateKey(bobPrivate);
        var alicePublic = alice.ExportPublicKey();
        var first = alice.DeriveRawSecretAgreement(bob.ExportPublicKey());
        var second = bob.DeriveRawSecretAgreement(alicePublic);

        Assert.Equal(expectedPublic, alicePublic);
        Assert.Equal(expectedShared, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void AesCtr_matches_nist_and_is_continuable()
    {
        var key = Hex.Parse("2b7e151628aed2a6abf7158809cf4f3c");
        var counter = Hex.Parse("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var plaintext = Hex.Parse("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51");
        var expected = Hex.Parse("874d6191b620e3261bef6864990db6ce9806f66b7970fdff8617187bb9fffdff");

        var together = new byte[plaintext.Length];
        using (var ctr = new AesCtr(key, counter))
        {
            ctr.Transform(plaintext, together);
        }

        Assert.Equal(expected, together);

        var split = new byte[plaintext.Length];
        using (var ctr = new AesCtr(key, counter))
        {
            ctr.Transform(plaintext.AsSpan(0, 16), split.AsSpan(0, 16));
            ctr.Skip(0);
            ctr.Transform(plaintext.AsSpan(16), split.AsSpan(16));
        }

        Assert.Equal(together, split);

        var skipped = new byte[16];
        using (var ctr = new AesCtr(key, counter))
        {
            ctr.Skip(16);
            ctr.Transform(plaintext.AsSpan(16), skipped);
        }

        Assert.Equal(together.AsSpan(16).ToArray(), skipped);
    }
}
