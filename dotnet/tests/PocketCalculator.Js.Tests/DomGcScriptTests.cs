using System.Text.Json.Nodes;
using PocketCalculator.Dom;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The DOM collector seen from script (DomTree.Gc.cs, PocketCalculatorJsRuntime.DomGc.cs,
/// _gcWeaken in bootstrap.js). Rust never frees a detached node and keeps every wrapper, so
/// a page that replaces a table in a loop runs into the M7 budget.
/// </summary>
public sealed class DomGcScriptTests
{
    private const long SmallBudget = 1024 * 1024;

    private static readonly IsolatedWorldTarget Utility = new(100, "__puppeteer_utility_world__", []);

    private static (PocketCalculatorJsRuntime Runtime, DomTree Dom) Page(long budget = 0)
    {
        var dom = HtmlParsing.ParseHtml("<html><body><table id=t><tbody id=b></tbody></table><div id=keep>kept</div></body></html>");
        if (budget > 0)
        {
            dom.ContentByteBudget = budget;
        }

        var runtime = new PocketCalculatorJsRuntime();
        runtime.SetDom(dom);
        runtime.SetUrl("http://example.com/test");
        runtime.RunPageInit();
        return (runtime, dom);
    }

    private static string Eval(PocketCalculatorJsRuntime runtime, string script) =>
        runtime.Evaluate(script)?.ToString() ?? "null";

    private const string RowsScript = """
        globalThis.rows = (n, tag) => {
          let html = '';
          for (let i = 0; i < n; i++) html += '<tr><td>' + tag + ' row ' + i + '</td><td>' + (i * 7) + '</td></tr>';
          return html;
        };
        """;

    [Fact]
    public void ASynchronousReplaceLoopStaysUnderASmallBudget()
    {
        var (runtime, dom) = Page(SmallBudget);
        using var _ = runtime;
        runtime.ExecuteScript("rows", RowsScript);

        // About 60 KB of rows per replacement: without the collector the budget refuses the
        // 17th or so, and in Rust the arena grows by 1000 nodes each time.
        string result = Eval(runtime, """
            (() => {
              const tbody = document.getElementById('b');
              try {
                for (let i = 0; i < 3000; i++) tbody.innerHTML = rows(200, 'r' + i);
              } catch (e) {
                return e.name;
              }
              return tbody.rows.length + ' ' + tbody.firstChild.firstChild.textContent;
            })()
            """);

        Assert.Equal("200 r2999 row 0", result);
        Assert.True(dom.ContentBytes <= SmallBudget);
        Assert.True(dom.SlotCount < 64 * 1024, $"arena grew to {dom.SlotCount} slots");
    }

    [Fact]
    public async Task ATimerDrivenDashboardStaysFlat()
    {
        var (runtime, dom) = Page(SmallBudget);
        using var _ = runtime;
        runtime.ExecuteScript("rows", RowsScript);
        runtime.ExecuteScript("dash", """
            globalThis.ticks = 0;
            globalThis.failure = null;
            const tick = () => {
              try {
                document.getElementById('b').innerHTML = rows(200, 't' + ticks);
                // Read something back, so rows get wrappers the collector has to weaken.
                document.getElementById('b').rows[3].cells[0].dataset.seen = '1';
              } catch (e) { failure = e.name; return; }
              if (++ticks < 400) setTimeout(tick, 0);
            };
            setTimeout(tick, 0);
            """);
        await runtime.RunEventLoopAsync();

        Assert.Equal("null 400", Eval(runtime, "String(failure) + ' ' + ticks"));
        Assert.True(dom.ContentBytes <= SmallBudget);
        Assert.True(dom.SlotCount < 64 * 1024, $"arena grew to {dom.SlotCount} slots");
    }

    [Fact]
    public void UnreferencedWrappedNodesAreFreed()
    {
        var (runtime, dom) = Page();
        using var _ = runtime;
        Eval(runtime, """
            (() => {
              for (let i = 0; i < 2000; i++) {
                const d = document.createElement('div');
                d.textContent = 'garbage ' + i;
                d.expando = { i };
                // A listener that closes over its own element must not keep it alive.
                d.addEventListener('click', () => d.expando);
              }
              return 1;
            })()
            """);

        var result = runtime.CollectDomGarbage();

        Assert.True(result.FreedNodes >= 4000, $"freed {result.FreedNodes}");
        Assert.Equal("kept", Eval(runtime, "document.getElementById('keep').textContent"));
    }

    [Fact]
    public void AHeldDetachedSubtreeKeepsIdentityExpandosAndListeners()
    {
        var (runtime, _) = Page();
        using var __ = runtime;
        Eval(runtime, """
            (() => {
              const list = document.createElement('ul');
              list.innerHTML = '<li>one</li><li>two</li>';
              // Expandos and listeners on a node script only reaches through the held root.
              list.lastChild.marker = 'm';
              list.lastChild.addEventListener('ping', (e) => { globalThis.pinged = e.type; });
              globalThis.held = list;
              globalThis.firstLi = list.firstChild;
              // A detached node of the document itself, held through its text.
              const keep = document.getElementById('keep');
              keep.remove();
              globalThis.keptText = keep.firstChild;
              return 1;
            })()
            """);

        var first = runtime.CollectDomGarbage();
        Eval(runtime, "(() => { for (let i = 0; i < 3000; i++) document.createElement('span'); return 1; })()");
        var second = runtime.CollectDomGarbage();
        Assert.True(second.FreedNodes >= 3000);

        Assert.Equal("true m", Eval(runtime, "String(held.firstChild === firstLi) + ' ' + held.lastChild.marker"));
        Assert.Equal("ping", Eval(runtime, "held.lastChild.dispatchEvent(new Event('ping')), globalThis.pinged"));
        Assert.Equal("true", Eval(runtime, "String(held.firstChild === held.firstChild)"));
        Assert.Equal("<ul><li>one</li><li>two</li></ul>|kept|DIV", Eval(runtime, """
            (() => {
              document.body.appendChild(held);
              document.body.appendChild(keptText.parentNode);
              return document.body.querySelector('ul').outerHTML + '|' + document.getElementById('keep').textContent
                + '|' + keptText.parentNode.tagName;
            })()
            """));
        GC.KeepAlive(first);
    }

    [Fact]
    public void RemovedNodesSurviveUntilTheirMutationRecordIsDelivered()
    {
        var (runtime, _) = Page(SmallBudget);
        using var __ = runtime;
        runtime.ExecuteScript("rows", RowsScript);
        string result = Eval(runtime, """
            (() => {
              globalThis.seen = [];
              const keep = document.getElementById('keep');
              keep.innerHTML = '<b>precious</b>';
              new MutationObserver((records) => {
                for (const r of records) for (const n of r.removedNodes) seen.push(n.textContent);
              }).observe(keep, { childList: true });
              // The <b> has no wrapper; only the pending record names it.
              keep.innerHTML = '';
              // Churn well past the budget, so the collector runs inside ops before the
              // record is delivered at the microtask checkpoint.
              const tbody = document.getElementById('b');
              for (let i = 0; i < 200; i++) tbody.innerHTML = rows(200, 'x' + i);
              return 'queued';
            })()
            """);
        Assert.Equal("queued", result);
        Assert.Equal("precious", Eval(runtime, "seen.join('|')"));
    }

    [Fact]
    public async Task AnIsolatedWorldKeepsItsNodes()
    {
        var (runtime, dom) = Page();
        using var _ = runtime;
        var created = await runtime.EvaluateForCdpWithTimeoutAsync("""
            (() => {
              const div = document.createElement('div');
              div.innerHTML = '<i>world</i>';
              div.tag = 'mine';
              globalThis.worldHeld = div;
              document.body.appendChild(div);
              div.remove();
              return 1;
            })()
            """, true, true, 5_000, Utility);
        Assert.False(created.Thrown, created.Description);

        runtime.CollectDomGarbage();

        var read = await runtime.EvaluateForCdpWithTimeoutAsync(
            "worldHeld.tag + ' ' + worldHeld.firstChild.textContent", true, true, 5_000, Utility);
        Assert.False(read.Thrown, read.Description);
        Assert.Equal("mine world", read.Value?.GetValue<string>());

        // Dropped by the world, it goes.
        await runtime.EvaluateForCdpWithTimeoutAsync("worldHeld = null; 1", true, true, 5_000, Utility);
        Assert.True(runtime.CollectDomGarbage().FreedNodes >= 3);
        Assert.Equal("kept", Eval(runtime, "document.getElementById('keep').textContent"));
        GC.KeepAlive(dom);
    }

    [Fact]
    public void StaleIdsNeverAliasANewNode()
    {
        var (runtime, dom) = Page();
        using var _ = runtime;
        var stale = Eval(runtime, "(() => { const d = document.createElement('div'); d.id = 'old'; return String(d._nid); })()");
        runtime.CollectDomGarbage();
        Assert.Null(dom.GetNode(NodeId.New(uint.Parse(stale, System.Globalization.CultureInfo.InvariantCulture))));

        // The freed slot is reused with a new generation, so the bare id names nothing.
        var fresh = Eval(runtime, "(() => { const s = document.createElement('section'); globalThis.fresh = s; return String(s._nid); })()");
        Assert.NotEqual(stale, fresh);
        Assert.Equal("false", Eval(runtime, $"String(_wrap({stale}) === fresh)"));
        Assert.Equal("SECTION", Eval(runtime, "fresh.tagName"));
    }
}
