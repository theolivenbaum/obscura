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
    /// <summary>
    /// L10: a page-added <c>toJSON</c> on <c>Object.prototype</c> or <c>Array.prototype</c>
    /// does not shape what the host decodes by value; Chromium's serialization never calls
    /// <c>toJSON</c> (a <c>Date</c> is <c>{}</c> there too). The shim's own URL still
    /// answers with its href.
    /// </summary>
    [Fact]
    public void PageToJsonDoesNotShapeByValueResults()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("tamper", """
            Object.prototype.toJSON = function () { return 'pwned'; };
            Array.prototype.toJSON = function () { return 'arr'; };
            """);

        Assert.Equal(
            """{"a":1,"b":[1,{"c":2},null],"d":"s","n":null}""",
            Eval(runtime, "({a: 1, b: [1, {c: 2}, undefined], d: 's', n: NaN, f() {}})"));
        Assert.Equal("{}", Eval(runtime, "new Date(0)"));
        Assert.Equal("\"http://example.com/x\"", Eval(runtime, "new URL('http://example.com/x')"));
        // The page's own serialization still sees its toJSON.
        Assert.Equal("true", Eval(runtime, "JSON.stringify({}) === '\"pwned\"'"));
    }

    /// <summary>
    /// L10: focus and the CDP click fallback are closure state. A page that writes
    /// upstream's <c>__obscura_focused</c> / <c>__obscura_click_target</c> globals moves
    /// neither <c>document.activeElement</c> nor what the host reads.
    /// </summary>
    [Fact]
    public void FocusAndClickTargetAreNotPageGlobals()
    {
        using var fixture = RuntimeFixture.Setup(Page + "<input id=real><input id=victim>");
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("focus", "document.getElementById('real').focus();");
        runtime.ExecuteScript("tamper", """
            globalThis.__obscura_focused = document.getElementById('victim');
            globalThis.__obscura_click_target = document.getElementById('victim');
            """);

        Assert.Equal("\"real\"", Eval(runtime, "document.activeElement.id"));
        Assert.Equal("\"real\"", runtime.EvaluateHost("__obscura_host.dom.activeElement().id")?.ToJsonString());
        Assert.Equal("\"real\"", runtime.EvaluateHost("__obscura_host.clickTarget.get().id")?.ToJsonString());
    }

    /// <summary>
    /// L10: the host helpers behind CDP and MCP input use the built-ins bootstrap captured.
    /// A page that replaced <c>Object.getPrototypeOf</c>, <c>Function.prototype.call</c> or
    /// <c>Array.prototype.map</c> does not stop a fill or a file upload, and a replaced
    /// <c>click</c> never receives the trusted-activation token a label forwards.
    /// </summary>
    [Fact]
    public void HostInputHelpersIgnorePageReplacements()
    {
        using var fixture = RuntimeFixture.Setup(
            "<html><body><label id=l for=c>check</label><input id=c type=checkbox>"
            + "<input id=t><input id=f type=file></body></html>");
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("tamper", """
            globalThis.__tokens = [];
            const realClick = HTMLElement.prototype.click || Element.prototype.click;
            Element.prototype.click = function (token) { __tokens.push(typeof token); };
            Object.getPrototypeOf = () => null;
            Function.prototype.call = function () { throw new Error('tampered call'); };
            Array.prototype.map = () => [];
            Element.prototype.matches = () => false;
            """);

        runtime.ExecuteHostScript("fill", """
            const h = __obscura_host.dom;
            __obscura_host.setFieldValue(h.querySelector(h.document(), '#t'), 'value', 'typed');
            __obscura_host.setInputFiles(h.querySelector(h.document(), '#f'), [{ name: 'a.txt', type: 'text/plain', b64: 'aGk=' }]);
            const label = h.querySelector(h.document(), '#l');
            __obscura_host.activateLabel(label, __obscura_host.labeledControl(label), true);
            """);

        Assert.Equal("\"typed\"", Eval(runtime, "document.getElementById('t').value"));
        Assert.Equal("\"a.txt:2\"", Eval(runtime, "(() => { const f = document.getElementById('f').files[0]; return f.name + ':' + f.size; })()"));
        Assert.Equal("true", Eval(runtime, "document.getElementById('c').checked"));
        Assert.Equal("0", Eval(runtime, "__tokens.length"));
    }
}
