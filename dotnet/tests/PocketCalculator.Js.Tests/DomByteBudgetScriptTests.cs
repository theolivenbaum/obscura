using PocketCalculator.Dom;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// SECURITY.md M7: a page appending 4 MB text nodes grew the process past 3 GB. A mutation
/// that would take the document past its byte budget now throws <c>QuotaExceededError</c>
/// and leaves the document as it was.
/// </summary>
public sealed class DomByteBudgetScriptTests
{
    private const long Budget = 1024 * 1024;

    private static (PocketCalculatorJsRuntime Runtime, DomTree Dom) Page()
    {
        var dom = HtmlParsing.ParseHtml("<html><body><div id=keep>kept</div></body></html>");
        dom.ContentByteBudget = Budget;
        var runtime = new PocketCalculatorJsRuntime();
        runtime.SetDom(dom);
        runtime.SetUrl("http://example.com/test");
        runtime.RunPageInit();
        return (runtime, dom);
    }

    private static string Eval(PocketCalculatorJsRuntime runtime, string script) =>
        runtime.Evaluate(script)?.ToString() ?? "null";

    [Fact]
    public void AppendingTextPastTheBudgetThrowsQuotaExceededError()
    {
        var (runtime, dom) = Page();
        using var _ = runtime;
        string result = Eval(runtime, """
            (() => {
              const chunk = 'x'.repeat(64 * 1024);
              let n = 0;
              try {
                for (let i = 0; i < 1000; i++) { document.body.appendChild(document.createTextNode(chunk)); n++; }
                return 'all';
              } catch (e) {
                return e.name + ' ' + (e instanceof DOMException) + ' ' + e.code + ' ' + n;
              }
            })()
            """);
        Assert.StartsWith("QuotaExceededError true 22 ", result);
        Assert.True(dom.ContentBytes <= Budget);

        // The page still works, and shrinking data gives room back.
        Assert.Equal("kept", Eval(runtime, "document.getElementById('keep').textContent"));
        Assert.Equal("ok", Eval(runtime, "(() => { for (const t of [...document.body.childNodes]) if (t.nodeType === 3) t.data = ''; document.body.appendChild(document.createTextNode('x'.repeat(64 * 1024))); return 'ok'; })()"));
    }

    [Fact]
    public void OversizedSettersThrowAndChangeNothing()
    {
        var (runtime, _) = Page();
        using var __ = runtime;
        string big = "'x'.repeat(2 * 1024 * 1024)";
        Assert.Equal("QuotaExceededError|null", Eval(runtime,
            $"(() => {{ const d = document.getElementById('keep'); try {{ d.setAttribute('title', {big}); return 'set'; }} catch (e) {{ return e.name + '|' + d.getAttribute('title'); }} }})()"));
        Assert.Equal("QuotaExceededError|kept", Eval(runtime,
            $"(() => {{ const d = document.getElementById('keep'); try {{ d.firstChild.data = {big}; return 'set'; }} catch (e) {{ return e.name + '|' + d.textContent; }} }})()"));
        Assert.Equal("QuotaExceededError|kept", Eval(runtime,
            $"(() => {{ const d = document.getElementById('keep'); try {{ d.innerHTML = '<b>' + {big} + '</b>'; return 'set'; }} catch (e) {{ return e.name + '|' + d.innerHTML; }} }})()"));
        Assert.Equal("QuotaExceededError", Eval(runtime,
            $"(() => {{ try {{ document.createComment({big}); return 'set'; }} catch (e) {{ return e.name; }} }})()"));

        // Ordinary sizes still go through.
        Assert.Equal("<i>small</i>", Eval(runtime,
            "(() => { const d = document.getElementById('keep'); d.innerHTML = '<i>small</i>'; d.setAttribute('title', 'x'.repeat(1000)); return d.innerHTML; })()"));
    }

    [Fact]
    public void AnUnbudgetedPageIsUnaffected()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        Assert.Equal("400", Eval(fixture.Runtime,
            "(() => { for (let i = 0; i < 400; i++) document.body.appendChild(document.createTextNode('x'.repeat(4096))); return String(document.body.childNodes.length); })()"));
    }
}
