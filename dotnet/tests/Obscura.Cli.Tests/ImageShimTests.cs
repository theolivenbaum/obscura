using System.Text;
using Xunit;
using ApiBrowser = Obscura.Api.Browser;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura/tests/image_shim.rs</c>.
/// </summary>
/// <remarks>
/// Issue #394 (crash half): <c>new Image()</c> must survive a page that
/// pre-defines a non-configurable own <c>src</c> on <c>&lt;img&gt;</c> elements,
/// the way Booking.com's anti-bot instrumentation does. The Image shim used to
/// unconditionally redefine <c>src</c> on the element it just created, throwing
/// <c>TypeError: Cannot redefine property: src</c>.
/// </remarks>
public sealed class ImageShimTests
{
    /// <summary>A valid 1x1 RGBA PNG, byte for byte the Rust fixture's.</summary>
    private static readonly byte[] PixelPng =
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x48,
        0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00,
        0x00, 0x1f, 0x15, 0xc4, 0x89, 0x00, 0x00, 0x00, 0x0d, 0x49, 0x44, 0x41, 0x54, 0x78,
        0xda, 0x63, 0xfc, 0xcf, 0xc0, 0x50, 0x0f, 0x00, 0x05, 0x83, 0x02, 0x7f, 0x94, 0xff,
        0x2f, 0x59, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82,
    ];

    /// <summary>Serves the page everywhere except <c>/pixel.png</c>.</summary>
    private static LocalHttpServer Serve(string html) => new(target =>
        target.StartsWith("/pixel.png", StringComparison.Ordinal)
            ? ("image/png", PixelPng)
            : ("text/html", Encoding.UTF8.GetBytes(html)));

    [Fact]
    public async Task New_image_survives_non_configurable_src()
    {
        using var server = Serve(
            """
            <!doctype html><html><body><div id="r">waiting</div>
            <script>
              var origCreate = document.createElement.bind(document);
              document.createElement = function (tag) {
                var el = origCreate(tag);
                if (String(tag).toLowerCase() === 'img') {
                  Object.defineProperty(el, 'src', { value: '', writable: true, configurable: false });
                }
                return el;
              };
              var img = new Image(10, 20);
              document.getElementById('r').textContent =
                'survived w=' + img.width + ' h=' + img.height;
            </script>
            </body></html>
            """);

        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);

        Assert.Equal(
            "survived w=10 h=20",
            PageProbe.Text(page.Evaluate("document.getElementById('r').textContent")));
    }

    [Fact]
    public async Task New_image_still_emulates_load_when_src_is_configurable()
    {
        using var server = Serve(
            """
            <!doctype html><html><body><div id="r">waiting</div>
            <script>
              var img = new Image();
              img.onload = function () {
                document.getElementById('r').textContent = 'loaded complete=' + img.complete;
              };
              img.src = '/pixel.png';
            </script>
            </body></html>
            """);

        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);
        // The shim fires `load` on a setTimeout(0); pump the event loop.
        await PageProbe.SettleUntilAsync(
            page,
            () => Probe(page).StartsWith("loaded", StringComparison.Ordinal),
            10,
            500);

        Assert.Equal("loaded complete=true", Probe(page));
    }

    [Fact]
    public async Task Invalid_image_bytes_emit_error_like_chromium()
    {
        using var server = Serve(
            """
            <!doctype html><html><body><div id="r">waiting</div>
            <script>
              var img = new Image();
              img.onerror = function () {
                document.getElementById('r').textContent =
                  'error complete=' + img.complete +
                  ' natural=' + img.naturalWidth + 'x' + img.naturalHeight;
              };
              img.src = '/broken.png';
            </script>
            </body></html>
            """);

        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);
        await PageProbe.SettleUntilAsync(
            page,
            () => Probe(page).StartsWith("error", StringComparison.Ordinal),
            10,
            500);

        Assert.Equal("error complete=true natural=0x0", Probe(page));
    }

    private static string Probe(Obscura.Api.Page page) =>
        PageProbe.Text(page.Evaluate("document.getElementById('r').textContent")) ?? string.Empty;
}
