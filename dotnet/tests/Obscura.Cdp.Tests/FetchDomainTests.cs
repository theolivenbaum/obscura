using System.Text.Json.Nodes;
using Obscura.Cdp.Domains;
using Obscura.Js.Ops;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// Coverage for <c>crates/obscura-cdp/src/domains/fetch.rs</c>, which carries no
/// <c>#[cfg(test)] mod tests</c> of its own.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class FetchDomainTests
{
    private static Task<DomainResult> HandleAsync(
        string method,
        string parameters,
        CdpContext ctx,
        string? sessionId = null) =>
        Fetch.HandleAsync(method, CdpDomainFixtures.Json(parameters), ctx, sessionId);

    /// <summary>Resolves every intercepted request the moment it is published.</summary>
    private sealed class ImmediateSink(Func<InterceptedRequest, InterceptResolution> decide)
        : IInterceptSink
    {
        public List<string> SeenUrls { get; } = [];

        public bool TrySend(InterceptedRequest request)
        {
            SeenUrls.Add(request.Url);
            request.Resolver.TrySetResult(decide(request));
            return true;
        }
    }

    private static async Task<JsonNode> EvaluateAsync(CdpContext ctx, string sessionId, string expression)
    {
        CdpResponse response = await Dispatcher.DispatchAsync(
            new CdpRequest
            {
                Id = 1,
                Method = "Runtime.evaluate",
                Params = new JsonObject
                {
                    ["expression"] = expression,
                    ["returnByValue"] = true,
                    ["awaitPromise"] = true,
                },
                SessionId = sessionId,
            },
            ctx);
        Assert.True(response.Error is null, response.Error?.Message);
        return response.Result!;
    }

    [Fact]
    public async Task EnableStoresPatternsAndArmsTheSessionPage()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        string pageId = ctx.Sessions[session];

        Assert.True((await HandleAsync(
            "enable",
            """{"patterns": [{"urlPattern": "*://a.test/*"}, {"urlPattern": "*.png"}, {"noPattern": 1}]}""",
            ctx,
            session)).IsOk);

        Assert.True(ctx.FetchIntercept.Enabled);
        Assert.Equal(["*://a.test/*", "*.png"], ctx.FetchIntercept.Patterns);
        var page = ctx.GetPage(pageId);
        Assert.NotNull(page);
        Assert.Equal(["*://a.test/*", "*.png"], page.InterceptBlockPatterns);
        Assert.True(page.InterceptEnabled);

        // An omitted `patterns` array means "everything", as Chrome documents.
        Assert.True((await HandleAsync("enable", "{}", ctx, session)).IsOk);
        Assert.Equal(["*"], ctx.FetchIntercept.Patterns);

        // ...but a present-and-empty array means exactly nothing.
        Assert.True((await HandleAsync("enable", """{"patterns": []}""", ctx, session)).IsOk);
        Assert.Empty(ctx.FetchIntercept.Patterns);
    }

    [Fact]
    public async Task DisableDisarmsThePageAndContinuesEveryPausedRequest()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        string pageId = ctx.Sessions[session];
        Assert.True((await HandleAsync("enable", "{}", ctx, session)).IsOk);

        var paused = new PausedRequest
        {
            RequestId = ctx.FetchIntercept.NextRequestId(),
            Url = "https://a.test/x",
            Method = "GET",
            Headers = new Dictionary<string, string>(StringComparer.Ordinal),
            ResourceType = "Fetch",
        };
        Assert.Equal("interception-1", paused.RequestId);
        ctx.FetchIntercept.Paused[paused.RequestId] = paused;

        Assert.True((await HandleAsync("disable", "{}", ctx, session)).IsOk);

        Assert.False(ctx.FetchIntercept.Enabled);
        Assert.Empty(ctx.FetchIntercept.Patterns);
        Assert.Empty(ctx.FetchIntercept.Paused);
        var page = ctx.GetPage(pageId);
        Assert.NotNull(page);
        Assert.False(page.InterceptEnabled);
        Assert.Empty(page.InterceptBlockPatterns);

        FetchResolution resolution = await paused.Resolver.Task;
        var cont = Assert.IsType<FetchResolution.Continue>(resolution);
        Assert.Null(cont.Url);
        Assert.Null(cont.Method);
        Assert.Null(cont.Headers);
        Assert.Null(cont.PostData);
    }

    [Fact]
    public async Task ResolutionMethodsHandTheirDecisionToThePausedRequest()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();

        PausedRequest Park(string id)
        {
            var request = new PausedRequest
            {
                RequestId = id,
                Url = "https://a.test/x",
                Method = "GET",
                Headers = new Dictionary<string, string>(StringComparer.Ordinal),
                ResourceType = "Fetch",
            };
            ctx.FetchIntercept.Paused[id] = request;
            return request;
        }

        PausedRequest continued = Park("c1");
        Assert.True((await HandleAsync(
            "continueRequest",
            """{"requestId": "c1", "url": "https://b.test/y", "method": "POST", "postData": "hi"}""",
            ctx,
            session)).IsOk);
        var cont = Assert.IsType<FetchResolution.Continue>(await continued.Resolver.Task);
        Assert.Equal("https://b.test/y", cont.Url);
        Assert.Equal("POST", cont.Method);
        Assert.Equal("hi", cont.PostData);
        // Rust never forwards headers on this path, so neither does the port.
        Assert.Null(cont.Headers);

        PausedRequest fulfilled = Park("f1");
        Assert.True((await HandleAsync(
            "fulfillRequest",
            """
            {
                "requestId": "f1", "responseCode": 418,
                "responseHeaders": [{"name": "X-A", "value": "1"}, {"name": "bad"}],
                "body": "teapot"
            }
            """,
            ctx,
            session)).IsOk);
        var fulfill = Assert.IsType<FetchResolution.Fulfill>(await fulfilled.Resolver.Task);
        Assert.Equal(418, fulfill.Status);
        Assert.Equal([new KeyValuePair<string, string>("X-A", "1")], fulfill.Headers);
        Assert.Equal("teapot", fulfill.Body);

        PausedRequest failed = Park("x1");
        Assert.True((await HandleAsync(
            "failRequest",
            """{"requestId": "x1", "errorReason": "BlockedByClient"}""",
            ctx,
            session)).IsOk);
        Assert.Equal(
            "BlockedByClient",
            Assert.IsType<FetchResolution.Fail>(await failed.Resolver.Task).Reason);

        // A default reason, an unknown id, and a missing id.
        PausedRequest defaulted = Park("x2");
        Assert.True((await HandleAsync("failRequest", """{"requestId": "x2"}""", ctx, session)).IsOk);
        Assert.Equal(
            "Failed",
            Assert.IsType<FetchResolution.Fail>(await defaulted.Resolver.Task).Reason);
        Assert.True((await HandleAsync(
            "continueRequest",
            """{"requestId": "nobody"}""",
            ctx,
            session)).IsOk);
        Assert.Contains(
            "requestId required",
            CdpDomainFixtures.ErrorOf(await HandleAsync("fulfillRequest", "{}", ctx, session)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResponseBodyIsAStubAndUnknownMethodsError()
    {
        var ctx = CdpContext.New();
        JsonNode body = CdpDomainFixtures.Unwrap(
            await HandleAsync("getResponseBody", """{"requestId": "any"}""", ctx));
        Assert.Equal(string.Empty, body["body"]!.GetValue<string>());
        Assert.False(body["base64Encoded"]!.GetValue<bool>());
        Assert.Contains(
            "Unknown Fetch method: nope",
            CdpDomainFixtures.ErrorOf(await HandleAsync("nope", "{}", ctx)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TakeResponseBodyAsStreamMovesTheCachedBodyIntoAnIoHandle()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        var page = ctx.GetSessionPageMut(session);
        Assert.NotNull(page);
        await page.NavigateAsync("data:text/html,<html><body>streamed body</body></html>");
        string requestId = page.NetworkEvents[0].RequestId;

        JsonNode taken = CdpDomainFixtures.Unwrap(await HandleAsync(
            "takeResponseBodyAsStream",
            $$"""{"requestId": "{{requestId}}"}""",
            ctx,
            session));
        string handle = taken["stream"]!.GetValue<string>();

        JsonNode chunk = CdpDomainFixtures.Unwrap(await Io.HandleAsync(
            "read",
            CdpDomainFixtures.Json($$"""{"handle": "{{handle}}"}"""),
            ctx));
        Assert.Equal(
            "<html><body>streamed body</body></html>",
            System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(chunk["data"]!.GetValue<string>())));
        Assert.True(chunk["eof"]!.GetValue<bool>());

        // The body was moved out of the page cache, so a second take fails.
        Assert.Contains(
            "no cached body for",
            CdpDomainFixtures.ErrorOf(await HandleAsync(
                "takeResponseBodyAsStream",
                $$"""{"requestId": "{{requestId}}"}""",
                ctx,
                session)),
            StringComparison.Ordinal);
        Assert.Contains(
            "requires requestId",
            CdpDomainFixtures.ErrorOf(await HandleAsync("takeResponseBodyAsStream", "{}", ctx, session)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>Fetch.continueRequest</c> that rewrites the URL must still clear the SSRF /
    /// scheme gate the original request and every redirect clear.
    /// </summary>
    /// <remarks>
    /// The handler deliberately forwards the rewrite untouched; the re-validation lives in
    /// <c>op_fetch_url</c> so a rewrite cannot reach the network through a path that skips it.
    /// This drives the whole chain - CDP handler, interception channel, fetch op - to prove the
    /// rewrite is gated rather than trusted.
    /// </remarks>
    [Fact]
    public async Task ContinueWithARewrittenUrlStillPassesTheSsrfGate()
    {
        using CdpTestServer server = CdpTestServer.ServeHtml("<html><body>origin</body></html>");
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        var page = ctx.GetSessionPageMut(session);
        Assert.NotNull(page);
        await page.NavigateAsync(server.Url);

        var sink = new ImmediateSink(request =>
            new InterceptResolution.Continue("file:///etc/passwd", null, null, null));
        ctx.InterceptSink = sink;
        Assert.True((await HandleAsync("enable", "{}", ctx, session)).IsOk);

        JsonNode blocked = await EvaluateAsync(
            ctx,
            session,
            "fetch('/target').then(r => 'ok:' + r.status).catch(e => 'blocked:' + e.message)");
        Assert.Equal("blocked:net::ERR_FAILED", blocked["result"]!["value"]!.GetValue<string>());
        Assert.Single(sink.SeenUrls);
        Assert.EndsWith("/target", sink.SeenUrls[0], StringComparison.Ordinal);

        // Control: the same plumbing with no rewrite reaches the server and succeeds, so the
        // assertion above is about the gate and not about interception being broken.
        var passthrough = new ImmediateSink(_ => new InterceptResolution.Continue(null, null, null, null));
        ctx.InterceptSink = passthrough;
        Assert.True((await HandleAsync("enable", "{}", ctx, session)).IsOk);
        JsonNode allowed = await EvaluateAsync(
            ctx,
            session,
            "fetch('/target').then(r => 'ok:' + r.status).catch(e => 'blocked:' + e.message)");
        Assert.Equal("ok:200", allowed["result"]!["value"]!.GetValue<string>());
    }
}
