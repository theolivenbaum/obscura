using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// A page that replaces built-ins keeps its replacements, but the shim's own DOM paths
/// do not call them (SECURITY.md L10).
/// </summary>
/// <remarks>
/// DEVIATION from the Rust engine, whose shim calls the page's current
/// <c>Array.prototype.map</c> and <c>JSON.parse</c>. Chromium's DOM is native, so a page
/// replacing them changes only what page script itself computes.
/// </remarks>
public sealed class PageTamperingTests
{
    private const string Page = """
        <html><body>
        <p id=a>one</p><p id=b>two</p>
        <select id=sel><option value=a>A</option><option value=b>B</option></select>
        <ul id=list><li>x</li><li>y</li></ul>
        </body></html>
        """;

    private const string Tamper = """
        Array.prototype.map = function () { return ['tampered']; };
        Array.prototype.filter = function () { return ['tampered']; };
        JSON.stringify = () => '"tampered"';
        JSON.parse = () => null;
        document.querySelectorAll = () => [];
        """;

    private static string Eval(PocketCalculatorJsRuntime runtime, string expression) =>
        runtime.Evaluate(expression)?.ToJsonString() ?? "null";

    [Fact]
    public void TamperedArrayAndJsonDoNotBreakDomQueries()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("tamper", Tamper);

        // The page's own call still gets its replacement.
        Assert.Equal("\"tampered\"", Eval(runtime, "[1, 2].map(x => x)[0]"));

        Assert.Equal("2", Eval(runtime, "Document.prototype.querySelectorAll.call(document, 'p').length"));
        Assert.Equal("\"b\"", Eval(runtime, "document.body.querySelectorAll('p')[1].id"));
        Assert.Equal("\"LI\"", Eval(runtime, "document.getElementById('list').children[1].tagName"));
        Assert.Equal("\"LI\"", Eval(runtime, "document.getElementById('list').childNodes[0].tagName"));
    }

    [Fact]
    public void HostRegistriesAreNotPageVisible()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        var runtime = fixture.Runtime;
        Assert.Equal(
            "\"false,false,false,false,false,false\"",
            Eval(runtime, "String(['__obscura_frameWindows', '__obscura_frameElements', '__obscura_frameObjects', '__obscura_objects', '__obscura_oid', '_wrap'].map(n => n in globalThis))"));
        Assert.Equal("[0,0,0]", runtime.EvaluateHost("__obscura_host.frameRegistrySize()")?.ToJsonString());
    }

    [Fact]
    public void TamperedArrayDoesNotBreakSelectValue()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("tamper", Tamper);

        Assert.Equal(
            "\"b\"",
            Eval(runtime, "(() => { const s = document.getElementById('sel'); s.value = 'b'; return s.value; })()"));
    }

    [Fact]
    public void TamperedQuerySelectorAllDoesNotAimHitTesting()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        var runtime = fixture.Runtime;
        var before = Eval(runtime, "(() => { const r = document.getElementById('b').getBoundingClientRect(); const el = document.elementFromPoint(r.left + 1, r.top + 1); return el && el.id; })()");
        Assert.Equal("\"b\"", before);

        runtime.ExecuteScript("tamper", Tamper);
        var after = Eval(runtime, "(() => { const r = document.getElementById('b').getBoundingClientRect(); const el = document.elementFromPoint(r.left + 1, r.top + 1); return el && el.id; })()");
        Assert.Equal("\"b\"", after);
    }
}
