using System.Text.Json.Nodes;

using Xunit;

using PageDomain = Obscura.Cdp.Domains.Page;
using RuntimeDomain = Obscura.Cdp.Domains.Runtime;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>tests</c> module in
/// <c>crates/obscura-cdp/src/domains/runtime.rs</c>.
/// </summary>
/// <remarks>
/// Issue #51 - Runtime.evaluate / callFunctionOn must read and validate contextId.
/// Pre-fix the parameter was silently dropped, so Playwright's locator (which targets the
/// utility world created by Page.createIsolatedWorld) ran in the wrong context and timed
/// out.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class RuntimeDomainTests
{
    [Fact]
    public async Task EvaluateRejectsUnknownContextId()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string error = CdpDomainFixtures.ErrorOf(await RuntimeDomain.HandleAsync(
            "evaluate",
            CdpDomainFixtures.Json("""{"expression":"1 + 1","contextId":9999}"""),
            ctx,
            null));
        Assert.Contains(
            "Cannot find context with specified id", error, StringComparison.Ordinal);
        Assert.Contains("9999", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallFunctionOnRejectsUnknownExecutionContextId()
    {
        var ctx = CdpContext.New();
        using IDisposable owned2 = CoreCdp.Owned(ctx);
        string error = CdpDomainFixtures.ErrorOf(await RuntimeDomain.HandleAsync(
            "callFunctionOn",
            CdpDomainFixtures.Json(
                """{"functionDeclaration":"() => 42","executionContextId":9999}"""),
            ctx,
            null));
        Assert.Contains(
            "Cannot find context with specified id", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateRejectsUnadvertisedCompatibilityContextIds()
    {
        foreach (int contextId in new[] { 1, 2 })
        {
            var ctx = CdpContext.New();
            using IDisposable owned3 = CoreCdp.Owned(ctx);
            string error = CdpDomainFixtures.ErrorOf(await RuntimeDomain.HandleAsync(
                "evaluate",
                new JsonObject { ["expression"] = "1 + 1", ["contextId"] = contextId },
                ctx,
                null));
            Assert.Contains("Cannot find context", error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EvaluateReportsARejectionThroughExceptionDetails()
    {
        var ctx = CdpContext.New();
        using IDisposable owned4 = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string sessionId = "evaluate-rejection";
        ctx.Sessions[sessionId] = pageId;

        JsonNode reply = CdpDomainFixtures.Unwrap(await RuntimeDomain.HandleAsync(
            "evaluate",
            CdpDomainFixtures.Json("""
                {"expression":"Promise.reject(new Error('boom'))","returnByValue":true,
                 "awaitPromise":true,"timeout":3000}
                """),
            ctx,
            sessionId));

        JsonNode details = reply["exceptionDetails"]
            ?? throw new InvalidOperationException("a thrown value is reported through exceptionDetails");
        Assert.Equal("Uncaught", details["text"].AsString());
        Assert.Equal("error", details["exception"]!["subtype"].AsString());
        Assert.Equal("Error", details["exception"]!["className"].AsString());
        Assert.Contains(
            "boom",
            details["exception"]!["description"].AsStringOr(string.Empty),
            StringComparison.Ordinal);
        // Chrome repeats the exception in `result`, so a client that reads only that field
        // still sees the error rather than a stale success.
        Assert.Equal(
            CdpJson.Serialize(details["exception"]), CdpJson.Serialize(reply["result"]));
    }

    /// <summary>
    /// This is the path Puppeteer's page.evaluate(fn) takes. It used to answer
    /// successfully with <c>{}</c> for a rejected Error, so the caller could not tell a
    /// failure from an empty object.
    /// </summary>
    [Fact]
    public async Task CallFunctionOnReportsARejectionThroughExceptionDetails()
    {
        var ctx = CdpContext.New();
        using IDisposable owned5 = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string sessionId = "call-rejection";
        ctx.Sessions[sessionId] = pageId;

        JsonNode reply = CdpDomainFixtures.Unwrap(await RuntimeDomain.HandleAsync(
            "callFunctionOn",
            CdpDomainFixtures.Json("""
                {"functionDeclaration":"() => Promise.reject(new Error('boom'))",
                 "returnByValue":true,"awaitPromise":true,"timeout":3000}
                """),
            ctx,
            sessionId));

        JsonNode details = reply["exceptionDetails"]
            ?? throw new InvalidOperationException("a rejected call is reported through exceptionDetails");
        Assert.Equal("error", details["exception"]!["subtype"].AsString());
        Assert.True(
            reply["result"]!["value"].IsNull(),
            $"the error must not be serialized by value: {reply}");
    }

    [Fact]
    public async Task AResolvedEvaluationCarriesNoExceptionDetails()
    {
        var ctx = CdpContext.New();
        using IDisposable owned6 = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string sessionId = "evaluate-resolved";
        ctx.Sessions[sessionId] = pageId;

        JsonNode reply = CdpDomainFixtures.Unwrap(await RuntimeDomain.HandleAsync(
            "evaluate",
            CdpDomainFixtures.Json("""
                {"expression":"Promise.resolve(2)","returnByValue":true,"awaitPromise":true,
                 "timeout":3000}
                """),
            ctx,
            sessionId));
        Assert.Equal(2.0, reply["result"]!["value"].AsF64());
        Assert.False(reply.AsObject().ContainsKey("exceptionDetails"));
    }

    [Fact]
    public async Task EvaluateAwaitPromiseReportsTheRequestedTimeout()
    {
        var ctx = CdpContext.New();
        using IDisposable owned7 = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string sessionId = "await-timeout-session";
        ctx.Sessions[sessionId] = pageId;

        string error = CdpDomainFixtures.ErrorOf(await RuntimeDomain.HandleAsync(
            "evaluate",
            CdpDomainFixtures.Json("""
                {"expression":"new Promise(() => {})","returnByValue":true,"awaitPromise":true,
                 "timeout":25}
                """),
            ctx,
            sessionId));
        Assert.True(
            error.Contains("25ms timeout", StringComparison.Ordinal)
            || error.Contains("within 25ms", StringComparison.Ordinal),
            $"unexpected timeout error: {error}");
    }

    /// <summary>
    /// Round-trip: Page.createIsolatedWorld returns contextId N, and a subsequent
    /// Runtime.evaluate targeting that contextId must NOT be rejected.
    /// </summary>
    [Fact]
    public async Task CreateIsolatedWorldRegistersIdForEvaluate()
    {
        var ctx = CdpContext.New();
        using IDisposable owned8 = CoreCdp.Owned(ctx);
        // Bypass the page-attached path of createIsolatedWorld by direct insert - mirrors
        // the same effect as calling the page handler with a real session.
        ctx.ValidContextIds.Add(100);

        DomainResult result = await RuntimeDomain.HandleAsync(
            "evaluate",
            CdpDomainFixtures.Json("""{"expression":"1 + 1","contextId":100}"""),
            ctx,
            null);
        if (!result.IsOk)
        {
            Assert.DoesNotContain("Cannot find context", result.Error!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Regression for #122 item 7: puppeteer-extra's FrameManager.initialize fires
    /// Runtime.enable on the browser-level WebSocket BEFORE any page target exists. Real
    /// Chrome replies with <c>{}</c>; before the fix Obscura returned
    /// <c>{"error":{"code":-32601,"message":"No page"}}</c> and the puppeteer connect flow
    /// died.
    /// </summary>
    [Fact]
    public async Task EnableSucceedsWhenNoSessionAttached()
    {
        var ctx = CdpContext.New();
        using IDisposable owned9 = CoreCdp.Owned(ctx);
        JsonNode result = CdpDomainFixtures.Unwrap(
            await RuntimeDomain.HandleAsync("enable", new JsonObject(), ctx, null));
        CdpDomainFixtures.AssertJson("{}", result);
    }

    /// <summary>
    /// SEC-002 / #578 - Runtime.removeBinding must validate the binding name the same way
    /// addBinding does. Before the fix the name was interpolated straight into
    /// <c>delete globalThis['{name}']</c>, so a CDP client could break out of the string
    /// delimiter and run arbitrary JS in the page. This drives the real handler against a
    /// live page and asserts the injected statement never executes.
    /// </summary>
    [Fact]
    public async Task RemoveBindingRejectsInjectionInName()
    {
        var ctx = CdpContext.New();
        using IDisposable owned10 = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        string session = $"{pageId}-session";
        ctx.Sessions[session] = pageId;

        CdpDomainFixtures.Unwrap(await PageDomain.HandleAsync(
            "navigate",
            CdpDomainFixtures.Json("""{"url":"data:text/html,<p>hi</p>","waitUntil":"load"}"""),
            ctx,
            session));

        // Canary the injection would flip from 0 to 1.
        ctx.GetSessionPageMut(session)!.Evaluate("globalThis.__pwned = 0");

        // The generated code is `delete globalThis['{name}']`, which the runtime wraps as
        // `return ( ... )`. A comma-expression payload stays a single valid expression
        // through that wrapper and runs the assignment:
        //   delete globalThis['x'] , (globalThis.__pwned = 1) , globalThis['y']
        CdpDomainFixtures.Unwrap(await RuntimeDomain.HandleAsync(
            "removeBinding",
            new JsonObject { ["name"] = "x'] , (globalThis.__pwned = 1) , globalThis['y" },
            ctx,
            session));

        JsonNode? pwned = ctx.GetSessionPageMut(session)!.Evaluate("globalThis.__pwned");
        Assert.NotEqual(1.0, pwned.AsF64());
    }
}
