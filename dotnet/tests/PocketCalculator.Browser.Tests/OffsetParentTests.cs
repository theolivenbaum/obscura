using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// HTMLElement's offsetParent, offsetTop and offsetLeft. The shim had no offsetParent and
/// gave offsetTop/offsetLeft as the viewport position, so code that sums offsets up the
/// offsetParent chain got nothing to walk. Expected values measured in Chromium
/// 141.0.7390.37 (headless, Playwright) on the same markup; widths are left out, since
/// they depend on glyph advances.
/// </summary>
public sealed class OffsetParentTests
{
    private const string Markup = """
        <!doctype html><html><head></head><body>
        <div id=a style="margin:10px;padding:5px;border:3px solid">
          <div id=b style="position:relative;margin:7px;border:4px solid;padding:2px">
            <span id=c style="display:inline-block;margin-left:11px">x</span>
            <table id=t style="margin:6px"><tr><td id=td style="padding:3px"><i id=i>y</i></td></tr></table>
          </div>
          <div id=f style="position:fixed;top:20px;left:30px">fixed</div>
          <div id=n style="display:none"><b id=nb>z</b></div>
        </div></body></html>
        """;

    [Fact]
    public async Task OffsetsAreMeasuredFromTheOffsetParent()
    {
        BrowserContext context = BrowserContext.WithStorageAndNetwork(
            "offset-parent", null, false, null, null, true);
        using var page = new Page("offset-parent-page", context);
        await page.NavigateAsync("data:text/html," + Uri.EscapeDataString(Markup));

        var result = page.Evaluate(
            """
            (() => {
              const row = (e) => {
                const op = e.offsetParent;
                return [op ? (op.id || op.localName) : 'null', e.offsetTop, e.offsetLeft].join(',');
              };
              const ids = ['a', 'b', 'c', 't', 'td', 'i', 'f', 'n', 'nb'];
              return ids.map((id) => id + ':' + row(document.getElementById(id)))
                .concat(['body:' + row(document.body), 'detached:' + row(document.createElement('div'))])
                .join(' ');
            })()
            """);
        Assert.Equal(
            "a:body,10,18 b:body,25,33 c:b,2,13 t:b,26,8 td:t,2,2 i:td,3,3 f:null,20,30 "
            + "n:null,0,0 nb:null,0,0 body:null,0,0 detached:null,0,0",
            result!.GetValue<string>());
    }
}
