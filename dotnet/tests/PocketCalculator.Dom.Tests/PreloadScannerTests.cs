// No counterpart in crates/obscura-dom: the Rust engine fetches the scripts of a document it
// has already parsed whole, so it has no preload scanner.
using PocketCalculator.Dom;

namespace PocketCalculator.Dom.Tests;

public class PreloadScannerTests
{
    [Fact]
    public void FindsExternalClassicScriptsOutsideCommentsRawTextAndTemplates()
    {
        PreloadScan scan = PreloadScanner.Scan(
            "<!doctype html><head><base href='/app/'><meta name=referrer content=no-referrer>"
            + "<script src=a.js></script>"
            + "<!-- <script src=commented.js></script> -->"
            + "<script>document.write('<script src=inline-text.js><\\/script>')</script>"
            + "<script type=module src=module.js></script><script nomodule src=legacy.js></script>"
            + "<script type=text/template src=template-type.js></script>"
            + "<textarea><script src=in-textarea.js></script></textarea>"
            + "<template><script src=in-template.js></script></template>"
            + "<SCRIPT SRC=\"b.js?x=1&amp;y=2\" referrerpolicy=origin defer></SCRIPT></head>");

        Assert.Equal("/app/", scan.BaseHref);
        Assert.Equal(
            [
                new PreloadScript("a.js", null, "no-referrer"),
                new PreloadScript("b.js?x=1&y=2", "origin", "no-referrer"),
            ],
            scan.Scripts);
    }
}
