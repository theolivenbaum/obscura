using System.Buffers.Binary;

namespace Obscura.Js.Ops;

/// <summary>
/// The FIPS 180-4 truncated SHA-512 variants, SHA-512/224 and SHA-512/256.
/// </summary>
/// <remarks>
/// The BCL ships SHA-1/256/384/512 but not these two, and WebCrypto exposes
/// them, so the compression function is implemented here. They are ordinary
/// SHA-512 with a different initial hash value (FIPS 180-4 section 5.3.6) and
/// the digest truncated to the leading 28 or 32 bytes. Not a hot path: this
/// runs only when page script explicitly asks for one of these two algorithms.
/// </remarks>
internal static class Sha512Truncated
{
    internal enum Variant
    {
        T224,
        T256,
    }

    // FIPS 180-4 section 5.3.6.1 / 5.3.6.2: the IVs are SHA-512 run over
    // "SHA-512/t" with each word of the standard IV XORed with 0xa5a5a5a5a5a5a5a5.
    private static ReadOnlySpan<ulong> Iv224 =>
    [
        0x8C3D37C819544DA2, 0x73E1996689DCD4D6, 0x1DFAB7AE32FF9C82, 0x679DD514582F9FCF,
        0x0F6D2B697BD44DA8, 0x77E36F7304C48942, 0x3F9D85A86A1D36C8, 0x1112E6AD91D692A1,
    ];

    private static ReadOnlySpan<ulong> Iv256 =>
    [
        0x22312194FC2BF72C, 0x9F555FA3C84C64C2, 0x2393B86B6F53B151, 0x963877195940EABD,
        0x96283EE2A88EFFE3, 0xBE5E1E2553863992, 0x2B0199FC2C85B8AA, 0x0EB72DDC81C52CA2,
    ];

    private static ReadOnlySpan<ulong> K =>
    [
        0x428A2F98D728AE22, 0x7137449123EF65CD, 0xB5C0FBCFEC4D3B2F, 0xE9B5DBA58189DBBC,
        0x3956C25BF348B538, 0x59F111F1B605D019, 0x923F82A4AF194F9B, 0xAB1C5ED5DA6D8118,
        0xD807AA98A3030242, 0x12835B0145706FBE, 0x243185BE4EE4B28C, 0x550C7DC3D5FFB4E2,
        0x72BE5D74F27B896F, 0x80DEB1FE3B1696B1, 0x9BDC06A725C71235, 0xC19BF174CF692694,
        0xE49B69C19EF14AD2, 0xEFBE4786384F25E3, 0x0FC19DC68B8CD5B5, 0x240CA1CC77AC9C65,
        0x2DE92C6F592B0275, 0x4A7484AA6EA6E483, 0x5CB0A9DCBD41FBD4, 0x76F988DA831153B5,
        0x983E5152EE66DFAB, 0xA831C66D2DB43210, 0xB00327C898FB213F, 0xBF597FC7BEEF0EE4,
        0xC6E00BF33DA88FC2, 0xD5A79147930AA725, 0x06CA6351E003826F, 0x142929670A0E6E70,
        0x27B70A8546D22FFC, 0x2E1B21385C26C926, 0x4D2C6DFC5AC42AED, 0x53380D139D95B3DF,
        0x650A73548BAF63DE, 0x766A0ABB3C77B2A8, 0x81C2C92E47EDAEE6, 0x92722C851482353B,
        0xA2BFE8A14CF10364, 0xA81A664BBC423001, 0xC24B8B70D0F89791, 0xC76C51A30654BE30,
        0xD192E819D6EF5218, 0xD69906245565A910, 0xF40E35855771202A, 0x106AA07032BBD1B8,
        0x19A4C116B8D2D0C8, 0x1E376C085141AB53, 0x2748774CDF8EEB99, 0x34B0BCB5E19B48A8,
        0x391C0CB3C5C95A63, 0x4ED8AA4AE3418ACB, 0x5B9CCA4F7763E373, 0x682E6FF3D6B2B8A3,
        0x748F82EE5DEFB2FC, 0x78A5636F43172F60, 0x84C87814A1F0AB72, 0x8CC702081A6439EC,
        0x90BEFFFA23631E28, 0xA4506CEBDE82BDE9, 0xBEF9A3F7B2C67915, 0xC67178F2E372532B,
        0xCA273ECEEA26619C, 0xD186B8C721C0C207, 0xEADA7DD6CDE0EB1E, 0xF57D4F7FEE6ED178,
        0x06F067AA72176FBA, 0x0A637DC5A2C898A6, 0x113F9804BEF90DAE, 0x1B710B35131C471B,
        0x28DB77F523047D84, 0x32CAAB7B40C72493, 0x3C9EBE0A15C9BEBC, 0x431D67C49C100D4C,
        0x4CC5D4BECB3E42B6, 0x597F299CFC657E2A, 0x5FCB6FAB3AD6FAEC, 0x6C44198C4A475817,
    ];

    internal static byte[] Hash(ReadOnlySpan<byte> data, Variant variant)
    {
        Span<ulong> h = stackalloc ulong[8];
        (variant == Variant.T224 ? Iv224 : Iv256).CopyTo(h);

        // Message schedule and padded tail live on the stack; the tail is at
        // most two blocks (message remainder + 0x80 + length field).
        Span<byte> tail = stackalloc byte[256];
        var full = data.Length - (data.Length % 128);

        for (var offset = 0; offset < full; offset += 128)
        {
            Compress(h, data.Slice(offset, 128));
        }

        var remainder = data[full..];
        remainder.CopyTo(tail);
        var tailLength = remainder.Length;
        tail[tailLength++] = 0x80;

        // The length field is 128 bits; messages here never approach 2^64 bits,
        // so the high half stays zero.
        var padded = tailLength <= 112 ? 128 : 256;
        tail[tailLength..padded].Clear();
        BinaryPrimitives.WriteUInt64BigEndian(tail[(padded - 8)..], (ulong)data.Length * 8);

        for (var offset = 0; offset < padded; offset += 128)
        {
            Compress(h, tail.Slice(offset, 128));
        }

        Span<byte> digest = stackalloc byte[64];
        for (var i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(digest[(i * 8)..], h[i]);
        }
        return digest[..(variant == Variant.T224 ? 28 : 32)].ToArray();
    }

    private static void Compress(Span<ulong> h, ReadOnlySpan<byte> block)
    {
        Span<ulong> w = stackalloc ulong[80];
        for (var i = 0; i < 16; i++)
        {
            w[i] = BinaryPrimitives.ReadUInt64BigEndian(block[(i * 8)..]);
        }
        for (var i = 16; i < 80; i++)
        {
            var s0 = RotateRight(w[i - 15], 1) ^ RotateRight(w[i - 15], 8) ^ (w[i - 15] >> 7);
            var s1 = RotateRight(w[i - 2], 19) ^ RotateRight(w[i - 2], 61) ^ (w[i - 2] >> 6);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }

        ulong a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];

        for (var i = 0; i < 80; i++)
        {
            var s1 = RotateRight(e, 14) ^ RotateRight(e, 18) ^ RotateRight(e, 41);
            var ch = (e & f) ^ (~e & g);
            var temp1 = hh + s1 + ch + K[i] + w[i];
            var s0 = RotateRight(a, 28) ^ RotateRight(a, 34) ^ RotateRight(a, 39);
            var maj = (a & b) ^ (a & c) ^ (b & c);
            var temp2 = s0 + maj;

            hh = g; g = f; f = e; e = d + temp1;
            d = c; c = b; b = a; a = temp1 + temp2;
        }

        h[0] += a; h[1] += b; h[2] += c; h[3] += d;
        h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
    }

    private static ulong RotateRight(ulong value, int bits) => (value >> bits) | (value << (64 - bits));
}
