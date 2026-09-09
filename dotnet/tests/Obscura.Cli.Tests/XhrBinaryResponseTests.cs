using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using ApiBrowser = Obscura.Api.Browser;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura/tests/xhr_binary_response.rs</c>.
/// </summary>
/// <remarks>
/// XHR with <c>responseType = 'arraybuffer'</c> must return the response bytes
/// unchanged. XHR is implemented over fetch and used to read every body with
/// <c>resp.text()</c>, then rebuild the binary response types from that string;
/// <c>text()</c> then <c>TextEncoder().encode()</c> is not a round trip, so every
/// byte >= 0x80 was rewritten. The fixture is 256 bytes holding every byte value,
/// and the assertion is on the bytes rather than only the length, because a
/// same-length corruption would otherwise pass.
/// </remarks>
public sealed class XhrBinaryResponseTests
{
    [Fact]
    public async Task Xhr_arraybuffer_returns_the_response_bytes_unchanged()
    {
        var allBytes = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            allBytes[i] = (byte)i;
        }

        using var server = new LocalHttpServer(
            target => target.StartsWith("/bytes.bin", StringComparison.Ordinal)
                ? ("application/octet-stream", allBytes)
                : ("text/html", Encoding.UTF8.GetBytes(
                    "<!doctype html><html><head><title>fixture</title></head><body></body></html>")),
            cors: true);

        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync($"{server.Base}/page");

        page.Evaluate(
            """
            (function () {
                var out = document.createElement('pre');
                out.id = 'probe-results';
                document.body.appendChild(out);
                function done(v) {
                    out.textContent = JSON.stringify(v);
                    document.body.setAttribute('data-done', '1');
                }
                var x = new XMLHttpRequest();
                x.open('GET', 'bytes.bin', true);
                x.responseType = 'arraybuffer';
                x.onload = function () {
                    var u8 = new Uint8Array(x.response);
                    done({ length: u8.length, bytes: Array.prototype.slice.call(u8) });
                };
                x.onerror = function () { done({ error: 'xhr failed' }); };
                x.send();
            })()
            """);

        await PageProbe.SettleUntilDoneAsync(page);
        Assert.Equal("1", PageProbe.DoneMarker(page));

        var result = PageProbe.ProbeResults(page);
        Assert.NotNull(result);
        Assert.True(
            result!["error"] is null || result["error"]!.GetValueKind() == JsonValueKind.Null,
            $"probe reported {result["error"]}");
        Assert.Equal(256.0, PageProbe.Number(result["length"]));

        var got = Assert.IsType<JsonArray>(result["bytes"]);
        Assert.Equal(256, got.Count);
        for (var i = 0; i < 256; i++)
        {
            // Asserted per byte so the failure names the first divergence, the
            // way the Rust test's first_diff does.
            Assert.Equal((double)i, PageProbe.Number(got[i]));
        }
    }
}
