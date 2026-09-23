using PocketCalculator.Js;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// A classic script's top-level <c>var</c> and <c>function</c> declarations
/// become globals whatever the script's strictness, and the body still runs in
/// the strictness its own directive asks for.
/// </summary>
/// <remarks>
/// The shim used to run a dynamically inserted classic script through
/// <c>(0, eval)(source)</c>, which is eval and not a script: a source starting
/// with <c>"use strict"</c> kept its declarations in the eval's own variable
/// environment, so every bundler prologue and every
/// <c>"use strict"; var lib = (() =&gt; { ... })();</c> library loaded, fired
/// <c>load</c>, and published nothing.
/// </remarks>
public class ClassicScriptScopeTests
{
    private const string StrictSource = """
        "use strict";
        var strictValue = { ok: 1 };
        function strictFunction() { return 7; }
        """;

    private static string Published(RuntimeFixture fixture) =>
        fixture.Runtime.Evaluate(
            "typeof globalThis.strictValue + '|' + "
            + "(typeof globalThis.strictFunction === 'function' ? globalThis.strictFunction() : 'missing')")!
            .GetValue<string>();

    [Fact]
    public void ParserInsertedStrictScriptPublishesTopLevelDeclarations()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        fixture.Runtime.ExecuteScript("http://example.com/parser.js", StrictSource);
        Assert.Equal("object|7", Published(fixture));
    }

    [Fact]
    public void InsertedInlineStrictScriptPublishesTopLevelDeclarations()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        fixture.Runtime.Evaluate($$"""
            (function () {
              const script = document.createElement('script');
              script.textContent = {{JsLiteral(StrictSource)}};
              document.head.appendChild(script);
            })()
            """);
        Assert.Equal("object|7", Published(fixture));
    }

    [Fact]
    public void InsertedInlineSloppyScriptStillPublishesTopLevelDeclarations()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        fixture.Runtime.Evaluate("""
            (function () {
              const script = document.createElement('script');
              script.textContent = 'var sloppyValue = { ok: 1 };';
              document.head.appendChild(script);
            })()
            """);
        Assert.Equal("object", fixture.Runtime.Evaluate("typeof globalThis.sloppyValue")!.GetValue<string>());
    }

    [Fact]
    public async Task InsertedExternalStrictScriptPublishesTopLevelDeclarations()
    {
        // A data: URL is decoded by the shim itself, so the fetched-body path is
        // exercised without a network round trip.
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        fixture.Runtime.Evaluate("""
            (function () {
              const script = document.createElement('script');
              script.src = 'data:text/javascript,'
                + encodeURIComponent('"use strict";\nvar strictValue = { ok: 1 };\nfunction strictFunction() { return 7; }');
              document.head.appendChild(script);
            })()
            """);
        await fixture.Runtime.RunEventLoopBoundedAsync(200);
        Assert.Equal("object|7", Published(fixture));
    }

    [Fact]
    public void InsertedStrictScriptStillRunsItsBodyInStrictMode()
    {
        // The declarations reach the global object because the source is compiled
        // as a script, not because the directive was stripped: an undeclared
        // assignment in the same body must still throw.
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        fixture.Runtime.Evaluate("""
            (function () {
              const script = document.createElement('script');
              script.textContent = '"use strict";\nvar thrown = "none";\ntry { undeclaredBinding = 1; } catch (e) { thrown = e.constructor.name; }';
              document.head.appendChild(script);
            })()
            """);
        Assert.Equal("ReferenceError", fixture.Runtime.Evaluate("globalThis.thrown")!.GetValue<string>());
        Assert.Equal("undefined", fixture.Runtime.Evaluate("typeof globalThis.undeclaredBinding")!.GetValue<string>());
    }

    [Fact]
    public void InsertedScriptErrorIsContainedAtTheInsertionPoint()
    {
        // What the shim's catch around the old eval call did: report the failure
        // and keep going, so a later script still runs and appendChild itself
        // never throws into page code.
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var outcome = fixture.Runtime.Evaluate("""
            (function () {
              let insertion = 'ok';
              try {
                const bad = document.createElement('script');
                bad.textContent = '"use strict";\nthrow new TypeError("boom");';
                document.head.appendChild(bad);
              } catch (e) { insertion = 'threw ' + e.message; }
              const good = document.createElement('script');
              good.textContent = '"use strict";\nvar afterFailure = 1;';
              document.head.appendChild(good);
              return insertion + '|' + typeof globalThis.afterFailure;
            })()
            """);
        Assert.Equal("ok|number", outcome!.GetValue<string>());
    }

    [Fact]
    public void ShimIsBridgedOntoTheClassicScriptOp()
    {
        // The bridge is what makes the cases above pass; pin that it applied
        // rather than silently finding nothing to rewrite.
        Assert.Contains("(0, eval)(body)", BootstrapSource.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("(0, eval)(body)", BootstrapSource.EngineText, StringComparison.Ordinal);
        Assert.DoesNotContain("(0, eval)(code)", BootstrapSource.EngineText, StringComparison.Ordinal);
        // The event-handler-attribute eval is left alone: its source is a function
        // body, not a script.
        Assert.Contains("(0, eval)(src)", BootstrapSource.EngineText, StringComparison.Ordinal);
        Assert.Contains("op_run_classic_script", BootstrapSource.OpNames);
    }

    private static string JsLiteral(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
