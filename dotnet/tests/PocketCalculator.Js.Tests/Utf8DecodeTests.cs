using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// M4: <c>Response.text()</c> decoded one character at a time, a V8 rope of one cons cell per
/// character (several GB for a 100 MB body). The chunked decoder must give the same string.
/// </summary>
public sealed class Utf8DecodeTests
{
    private const string OldDecoder = """
        (bytes, start) => {
          let str = '', i = start | 0;
          const n = bytes.length;
          while (i < n) {
            let c = bytes[i++];
            if (c < 0x80) str += String.fromCharCode(c);
            else if (c < 0xE0) str += String.fromCharCode(((c & 0x1F) << 6) | (bytes[i++] & 0x3F));
            else if (c < 0xF0) { const b1 = bytes[i++], b2 = bytes[i++]; str += String.fromCharCode(((c & 0x0F) << 12) | ((b1 & 0x3F) << 6) | (b2 & 0x3F)); }
            else { const b1 = bytes[i++], b2 = bytes[i++], b3 = bytes[i++]; const cp = ((c & 0x07) << 18) | ((b1 & 0x3F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F); if (cp > 0xFFFF) { const s = cp - 0x10000; str += String.fromCharCode(0xD800 + (s >> 10), 0xDC00 + (s & 0x3FF)); } else str += String.fromCharCode(cp); }
          }
          return str;
        }
        """;

    [Fact]
    public void ChunkedDecodeMatchesTheCharacterAtATimeDecode()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        string result = fixture.Runtime.Evaluate($$"""
            (() => { try {
              const old = {{OldDecoder}};
              const cases = [[], [0x41], [0xEF, 0xBB, 0xBF, 0x41], [0xFF, 0xFE, 0xC3], [0xE2, 0x82], [0xF0, 0x9F, 0x98, 0x80], [0xF7, 0xBF, 0xBF, 0xBF]];
              let seed = 7;
              const noise = new Uint8Array(50000);
              for (let i = 0; i < noise.length; i++) { seed = (seed * 1103515245 + 12345) & 0x7fffffff; noise[i] = seed & 0xff; }
              cases.push(Array.from(noise));
              cases.push(Array.from(new TextEncoder().encode('a\u{1F600}"\\é中'.repeat(5000))));
              for (const c of cases) {
                const bytes = new Uint8Array(c);
                for (const start of [0, 3]) {
                  const sub = bytes.subarray(Math.min(start, bytes.length));
                  const bom = sub.length >= 3 && sub[0] === 0xEF && sub[1] === 0xBB && sub[2] === 0xBF ? 3 : 0;
                  if (new TextDecoder().decode(sub) !== old(sub, bom)) return 'mismatch at ' + c.length + '/' + start;
                }
              }
              return String(new TextDecoder().decode(new Uint8Array(20000).fill(0x61)).length);
            } catch (e) { return String(e); } })()
            """)?.ToString() ?? "null";
        Assert.Equal("20000", result);
    }
}
