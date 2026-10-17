using System.Numerics;
using System.Security.Cryptography;

namespace AirPlayServer.Core.Crypto;

/// <summary>
/// RFC 8032 Ed25519, used only for the transient AirPlay pair-verify signatures.
/// Field math goes through <see cref="BigInteger"/> because a handshake happens
/// once per connection. A limb or SIMD field would be faster and is not worth
/// the extra code on this path. The scalar multiplication is not constant-time.
/// </summary>
internal static class Ed25519
{
    public const int SeedSize = 32;
    public const int PublicKeySize = 32;
    public const int SignatureSize = 64;

    private static readonly BigInteger P = (BigInteger.One << 255) - 19;
    private static readonly BigInteger L = (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D;
    private static readonly BigInteger SqrtMinusOne;
    private static readonly EdwardsPoint Base;

    static Ed25519()
    {
        D = Mod(-121665 * ModInverse(121666));
        SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);
        var y = Mod(4 * ModInverse(5));
        if (!TryRecoverX(y, signBit: false, out var x))
        {
            throw new InvalidOperationException("Ed25519 base point is not on the curve.");
        }

        Base = new EdwardsPoint(x, y);
    }

    public static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed)
    {
        var (clamped, _) = Expand(seed);
        Span<byte> publicKey = stackalloc byte[PublicKeySize];
        ScalarMultiply(Base, clamped).Encode(publicKey);
        return publicKey.ToArray();
    }

    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        var (scalar, prefix) = Expand(seed);
        Span<byte> publicKey = stackalloc byte[PublicKeySize];
        ScalarMultiply(Base, scalar).Encode(publicKey);

        var r = HashModL(prefix, message);
        Span<byte> encodedR = stackalloc byte[PublicKeySize];
        ScalarMultiply(Base, r).Encode(encodedR);

        var k = HashModL(encodedR, publicKey, message);
        var s = ModL(r + (k * scalar));

        var signature = new byte[SignatureSize];
        encodedR.CopyTo(signature);
        WriteLe(s, signature.AsSpan(PublicKeySize));
        return signature;
    }

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize)
        {
            return false;
        }

        if (!TryDecode(publicKey, out var a) || a.IsIdentity)
        {
            return false;
        }

        if (!TryDecode(signature[..PublicKeySize], out var r))
        {
            return false;
        }

        var s = ReadLe(signature[PublicKeySize..]);
        if (s < 0 || s >= L)
        {
            return false;
        }

        var k = HashModL(signature[..PublicKeySize], publicKey, message);
        var left = ScalarMultiply(Base, s);
        var right = Add(r, ScalarMultiply(a, k));
        return left.X == right.X && left.Y == right.Y;
    }

    private static (BigInteger Scalar, byte[] Prefix) Expand(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedSize)
        {
            throw new ArgumentException("Ed25519 seed must be 32 bytes.", nameof(seed));
        }

        Span<byte> digest = stackalloc byte[64];
        SHA512.HashData(seed, digest);
        digest[0] &= 248;
        digest[31] &= 127;
        digest[31] |= 64;
        var scalar = ReadLe(digest[..32]);
        return (scalar, digest[32..].ToArray());
    }

    private static BigInteger HashModL(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Span<byte> hash = stackalloc byte[64];
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        incremental.AppendData(a);
        incremental.AppendData(b);
        incremental.TryGetHashAndReset(hash, out _);
        return ModL(ReadLe(hash));
    }

    private static BigInteger HashModL(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        Span<byte> hash = stackalloc byte[64];
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        incremental.AppendData(a);
        incremental.AppendData(b);
        incremental.AppendData(c);
        incremental.TryGetHashAndReset(hash, out _);
        return ModL(ReadLe(hash));
    }

    private static EdwardsPoint ScalarMultiply(EdwardsPoint point, BigInteger scalar)
    {
        var result = EdwardsPoint.Identity;
        var addend = point;
        while (scalar > 0)
        {
            if (!scalar.IsEven)
            {
                result = Add(result, addend);
            }

            addend = Add(addend, addend);
            scalar >>= 1;
        }

        return result;
    }

    private static EdwardsPoint Add(EdwardsPoint p, EdwardsPoint q)
    {
        var x1y2 = Mul(p.X, q.Y);
        var x2y1 = Mul(q.X, p.Y);
        var y1y2 = Mul(p.Y, q.Y);
        var x1x2 = Mul(p.X, q.X);
        var dProduct = Mul(D, Mul(x1x2, y1y2));
        var x3 = Mul(Mod(x1y2 + x2y1), ModInverse(Mod(BigInteger.One + dProduct)));
        var y3 = Mul(Mod(y1y2 + x1x2), ModInverse(Mod(BigInteger.One - dProduct)));
        return new EdwardsPoint(x3, y3);
    }

    private static bool TryDecode(ReadOnlySpan<byte> encoded, out EdwardsPoint point)
    {
        var signBit = (encoded[31] & 0x80) != 0;
        Span<byte> yBytes = stackalloc byte[32];
        encoded.CopyTo(yBytes);
        yBytes[31] &= 0x7F;
        var y = ReadLe(yBytes);
        if (y >= P || !TryRecoverX(y, signBit, out var x))
        {
            point = default;
            return false;
        }

        point = new EdwardsPoint(x, y);
        return true;
    }

    private static bool TryRecoverX(BigInteger y, bool signBit, out BigInteger x)
    {
        var y2 = Mul(y, y);
        var u = Mod(y2 - 1);
        var v = Mod((D * y2) + 1);
        var x2 = Mul(u, ModInverse(v));
        if (x2.IsZero)
        {
            x = BigInteger.Zero;
            return !signBit;
        }

        x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(Mul(x, x) - x2) != 0)
        {
            x = Mul(x, SqrtMinusOne);
        }

        if (Mod(Mul(x, x) - x2) != 0)
        {
            x = BigInteger.Zero;
            return false;
        }

        var xIsOdd = !x.IsEven;
        if (xIsOdd != signBit)
        {
            x = Mod(P - x);
        }

        return true;
    }

    private static BigInteger Mul(BigInteger left, BigInteger right) => Mod(left * right);

    private static BigInteger Mod(BigInteger value)
    {
        var remainder = value % P;
        return remainder.Sign < 0 ? remainder + P : remainder;
    }

    private static BigInteger ModL(BigInteger value)
    {
        var remainder = value % L;
        return remainder.Sign < 0 ? remainder + L : remainder;
    }

    private static BigInteger ModInverse(BigInteger value)
    {
        var positive = Mod(value);
        if (positive.IsZero)
        {
            throw new InvalidOperationException("Ed25519 field element has no inverse.");
        }

        return BigInteger.ModPow(positive, P - 2, P);
    }

    private static BigInteger ReadLe(ReadOnlySpan<byte> bytes)
    {
        Span<byte> littleEndian = stackalloc byte[bytes.Length + 1];
        bytes.CopyTo(littleEndian);
        return new BigInteger(littleEndian);
    }

    private static void WriteLe(BigInteger value, Span<byte> destination)
    {
        destination.Clear();
        Span<byte> raw = stackalloc byte[destination.Length + 1];
        if (!value.TryWriteBytes(raw, out var written, isUnsigned: true, isBigEndian: false))
        {
            throw new InvalidOperationException("Integer does not fit the Ed25519 field element.");
        }

        raw[..Math.Min(written, destination.Length)].CopyTo(destination);
    }

    private readonly struct EdwardsPoint
    {
        public EdwardsPoint(BigInteger x, BigInteger y)
        {
            X = x;
            Y = y;
        }

        public static EdwardsPoint Identity { get; } = new(BigInteger.Zero, BigInteger.One);

        public BigInteger X { get; }

        public BigInteger Y { get; }

        public bool IsIdentity => X.IsZero && Y == BigInteger.One;

        public void Encode(Span<byte> destination)
        {
            WriteLe(Y, destination);
            if (!X.IsEven)
            {
                destination[31] |= 0x80;
            }
        }
    }
}
