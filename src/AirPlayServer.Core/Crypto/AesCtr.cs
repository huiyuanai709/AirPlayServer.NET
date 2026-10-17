using System.Security.Cryptography;

namespace AirPlayServer.Core.Crypto;

/// <summary>
/// AES-128-CTR with a 128-bit big-endian counter. The runtime AES
/// implementation selects AES-NI or the platform equivalent; this type only
/// owns the counter and the XOR. Pair-verify uses it to mask Ed25519 signatures.
/// </summary>
internal sealed class AesCtr : IDisposable
{
    private readonly Aes _aes;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _keystream = new byte[16];
    private int _offset = 16;

    public AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != 16 || iv.Length != 16)
        {
            throw new ArgumentException("AES-128-CTR requires a 16-byte key and a 16-byte counter block.");
        }

        _aes = Aes.Create();
        _aes.Key = key.ToArray();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        iv.CopyTo(_counter);
    }

    public void Transform(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length < input.Length)
        {
            throw new ArgumentException("CTR output is shorter than the input.", nameof(output));
        }

        for (var i = 0; i < input.Length; i++)
        {
            if (_offset == 16)
            {
                var written = _aes.EncryptEcb(_counter, _keystream, PaddingMode.None);
                if (written != 16)
                {
                    throw new CryptographicException("AES-ECB did not return one block.");
                }

                Increment(_counter);
                _offset = 0;
            }

            output[i] = (byte)(input[i] ^ _keystream[_offset++]);
        }
    }

    public void Skip(int byteCount)
    {
        if (byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        Span<byte> discarded = stackalloc byte[64];
        while (byteCount > 0)
        {
            var count = Math.Min(byteCount, discarded.Length);
            Transform(discarded[..count], discarded[..count]);
            byteCount -= count;
        }
    }

    public void Dispose() => _aes.Dispose();

    private static void Increment(Span<byte> counter)
    {
        for (var i = counter.Length - 1; i >= 0; i--)
        {
            counter[i]++;
            if (counter[i] != 0)
            {
                return;
            }
        }
    }
}
