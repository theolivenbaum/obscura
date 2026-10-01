using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Origin decisions the host makes rather than the shim (SECURITY.md C1, C2, C3, H4, L9):
/// a page that replaces <c>URL</c>, <c>JSON.parse</c> or an iframe expando must not
/// change which origin a request, a message or a frame belongs to.
/// </summary>
public sealed partial class RuntimeTests
{
    /// <summary>
    /// C1: a page replacing <c>window.URL</c> so its document URL reports the target's
    /// origin must still fetch as its own origin: no cookies, and the CORS check fails.
    /// </summary>
    [Fact]
    public async Task FetchOriginIsTheDocumentsNotThePageComputedOne()
    {
        string? victimOrigin = null;
        using var server = new RawHttpServer(request =>
        {
            // Only the victim's own origin is allowed to read, with credentials.
            return "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n"
                + $"Access-Control-Allow-Origin: {victimOrigin}\r\n"
                + "Access-Control-Allow-Credentials: true\r\n"
                + "Content-Length: 6\r\nConnection: close\r\n\r\nsecret";
        });
        victimOrigin = server.Origin;
        var jar = new CookieJar();
        jar.SetCookie("sid=victim-session; Path=/", new Uri(server.Origin + "/"));
        using var fixture = RuntimeFixture.Blank();
        var runtime = fixture.Runtime;
        runtime.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        runtime.SetUrl("http://attacker.example/page");
        runtime.SetHttpClient(new PocketCalculatorHttpClient(jar, null, allowPrivateNetwork: true));
        runtime.RunPageInit();

        var result = await runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                const Real = URL;
                globalThis.URL = class extends Real {
                    get origin() { return "{{server.Origin}}"; }
                };
                try {
                    const r = await fetch("{{server.Origin}}/account");
                    return "read:" + await r.text();
                } catch (e) {
                    return "blocked";
                }
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        Assert.Equal("blocked", result.Value!.GetValue<string>());
        var request = Assert.Single(server.Requests);
        Assert.DoesNotContain("victim-session", request, StringComparison.Ordinal);
        Assert.Contains("Origin: http://attacker.example\r\n", request, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// C1, in a frame: a frame's fetch is made as the frame's document, whatever the frame
    /// claims through <c>URL</c>, and from a promise continuation as well as synchronously.
    /// </summary>
    [Fact]
    public async Task FrameFetchOriginIsTheFramesDocument()
    {
        using var server = new RawHttpServer(_ =>
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        using var fixture = RuntimeFixture.Blank();
        var runtime = fixture.Runtime;
        runtime.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        runtime.SetUrl("http://parent.example/");
        runtime.SetHttpClient(new PocketCalculatorHttpClient(new CookieJar(), null, allowPrivateNetwork: true));
        runtime.RunPageInit();
        using var frame = FrameRealm.Create(runtime, 1, 0, "http://frame.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);

        frame.ExecuteScript(
            "const RealURL = URL; globalThis.URL = class extends RealURL {"
            + " get origin() { return 'http://parent.example'; } };"
            + $"fetch('{server.Origin}/sync').catch(() => {{}});"
            + $"Promise.resolve().then(() => fetch('{server.Origin}/later').catch(() => {{}}));");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (server.Requests.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(2, server.Requests.Count);
        foreach (var request in server.Requests)
        {
            Assert.Contains("Origin: http://frame.example\r\n", request, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// M3: the fetch deadline covers the body as well as the headers. A server that sends
    /// its headers at once and then one byte every 200 ms used to hold the op open for as
    /// long as it liked.
    /// </summary>
    [Fact]
    public async Task FetchTimeoutCoversATricklingBody()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        new Thread(() =>
        {
            using (listener)
            {
                try
                {
                    using var socket = listener.AcceptSocket();
                    using var stream = new NetworkStream(socket, ownsSocket: false);
                    var header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 200\r\nConnection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length);
                    for (var i = 0; i < 200 && !stop.IsCancellationRequested; i++)
                    {
                        stream.Write("a"u8);
                        Thread.Sleep(200);
                    }
                }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
                {
                }
            }
        })
        {
            IsBackground = true,
            Name = "trickle-body",
        }.Start();

        var state = new PocketCalculatorState
        {
            Url = $"http://127.0.0.1:{port}/",
            HttpClient = new PocketCalculatorHttpClient(new CookieJar(), null, allowPrivateNetwork: true),
        };
        FetchOps.FetchTimeoutOverride.Value = TimeSpan.FromSeconds(1);
        var clock = Stopwatch.StartNew();
        try
        {
            var error = await Assert.ThrowsAsync<OpException>(() => FetchOps.OpFetchUrlAsync(
                state, $"http://127.0.0.1:{port}/slow", "GET", "{}", [], "", "cors", "same-origin"));
            Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            FetchOps.FetchTimeoutOverride.Value = null;
            stop.Cancel();
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    private const string SpoofBankOrigin =
        "const RealURL = URL; globalThis.URL = class extends RealURL {"
        + " get origin() { return 'https://bank.example'; } };";

    /// <summary>
    /// H4: a frame that replaces <c>URL</c> still posts as its own origin. The host fills
    /// <c>event.origin</c> in from the sending realm.
    /// </summary>
    [Fact]
    public void PostMessageOriginIsTheSendersNotAPageComputedOne()
    {
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        var parent = page.Runtime;
        using var frame = FrameRealm.Create(parent, 1, 0, "https://evil.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);

        frame.ExecuteScript(SpoofBankOrigin + "parent.postMessage('hi', '*');");

        var queued = Assert.Single(parent.TakePendingFrameMessages());
        Assert.Equal("https://evil.example", queued.Origin);
        Assert.Equal(1u, queued.SourceFrameId);
    }

    /// <summary>
    /// H4, the receiving side: a frame that replaces <c>URL</c> to claim an origin does not
    /// receive a message whose targetOrigin names that origin.
    /// </summary>
    [Fact]
    public void PostMessageTargetOriginIsCheckedAgainstTheReceiversRealOrigin()
    {
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        using var frame = FrameRealm.Create(
            page.Runtime, 1, 0, "https://evil.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);
        frame.ExecuteScript(
            SpoofBankOrigin
            + "globalThis.got = []; addEventListener('message', (e) => globalThis.got.push(e.data));");

        frame.DeliverMessage("{\"v\":\"secret\"}", "https://parent.example", 0, "https://bank.example");
        frame.DeliverMessage("{\"v\":\"public\"}", "https://parent.example", 0, "*");

        Assert.Equal("[\"public\"]", frame.Evaluate("globalThis.got")!.ToJsonString());
        // And the shim's own check agrees with the host when called directly.
        frame.ExecuteHostScript(
            "__obscura_host.deliverMessage('{\"v\":\"direct\"}', 'https://parent.example', 0, 'https://bank.example');");
        Assert.Equal("[\"public\"]", frame.Evaluate("globalThis.got")!.ToJsonString());
    }

    private static string TextResponse(string contentType, string body) =>
        $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: "
            + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;

    /// <summary>
    /// Page-side hooks that record any string carrying <c>SECRET</c> passing through
    /// <c>JSON.parse</c>, <c>String.prototype.replace</c> or a promise reaction, and a
    /// <c>URL</c> that claims <paramref name="origin"/> for every URL.
    /// </summary>
    private static string LeakHooks(string origin) =>
        "globalThis.__leak = [];"
        + "const note = (v) => { if (typeof v === 'string' && v.includes('SECRET')) __leak.push(v); };"
        + "const realParse = JSON.parse; JSON.parse = function (s, r) { note(s); return realParse(s, r); };"
        + "const realReplace = String.prototype.replace;"
        + "String.prototype.replace = function (...a) { note(String(this)); return realReplace.apply(this, a); };"
        + "const realThen = Promise.prototype.then;"
        + "Promise.prototype.then = function (ok, bad) {"
        + "  return realThen.call(this, (v) => { note(v); return typeof ok === 'function' ? ok(v) : v; }, bad); };"
        + "const RealURL = URL; globalThis.URL = class extends RealURL {"
        + $"  get origin() {{ return '{origin}'; }} }};";

    /// <summary>
    /// C3: a cross-origin dynamic script still runs, but its source never passes through
    /// anything page script can hook.
    /// </summary>
    [Fact]
    public async Task CrossOriginScriptSourceNeverReachesPageHooks()
    {
        using var server = new RawHttpServer(_ => TextResponse(
            "text/javascript", "globalThis.__crossRan = true; /* SECRET-SCRIPT */"));
        using var fixture = RedirectRuntimeForOrigin("http://example.com");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                {{LeakHooks(server.Origin)}}
                const script = document.createElement("script");
                script.src = "{{server.Origin}}/app.js";
                const outcome = await new Promise(resolve => {
                    script.onload = () => resolve("load");
                    script.onerror = () => resolve("error");
                    document.head.appendChild(script);
                });
                Promise.prototype.then = realThen;
                return { outcome, ran: globalThis.__crossRan === true, leaked: __leak.length };
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        AssertJsonEquals("""{ "outcome": "load", "ran": true, "leaked": 0 }""", result.Value);
    }

    /// <summary>
    /// C3: a cross-origin dynamic stylesheet is judged not origin-clean by the host even when
    /// page script replaces <c>URL</c> to claim the sheet's origin, and its text never passes
    /// through a page hook. It still applies.
    /// </summary>
    [Fact]
    public async Task CrossOriginStylesheetStaysUnreadableUnderPageHooks()
    {
        using var server = new RawHttpServer(_ => TextResponse(
            "text/css", ".secret-rule { color: red } /* SECRET-SHEET */"));
        using var fixture = RedirectRuntimeForOrigin("http://example.com");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                {{LeakHooks(server.Origin)}}
                const link = document.createElement("link");
                link.setAttribute("rel", "stylesheet");
                link.setAttribute("href", "{{server.Origin}}/site.css");
                const outcome = await new Promise(resolve => {
                    link.onload = () => resolve("load");
                    link.onerror = () => resolve("error");
                    document.head.appendChild(link);
                });
                Promise.prototype.then = realThen;
                let rules;
                try { rules = Array.from(link.sheet.cssRules, r => r.selectorText); }
                catch (e) { rules = e.name; }
                return { outcome, rules, leaked: __leak.length };
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        AssertJsonEquals(
            """{ "outcome": "load", "rules": "SecurityError", "leaked": 0 }""",
            result.Value);
    }

    /// <summary>
    /// C2: a cross-origin iframe's document is unreachable: not through
    /// <c>contentDocument</c>, not through <c>contentWindow.document</c>, not through an
    /// expando, and not by rewriting one. Its window keeps the cross-origin surface, and the
    /// host has the frame queued with its real URL.
    /// </summary>
    [Fact]
    public async Task CrossOriginIframeDocumentIsUnreachable()
    {
        using var server = new RawHttpServer(_ => TextResponse(
            "text/html", "<!doctype html><title>victim</title><p id=s>SECRET-FRAME</p>"));
        using var fixture = RedirectRuntimeForOrigin("http://example.com");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                {{LeakHooks(server.Origin)}}
                const frame = document.createElement("iframe");
                const loaded = new Promise(resolve => frame.onload = resolve);
                frame.src = "{{server.Origin}}/account";
                document.body.appendChild(frame);
                await loaded;
                Promise.prototype.then = realThen;
                frame._iframeLoadedUrl = "about:blank";
                const win = frame.contentWindow;
                return {
                    contentDocument: frame.contentDocument === null,
                    windowDocument: win.document === undefined,
                    expando: frame._iframeDoc === undefined && frame._iframeWin === undefined,
                    postMessage: typeof win.postMessage,
                    parent: win.parent === window,
                    stable: frame.contentWindow === win,
                    leaked: __leak.length,
                };
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        AssertJsonEquals(
            """
            {
                "contentDocument": true,
                "windowDocument": true,
                "expando": true,
                "postMessage": "function",
                "parent": true,
                "stable": true,
                "leaked": 0
            }
            """,
            result.Value);
        var pending = Assert.Single(fixture.Runtime.TakePendingFrames());
        Assert.Equal(server.Origin + "/account", pending.Url);
        Assert.Contains("SECRET-FRAME", pending.Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// C2: a same-origin frame stays readable, and a sandboxed one without
    /// <c>allow-same-origin</c> gets an opaque origin, in the parent's view and in the realm
    /// the host builds for it.
    /// </summary>
    [Fact]
    public async Task SameOriginIframeIsReadableUnlessSandboxed()
    {
        using var server = new RawHttpServer(_ => TextResponse(
            "text/html", "<!doctype html><title>mine</title><p id=s>own</p>"));
        using var fixture = RedirectRuntimeForOrigin(server.Origin);
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const load = (sandbox) => {
                    const frame = document.createElement("iframe");
                    if (sandbox !== null) frame.setAttribute("sandbox", sandbox);
                    const loaded = new Promise(resolve => frame.onload = resolve);
                    frame.src = "/child";
                    document.body.appendChild(frame);
                    return loaded.then(() => frame);
                };
                const plain = await load(null);
                const sandboxed = await load("allow-scripts");
                return {
                    plain: plain.contentDocument?.getElementById("s")?.textContent ?? null,
                    sandboxed: sandboxed.contentDocument === null,
                };
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        AssertJsonEquals("""{ "plain": "own", "sandboxed": true }""", result.Value);
        var pending = fixture.Runtime.TakePendingFrames();
        Assert.Equal(2, pending.Count);
        Assert.False(pending[0].OpaqueOrigin);
        Assert.True(pending[1].OpaqueOrigin);
    }

    /// <summary>
    /// L9: <c>history.pushState</c> and <c>replaceState</c> refuse a URL of another origin
    /// with Chromium's SecurityError, so <c>location.origin</c> cannot be made to report one.
    /// </summary>
    [Fact]
    public void HistoryRefusesACrossOriginUrl()
    {
        using var fixture = RuntimeFixture.Page("http://example.com/page", "<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
                const out = {};
                try { history.pushState({}, "", "https://other.example/"); out.push = "ok"; }
                catch (e) { out.push = e.name + ": " + e.message; }
                try { history.replaceState({}, "", "http://example.com:8080/"); out.replace = "ok"; }
                catch (e) { out.replace = e.name; }
                history.pushState({}, "", "/next?x=1#y");
                out.href = location.href;
                out.origin = location.origin;
                out.length = history.length;
                return out;
            })()
            """);

        AssertJsonEquals(
            """
            {
                "push": "SecurityError: Failed to execute 'pushState' on 'History': A history state object with URL 'https://other.example/' cannot be created in a document with origin 'http://example.com' and URL 'http://example.com/page'.",
                "replace": "SecurityError",
                "href": "http://example.com/next?x=1#y",
                "origin": "http://example.com",
                "length": 2
            }
            """,
            result);
    }

    /// <summary>
    /// L9: the History API URL is host state. A page that writes the upstream global
    /// <c>__virtualUrl</c> moves neither <c>location</c> nor the URL the host reports, and
    /// <c>op_history_url</c> refuses another origin even when called directly.
    /// </summary>
    [Fact]
    public void TheHistoryUrlIsHostStateThePageCannotWrite()
    {
        using var fixture = RuntimeFixture.Page("http://example.com/page", "<html><body></body></html>");
        var tampered = fixture.Runtime.Evaluate(
            """
            (() => {
                globalThis.__virtualUrl = "https://bank.example/login";
                return location.href;
            })()
            """);
        Assert.Equal("http://example.com/page", tampered?.ToString());
        Assert.Null(fixture.Runtime.HistoryUrl);

        fixture.Runtime.Evaluate("history.pushState({}, '', '/next#a')");
        Assert.Equal("http://example.com/next#a", fixture.Runtime.HistoryUrl);

        Assert.Equal("false", fixture.Runtime.Evaluate(
            "String(__obscura_test_ops.op_history_url('https://bank.example/', 0))")?.ToString());
        Assert.Equal("http://example.com/next#a", fixture.Runtime.HistoryUrl);

        fixture.Runtime.Evaluate("history.back()");
        Assert.Null(fixture.Runtime.HistoryUrl);
        Assert.Equal("http://example.com/page", fixture.Runtime.Evaluate("location.href")?.ToString());
    }

    /// <summary>
    /// L10: the crypto ops build their result with the realm's own <c>Uint8Array</c>, not
    /// with whatever page script has put on the global by the time the op runs.
    /// </summary>
    [Fact]
    public void CryptoOpsDoNotCallThePagesUint8Array()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
                const Real = Uint8Array;
                let called = 0;
                globalThis.Uint8Array = function (...args) { called++; return new Real(...args); };
                try {
                    const bytes = __obscura_test_ops.op_random_bytes(8);
                    return { called, real: bytes instanceof Real, length: bytes.length };
                } finally {
                    globalThis.Uint8Array = Real;
                }
            })()
            """);

        AssertJsonEquals("""{ "called": 0, "real": true, "length": 8 }""", result);
    }
}
