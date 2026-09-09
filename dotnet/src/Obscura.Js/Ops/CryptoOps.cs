using System.Security.Cryptography;

namespace Obscura.Js.Ops;

/// <summary>
/// WebCrypto (<c>crypto.subtle</c>) secret-key primitives, backing the ops of
/// the same name.
/// </summary>
/// <remarks>
/// These ops are stateless. The JS shim owns the <c>CryptoKey</c> objects and
/// their raw key bytes, and hands the bytes plus normalized algorithm parameters
/// here for each operation. Only secret-key algorithms live here (HMAC,
/// AES-GCM/CBC/CTR, PBKDF2, HKDF); public-key algorithms are rejected in the
/// shim. A failure throws <see cref="CryptoOperationException"/>, which the op
/// layer surfaces to the shim as the DOMException it expects.
/// </remarks>
public static class CryptoOps
{
    /// <summary>
    /// <c>crypto.subtle.digest</c>. The shim validates the algorithm name, so an
    /// unrecognized value is unreachable and mirrors Rust by returning empty.
    /// </summary>
    public static byte[] Digest(string algorithm, ReadOnlySpan<byte> data) =>
        algorithm.ToUpperInvariant() switch
        {
            "SHA-1" => SHA1.HashData(data),
            "SHA-256" => SHA256.HashData(data),
            "SHA-384" => SHA384.HashData(data),
            "SHA-512" => SHA512.HashData(data),
            // Not in the BCL: FIPS 180-4 truncated SHA-512 variants.
            "SHA-512/224" => Sha512Truncated.Hash(data, Sha512Truncated.Variant.T224),
            "SHA-512/256" => Sha512Truncated.Hash(data, Sha512Truncated.Variant.T256),
            _ => [],
        };

    /// <summary>
    /// HMAC sign. Any key length is accepted (HMAC pads or hashes the key per
    /// RFC 2104). Returns the MAC bytes; the shim does the compare for verify.
    /// </summary>
    public static byte[] Hmac(string hash, byte[] key, ReadOnlySpan<byte> data) => hash switch
    {
        "SHA-1" => HMACSHA1.HashData(key, data),
        "SHA-256" => HMACSHA256.HashData(key, data),
        "SHA-384" => HMACSHA384.HashData(key, data),
        "SHA-512" => HMACSHA512.HashData(key, data),
        _ => throw new CryptoOperationException("unsupported HMAC hash"),
    };

    /// <summary>
    /// AES-GCM. WebCrypto's ciphertext carries the auth tag appended; this
    /// splits and rejoins it. Restricted to a 96-bit IV and 128-bit tag, the
    /// WebCrypto defaults; the shim rejects other tag lengths.
    /// </summary>
    public static byte[] AesGcm(bool encrypt, byte[] key, byte[] iv, byte[] aad, byte[] data)
    {
        const int TagLength = 16;
        if (iv.Length != 12)
        {
            throw new CryptoOperationException("AES-GCM requires a 96-bit (12-byte) IV");
        }
        RequireAesKeyLength(key, "AES-GCM");

        using var gcm = new System.Security.Cryptography.AesGcm(key, TagLength);
        if (encrypt)
        {
            var cipher = new byte[data.Length + TagLength];
            gcm.Encrypt(iv, data, cipher.AsSpan(0, data.Length), cipher.AsSpan(data.Length), aad);
            return cipher;
        }

        if (data.Length < TagLength)
        {
            throw new CryptoOperationException("AES-GCM decryption failed");
        }
        var plain = new byte[data.Length - TagLength];
        try
        {
            gcm.Decrypt(iv, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), plain, aad);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new CryptoOperationException("AES-GCM decryption failed");
        }
        return plain;
    }

    /// <summary>AES-CBC with PKCS#7 padding.</summary>
    public static byte[] AesCbc(bool encrypt, byte[] key, byte[] iv, byte[] data)
    {
        if (iv.Length != 16)
        {
            throw new CryptoOperationException("AES-CBC requires a 16-byte IV");
        }
        RequireAesKeyLength(key, "AES-CBC");

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        try
        {
            return encrypt
                ? aes.EncryptCbc(data, iv, PaddingMode.PKCS7)
                : aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            throw new CryptoOperationException("AES-CBC decryption failed: invalid padding");
        }
    }

    /// <summary>
    /// AES-CTR. Encrypt and decrypt are the same keystream XOR.
    /// <paramref name="counterLength"/> is the WebCrypto counter width in bits:
    /// only the low <paramref name="counterLength"/> bits of the 16-byte block
    /// increment, and they wrap within that window.
    /// </summary>
    public static byte[] AesCtr(byte[] key, byte[] counter, uint counterLength, byte[] data)
    {
        if (counter.Length != 16)
        {
            throw new CryptoOperationException("AES-CTR requires a 16-byte counter block");
        }
        if (counterLength is 0 or > 128)
        {
            throw new CryptoOperationException("AES-CTR counter length must be between 1 and 128 bits");
        }
        RequireAesKeyLength(key, "AES-CTR");

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        var output = new byte[data.Length];
        Span<byte> block = stackalloc byte[16];
        Span<byte> keystream = stackalloc byte[16];
        counter.CopyTo(block);

        for (var offset = 0; offset < data.Length; offset += 16)
        {
            aes.EncryptEcb(block, keystream, PaddingMode.None);
            var n = Math.Min(16, data.Length - offset);
            for (var i = 0; i < n; i++)
            {
                output[offset + i] = (byte)(data[offset + i] ^ keystream[i]);
            }
            IncrementCounter(block, (int)counterLength);
        }
        return output;
    }

    /// <summary>
    /// Generous upper bounds on PBKDF2 parameters.
    /// </summary>
    /// <remarks>
    /// WebCrypto imposes no limit, but page JS drives this op on the single-threaded
    /// runtime: an unbounded iteration count pins the V8 isolate (blocking every other
    /// CDP command on the connection) and a huge output length forces an unbounded
    /// allocation. Both caps sit far above any legitimate use - OWASP recommends
    /// ~600k iterations and derived keys are tens of bytes.
    /// </remarks>
    public const uint Pbkdf2MaxIterations = 10_000_000;

    /// <inheritdoc cref="Pbkdf2MaxIterations"/>
    public const uint Pbkdf2MaxOutputBytes = 1024 * 1024;

    /// <summary>PBKDF2. <paramref name="length"/> is the output in bytes.</summary>
    public static byte[] Pbkdf2(string hash, byte[] password, byte[] salt, uint iterations, uint length)
    {
        if (iterations > Pbkdf2MaxIterations)
        {
            throw new CryptoOperationException(
                $"PBKDF2 iteration count {iterations} exceeds the supported maximum of {Pbkdf2MaxIterations}");
        }

        if (length > Pbkdf2MaxOutputBytes)
        {
            throw new CryptoOperationException(
                $"PBKDF2 output length {length} bytes exceeds the supported maximum of {Pbkdf2MaxOutputBytes}");
        }

        return Rfc2898DeriveBytes.Pbkdf2(
            password, salt, (int)iterations, HashName(hash, "PBKDF2"), (int)length);
    }

    /// <summary>
    /// HKDF. <paramref name="length"/> is the output in bytes. An empty salt
    /// behaves as RFC 5869 specifies (HMAC zero-pads it to the block size).
    /// </summary>
    public static byte[] Hkdf(string hash, byte[] ikm, byte[] salt, byte[] info, uint length)
    {
        try
        {
            return HKDF.DeriveKey(HashName(hash, "HKDF"), ikm, (int)length, salt, info);
        }
        catch (ArgumentException)
        {
            throw new CryptoOperationException("HKDF: requested key length is too long");
        }
    }

    /// <summary>
    /// Fills <paramref name="length"/> bytes from the OS CSPRNG. Backs
    /// <c>crypto.getRandomValues</c>, <c>crypto.randomUUID</c>, and key
    /// generation. Must stay cryptographically random: a Math.random-style shim
    /// is both non-uniform across typed-array widths and a fingerprinting tell.
    /// </summary>
    public static byte[] RandomBytes(uint length) => RandomNumberGenerator.GetBytes((int)length);

    private static void RequireAesKeyLength(byte[] key, string algorithm)
    {
        if (key.Length is not (16 or 24 or 32))
        {
            throw new CryptoOperationException($"{algorithm} key must be 128, 192, or 256 bits");
        }
    }

    private static HashAlgorithmName HashName(string hash, string algorithm) => hash switch
    {
        "SHA-1" => HashAlgorithmName.SHA1,
        "SHA-256" => HashAlgorithmName.SHA256,
        "SHA-384" => HashAlgorithmName.SHA384,
        "SHA-512" => HashAlgorithmName.SHA512,
        _ => throw new CryptoOperationException($"unsupported {algorithm} hash"),
    };

    /// <summary>
    /// Increments the low <paramref name="bits"/> bits of a big-endian counter
    /// block, wrapping within that window and leaving the nonce prefix intact.
    /// </summary>
    private static void IncrementCounter(Span<byte> block, int bits)
    {
        var fullBytes = bits / 8;
        var partialBits = bits % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            var idx = block.Length - 1 - i;
            if (++block[idx] != 0)
            {
                return;
            }
        }

        if (partialBits == 0)
        {
            return;
        }

        // The partially-covered byte: increment only its low bits, wrapping
        // inside the mask so the untouched high bits stay part of the nonce.
        var partialIdx = block.Length - 1 - fullBytes;
        var mask = (byte)((1 << partialBits) - 1);
        var incremented = (byte)(((block[partialIdx] & mask) + 1) & mask);
        block[partialIdx] = (byte)((block[partialIdx] & ~mask) | incremented);
    }
}

/// <summary>
/// A WebCrypto operation failure. The shim maps this to the appropriate
/// DOMException (OperationError for a bad tag or padding, and so on).
/// </summary>
public sealed class CryptoOperationException(string message) : Exception(message);
