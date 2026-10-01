using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// SECURITY.md M11, parse half: every way script hands the parser markup must cost time linear
/// in the markup. AngleSharp's fragment parser moved each top-level node into the context
/// element one at a time, each move scanning the node list: <c>innerHTML</c> with 50,000
/// siblings took 100 s, and <c>el.innerHTML = el.innerHTML</c> over 30,000 did not finish in
/// 40 s. Its tree builder scanned the whole stack of open elements per block start tag, so
/// 50,000 nested divs took 20 s. The document.write stream re-parsed everything written so far
/// on every call, and document.body, which each call reads, walked the whole document, so
/// 5,000 calls took 30 s. Each case below takes well under a second or two now; the
/// bounds are loose enough for a loaded machine and far below the old cost.
/// </summary>
public sealed class MarkupInsertionComplexityTests
{
    private const double BoundMs = 15000;

    private static (double Ms, string Check) Run(string body)
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id=t></div></body></html>");
        var result = fixture.Runtime.Evaluate(
            "(() => { const t = document.getElementById('t'); const t0 = Date.now(); "
            + body
            + "; return JSON.stringify({ ms: Date.now() - t0, check: String(check) }); })()");
        var state = System.Text.Json.Nodes.JsonNode.Parse(result!.GetValue<string>())!;
        return (state["ms"]!.GetValue<double>(), state["check"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("innerHTML", "t.innerHTML = '<div>x</div>'.repeat(50000); const check = t.children.length", "50000")]
    [InlineData("innerHTML round trip", "t.innerHTML = '<p>x</p>'.repeat(30000); t.innerHTML = t.innerHTML; const check = t.children.length", "30000")]
    [InlineData("innerHTML nested", "t.innerHTML = '<div>'.repeat(50000); const check = t.querySelectorAll('div').length", "50000")]
    [InlineData("insertAdjacentHTML", "t.insertAdjacentHTML('beforeend', '<div>x</div>'.repeat(20000)); const check = t.children.length", "20000")]
    [InlineData("createContextualFragment", "const r = document.createRange(); r.selectNodeContents(t); t.appendChild(r.createContextualFragment('<div>x</div>'.repeat(20000))); const check = t.children.length", "20000")]
    [InlineData("template innerHTML", "const tp = document.createElement('template'); tp.innerHTML = '<div>x</div>'.repeat(50000); const check = tp.content.childNodes.length", "50000")]
    [InlineData("DOMParser siblings", "const d = new DOMParser().parseFromString('<div>x</div>'.repeat(50000), 'text/html'); const check = d.body.children.length", "50000")]
    [InlineData("DOMParser nested", "const d = new DOMParser().parseFromString('<div>'.repeat(50000), 'text/html'); const check = d.querySelectorAll('div').length", "50000")]
    [InlineData("document.write", "document.write('<b>x</b>'.repeat(20000)); const check = document.querySelectorAll('b').length", "20000")]
    [InlineData("document.write calls", "for (let i = 0; i < 20000; i++) document.write('<b>x</b>'); const check = document.querySelectorAll('b').length", "20000")]
    public void MarkupInsertionIsLinear(string name, string body, string expected)
    {
        var (ms, check) = Run(body);
        Assert.Equal(expected, check);
        Assert.True(ms < BoundMs, $"{name} took {ms}ms");
    }
}
