using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// What the realm learns when the document's parser inserts nodes natively while page script
/// is live (<see cref="Runtime.PocketCalculatorJsRuntime.ParserInserted"/>), as the DOM insertion
/// steps would have told it, and the shim's own halves of running scripts while parsing.
/// Expectations are Chromium 141's (scripts/script-order-conformance). No Rust counterpart: the
/// reference parses the whole document before the realm runs any of it.
/// </summary>
public class ParserInsertionTests
{
    private static NodeId AppendToBody(RuntimeFixture fixture, string local, string id) =>
        fixture.Runtime.WithDom(dom =>
        {
            Assert.True(dom.TryQuerySelector("body", out var body, out _));
            var node = dom.NewNode(NodeData.Element(QualName.Html(local), [new PocketCalculator.Dom.Attribute(QualName.Attr("id"), id)]));
            dom.AppendChild(body!.Value, node);
            return node;
        });

    [Fact]
    public void ParsedNodesReachMutationObserversNamedAccessAndCachedTreeState()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><p id=first></p></body></html>");
        fixture.Runtime.ExecuteScript("setup", """
            globalThis.records = [];
            new MutationObserver(rs => { for (const r of rs) for (const n of r.addedNodes) records.push(r.target.localName + '>' + n.id); })
              .observe(document, { childList: true, subtree: true });
            globalThis.before = document.body.children.length;
            """);
        var late = AppendToBody(fixture, "div", "late");
        fixture.Runtime.ParserInserted([late]);
        fixture.Runtime.PerformMicrotaskCheckpoint();
        Assert.Equal(
            "body>late|true|1|2",
            fixture.Runtime.Evaluate(
                "[records.join(','), window.late === document.getElementById('late'), before, document.body.children.length].join('|')")!
                .GetValue<string>());
    }

    [Fact]
    public void AParsedDefinedCustomElementIsUpgraded()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        fixture.Runtime.ExecuteScript("setup", """
            globalThis.log = [];
            customElements.define('x-parsed', class extends HTMLElement {
              constructor() { super(); log.push('constructed'); }
              connectedCallback() { log.push('connected ' + this.id); }
            });
            """);
        Assert.Contains("x-parsed", fixture.Runtime.State.DefinedCustomElements);
        var element = AppendToBody(fixture, "x-parsed", "a");
        fixture.Runtime.ParserInserted([element]);
        Assert.Equal(
            "constructed,connected a",
            fixture.Runtime.Evaluate("log.join(',')")!.GetValue<string>());
    }

    [Fact]
    public void AnInsertedClassicNomoduleScriptDoesNotRun()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        fixture.Runtime.Evaluate("""
            (function () {
              globalThis.ran = [];
              const skipped = document.createElement('script');
              skipped.setAttribute('nomodule', '');
              skipped.textContent = "ran.push('nomodule')";
              document.head.appendChild(skipped);
              const plain = document.createElement('script');
              plain.textContent = "ran.push('plain')";
              document.head.appendChild(plain);
            })()
            """);
        Assert.Equal("plain", fixture.Runtime.Evaluate("ran.join(',')")!.GetValue<string>());
    }

    [Fact]
    public void DocumentWriteWithoutARunningParserKeepsTheShimsOwnStream()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><script id=s></script></body></html>");
        Assert.Equal(
            "false|<p id=\"w\">x</p>",
            fixture.Runtime.Evaluate("""
                (function () {
                  const open = document.open === Document.prototype.open;
                  document.write('<p id=w>x</p>');
                  return [!open, document.getElementById('w').outerHTML].join('|');
                })()
                """)!.GetValue<string>());
    }
}
