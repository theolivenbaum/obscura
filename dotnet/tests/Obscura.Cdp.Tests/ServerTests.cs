using System.Text.Json.Nodes;
using System.Threading.Channels;
using Obscura.Js.Ops;
using Obscura.Net;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>tests</c> module in
/// <c>crates/obscura-cdp/src/server.rs</c>.
/// </summary>
public sealed class ServerTests
{
    private static CookieInfo Cookie(string name, string value) => new()
    {
        Name = name,
        Value = value,
        Domain = "example.com",
        Path = "/",
        Secure = false,
        HttpOnly = false,
        SameSite = "Lax",
        Expires = null,
    };

    [Fact]
    public void PageRuntimeAdvancesWhileCdpClientIsSilent()
    {
        SingleThreadedSynchronizationContext.Run(async () =>
        {
            var server = Channel.CreateUnbounded<ServerMessage>();
            var replies = Channel.CreateUnbounded<string>();
            using var shutdown = new CancellationTokenSource();
            using var stop = new CancellationTokenSource();
            var defaultContext = CdpContext.New().DefaultContext;
            var processor = CdpServer.CdpProcessorAsync(
                server.Reader, defaultContext, shutdown.Token, stop.Token);

            Assert.True(server.Writer.TryWrite(new ServerMessage.NewConnection(replies.Writer)));
            var init = await Receive(replies.Reader, TimeSpan.FromSeconds(2), "processor init");
            Assert.Contains("__init", init, StringComparison.Ordinal);

            void Send(JsonNode value) =>
                Assert.True(server.Writer.TryWrite(
                    new ServerMessage.Cdp(CdpJson.Serialize(value), replies.Writer)));

            Send(new JsonObject
            {
                ["id"] = 1,
                ["method"] = "Target.createTarget",
                ["params"] = new JsonObject { ["url"] = "about:blank" },
            });

            string? sessionId = null;
            while (true)
            {
                var value = CdpJson.Parse(
                    await Receive(replies.Reader, TimeSpan.FromSeconds(5), "create target response"));
                sessionId ??= value.Get("params").Get("sessionId").AsString();
                if (value.Get("id").AsU64() == 1)
                {
                    break;
                }
            }

            Assert.NotNull(sessionId);

            Send(new JsonObject
            {
                ["id"] = 2,
                ["method"] = "Runtime.evaluate",
                ["sessionId"] = sessionId,
                ["params"] = new JsonObject
                {
                    ["expression"] =
                        "(() => { setTimeout(() => globalThis.__autonomousDone = 'yes', 40); return 'armed'; })()",
                    ["returnByValue"] = true,
                },
            });
            while (true)
            {
                var value = CdpJson.Parse(
                    await Receive(replies.Reader, TimeSpan.FromSeconds(5), "timer arm response"));
                if (value.Get("id").AsU64() == 2)
                {
                    break;
                }
            }

            // This is deliberately host/client time. No CDP message is sent while
            // the timeout becomes due; Chrome's renderer still runs, and
            // Obscura's connection-owned page pump must do the same.
            await Task.Delay(120);

            Send(new JsonObject
            {
                ["id"] = 3,
                ["method"] = "Runtime.evaluate",
                ["sessionId"] = sessionId,
                ["params"] = new JsonObject
                {
                    ["expression"] = "globalThis.__autonomousDone || 'missing'",
                    ["returnByValue"] = true,
                },
            });
            while (true)
            {
                var value = CdpJson.Parse(
                    await Receive(replies.Reader, TimeSpan.FromSeconds(5), "timer observation response"));
                if (value.Get("id").AsU64() == 3)
                {
                    Assert.Equal("yes", value.Get("result").Get("result").Get("value").AsString());
                    break;
                }
            }

            server.Writer.TryComplete();
            await processor.WaitAsync(TimeSpan.FromSeconds(5));
        });
    }

    [Fact]
    public void CookieDeltaMergesChangesWithoutRevertingOtherConnections()
    {
        var destination = new CookieJar();
        destination.SetCookiesFromCdp([Cookie("sid", "newer"), Cookie("other", "kept")]);
        List<CookieInfo> initial = [Cookie("sid", "old"), Cookie("removed", "old")];
        List<CookieInfo> current = [Cookie("sid", "old"), Cookie("added", "value")];

        ServerSupport.MergeCookieDelta(destination, initial, current);

        var cookies = destination.GetAllCookies();
        Assert.Contains(cookies, c =>
            string.Equals(c.Name, "sid", StringComparison.Ordinal) &&
            string.Equals(c.Value, "newer", StringComparison.Ordinal));
        Assert.Contains(cookies, c => string.Equals(c.Name, "other", StringComparison.Ordinal));
        Assert.Contains(cookies, c => string.Equals(c.Name, "added", StringComparison.Ordinal));
        Assert.DoesNotContain(cookies, c => string.Equals(c.Name, "removed", StringComparison.Ordinal));
    }

    /// <summary>
    /// Issue #363: only an exact <c>Page.navigate</c> may take the spawn-and-defer
    /// navigation path. A substring match also caught
    /// <c>Page.navigateToHistoryEntry</c> (goBack / goForward), which has no
    /// <c>url</c> param, so it was misrouted into the raw-navigate path and failed
    /// with "Invalid URL" instead of reaching its real handler.
    /// </summary>
    [Fact]
    public void OnlyExactPageNavigateRoutesAsNavigation()
    {
        Assert.True(ServerSupport.IsNavigateMethod(
            """{"id":1,"method":"Page.navigate","params":{"url":"https://example.com"}}"""));
        Assert.False(ServerSupport.IsNavigateMethod(
            """{"id":2,"method":"Page.navigateToHistoryEntry","params":{"entryId":0}}"""));
    }

    /// <summary>
    /// A <c>Runtime.evaluate</c> whose expression merely contains the literal
    /// "Page.navigate" must not be misrouted, and malformed input is not a
    /// navigation.
    /// </summary>
    [Fact]
    public void UnrelatedMethodsDoNotRouteAsNavigation()
    {
        Assert.False(ServerSupport.IsNavigateMethod(
            """{"id":3,"method":"Runtime.evaluate","params":{"expression":"'Page.navigate'"}}"""));
        Assert.False(ServerSupport.IsNavigateMethod("not json"));
    }

    /// <summary>
    /// Issue #365: <c>Fetch.continueRequest</c> header overrides must be parsed
    /// from the CDP <c>[{name, value}]</c> list so they can be applied to the
    /// outgoing request.
    /// </summary>
    [Fact]
    public void ParseCdpHeadersReadsNameValuePairs()
    {
        var parameters = new JsonObject
        {
            ["headers"] = new JsonArray
            {
                new JsonObject { ["name"] = "X-A", ["value"] = "1" },
                new JsonObject { ["name"] = "X-B", ["value"] = "2" },
            },
        };
        var headers = ServerSupport.ParseCdpHeaders(parameters);
        Assert.NotNull(headers);
        Assert.Equal("1", headers.GetValueOrDefault("X-A"));
        Assert.Equal("2", headers.GetValueOrDefault("X-B"));
    }

    /// <summary>
    /// No <c>headers</c> field means "leave the request's headers untouched",
    /// which is null, not an empty map that would clear them.
    /// </summary>
    [Fact]
    public void ParseCdpHeadersAbsentIsNone() =>
        Assert.Null(ServerSupport.ParseCdpHeaders(
            new JsonObject { ["url"] = "https://example.com" }));

    [Fact]
    public void FetchResolutionIsHandledOnceByTheOuterProcessor()
    {
        var resolution = new TaskCompletionSource<InterceptResolution>();
        Dictionary<string, TaskCompletionSource<InterceptResolution>> paused =
            new(StringComparer.Ordinal) { ["request-1"] = resolution };
        var replies = Channel.CreateUnbounded<string>();
        var ctx = CdpContext.New();

        Assert.True(ServerSupport.HandleFetchResolution(
            """{"id":17,"method":"Fetch.continueRequest","params":{"requestId":"request-1"}}""",
            ctx,
            replies.Writer,
            paused));
        Assert.True(resolution.Task.IsCompletedSuccessfully);
        Assert.IsType<InterceptResolution.Continue>(resolution.Task.Result);

        Assert.True(replies.Reader.TryRead(out var raw));
        var response = CdpJson.Parse(raw);
        Assert.Equal(17UL, response.Get("id").AsU64());
        Assert.False(replies.Reader.TryRead(out _), "must not emit a duplicate response");
    }

    /// <summary>
    /// A <c>Fetch.*</c> frame that is not a resolution must leave the paused
    /// request parked.
    /// </summary>
    /// <remarks>
    /// The reference implementation removes the resolver before it knows the
    /// method and returns false on the default arm, so a
    /// <c>Fetch.getResponseBody</c> naming a paused request destroyed that
    /// request's only channel back to the waiting fetch op.
    /// </remarks>
    [Fact]
    public void NonResolutionFetchFrameLeavesTheRequestParked()
    {
        var resolution = new TaskCompletionSource<InterceptResolution>();
        Dictionary<string, TaskCompletionSource<InterceptResolution>> paused =
            new(StringComparer.Ordinal) { ["request-1"] = resolution };
        var replies = Channel.CreateUnbounded<string>();
        var ctx = CdpContext.New();

        Assert.False(ServerSupport.HandleFetchResolution(
            """{"id":18,"method":"Fetch.getResponseBody","params":{"requestId":"request-1"}}""",
            ctx,
            replies.Writer,
            paused));
        Assert.True(paused.ContainsKey("request-1"));
        Assert.False(resolution.Task.IsCompleted);
        Assert.False(replies.Reader.TryRead(out _));
    }

    [Fact]
    public void FulfillRequestDecodesTheLenientBase64TheReferenceUses()
    {
        // Padding and stray whitespace are filtered out rather than rejected, and
        // a trailing partial group still yields its whole bytes.
        Assert.Equal("hello", ServerSupport.DecodeBase64("aGVsbG8="));
        Assert.Equal("hello", ServerSupport.DecodeBase64("aGVs bG8="));
        Assert.Equal("hi", ServerSupport.DecodeBase64("aGk="));
        Assert.Equal(string.Empty, ServerSupport.DecodeBase64(string.Empty));
    }

    /// <summary>
    /// A <c>Page.navigate</c> arriving on the wire takes the spawn-and-defer path
    /// rather than the plain dispatch one, so its response and its event stream
    /// are produced by the server rather than by the Page domain handler.
    /// </summary>
    [Fact]
    public void PageNavigateTakesTheSpawnAndDeferPathAndStillAnswers()
    {
        SingleThreadedSynchronizationContext.Run(async () =>
        {
            var server = Channel.CreateUnbounded<ServerMessage>();
            var replies = Channel.CreateUnbounded<string>();
            using var shutdown = new CancellationTokenSource();
            using var stop = new CancellationTokenSource();
            var processor = CdpServer.CdpProcessorAsync(
                server.Reader, CdpContext.New().DefaultContext, shutdown.Token, stop.Token);

            server.Writer.TryWrite(new ServerMessage.NewConnection(replies.Writer));
            await Receive(replies.Reader, TimeSpan.FromSeconds(2), "processor init");

            void Send(JsonNode value) =>
                server.Writer.TryWrite(
                    new ServerMessage.Cdp(CdpJson.Serialize(value), replies.Writer));

            Send(new JsonObject
            {
                ["id"] = 1,
                ["method"] = "Target.createTarget",
                ["params"] = new JsonObject { ["url"] = "about:blank" },
            });

            string? sessionId = null;
            while (true)
            {
                var value = CdpJson.Parse(
                    await Receive(replies.Reader, TimeSpan.FromSeconds(10), "create target"));
                sessionId ??= value.Get("params").Get("sessionId").AsString();
                if (value.Get("id").AsU64() == 1)
                {
                    break;
                }
            }

            Assert.NotNull(sessionId);

            Send(new JsonObject
            {
                ["id"] = 4,
                ["method"] = "Page.navigate",
                ["sessionId"] = sessionId,
                ["params"] = new JsonObject
                {
                    ["url"] = "data:text/html,<html><body><p id=ok>hi</p></body></html>",
                    ["waitUntil"] = "load",
                },
            });

            // On this path the command response goes out BEFORE the navigation
            // events, which is the reverse of the plain dispatch path, so read
            // past the response rather than stopping at it.
            var sawResponse = false;
            var sawFrameNavigated = false;
            while (!sawResponse || !sawFrameNavigated)
            {
                var value = CdpJson.Parse(
                    await Receive(replies.Reader, TimeSpan.FromSeconds(20), "navigate"));
                if (string.Equals(
                        value.Get("method").AsString(), "Page.frameNavigated", StringComparison.Ordinal))
                {
                    sawFrameNavigated = true;
                }

                if (value.Get("id").AsU64() == 4)
                {
                    Assert.Null(value.Get("error"));
                    Assert.NotNull(value.Get("result").Get("frameId").AsString());
                    Assert.StartsWith(
                        "loader-",
                        value.Get("result").Get("loaderId").AsStringOr(string.Empty),
                        StringComparison.Ordinal);
                    Assert.False(
                        sawFrameNavigated,
                        "the spawn-and-defer path answers the command before its events");
                    sawResponse = true;
                }
            }

            server.Writer.TryComplete();
            await processor.WaitAsync(TimeSpan.FromSeconds(10));
        });
    }

    /// <summary>
    /// The frames answered straight off the reader, without waking the
    /// connection's processor at all.
    /// </summary>
    [Fact]
    public void FastPathAnswersTheConstantCommands()
    {
        var enable = ServerSupport.FastPathResponse(
            """{"id":8,"method":"Network.enable","sessionId":"s"}""");
        Assert.Equal("""{"id":8,"result":{},"sessionId":"s"}""", enable);

        var version = ServerSupport.FastPathResponse("""{"id":9,"method":"Browser.getVersion"}""");
        Assert.NotNull(version);
        var parsed = CdpJson.Parse(version);
        Assert.Equal("1.3", parsed.Get("result").Get("protocolVersion").AsString());
        Assert.Equal("Chrome/145.0.0.0", parsed.Get("result").Get("product").AsString());

        // Anything not on the list falls through to the processor.
        Assert.Null(ServerSupport.FastPathResponse("""{"id":10,"method":"Page.navigate"}"""));
        Assert.Null(ServerSupport.FastPathResponse("garbage"));
    }

    /// <summary>
    /// An active screencast keeps producing frames from the connection's own
    /// pump, and a frame that could not be sent because the acknowledgement
    /// window was full is not lost: its damage stays pending and is captured as
    /// soon as capacity returns.
    /// </summary>
    [Fact]
    public async Task AutonomousScreencastPumpsTimersAndRetainsBackpressuredDamage()
    {
        var ctx = CdpContext.New();
        var pageId = ctx.CreatePage();
        var sessionId = $"{pageId}-session";
        ctx.Sessions[sessionId] = pageId;
        ctx.GetSessionPageMut(sessionId)!.SetViewport((96.0f, 64.0f));

        var navigated = await Domains.Page.HandleAsync(
            "navigate",
            new JsonObject
            {
                ["url"] =
                    "data:text/html,<html style='margin:0'><body style='margin:0;width:96px;height:64px;background:red'></body></html>",
                ["waitUntil"] = "load",
            },
            ctx,
            sessionId);
        Assert.True(navigated.IsOk, navigated.Error);
        ctx.PendingEvents.Clear();

        var started = await Domains.Page.HandleAsync("startScreencast", new JsonObject(), ctx, sessionId);
        Assert.True(started.IsOk, started.Error);
        var streamId = ctx.PendingEvents
            .First(e => string.Equals(e.Method, "Page.screencastFrame", StringComparison.Ordinal))
            .Params.Get("sessionId").AsI64();
        Assert.NotNull(streamId);
        ctx.PendingEvents.Clear();

        var replies = Channel.CreateUnbounded<string>();
        ctx.GetSessionPageMut(sessionId)!.Evaluate(
            "setTimeout(() => document.body.setAttribute('style', 'margin:0;width:96px;height:64px;background:green'), 0)");
        await CdpServer.PumpLivePageEventLoopAsync(ctx);
        await CdpServer.PumpAndForwardScreencastFramesAsync(ctx, replies.Writer);

        Assert.True(replies.Reader.TryRead(out var first), "timer mutation should emit a frame");
        var firstUpdate = CdpJson.Parse(first);
        Assert.Equal("Page.screencastFrame", firstUpdate.Get("method").AsString());
        Assert.Equal(streamId, firstUpdate.Get("params").Get("sessionId").AsI64());
        Assert.False(replies.Reader.TryRead(out _));
        Assert.Equal(2, ctx.Screencasts[sessionId].FramesInFlight);

        // The second mutation is pumped while the two-frame acknowledgement
        // window is full. It must not emit yet, but its damage must remain
        // pending and appear immediately after capacity is returned.
        ctx.GetSessionPageMut(sessionId)!.Evaluate(
            "setTimeout(() => document.body.setAttribute('style', 'margin:0;width:96px;height:64px;background:blue'), 0)");
        await CdpServer.PumpLivePageEventLoopAsync(ctx);
        await CdpServer.PumpAndForwardScreencastFramesAsync(ctx, replies.Writer);
        Assert.False(replies.Reader.TryRead(out _));
        Assert.True(ctx.Screencasts[sessionId].AutonomousFramePending);

        var acked = await Domains.Page.HandleAsync(
            "screencastFrameAck",
            new JsonObject { ["sessionId"] = streamId },
            ctx,
            sessionId);
        Assert.True(acked.IsOk, acked.Error);
        await CdpServer.PumpAndForwardScreencastFramesAsync(ctx, replies.Writer);

        Assert.True(
            replies.Reader.TryRead(out var second),
            "backpressured damage should emit after ack");
        var afterAck = CdpJson.Parse(second);
        Assert.Equal("Page.screencastFrame", afterAck.Get("method").AsString());
        Assert.Equal(streamId, afterAck.Get("params").Get("sessionId").AsI64());
        Assert.False(ctx.Screencasts[sessionId].AutonomousFramePending);
    }

    /// <summary>
    /// A visual mutation delivered by <c>requestAnimationFrame</c>, with no CDP
    /// command in between, still reaches the client: the active stream's periodic
    /// event-loop and render pump is the only path that can capture it.
    /// </summary>
    [Fact]
    public async Task AutonomousScreencastObservesRafVisualMutations()
    {
        var ctx = CdpContext.New();
        var pageId = ctx.CreatePage();
        var sessionId = $"{pageId}-session";
        ctx.Sessions[sessionId] = pageId;
        ctx.GetSessionPageMut(sessionId)!.SetViewport((96.0f, 64.0f));

        var navigated = await Domains.Page.HandleAsync(
            "navigate",
            new JsonObject
            {
                ["url"] =
                    "data:text/html,<html style='margin:0'><body style='margin:0;width:96px;height:64px;background:red'></body></html>",
                ["waitUntil"] = "load",
            },
            ctx,
            sessionId);
        Assert.True(navigated.IsOk, navigated.Error);
        ctx.PendingEvents.Clear();

        var started = await Domains.Page.HandleAsync("startScreencast", new JsonObject(), ctx, sessionId);
        Assert.True(started.IsOk, started.Error);
        var initial = ctx.PendingEvents
            .First(e => string.Equals(e.Method, "Page.screencastFrame", StringComparison.Ordinal));
        var streamId = initial.Params.Get("sessionId").AsI64();
        var initialData = initial.Params.Get("data").AsString();
        Assert.NotNull(initialData);
        ctx.PendingEvents.Clear();

        var acked = await Domains.Page.HandleAsync(
            "screencastFrameAck",
            new JsonObject { ["sessionId"] = streamId },
            ctx,
            sessionId);
        Assert.True(acked.IsOk, acked.Error);

        // Bypass CDP dispatch after scheduling the callback. The only path which
        // can deliver and capture this update is the active stream's periodic
        // event-loop and render pump.
        ctx.GetSessionPageMut(sessionId)!.Evaluate(
            "requestAnimationFrame(() => document.body.setAttribute('style','margin:0;width:96px;height:64px;background:lime'))");
        await Task.Delay(25);
        await CdpServer.PumpLivePageEventLoopAsync(ctx);
        await CdpServer.PumpAndForwardScreencastFramesAsync(ctx, null);

        var rafFrame = ctx.PendingEvents
            .FirstOrDefault(e =>
                string.Equals(e.Method, "Page.screencastFrame", StringComparison.Ordinal));
        Assert.True(rafFrame is not null, "RAF visual mutation must autonomously emit a frame");
        Assert.NotEqual(initialData, rafFrame.Params.Get("data").AsString());
    }

    /// <summary>
    /// The three <c>/json/*</c> endpoints, byte for byte. DevTools clients read
    /// these off the same accept thread that serves the WebSocket upgrade, and
    /// the bodies are <c>serde_json::to_string_pretty</c> output, so the
    /// whitespace and key order are part of the response.
    /// </summary>
    [Fact]
    public async Task JsonControlPlaneEndpointsMatchTheReferenceBodies()
    {
        await using var server = await CdpServerHandle.StartAsync();
        var timeout = TimeSpan.FromSeconds(5);

        var version = await CdpTestClient.HttpGetAsync(server.Port, "/json/version", timeout);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", version, StringComparison.Ordinal);
        Assert.Contains(
            "Content-Type: application/json; charset=utf-8\r\n", version, StringComparison.Ordinal);
        Assert.Contains(
            "{\n  \"Browser\": \"Chrome/145.0.0.0\",\n  \"Protocol-Version\": \"1.3\",\n",
            version,
            StringComparison.Ordinal);
        Assert.EndsWith(
            $"  \"webSocketDebuggerUrl\": \"ws://127.0.0.1:{server.Port}/devtools/browser\"\n}}",
            version,
            StringComparison.Ordinal);

        var list = await CdpTestClient.HttpGetAsync(server.Port, "/json/list", timeout);
        Assert.Contains(
            "[\n  {\n    \"description\": \"\",\n    \"devtoolsFrontendUrl\": \"\",\n    \"id\": \"page-1\",\n",
            list,
            StringComparison.Ordinal);
        Assert.EndsWith(
            $"    \"webSocketDebuggerUrl\": \"ws://127.0.0.1:{server.Port}/devtools/page/page-1\"\n  }}\n]",
            list,
            StringComparison.Ordinal);

        // `GET /json` (no suffix) is the same listing endpoint.
        var bare = await CdpTestClient.HttpGetAsync(server.Port, "/json", timeout);
        Assert.Contains("\"id\": \"page-1\"", bare, StringComparison.Ordinal);

        var protocol = await CdpTestClient.HttpGetAsync(server.Port, "/json/protocol", timeout);
        Assert.EndsWith(
            "{\n  \"version\": {\n    \"major\": \"1\",\n    \"minor\": \"3\"\n  }\n}",
            protocol,
            StringComparison.Ordinal);
    }

    private static async Task<string> Receive(
        ChannelReader<string> reader,
        TimeSpan timeout,
        string what)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (reader.TryRead(out var message))
            {
                return message;
            }

            await Task.Delay(1);
        }

        throw new TimeoutException($"{what} timeout");
    }
}

/// <summary>
/// The wire encoding is a contract with the Rust server: strict CDP clients read
/// the same bytes from both, and the <c>/json/*</c> endpoints are compared
/// against Chrome's own. These pin the three places <c>System.Text.Json</c>'s
/// defaults would have changed them.
/// </summary>
public sealed class WireFormatTests
{
    [Fact]
    public void StringsEscapeOnlyWhatSerdeEscapes()
    {
        // System.Text.Json's default encoder escapes every one of these; serde
        // escapes none of them.
        Assert.Equal(
            "{\"s\":\"a+b<c>d&e'fé\"}",
            CdpJson.Serialize(new JsonObject { ["s"] = "a+b<c>d&e'fé" }));

        // What it does escape: quote, backslash, and the C0 controls, with the
        // short forms where serde has them.
        Assert.Equal(
            "{\"s\":\"\\\"\\\\\\n\\r\\t\\b\\f\\u0000\\u001f\"}",
            CdpJson.Serialize(new JsonObject { ["s"] = "\"\\\n\r\t\b\f\u0000\u001f" }));

        // DEL and U+2028 stay raw, as they do in serde_json.
        Assert.Equal(
            "\"\u007f\u2028\"",
            CdpJson.Serialize(JsonValue.Create("\u007f\u2028")));
    }

    /// <summary>
    /// Key order on the wire is insertion order, not sorted.
    /// </summary>
    /// <remarks>
    /// <c>serde_json::Map</c> is a <c>BTreeMap</c> by default, which would sort,
    /// but <c>deno_core</c> enables <c>preserve_order</c> and cargo unifies the
    /// feature across the workspace. Sorting here produced a wire frame that
    /// parsed the same and compared byte-different against the reference binary,
    /// which is the only place the difference shows.
    /// </remarks>
    [Fact]
    public void ObjectKeysGoOutInInsertionOrder()
    {
        Assert.Equal(
            """{"zeta":1,"alpha":2,"Mid":3,"_u":4}""",
            CdpJson.Serialize(new JsonObject
            {
                ["zeta"] = 1,
                ["alpha"] = 2,
                ["Mid"] = 3,
                ["_u"] = 4,
            }));

        // Round-tripping a parsed frame keeps the document's own order too.
        const string Frame = """{"b":1,"a":{"d":2,"c":3}}""";
        Assert.Equal(Frame, CdpJson.Serialize(CdpJson.Parse(Frame)));
    }

    [Theory]
    // Floats always carry a decimal point, and ryu signs the exponent both ways.
    [InlineData(1.0, "1.0")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(0.0, "0.0")]
    [InlineData(1e5, "100000.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e30, "1e+30")]
    [InlineData(1.234e33, "1.234e+33")]
    [InlineData(1e-5, "0.00001")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(1.5e-9, "1.5e-9")]
    [InlineData(1e100, "1e+100")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    public void FloatsUseRyuPrettyFormatting(double value, string expected) =>
        Assert.Equal(expected, CdpJson.Serialize(JsonValue.Create(value)));

    [Fact]
    public void NegativeZeroKeepsItsSign() =>
        Assert.Equal("-0.0", CdpJson.Serialize(JsonValue.Create(-0.0)));

    [Fact]
    public void IntegersStayIntegers() =>
        Assert.Equal(
            """{"i":-5,"u":1234567890123456789}""",
            CdpJson.Serialize(new JsonObject { ["i"] = -5, ["u"] = 1234567890123456789L }));

    [Fact]
    public void PrettyMatchesSerdeToStringPretty()
    {
        Assert.Equal("{}", CdpJson.SerializePretty(new JsonObject()));
        Assert.Equal("[]", CdpJson.SerializePretty(new JsonArray()));
        Assert.Equal(
            "{\n  \"a\": {},\n  \"b\": [],\n  \"c\": [\n    1,\n    {\n      \"d\": 2.5\n    }\n  ]\n}",
            CdpJson.SerializePretty(new JsonObject
            {
                ["a"] = new JsonObject(),
                ["b"] = new JsonArray(),
                ["c"] = new JsonArray { 1, new JsonObject { ["d"] = 2.5 } },
            }));
    }

    [Fact]
    public void ResponsesKeepSerdeFieldOrderAndSkipNones()
    {
        Assert.Equal(
            """{"id":7,"result":{"a":1}}""",
            CdpResponse.Success(7, new JsonObject { ["a"] = 1 }, null).ToJson());
        Assert.Equal(
            """{"id":7,"result":{},"sessionId":"s"}""",
            CdpResponse.Success(7, new JsonObject(), "s").ToJson());
        Assert.Equal(
            """{"id":7,"error":{"code":-32601,"message":"nope"},"sessionId":"s"}""",
            CdpResponse.Failure(7, -32601, "nope", "s").ToJson());
    }

    [Fact]
    public void EventsKeepSerdeFieldOrderAndSkipNones()
    {
        Assert.Equal(
            """{"method":"A.b","params":{"x":1}}""",
            CdpEvent.New("A.b", new JsonObject { ["x"] = 1 }).ToJson());
        Assert.Equal(
            """{"method":"A.b","params":{},"sessionId":"s"}""",
            CdpEvent.WithSession("A.b", new JsonObject(), "s").ToJson());
    }

    [Fact]
    public void RequestParsingMatchesTheSerdeContract()
    {
        var req = CdpRequest.TryParse("""{"id":3,"method":"X.y"}""");
        Assert.NotNull(req);
        Assert.Equal(3UL, req.Id);
        Assert.Equal("X.y", req.Method);
        Assert.True(req.Params.IsNull());
        Assert.Null(req.SessionId);

        // Missing or mistyped required fields are a parse failure, which is what
        // every `serde_json::from_str::<CdpRequest>(..)` call site branches on.
        Assert.Null(CdpRequest.TryParse("""{"method":"X.y"}"""));
        Assert.Null(CdpRequest.TryParse("""{"id":3}"""));
        Assert.Null(CdpRequest.TryParse("""{"id":-1,"method":"X.y"}"""));
        Assert.Null(CdpRequest.TryParse("""{"id":3,"method":7}"""));
        Assert.Null(CdpRequest.TryParse("""{"id":3,"method":"X.y","sessionId":7}"""));
        Assert.Null(CdpRequest.TryParse("not json"));

        // An explicit null sessionId is None, not an error.
        Assert.Null(CdpRequest.TryParse("""{"id":3,"method":"X.y","sessionId":null}""")?.SessionId);
    }
}
