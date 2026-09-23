using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// innerText, Range.toString() and their siblings on deep and wide trees. innerText used to call
/// getComputedStyle() once per element and recurse once per level (a 10k-deep chain overflowed the
/// stack after 10 s, 50k siblings took 18 s); Range.toString() compared both boundary points
/// against every node of the common ancestor's subtree (28 s on a 10k-deep chain); contains() and
/// compareDocumentPosition() collected a whole subtree per call; appendChild() snapshotted the
/// parent's children on every append (50k appends took a minute). Chromium answers each of these
/// in milliseconds. The bounds are loose enough for a loaded machine and far below the old costs.
/// </summary>
public sealed class InnerTextRangeComplexityTests
{
    private const double BoundMs = 5000;

    private static JsonNode Run(RuntimeFixture fixture, string script) =>
        JsonNode.Parse(fixture.Runtime.Evaluate(script)!.GetValue<string>())!;

    private static string DeepChainScript(int depth, string measured) =>
        $$"""
        (() => {
          let p = document.body;
          for (let i = 0; i < {{depth}}; i++) {
            const d = document.createElement(i % 2 ? 'span' : 'div');
            d.appendChild(document.createTextNode('t' + i + ' '));
            p.appendChild(d);
            p = d;
          }
          const root = document.body.firstChild, leaf = p;
          getComputedStyle(root).display; // the first layout is not what is measured
          const t0 = Date.now();
          const value = (() => { {{measured}} })();
          return JSON.stringify({ ms: Date.now() - t0, value });
        })()
        """;

    private static string WideHtml(int count)
    {
        var sb = new StringBuilder("<html><body><div id=\"w\">");
        for (var i = 0; i < count; i++)
        {
            sb.Append(i % 2 == 0 ? "<p>t" : "<span>t").Append(i).Append(i % 2 == 0 ? " </p>" : " </span>");
        }

        return sb.Append("</div></body></html>").ToString();
    }

    private static string WideScript(string measured) =>
        $$"""
        (() => {
          const root = document.getElementById('w'), leaf = root.lastChild;
          getComputedStyle(root).display;
          const t0 = Date.now();
          const value = (() => { {{measured}} })();
          return JSON.stringify({ ms: Date.now() - t0, value });
        })()
        """;

    private static void AssertFast(JsonNode state, string what) =>
        Assert.True(state["ms"]!.GetValue<double>() < BoundMs, $"{what} took {state["ms"]}ms");

    [Fact]
    public void InnerTextOfADeepChainIsLinear()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var state = Run(fixture, DeepChainScript(10000, "const s = root.innerText; return [s.length, s.slice(0, 12), s.slice(-12)];"));
        AssertFast(state, "innerText over a 10000-deep chain");
        var value = state["value"]!.AsArray();

        // Every level contributes "t<i> "; the even levels are divs, so their text starts a line.
        Assert.Equal("t0 t1\nt2 t3\n", value[1]!.GetValue<string>());
        Assert.Equal("\nt9998 t9999", value[2]!.GetValue<string>());
    }

    [Fact]
    public void InnerTextOfManySiblingsIsLinear()
    {
        using var fixture = RuntimeFixture.Setup(WideHtml(50000));
        var state = Run(fixture, WideScript("const s = root.innerText; return [s.length, s.slice(0, 16), s.slice(-14)];"));
        AssertFast(state, "innerText over 50000 siblings");
        var value = state["value"]!.AsArray();
        Assert.Equal("t0\n\nt1\n\nt2\n\nt3\n\n", value[1]!.GetValue<string>());
        Assert.Equal("t49998\n\nt49999", value[2]!.GetValue<string>());
    }

    [Fact]
    public void RangeToStringOverADeepChainIsLinear()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var state = Run(
            fixture,
            DeepChainScript(
                10000,
                """
                const r = document.createRange();
                r.selectNodeContents(root);
                const all = r.toString();
                const s = getSelection();
                s.selectAllChildren(root);
                return [all.length, all === root.textContent, s.toString() === all];
                """));
        AssertFast(state, "Range/Selection toString over a 10000-deep chain");
        var value = state["value"]!.AsArray();
        Assert.True(value[1]!.GetValue<bool>());
        Assert.True(value[2]!.GetValue<bool>());
    }

    [Fact]
    public void RangeToStringOverManySiblingsIsLinear()
    {
        using var fixture = RuntimeFixture.Setup(WideHtml(30000));
        var state = Run(
            fixture,
            WideScript(
                """
                const r = document.createRange();
                r.setStart(root.firstChild.firstChild, 1);
                r.setEnd(leaf.firstChild, 3);
                const s = r.toString();
                return [s.slice(0, 6), s.slice(-6), s.length === root.textContent.length - 1 - 4];
                """));
        AssertFast(state, "Range.toString over 30000 siblings");
        var value = state["value"]!.AsArray();
        Assert.Equal("0 t1 t", value[0]!.GetValue<string>());
        Assert.Equal("98 t29", value[1]!.GetValue<string>());
        Assert.True(value[2]!.GetValue<bool>());
    }

    [Fact]
    public void ContainsAndCompareDocumentPositionDoNotWalkTheSubtree()
    {
        using var fixture = RuntimeFixture.Setup(WideHtml(30000));
        var state = Run(
            fixture,
            WideScript(
                """
                let hits = 0;
                for (let i = 0; i < 1000; i++) {
                  if (document.contains(leaf)) hits++;
                  if (root.contains(leaf.firstChild)) hits++;
                  if (root.compareDocumentPosition(leaf) === 20) hits++;
                }
                return [hits, leaf.contains(root), root.contains(root), document.body.contains(document.documentElement)];
                """));
        AssertFast(state, "3000 contains/compareDocumentPosition calls over 30000 siblings");
        var value = state["value"]!.AsArray();
        Assert.Equal(3000, value[0]!.GetValue<int>());
        Assert.False(value[1]!.GetValue<bool>());
        Assert.True(value[2]!.GetValue<bool>());
        Assert.False(value[3]!.GetValue<bool>());
    }

    /// <summary>contains() is an inclusive-descendant test, as in Chromium.</summary>
    [Fact]
    public void ContainsIsInclusive()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id=\"a\"><b>x</b></div></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
              const a = document.getElementById('a'), b = a.firstChild, t = b.firstChild;
              return [a.contains(a), t.contains(t), document.contains(document), a.contains(t),
                t.contains(a), a.contains(null), document.body.contains(a)].join();
            })()
            """);
        Assert.Equal("true,true,true,true,false,false,true", result!.GetValue<string>());
    }

    [Fact]
    public void AppendingManySiblingsIsLinear()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id=\"w\"></div></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
              const w = document.getElementById('w');
              getComputedStyle(w).display;
              const t0 = Date.now();
              for (let i = 0; i < 50000; i++) {
                const d = document.createElement('p');
                d.appendChild(document.createTextNode('x'));
                w.appendChild(d);
              }
              // Re-appending the last child is still recognised as no change.
              w.appendChild(w.lastChild);
              return JSON.stringify({ ms: Date.now() - t0, value: w.childNodes.length });
            })()
            """);
        var state = JsonNode.Parse(result!.GetValue<string>())!;
        AssertFast(state, "50000 appends to one parent");
        Assert.Equal(50000, state["value"]!.GetValue<int>());
    }

    /// <summary>
    /// The output of the native pass, pinned to what bootstrap's per-element script walk
    /// produced for the same markup (checked element by element against the old build).
    /// </summary>
    [Fact]
    public void InnerTextMatchesTheScriptWalk()
    {
        using var fixture = RuntimeFixture.Setup(
            """
            <html><head><title>cases</title><style>.h{visibility:hidden}.v{visibility:visible}.n{display:none}.ib{display:inline-block}.f{display:flex}.g{display:grid}.t{display:table}.tr{display:table-row}.tc{display:table-cell}.pre{white-space:pre}.pw{white-space:pre-wrap}.nw{white-space:nowrap}.bs{white-space:break-spaces}.c{display:contents}.fr{display:flow-root}</style></head><body>
            <div id=a>  lead   space  <span> inner  </span> tail  </div>
            <p>para one</p><p>para two</p>text after
            <div class=pre>  pre   text
             line2  </div><div class=pw> pw  a </div><div class=nw>  nw   c  </div><div class=bs> bs </div>
            <table><tr><td> c1 </td><td>c2</td></tr><tr><th>h1</th><td>d<b>2</b></td></tr></table>
            <div class=t><div class=tr><div class=tc>tc1</div><div class=tc>tc2</div></div></div>
            <div class=n>none <span>x</span></div>
            <span class=ib>ib1</span><span class=ib>ib2</span><div class=f><span>f1</span><span>f2</span></div><div class=g><i>g1</i><i>g2</i></div>
            <ul><li>l1</li><li>l2 <ol><li>n1</li></ol></li></ul><div class=c>contents<div>inner</div></div><div class=fr>flow</div>
            <script>var s=1;</script><style>.x{}</style><noscript>ns</noscript><template>tpl</template>
            <pre>
             pre block
            	tab</pre><div>&nbsp;nbsp&nbsp; </div>
            <div><p></p><p> </p><div></div>after empties</div>
            <section><article><h1>H</h1><h2>h2</h2></article></section>
            </body></html>
            """);
        var result = fixture.Runtime.Evaluate(
            "JSON.stringify([document.body.innerText, document.getElementById('a').innerText, document.querySelector('table').innerText, document.querySelector('style').innerText])");
        var value = JsonNode.Parse(result!.GetValue<string>())!.AsArray();
        Assert.Equal(
            "lead space inner tail\n\npara one\n\npara two\n\ntext after\n  pre   text\n line2  \n pw  a \nnw c\n bs \nc1\tc2\nh1\td2\ntc1\ttc2\nib1ib2\nf1\nf2\ng1\ng2\nl1\nl2\nn1\ncontents\ninner\nflow\n pre block\n\ttab\n nbsp \n\nafter empties\nH\nh2",
            value[0]!.GetValue<string>());
        Assert.Equal("lead space inner tail", value[1]!.GetValue<string>());
        Assert.Equal("c1\tc2\nh1\td2", value[2]!.GetValue<string>());
        Assert.Equal(".h{visibility:hidden}.v{visibility:visible}.n{display:none}.ib{display:inline-block}.f{display:flex}.g{display:grid}.t{display:table}.tr{display:table-row}.tc{display:table-cell}.pre{white-space:pre}.pw{white-space:pre-wrap}.nw{white-space:nowrap}.bs{white-space:break-spaces}.c{display:contents}.fr{display:flow-root}", value[3]!.GetValue<string>());
    }

    [Fact]
    public void RangeToStringTakesPartialEndsAndSkipsComments()
    {
        using var fixture = RuntimeFixture.Setup(
            "<html><body><div id=\"d\">ab<!--cc--><b>de<i>fg</i></b>hi<p id=\"p\">jk</p>lm</div></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
              const d = document.getElementById('d'), p = document.getElementById('p');
              const out = [];
              const r = document.createRange();
              r.setStart(d.firstChild, 1); r.setEnd(p.firstChild, 1); out.push(r.toString());
              r.setStart(d, 1); r.setEnd(d, 3); out.push(r.toString());
              r.setStart(d.childNodes[2], 1); r.setEnd(d, 5); out.push(r.toString());
              r.setStart(d.firstChild, 0); r.setEnd(d.firstChild, 2); out.push(r.toString());
              r.selectNode(p); out.push(r.toString());
              r.setStart(d, 5); r.setEnd(d, 5); out.push(r.toString());
              r.setStart(d.childNodes[1], 1); r.setEnd(d.lastChild, 1); out.push(r.toString());
              r.selectNodeContents(document); out.push(r.toString() === document.documentElement.textContent);
              return JSON.stringify(out);
            })()
            """);
        Assert.Equal(
            """["bdefghij","defg","fghijk","ab","jk","","defghijkl",true]""",
            result!.GetValue<string>());
    }

    [Fact]
    public void GetComputedStyleInheritsBorderSpacingFromTheNearestTable()
    {
        using var fixture = RuntimeFixture.Setup(
            """
            <html><body><div id="out"><i id="o2">x</i></div>
            <table style="border-spacing:5px 3px"><tr><td><div id="in1"><span id="in2">a</span>
            <table style="border-spacing:7px"><tr><td><b id="in3">b</b></td></tr></table><em id="in4">c</em></div></td></tr></table>
            </body></html>
            """);
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
              const ids = ['in2', 'in1', 'in3', 'in4', 'o2', 'out', 'in3', 'in2'];
              return ids.map(id => getComputedStyle(document.getElementById(id)).borderSpacing).join('|');
            })()
            """);
        Assert.Equal("5px 3px|5px 3px|7px|5px 3px|0px|0px|7px|5px 3px", result!.GetValue<string>());
    }
}
