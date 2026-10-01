using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The <c>outerHTML</c> setter. crates/obscura-js has only the getter, so an assignment was
/// dropped. Expected values are Chromium 141's.
/// </summary>
public sealed class OuterHtmlSetterScriptTests
{
    private static string[] Evaluate(string html, string script)
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(html));
        rt.SetViewport(1280.0, 720.0);
        rt.RunPageInit();
        var result = rt.Evaluate(script) ?? throw new InvalidOperationException("no result");
        return [.. result.AsArray().Select(static node => node!.GetValue<string>())];
    }

    [Fact]
    public void ReplacesTheElementInOneRecordWithTheParentAsContext()
    {
        string[] result = Evaluate(
            """
            <body><div id="host"><p id="a">a</p><span id="b">b</span></div>
            <table><tbody id="tb"><tr id="r"><td>1</td></tr></tbody></table></body>
            """,
            """
            (() => {
              const host = document.getElementById("host"), a = document.getElementById("a"), out = [];
              const mo = new MutationObserver(() => {});
              mo.observe(host, { childList: true, subtree: true });
              a.outerHTML = "<i>x</i>text<b>y</b>";
              const records = mo.takeRecords();
              out.push(host.innerHTML, String(records.length),
                records.map(r => [...r.addedNodes].map(n => n.nodeName).join(",") + "/" + [...r.removedNodes].map(n => n.nodeName).join(",")).join("|"),
                String(a.parentNode));
              document.getElementById("r").outerHTML = "<tr><td>2</td></tr><td>3</td>";
              out.push(document.getElementById("tb").innerHTML);
              document.getElementById("b").outerHTML = null;
              out.push(host.innerHTML);
              return out;
            })()
            """);
        Assert.Equal(
            [
                "<i>x</i>text<b>y</b><span id=\"b\">b</span>", "1", "I,#text,B/P", "null",
                "<tr><td>2</td></tr><tr><td>3</td></tr>", "<i>x</i>text<b>y</b>",
            ],
            result);
    }

    [Fact]
    public void ThrowsWithoutAnElementParent()
    {
        string[] result = Evaluate(
            "<body></body>",
            """
            (() => {
              const out = [];
              const attempt = f => { try { f(); out.push("no throw"); } catch (e) { out.push(e.name + ": " + e.message); } };
              attempt(() => { document.documentElement.outerHTML = "<html></html>"; });
              attempt(() => { document.createElement("div").outerHTML = "<p></p>"; });
              const frag = document.createDocumentFragment();
              const child = frag.appendChild(document.createElement("div"));
              attempt(() => { child.outerHTML = "<p></p>"; });
              return out;
            })()
            """);
        const string Prefix = "NoModificationAllowedError: Failed to set the 'outerHTML' property on 'Element': ";
        Assert.Equal(
            [
                Prefix + "This element's parent is of type '#document', which is not an element node.",
                Prefix + "This element has no parent node.",
                Prefix + "This element's parent is of type '#document-fragment', which is not an element node.",
            ],
            result);
    }
}
