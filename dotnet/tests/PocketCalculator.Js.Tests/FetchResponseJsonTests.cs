using System.Globalization;
using System.Text;
using PocketCalculator.Js.Ops;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// SECURITY.md M4: op_fetch_url held about five copies of a body. The result is now written
/// from the bytes in two passes; the op protocol is a contract, so the JSON must be exactly
/// what the StringBuilder version produced.
/// </summary>
public sealed class FetchResponseJsonTests
{
    /// <summary>The builder <c>op_fetch_url</c> used before, kept as the oracle.</summary>
    private static string Reference(int status, byte[] body, string requestId, string url, bool redirected, bool opaque, Dictionary<string, string> headers)
    {
        var text = Encoding.UTF8.GetString(body);
        var base64 = Convert.ToBase64String(body);
        var result = new StringBuilder();
        result.Append("{\"status\":").Append(opaque ? "0" : status.ToString(CultureInfo.InvariantCulture));
        result.Append(",\"body\":");
        AppendJsonString(result, opaque ? string.Empty : text);
        result.Append(",\"bodyBase64\":");
        AppendJsonString(result, opaque ? string.Empty : base64);
        result.Append(",\"requestId\":");
        AppendJsonString(result, requestId);
        result.Append(",\"url\":");
        AppendJsonString(result, url);
        result.Append(",\"redirected\":").Append(redirected ? "true" : "false");
        result.Append(",\"opaque\":").Append(opaque ? "true" : "false");
        result.Append(",\"headers\":{");
        var first = true;
        foreach (var (key, value) in headers)
        {
            if (!first)
            {
                result.Append(',');
            }

            first = false;
            AppendJsonString(result, key);
            result.Append(':');
            AppendJsonString(result, value);
        }

        result.Append("}}");
        return result.ToString();
    }

    private static void AppendJsonString(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        output.Append("\\u00").Append("0123456789abcdef"[c >> 4]).Append("0123456789abcdef"[c & 0xF]);
                    }
                    else
                    {
                        output.Append(c);
                    }

                    break;
            }
        }

        output.Append('"');
    }

    public static TheoryData<byte[]> Bodies()
    {
        var random = new Random(7);
        var noise = new byte[100_000];
        random.NextBytes(noise);
        var controls = new byte[64];
        for (var i = 0; i < controls.Length; i++)
        {
            controls[i] = (byte)i;
        }

        // A four-byte sequence straddling the decoder's chunk boundary, many times over.
        var emoji = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("a\U0001F600\"\\é中", 20_000)));
        return new TheoryData<byte[]>
        {
            Array.Empty<byte>(),
            Encoding.UTF8.GetBytes("hello \"world\"\n\t\\ </script>"),
            controls,
            new byte[] { 0xFF, 0xFE, 0xC3, 0x28, 0xE2, 0x82, 0xF0, 0x9F, 0x98 },
            noise,
            emoji,
        };
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public void MatchesTheStringBuilderResult(byte[] body)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["content-type"] = "text/plain; charset=\"utf-8\"",
            ["x-a"] = "b\u0001",
        };
        foreach (var opaque in new[] { false, true })
        {
            var expected = Reference(200, body, "fetch-3", "https://example.com/a?b=\"c\"", true, opaque, opaque ? [] : headers);
            var actual = FetchOps.ScriptResponseJson(
                opaque ? "0" : "200", body, "fetch-3", "https://example.com/a?b=\"c\"", true, opaque, opaque ? new Dictionary<string, string>() : headers);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void BuildingTheResultAllocatesAboutTheResultOnce()
    {
        var body = Encoding.UTF8.GetBytes(new string('x', 4 * 1024 * 1024));
        var headers = new Dictionary<string, string>();
        FetchOps.ScriptResponseJson("200", body, "fetch-1", "https://example.com/", false, false, headers);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var json = FetchOps.ScriptResponseJson("200", body, "fetch-1", "https://example.com/", false, false, headers);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The result string alone is 2 bytes per char. The StringBuilder version held the
        // decoded body, its base64, a builder for both and the final copy: about 4x that.
        Assert.InRange(allocated, json.Length * 2L, (json.Length * 2L) + (1024 * 1024));
    }
}
