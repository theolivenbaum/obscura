using System.Security.Cryptography;
using Obscura.Js.Ops;
using Xunit;

namespace Obscura.Js.Tests;

public sealed class CryptoOpsTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    // FIPS 180-4 published vectors for the truncated SHA-512 variants, which
    // the BCL does not implement.
    [Theory]
    [InlineData("SHA-512/224", "abc", "4634270f707b6a54daae7530460842e20e37ed265ceee9a43e8924aa")]
    [InlineData("SHA-512/256", "abc", "53048e2681941ef99b2e29b76b4c7dabe4c2d0c634fc6d46e0e2f13107e7af23")]
    [InlineData("SHA-1", "abc", "a9993e364706816aba3e25717850c26c9cd0d89d")]
    [InlineData("SHA-256", "abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void Digest_matches_published_vectors(string algorithm, string message, string expected) =>
        Assert.Equal(expected, Hex(CryptoOps.Digest(algorithm, Ascii(message))));

    [Theory]
    [InlineData("SHA-512/224", "", "6ed0dd02806fa89e25de060c19d3ac86cabb87d6a0ddd05c333b84f4")]
    [InlineData("SHA-512/256", "", "c672b8d1ef56ed28ab87c3622c5114069bdd3ad7b8f9737498d0c01ecef0967a")]
    public void Digest_truncated_variants_handle_empty_input(string algorithm, string message, string expected) =>
        Assert.Equal(expected, Hex(CryptoOps.Digest(algorithm, Ascii(message))));

    [Fact]
    public void Digest_truncated_variants_span_multiple_blocks()
    {
        // 1000 'a' bytes: forces the multi-block path and a padded tail that
        // spills into a second padding block.
        var input = new byte[1000];
        input.AsSpan().Fill((byte)'a');
        Assert.Equal(28, CryptoOps.Digest("SHA-512/224", input).Length);
        Assert.Equal(32, CryptoOps.Digest("SHA-512/256", input).Length);

        // A 112-byte message is the exact boundary where padding needs a second block.
        var boundary = new byte[112];
        Assert.Equal(32, CryptoOps.Digest("SHA-512/256", boundary).Length);
    }

    [Fact]
    public void Digest_returns_empty_for_unknown_algorithm() =>
        Assert.Empty(CryptoOps.Digest("SHA-3", Ascii("abc")));

    // RFC 4231 test case 1.
    [Fact]
    public void Hmac_matches_rfc4231()
    {
        var key = Hex("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
        var data = Ascii("Hi There");
        Assert.Equal("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7",
            Hex(CryptoOps.Hmac("SHA-256", key, data)));
    }

    [Fact]
    public void Hmac_rejects_unsupported_hash() =>
        Assert.Throws<CryptoOperationException>(() => CryptoOps.Hmac("MD5", [1], [2]));

    [Fact]
    public void AesGcm_roundtrips_and_appends_the_tag()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] iv = RandomNumberGenerator.GetBytes(12);
        byte[] aad = Ascii("header");
        byte[] plain = Ascii("obscura");

        var cipher = CryptoOps.AesGcm(encrypt: true, key, iv, aad, plain);
        // WebCrypto's ciphertext carries the 128-bit tag appended.
        Assert.Equal(plain.Length + 16, cipher.Length);
        Assert.Equal(plain, CryptoOps.AesGcm(encrypt: false, key, iv, aad, cipher));
    }

    [Fact]
    public void AesGcm_fails_on_a_tampered_tag()
    {
        byte[] key = RandomNumberGenerator.GetBytes(16);
        byte[] iv = RandomNumberGenerator.GetBytes(12);
        var cipher = CryptoOps.AesGcm(true, key, iv, [], Ascii("obscura"));
        cipher[^1] ^= 0xFF;
        Assert.Throws<CryptoOperationException>(() => CryptoOps.AesGcm(false, key, iv, [], cipher));
    }

    [Fact]
    public void AesGcm_rejects_a_non_96_bit_iv() =>
        Assert.Throws<CryptoOperationException>(
            () => CryptoOps.AesGcm(true, RandomNumberGenerator.GetBytes(16), new byte[16], [], [1]));

    [Fact]
    public void AesCbc_roundtrips_with_pkcs7()
    {
        byte[] key = RandomNumberGenerator.GetBytes(24);
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        byte[] plain = Ascii("exactly-sixteen!");
        var cipher = CryptoOps.AesCbc(true, key, iv, plain);
        // PKCS#7 always adds a full block when the input is block-aligned.
        Assert.Equal(32, cipher.Length);
        Assert.Equal(plain, CryptoOps.AesCbc(false, key, iv, cipher));
    }

    [Fact]
    public void AesCbc_fails_on_invalid_padding()
    {
        byte[] key = RandomNumberGenerator.GetBytes(16);
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        var cipher = CryptoOps.AesCbc(true, key, iv, Ascii("obscura"));
        cipher[^1] ^= 0xFF;
        Assert.Throws<CryptoOperationException>(() => CryptoOps.AesCbc(false, key, iv, cipher));
    }

    [Fact]
    public void AesCbc_rejects_a_bad_iv_length() =>
        Assert.Throws<CryptoOperationException>(
            () => CryptoOps.AesCbc(true, RandomNumberGenerator.GetBytes(16), new byte[8], [1]));

    // NIST SP 800-38A F.5.1, AES-128-CTR, first two blocks.
    [Fact]
    public void AesCtr_matches_nist_sp800_38a()
    {
        var key = Hex("2b7e151628aed2a6abf7158809cf4f3c");
        var counter = Hex("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
        var plain = Hex("6bc1bee22e409f96e93d7e117393172a" + "ae2d8a571e03ac9c9eb76fac45af8e51");
        var expected = "874d6191b620e3261bef6864990db6ce" + "9806f66b7970fdff8617187bb9fffdff";
        Assert.Equal(expected, Hex(CryptoOps.AesCtr(key, counter, 128, plain)));
    }

    [Fact]
    public void AesCtr_is_its_own_inverse()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] counter = RandomNumberGenerator.GetBytes(16);
        byte[] plain = RandomNumberGenerator.GetBytes(70);
        var cipher = CryptoOps.AesCtr(key, counter, 64, plain);
        Assert.Equal(plain, CryptoOps.AesCtr(key, counter, 64, cipher));
    }

    [Fact]
    public void AesCtr_wraps_within_the_counter_window()
    {
        // A counter whose low 8 bits are all ones must wrap to zero and leave
        // the nonce prefix untouched, rather than carrying into it.
        byte[] key = RandomNumberGenerator.GetBytes(16);
        var counter = Hex("000000000000000000000000000000ff");
        var wrapped = Hex("00000000000000000000000000000000");
        byte[] data = new byte[32];

        var withWrap = CryptoOps.AesCtr(key, counter, 8, data);
        // Block 2 of the wrapping run must equal block 1 of a run starting at 0.
        var fromZero = CryptoOps.AesCtr(key, wrapped, 8, data);
        Assert.Equal(fromZero.AsSpan(0, 16).ToArray(), withWrap.AsSpan(16, 16).ToArray());
    }

    [Fact]
    public void AesCtr_rejects_a_bad_counter_block_or_width()
    {
        byte[] key = RandomNumberGenerator.GetBytes(16);
        Assert.Throws<CryptoOperationException>(() => CryptoOps.AesCtr(key, new byte[8], 64, [1]));
        Assert.Throws<CryptoOperationException>(() => CryptoOps.AesCtr(key, new byte[16], 0, [1]));
        Assert.Throws<CryptoOperationException>(() => CryptoOps.AesCtr(key, new byte[16], 129, [1]));
    }

    // RFC 6070 test case 2.
    [Fact]
    public void Pbkdf2_matches_rfc6070() =>
        Assert.Equal("ea6c014dc72d6f8ccd1ed92ace1d41f0d8de8957",
            Hex(CryptoOps.Pbkdf2("SHA-1", Ascii("password"), Ascii("salt"), 2, 20)));

    // RFC 5869 test case 1.
    [Fact]
    public void Hkdf_matches_rfc5869()
    {
        var ikm = Hex("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
        var salt = Hex("000102030405060708090a0b0c");
        var info = Hex("f0f1f2f3f4f5f6f7f8f9");
        Assert.Equal("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865",
            Hex(CryptoOps.Hkdf("SHA-256", ikm, salt, info, 42)));
    }

    [Fact]
    public void Hkdf_with_an_empty_salt_zero_pads_per_rfc5869()
    {
        // An empty salt must behave as HMAC with a zero-filled block, which is
        // what browsers do; it must not throw or fall back to a random salt.
        var derived = CryptoOps.Hkdf("SHA-256", Ascii("ikm"), [], [], 32);
        Assert.Equal(32, derived.Length);
        Assert.Equal(Hex(derived), Hex(CryptoOps.Hkdf("SHA-256", Ascii("ikm"), [], [], 32)));
    }

    [Fact]
    public void RandomBytes_returns_the_requested_length()
    {
        Assert.Empty(CryptoOps.RandomBytes(0));
        Assert.Equal(64, CryptoOps.RandomBytes(64).Length);
        Assert.NotEqual(Hex(CryptoOps.RandomBytes(32)), Hex(CryptoOps.RandomBytes(32)));
    }
}
