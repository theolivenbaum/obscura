using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// A page that copies itself into a document without a browsing context keeps its own tree and
/// layout. dell.com's bot detector (detector-lazy.min.js) does
/// <c>createHTMLDocument("cloner-doc").importNode(node, false)</c> for every node and appends
/// the copies there; the port's importNode returned the node itself, so about 20 s after load
/// the page's &lt;html&gt; moved into the cloner document and screenshots went blank. Expected
/// values measured in Chromium 141 (headless, Playwright) on the same markup.
/// </summary>
public sealed class ParsedDocumentPageTests
{
    private const string Markup = """
        <!doctype html><html><head><title>t</title></head><body>
        <div id=box style="width:200px;height:50px">box</div>
        <script>
          (function () {
            const cd = document.implementation.createHTMLDocument("cloner-doc");
            const copy = (node, parent) => {
              const c = cd.importNode(node, false);
              parent.appendChild(c);
              for (let k = node.firstChild; k; k = k.nextSibling) copy(k, c);
            };
            cd.replaceChild(cd.importNode(document.documentElement, false), cd.documentElement);
            for (let k = document.documentElement.firstChild; k; k = k.nextSibling) copy(k, cd.documentElement);
            window.__cloner = cd;
          })();
        </script>
        </body></html>
        """;

    [Fact]
    public async Task CopyingThePageIntoAClonerDocumentLeavesThePageAndItsLayout()
    {
        BrowserContext context = BrowserContext.WithStorageAndNetwork(
            "parsed-document", null, false, null, null, true);
        using var page = new Page("parsed-document-page", context);
        await page.NavigateAsync("data:text/html," + Uri.EscapeDataString(Markup));

        var result = page.Evaluate(
            """
            (() => {
              const cd = window.__cloner;
              const box = document.getElementById('box').getBoundingClientRect();
              return [document.documentElement.parentNode === document, document.body.children.length,
                cd.getElementById('box') !== document.getElementById('box'),
                cd.getElementById('box').ownerDocument === cd,
                cd.querySelectorAll('*').length === document.querySelectorAll('*').length,
                box.width + 'x' + box.height,
                document.elementFromPoint(20, 20) === document.getElementById('box')].join('|');
            })()
            """);
        Assert.Equal("true|2|true|true|true|200x50|true", result!.GetValue<string>());
    }
}
