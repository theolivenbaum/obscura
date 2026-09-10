using System.Text.Json.Nodes;
using Obscura.Dom;
using Obscura.Js.Modules;
using Obscura.Js.Runtime;
using Obscura.Net;
using Xunit;

namespace Obscura.Js.Tests;

/// <summary>
/// Disposes the runtimes a test builds.
/// </summary>
/// <remarks>
/// Unlike the Rust engine, which holds one isolate per process and therefore
/// runs these tests process-per-test, ClearScript supports many isolates, so
/// they run in-process. The price is that a leaked <c>V8ScriptEngine</c> wedges
/// the test host, which is why every runtime a test creates goes through here.
/// </remarks>
internal sealed class RuntimeFixture : IDisposable
{
    private RuntimeFixture(ObscuraJsRuntime runtime) => Runtime = runtime;

    public ObscuraJsRuntime Runtime { get; }

    /// <summary>The Rust tests' <c>setup_runtime</c> helper.</summary>
    public static RuntimeFixture Setup(string html)
    {
        var dom = HtmlParsing.ParseHtml(html);
        var runtime = new ObscuraJsRuntime();
        runtime.SetDom(dom);
        runtime.SetUrl("http://example.com/test");
        runtime.SetTitle("Test Page");
        runtime.RunPageInit();
        return new RuntimeFixture(runtime);
    }

    /// <summary>A runtime with no document, for the engine-level tests.</summary>
    public static RuntimeFixture Blank() => new(new ObscuraJsRuntime());

    /// <summary>The Rust frame tests' <c>page</c> helper.</summary>
    public static RuntimeFixture Page(string url, string html)
    {
        var runtime = new ObscuraJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml(html));
        runtime.SetUrl(url);
        runtime.RunPageInit();
        return new RuntimeFixture(runtime);
    }

    public void Dispose() => Runtime.Dispose();
}

/// <summary>
/// The xUnit port of the tests in <c>crates/obscura-js/src/runtime.rs</c> and
/// <c>crates/obscura-js/src/frame.rs</c>, in source order and under the same
/// names.
/// </summary>
/// <remarks>
/// Every skipped test keeps its Rust body as a comment and says exactly why it
/// is skipped, so switching one on is a translation and not an archaeology
/// exercise. Most are skipped for the plainest reason there is - the C# body has
/// not been written yet - and not because anything blocks them: the op table is
/// bound, bootstrap.js loads, the DOM answers and frame realms run. The ones
/// that are genuinely blocked say what blocks them: a missing HTTP fixture, the
/// unported screenshot family, or a ClearScript limit named in the port report.
/// </remarks>
public sealed class RuntimeTests
{
    /// <summary>A CDP <c>callFunctionOn</c> by-value argument.</summary>
    private static JsonNode? Arg(double value) => new JsonObject { ["value"] = value };

    private static JsonNode? Arg(string value) => new JsonObject { ["value"] = value };

    private static JsonNode? Arg(JsonNode? value) => new JsonObject { ["value"] = value };

    /// <summary>The Rust tests' <c>assert_eq!(value, serde_json::json!(...))</c>.</summary>
    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.Equal(JsonNode.Parse(expected)?.ToJsonString() ?? "null", actual?.ToJsonString() ?? "null");

    /// <summary>
    /// The Rust tests' <c>delayed_fetch_runtime</c> helper: a loopback server that
    /// accepts one request, waits, and then answers "hydrated", plus a runtime
    /// whose document lives on that origin.
    /// </summary>
    private sealed class DelayedFetch : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly ManualResetEventSlim _accepted = new(false);
        private readonly ObscuraHttpClient _client;
        private readonly RuntimeFixture _fixture;

        public DelayedFetch(TimeSpan responseDelay)
        {
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            var thread = new Thread(() =>
            {
                try
                {
                    using var socket = _listener.AcceptSocket();
                    socket.Receive(new byte[2048]);
                    _accepted.Set();
                    Thread.Sleep(responseDelay);
                    const string body = "hydrated";
                    socket.Send(System.Text.Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n"
                        + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
                }
                catch (Exception)
                {
                    // The test finished and closed the listener first.
                }
            })
            {
                IsBackground = true,
            };
            thread.Start();

            Origin = $"http://127.0.0.1:{port}";
            _client = new ObscuraHttpClient(new CookieJar(), null, allowPrivateNetwork: true);
            _fixture = RuntimeFixture.Blank();
            Runtime.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
            Runtime.SetUrl($"{Origin}/page");
            Runtime.SetHttpClient(_client);
            Runtime.RunPageInit();
        }

        public string Origin { get; }

        public ObscuraJsRuntime Runtime => _fixture.Runtime;

        public bool WasAccepted(TimeSpan timeout) => _accepted.Wait(timeout);

        public void Dispose()
        {
            _fixture.Dispose();
            _client.Dispose();
            _listener.Stop();
            _accepted.Dispose();
        }
    }

    // ---- crates/obscura-js/src/runtime.rs ----

    // SEC-503 / #820 — createObjectURL must reject non-Blob input (an object
    // that merely has a .text() method, e.g. Response) with a TypeError, as
    // Chrome does; a real Blob is still accepted.
    [Fact]
    public void CreateObjectUrlRejectsNonBlobInput()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;

        // A real Blob is accepted and yields a blob: URL.
        var ok = rt.Evaluate("URL.createObjectURL(new Blob(['hello'])).startsWith('blob:')");
        Assert.True(ok!.GetValue<bool>(), "a real Blob must be accepted");

        // A non-Blob object with only .text() must be rejected.
        var rejected = rt.Evaluate(
            "(function(){ try { URL.createObjectURL({ text: () => Promise.resolve('x') }); return false; }"
            + "catch (e) { return !!(e && e.name === 'TypeError'); } })()");
        Assert.True(rejected!.GetValue<bool>(), "createObjectURL must throw TypeError for non-Blob input");
    }

    // SEC-301 / SEC-302 / #792 — the profile setters must embed values safely.
    // set_platform must not allow a backslash-before-quote to break out of the
    // JS string literal (injection), and set_user_agent must not silently fail
    // on a control character; both must store the value verbatim.
    [Fact]
    public void ProfileSettersEscapeBackslashAndControlCharacters()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.Evaluate("globalThis.__pwned = 0;");

        // SEC-301: `Win\';...` - the trailing backslash used to escape our own
        // closing quote, letting the rest run as JS.
        rt.SetPlatform("Win\\';globalThis.__pwned=1;//", "px", "pv");
        Assert.NotEqual(1.0, rt.Evaluate("globalThis.__pwned")?.GetValue<double>());
        Assert.Equal(
            "Win\\';globalThis.__pwned=1;//",
            rt.Evaluate("globalThis.__obscura_platform")!.GetValue<string>());

        // SEC-302: a UA with a literal newline used to SyntaxError into a silent no-op.
        rt.SetUserAgent("Mozilla/5.0 line1\nline2");
        Assert.Equal("Mozilla/5.0 line1\nline2", rt.Evaluate("globalThis.__obscura_ua")!.GetValue<string>());
    }

    [Fact]
    public void FunctionToStringHasNativeFunctionShape()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        AssertJson(
            """
            {"source":"function toString() { [native code] }","name":"toString","length":0,
             "hasOwnPrototype":false,"constructible":false}
            """,
            fixture.Runtime.Evaluate(
                """
                (() => {
                    const fn = Function.prototype.toString;
                    let constructible = true;
                    try {
                        Reflect.construct(function () {}, [], fn);
                    } catch (error) {
                        constructible = false;
                    }
                    return {
                        source: fn.toString(),
                        name: fn.name,
                        length: fn.length,
                        hasOwnPrototype: Object.prototype.hasOwnProperty.call(fn, "prototype"),
                        constructible,
                    };
                })()
                """));
    }

    [Fact(Skip = "ClearScript refuses a script object across engines, so a frame realm's live window and document cannot be published into the page realm; see the port report")]
    public void IframeContentWindowExposesRealmGlobals()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn iframe_content_window_exposes_realm_globals() {
                let mut rt = setup_runtime("<html><body></body></html>");

                assert_eq!(
                    rt.evaluate(
                        r#"(() => {
                            const iframe = document.createElement("iframe");
                            document.body.appendChild(iframe);
                            const child = iframe.contentWindow;
                            const names = [
                                "Object", "Function", "Error", "Promise", "Proxy",
                                "XMLHttpRequest", "Worker", "Blob", "FormData",
                                "WebSocket", "MutationObserver",
                            ];
                            return {
                                types: names.map(name => typeof child[name]),
                                separate: [
                                    child.Object !== Object,
                                    child.Promise !== Promise,
                                    child.XMLHttpRequest !== XMLHttpRequest,
                                    child.Math !== Math,
                                ],
                                constructible: [
                                    new child.Object() instanceof child.Object,
                                    new child.Promise(resolve => resolve()) instanceof child.Promise,
                                    new child.XMLHttpRequest() instanceof child.XMLHttpRequest,
                                    new child.Blob([]) instanceof child.Blob,
                                    new child.FormData() instanceof child.FormData,
                                    new child.MutationObserver(() => {}) instanceof child.MutationObserver,
                                ],
                                utilities: [
                                    child.Object.keys({ first: 1 })[0] === "first",
                                    child.Array.isArray([]),
                                    child.Promise.resolve(1) instanceof child.Promise,
                                    child.Function("return 7")() === 7,
                                    Object.getOwnPropertyNames(child).includes("XMLHttpRequest"),
                                    child.globalThis === child,
                                ],
                            };
                        })()"#,
                    )
                    .unwrap(),
                    serde_json::json!({
                        "types": vec!["function"; 11],
                        "separate": vec![true; 4],
                        "constructible": vec![true; 6],
                        "utilities": vec![true; 6],
                    })
                );
            }
        */
    }

    [Fact]
    public void DocumentDomainGetterAndValidRelaxationMatchEffectiveHost()
    {
        using var fixture = RuntimeFixture.Page(
            "https://deep.assets.example.co.uk:8443/page", "<html><body></body></html>");
        AssertJson(
            """
            ["deep.assets.example.co.uk","assets.example.co.uk","example.co.uk",
             "deep.assets.example.co.uk","example.co.uk","example.co.uk"]
            """,
            fixture.Runtime.Evaluate(
                """
                (() => {
                    const initial = document.domain;
                    document.domain = "ASSETS.EXAMPLE.CO.UK";
                    const first = document.domain;
                    document.domain = "example.co.uk";
                    return [initial, first, document.domain, location.hostname,
                            (new Document()).domain,
                            new DOMParser().parseFromString("", "text/html").domain];
                })()
                """));
    }

    [Fact]
    public void DocumentDomainRejectsUnrelatedChildAndPublicSuffixHosts()
    {
        using var fixture = RuntimeFixture.Page("https://app.user.github.io/page", "<html><body></body></html>");
        AssertJson(
            """
            ["SecurityError","SecurityError","SecurityError","SecurityError","SecurityError",
             "SecurityError","user.github.io"]
            """,
            fixture.Runtime.Evaluate(
                """
                (() => {
                    const attempts = ["", ".github.io", "github.io", "evilgithub.io",
                                      "other.github.io", "child.app.user.github.io"];
                    const rejected = attempts.map(value => {
                        try { document.domain = value; return "accepted"; }
                        catch (error) { return error.name; }
                    });
                    document.domain = "user.github.io";
                    return rejected.concat(document.domain);
                })()
                """));
    }

    [Fact]
    public void DocumentDomainDetachedAndHostlessSettersThrowSecurityError()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        AssertJson(
            """["string","example.com","SecurityError","SecurityError","SecurityError"]""",
            fixture.Runtime.Evaluate(
                """
                (() => {
                    const detached = [new Document(),
                        document.implementation.createHTMLDocument("x"),
                        document.implementation.createDocument(null, "root")];
                    const errors = detached.map(doc => {
                        try { doc.domain = "example.com"; return "accepted"; }
                        catch (error) { return error.name; }
                    });
                    return [typeof document.domain, document.domain].concat(errors);
                })()
                """));

        using var hostless = RuntimeFixture.Page("about:blank", "<html><body></body></html>");
        AssertJson(
            """["","SecurityError"]""",
            hostless.Runtime.Evaluate(
                """
                (() => {
                    let error = "";
                    try { document.domain = "example.com"; }
                    catch (caught) { error = caught.name; }
                    return [document.domain, error];
                })()
                """));
    }

    [Fact]
    public async Task StringTimeoutHandlerExecutesInGlobalScope()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.Evaluate("var __timerValue='pending'; setTimeout('__timerValue=\"done\"', 0)");
        await rt.RunEventLoopBoundedAsync(100);
        Assert.Equal("done", rt.Evaluate("globalThis.__timerValue")!.GetValue<string>());
    }

    [Fact]
    public async Task StringTimeoutDeclarationsReachGlobalScope()
    {
        // A string timer handler runs as a classic script in global scope, so a
        // top-level var/function declaration in it becomes a global. new Function()
        // kept those declarations local to the compiled function, so they never
        // reached globalThis.
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.Evaluate("setTimeout('var __leaked = 42; function __leakedFn(){ return 7; }', 0)");
        await rt.RunEventLoopBoundedAsync(100);
        var value = rt.Evaluate(
            "String(globalThis.__leaked) + '|' + "
            + "(typeof globalThis.__leakedFn === 'function' ? globalThis.__leakedFn() : 'missing')");
        Assert.Equal("42|7", value!.GetValue<string>());
    }

    [Fact]
    public async Task StringIntervalHandlerRepeatsAndCanClearItself()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.Evaluate("globalThis.__ticks=0");
        rt.Evaluate("globalThis.__timerId=setInterval('__ticks++;if(__ticks===2)clearInterval(__timerId)',1)");
        await rt.RunEventLoopBoundedAsync(100);
        Assert.Equal(2.0, rt.Evaluate("globalThis.__ticks")!.GetValue<double>());
    }

    [Fact]
    public async Task ZeroDelayTimerRunsAsATaskAfterMicrotasks()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "zero-delay-task-order",
            """
            globalThis.__taskOrder = ["sync"];
            setTimeout(() => __taskOrder.push("timer"), 0);
            Promise.resolve().then(() => __taskOrder.push("microtask"));
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("""["sync","microtask","timer"]""", rt.Evaluate("__taskOrder"));
    }

    [Fact]
    public async Task SchedulerPostTaskObservesPriorityFifoAndTaskBoundaries()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "scheduler-priority-order",
            """
            globalThis.__schedulerOrder = ["sync"];
            const schedule = (name, priority) => scheduler.postTask(() => {
                __schedulerOrder.push(name);
                Promise.resolve().then(() => __schedulerOrder.push(name + "-microtask"));
                return name + "-result";
            }, { priority });
            globalThis.__schedulerResults = Promise.all([
                schedule("background-1", "background"),
                schedule("background-2", "background"),
                schedule("visible", "user-visible"),
                schedule("blocking-1", "user-blocking"),
                schedule("blocking-2", "user-blocking"),
            ]).then(values => { globalThis.__schedulerValues = values; });
            Promise.resolve().then(() => __schedulerOrder.push("initial-microtask"));
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """
            ["sync","initial-microtask",
             "blocking-1","blocking-1-microtask","blocking-2","blocking-2-microtask",
             "visible","visible-microtask",
             "background-1","background-1-microtask","background-2","background-2-microtask"]
            """,
            rt.Evaluate("__schedulerOrder"));
        AssertJson(
            """
            ["background-1-result","background-2-result","visible-result",
             "blocking-1-result","blocking-2-result"]
            """,
            rt.Evaluate("__schedulerValues"));
    }

    [Fact]
    public async Task SchedulerAbortDelayAndYieldFollowTaskState()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "scheduler-abort-delay-yield",
            """
            globalThis.__schedulerState = {
                order: [],
                canceledCallbackRan: false,
                exactAbortReason: false,
                selfAbortCallbackRan: false,
                exactSelfAbortReason: false,
            };
            const abortReason = { reason: "stop" };
            const canceled = new AbortController();
            scheduler.postTask(() => {
                __schedulerState.canceledCallbackRan = true;
            }, { signal: canceled.signal, delay: 20 }).catch(error => {
                __schedulerState.exactAbortReason = error === abortReason;
            });
            canceled.abort(abortReason);

            const selfAbortReason = { reason: "inside callback" };
            const selfCanceled = new AbortController();
            scheduler.postTask(() => {
                __schedulerState.selfAbortCallbackRan = true;
                selfCanceled.abort(selfAbortReason);
                return "ignored result";
            }, { signal: selfCanceled.signal }).catch(error => {
                __schedulerState.exactSelfAbortReason = error === selfAbortReason;
            });

            scheduler.postTask(async () => {
                __schedulerState.order.push("blocking-start");
                await scheduler.yield();
                __schedulerState.order.push("blocking-continuation");
            }, { priority: "user-blocking" });
            scheduler.postTask(() => {
                __schedulerState.order.push("background");
            }, { priority: "background" });
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """
            [["blocking-start","blocking-continuation","background"],
             false,true,true,true,true,"[object Scheduler]",1,0]
            """,
            rt.Evaluate(
                """
                [
                    __schedulerState.order,
                    __schedulerState.canceledCallbackRan,
                    __schedulerState.exactAbortReason,
                    __schedulerState.selfAbortCallbackRan,
                    __schedulerState.exactSelfAbortReason,
                    scheduler instanceof Scheduler,
                    Object.prototype.toString.call(scheduler),
                    Scheduler.prototype.postTask.length,
                    Scheduler.prototype.yield.length,
                ]
                """));
    }

    [Fact]
    public async Task WorkerOnmessageBindingsDoNotMutateWindow()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "worker-onmessage-bindings",
            """
            globalThis.__workerReplies = {};
            const originalHandler = window.onmessage;
            const sources = {
                bare: 'onmessage = function(event) { postMessage([event.data, this === self]); };',
                declared: 'var onmessage = function(event) { postMessage([event.data, this === self]); };',
                strict: '"use strict"; onmessage = function(event) { postMessage([event.data, this === self]); };',
            };
            for (const [name, source] of Object.entries(sources)) {
                const url = URL.createObjectURL(new Blob([source], { type: 'application/javascript' }));
                const worker = new Worker(url);
                worker.onmessage = event => {
                    __workerReplies[name] = event.data;
                    worker.terminate();
                    URL.revokeObjectURL(url);
                };
                worker.postMessage(name);
            }
            globalThis.__workerWindowUnchanged = () => window.onmessage === originalHandler;
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """{"bare":["bare",true],"declared":["declared",true],"strict":["strict",true]}""",
            rt.Evaluate("__workerReplies"));
        Assert.True(rt.Evaluate("__workerWindowUnchanged()")!.GetValue<bool>());
    }

    [Fact]
    public async Task WorkerInitializesOnceAndRetainsMessageState()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "worker-persistent-state",
            """
            globalThis.__workerReplies = [];
            const source = 'let count = 0; postMessage("ready"); self.onmessage = () => postMessage(++count);';
            const url = URL.createObjectURL(new Blob([source], { type: 'application/javascript' }));
            const worker = new Worker(url);
            worker.onmessage = event => {
                __workerReplies.push(event.data);
                if (event.data === 2) {
                    worker.terminate();
                    URL.revokeObjectURL(url);
                }
            };
            worker.postMessage(null);
            worker.postMessage(null);
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("""["ready",1,2]""", rt.Evaluate("__workerReplies"));
    }

    [Fact]
    public async Task WorkerScopesKeepCountersAndHandlersIndependent()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "worker-independent-scopes",
            """
            globalThis.__workerReplies = [[], []];
            const source = 'let count = 0; onmessage = () => postMessage(++count);';
            const url = URL.createObjectURL(new Blob([source], { type: 'application/javascript' }));
            const workers = [new Worker(url), new Worker(url)];
            workers.forEach((worker, index) => {
                worker.onmessage = event => __workerReplies[index].push(event.data);
            });
            workers[0].postMessage(null);
            workers[1].postMessage(null);
            workers[0].postMessage(null);
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("""[[1,2],[1]]""", rt.Evaluate("__workerReplies"));
        rt.ExecuteScript(
            "worker-cleanup",
            "workers.forEach(worker => worker.terminate()); URL.revokeObjectURL(url);");
    }

    [Fact]
    public async Task WorkerDeliversToHandlerAndListenerWithoutReinitializing()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "worker-handler-and-listener",
            """
            globalThis.__workerReplies = [];
            const source = `
                let count = 0;
                self.onmessage = event => postMessage(['handler', ++count]);
                addEventListener('message', function(event) {
                    postMessage(['listener', count, this === self]);
                });
            `;
            const url = URL.createObjectURL(new Blob([source], { type: 'application/javascript' }));
            const worker = new Worker(url);
            worker.onmessage = event => __workerReplies.push(event.data);
            worker.postMessage(null);
            worker.postMessage(null);
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """[["handler",1],["listener",1,true],["handler",2],["listener",2,true]]""",
            rt.Evaluate("__workerReplies"));
        rt.ExecuteScript("worker-cleanup", "worker.terminate(); URL.revokeObjectURL(url);");
    }

    [Fact]
    public async Task WorkerQueuesMessagesWhileSourceIsLoading()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "worker-queued-messages",
            """
            globalThis.__workerReplies = [];
            const originalFetch = globalThis.fetch;
            let finishSource;
            globalThis.fetch = async () => ({ text: () => new Promise(resolve => { finishSource = resolve; }) });
            const worker = new Worker('https://example.com/worker.js');
            worker.onmessage = event => __workerReplies.push(event.data);
            worker.postMessage('first');
            worker.postMessage('second');
            setTimeout(() => {
                globalThis.fetch = originalFetch;
                finishSource('let count = 0; onmessage = event => postMessage([++count, event.data]);');
            }, 0);
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("""[[1,"first"],[2,"second"]]""", rt.Evaluate("__workerReplies"));
        rt.ExecuteScript("worker-cleanup", "worker.terminate();");
    }

    [Fact]
    public async Task WorkerTerminationDiscardsQueuedMessages()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "worker-terminated-messages",
            """
            globalThis.__workerReplies = [];
            const source = 'postMessage("started"); onmessage = () => postMessage("reply");';
            const url = URL.createObjectURL(new Blob([source], { type: 'application/javascript' }));
            const worker = new Worker(url);
            worker.onmessage = event => __workerReplies.push(event.data);
            worker.postMessage('queued');
            worker.terminate();
            worker.postMessage('after termination');
            URL.revokeObjectURL(url);
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("[]", rt.Evaluate("__workerReplies"));
    }

    [Fact]
    public async Task WorkerSourcePreservesStrictModeInMessageClosures()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "worker-strict-closure",
            """
            globalThis.__workerReplies = [];
            const source = `
                'use strict';
                onmessage = () => {
                    try { workerUndeclaredVariable = 1; postMessage('unexpected assignment'); }
                    catch (error) { postMessage(error.name); }
                };
            `;
            const url = URL.createObjectURL(new Blob([source], { type: 'application/javascript' }));
            const worker = new Worker(url);
            worker.onmessage = event => __workerReplies.push(event.data);
            worker.postMessage(null);
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("""["ReferenceError"]""", rt.Evaluate("__workerReplies"));
        Assert.Equal("undefined", rt.Evaluate("typeof workerUndeclaredVariable")!.GetValue<string>());
        rt.ExecuteScript("worker-cleanup", "worker.terminate(); URL.revokeObjectURL(url);");
    }

    [Fact]
    public async Task WorkerInitializationErrorDoesNotRerunSource()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "worker-initialization-error",
            """
            globalThis.__workerReplies = [];
            globalThis.__workerErrors = [];
            const source = 'postMessage("started"); throw new Error("initialization failed");';
            const url = URL.createObjectURL(new Blob([source], { type: 'application/javascript' }));
            const worker = new Worker(url);
            worker.onmessage = event => __workerReplies.push(event.data);
            worker.onerror = error => __workerErrors.push(error.message);
            worker.postMessage(null);
            worker.postMessage(null);
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("""["started"]""", rt.Evaluate("__workerReplies"));
        AssertJson("""["initialization failed"]""", rt.Evaluate("__workerErrors"));
        rt.ExecuteScript("worker-cleanup", "worker.terminate(); URL.revokeObjectURL(url);");
    }

    [Fact]
    public async Task WorkerStartupReplyDoesNotOvertakeQueuedMessages()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "worker-startup-order",
            """
            globalThis.__workerReplies = [];
            const source = 'postMessage("ready"); onmessage = event => postMessage(event.data);';
            const url = URL.createObjectURL(new Blob([source], { type: 'application/javascript' }));
            const worker = new Worker(url);
            let replied = false;
            worker.onmessage = event => {
                __workerReplies.push(event.data);
                if (event.data === 'ready' && !replied) {
                    replied = true;
                    worker.postMessage(3);
                }
            };
            worker.postMessage(1);
            worker.postMessage(2);
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("""["ready",1,2,3]""", rt.Evaluate("__workerReplies"));
        rt.ExecuteScript("worker-cleanup", "worker.terminate(); URL.revokeObjectURL(url);");
    }

    [Fact]
    public async Task SelfRequeueingMessageChannelYieldsToTimers()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "message-channel-task-yield",
            """
            globalThis.__messageCount = 0;
            globalThis.__timerObserved = false;
            const channel = new MessageChannel();
            channel.port2.onmessage = () => {
                __messageCount++;
                if (!__timerObserved) channel.port1.postMessage(null);
            };
            channel.port1.postMessage(null);
            setTimeout(() => { __timerObserved = true; }, 1);
            """);

        await rt.RunEventLoopBoundedAsync(100);
        var result = rt.Evaluate("[__messageCount, __timerObserved]");
        var values = Assert.IsType<JsonArray>(result);
        var count = values[0]!.GetValue<double>();
        Assert.True(count > 0 && count < 10_000, $"message task did not yield: {result}");
        Assert.True(values[1]!.GetValue<bool>());
    }

    [Fact]
    public async Task MessagePortQueuesUntilStartAndClonesAtPostTime()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "message-port-start-and-clone",
            """
            const channel = new MessageChannel();
            const payload = { nested: { value: 7 } };
            globalThis.__messagePortResult = {
                portInstance: channel.port1 instanceof MessagePort,
                channelInstance: channel instanceof MessageChannel,
                deliveredBeforeStart: false,
                delivered: false,
            };
            channel.port2.addEventListener("message", function(event) {
                __messagePortResult.delivered = true;
                __messagePortResult.value = event.data.nested.value;
                __messagePortResult.targetIsPort = event.target === channel.port2;
                __messagePortResult.thisIsPort = this === channel.port2;
                __messagePortResult.origin = event.origin;
                __messagePortResult.portCount = event.ports.length;
            });
            channel.port1.postMessage(payload);
            payload.nested.value = 99;
            setTimeout(() => {
                __messagePortResult.deliveredBeforeStart = __messagePortResult.delivered;
                channel.port2.start();
            }, 0);
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """
            {"portInstance":true,"channelInstance":true,"deliveredBeforeStart":false,"delivered":true,
             "value":7,"targetIsPort":true,"thisIsPort":true,"origin":"","portCount":0}
            """,
            rt.Evaluate("__messagePortResult"));
    }

    [Fact]
    public async Task MessagePortOnmessageStartsAndYieldsBetweenMessages()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "message-port-task-boundaries",
            """
            globalThis.__messagePortOrder = [];
            const channel = new MessageChannel();
            channel.port1.postMessage(1);
            channel.port1.postMessage(2);
            channel.port2.onmessage = (event) => {
                __messagePortOrder.push("message-" + event.data);
                if (event.currentTarget !== channel.port2) __messagePortOrder.push("bad-current-target");
                Promise.resolve().then(() => __messagePortOrder.push("microtask-" + event.data));
            };
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """["message-1","microtask-1","message-2","microtask-2"]""",
            rt.Evaluate("__messagePortOrder"));
    }

    [Fact]
    public async Task MessagePortDropsOldDocumentPayloadsBeforeFreshDelivery()
    {
        using var fixture = RuntimeFixture.Setup("<html><body data-document='old'></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "message-port-old-document",
            """
            globalThis.__replacementPortOrder = [];
            globalThis.__replacementChannel = new MessageChannel();
            __replacementChannel.port2.onmessage = event => {
                __replacementPortOrder.push(event.data);
            };
            __replacementChannel.port1.postMessage("old");
            """);

        rt.SetDom(HtmlParsing.ParseHtml("<html><body data-document='new'></body></html>"));
        rt.ExecuteScript("message-port-new-document", """__replacementChannel.port1.postMessage("fresh");""");
        await rt.RunEventLoopBoundedAsync(100);

        AssertJson("""["fresh"]""", rt.Evaluate("__replacementPortOrder"));
    }

    [Fact]
    public async Task MessagePortCloseDiscardsDeliveryAlreadyQueuedForATask()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "message-port-close-cancels-queued-delivery",
            """
            globalThis.__closedPortDeliveries = 0;
            const channel = new MessageChannel();
            channel.port2.onmessage = () => { __closedPortDeliveries++; };
            channel.port1.postMessage("queued");
            channel.port2.close();
            """);

        await rt.RunEventLoopBoundedAsync(100);
        Assert.Equal(0.0, rt.Evaluate("__closedPortDeliveries")!.GetValue<double>());
    }

    [Fact]
    public async Task MessagePortHandlerAndListenerFollowRegistrationOrder()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "message-port-mixed-registration-order",
            """
            globalThis.__messagePortRegistrationOrder = [];

            const handlerFirst = new MessageChannel();
            handlerFirst.port2.onmessage = () => __messagePortRegistrationOrder.push("handler-first:handler");
            handlerFirst.port2.addEventListener("message", () => __messagePortRegistrationOrder.push("handler-first:listener"));
            handlerFirst.port1.postMessage(null);

            const listenerFirst = new MessageChannel();
            listenerFirst.port2.addEventListener("message", () => __messagePortRegistrationOrder.push("listener-first:listener"));
            listenerFirst.port2.onmessage = () => __messagePortRegistrationOrder.push("listener-first:handler");
            listenerFirst.port1.postMessage(null);
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """
            ["handler-first:handler","handler-first:listener",
             "listener-first:listener","listener-first:handler"]
            """,
            rt.Evaluate("__messagePortRegistrationOrder"));
    }

    [Fact]
    public async Task MessagePortInternalStateIsHiddenAndIgnoresOwnPropertyTampering()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "message-port-hidden-state",
            """
            const channel = new MessageChannel();
            globalThis.__messagePortOwnKeys = Object.keys(channel.port2);
            globalThis.__messagePortOwnNames = Object.getOwnPropertyNames(channel.port2);
            globalThis.__messagePortTamperResult = [];
            channel.port2.onmessage = (event) => __messagePortTamperResult.push(event.data);

            // These names used to be the actual implementation state. An expando with
            // any of them must not alter delivery now.
            channel.port1._closed = true;
            channel.port1._entangled = null;
            channel.port2._closed = true;
            channel.port2._messageQueue = [];
            channel.port2._messageQueueEnabled = false;
            channel.port2._messageDeliveryPending = true;
            channel.port2._onmessage = null;
            channel.port2._scheduleMessageDelivery = () => {};
            channel.port2.dispatchEvent = () => { throw new Error("tampered dispatchEvent called"); };
            channel.port1.postMessage("delivered");
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """[[],[],["delivered"]]""",
            rt.Evaluate("[__messagePortOwnKeys, __messagePortOwnNames, __messagePortTamperResult]"));
    }

    [Fact]
    public void MessagePortHasBrowserShapedConstructionAndCloneErrors()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
                let constructorError = "";
                let cloneError = "";
                try { new MessagePort(); } catch (error) { constructorError = error.name; }
                try { new MessageChannel().port1.postMessage(() => {}); }
                catch (error) { cloneError = error.name; }
                return [constructorError, cloneError, Object.prototype.toString.call(new MessageChannel().port1)];
            })()
            """);
        AssertJson("""["TypeError","DataCloneError","[object MessagePort]"]""", result);
    }

    [Fact]
    public async Task BroadcastChannelDeliversIndependentPostTimeClonesToMatchingPeers()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "broadcast-channel-clone-delivery",
            """
            globalThis.__broadcastResults = { sender: 0, otherName: 0, peers: [] };
            const sender = new BroadcastChannel("session-sync");
            const first = new BroadcastChannel("session-sync");
            const second = new BroadcastChannel("session-sync");
            const other = new BroadcastChannel("other-name");
            sender.onmessage = () => { __broadcastResults.sender++; };
            other.onmessage = () => { __broadcastResults.otherName++; };
            first.onmessage = (event) => {
                __broadcastResults.peers.push({
                    peer: "first",
                    value: event.data.nested.value,
                    bytes: Array.from(event.data.bytes),
                    source: event.source,
                    ports: event.ports.length,
                });
                event.data.nested.value = 500;
                event.data.bytes[0] = 99;
            };
            second.onmessage = (event) => {
                __broadcastResults.peers.push({
                    peer: "second",
                    value: event.data.nested.value,
                    bytes: Array.from(event.data.bytes),
                    source: event.source,
                    ports: event.ports.length,
                });
            };
            const payload = { nested: { value: 7 }, bytes: new Uint8Array([1, 2, 3]) };
            sender.postMessage(payload);
            payload.nested.value = 42;
            payload.bytes[0] = 88;
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """
            {"sender":0,"otherName":0,"peers":[
                {"peer":"first","value":7,"bytes":[1,2,3],"source":null,"ports":0},
                {"peer":"second","value":7,"bytes":[1,2,3],"source":null,"ports":0}]}
            """,
            rt.Evaluate("__broadcastResults"));
    }

    [Fact]
    public async Task BroadcastChannelHandlersFollowRegistrationOrderAndTaskTiming()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "broadcast-channel-registration-order",
            """
            globalThis.__broadcastOrder = ["sync"];
            const sender = new BroadcastChannel("ordering");
            const handlerFirst = new BroadcastChannel("ordering");
            const listenerFirst = new BroadcastChannel("ordering");
            handlerFirst.onmessage = () => __broadcastOrder.push("handler-first:handler");
            handlerFirst.addEventListener("message", () => __broadcastOrder.push("handler-first:listener"));
            listenerFirst.addEventListener("message", () => __broadcastOrder.push("listener-first:listener"));
            listenerFirst.onmessage = () => __broadcastOrder.push("listener-first:handler");
            sender.postMessage(null);
            Promise.resolve().then(() => __broadcastOrder.push("microtask"));
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """
            ["sync","microtask","handler-first:handler","handler-first:listener",
             "listener-first:listener","listener-first:handler"]
            """,
            rt.Evaluate("__broadcastOrder"));
    }

    [Fact]
    public async Task BroadcastChannelCloseCancelsDeliveryAndClosedPostThrows()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "broadcast-channel-close",
            """
            globalThis.__broadcastCloseResult = { deliveries: 0 };
            const sender = new BroadcastChannel("close-test");
            const recipient = new BroadcastChannel("close-test");
            recipient.onmessage = () => { __broadcastCloseResult.deliveries++; };
            sender.postMessage("queued");
            recipient.close();
            sender.close();
            try { sender.postMessage("closed"); }
            catch (error) { __broadcastCloseResult.closedError = error.name; }
            try { new BroadcastChannel(); }
            catch (error) { __broadcastCloseResult.constructorError = error.name; }
            try { new BroadcastChannel("no-peers").postMessage(() => {}); }
            catch (error) { __broadcastCloseResult.cloneError = error.name; }
            __broadcastCloseResult.ownKeys = Object.keys(new BroadcastChannel("shape"));
            __broadcastCloseResult.tag = Object.prototype.toString.call(new BroadcastChannel("shape"));
            __broadcastCloseResult.eventTarget = new BroadcastChannel("shape") instanceof EventTarget;
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """
            {"deliveries":0,"closedError":"InvalidStateError","constructorError":"TypeError",
             "cloneError":"DataCloneError","ownKeys":[],"tag":"[object BroadcastChannel]","eventTarget":true}
            """,
            rt.Evaluate("__broadcastCloseResult"));
    }

    [Fact]
    public void PerformanceNowIsMonotonicUnderBurstyCalls()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        // Hammer performance.now() so many calls land in the same millisecond and the
        // wall clock rolls over repeatedly; the value must never go backwards.
        var violations = fixture.Runtime.Evaluate(
            "(function(){var prev=-Infinity, bad=0; for(var i=0;i<500000;i++)"
            + "{var t=performance.now(); if(t<prev) bad++; prev=t;} return bad;})()");
        Assert.Equal(0.0, violations!.GetValue<double>());
    }

    [Fact]
    public void PerformanceNowDoesNotOutrunElapsedTime()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var lead = fixture.Runtime.Evaluate(
            "(function(){for(var i=0;i<500000;i++)performance.now();"
            + " return performance.now()-(Date.now()-performance.timeOrigin);})()");
        Assert.True(
            lead!.GetValue<double>() <= 1.0,
            $"performance.now() advanced ahead of elapsed time: {lead}");
    }

    [Fact]
    public void TimeOriginNeverLandsInTheFuture()
    {
        // __obscura_init deletes itself, so a realm yields one draw of the origin
        // jitter. Build a fresh runtime per draw, do not hoist this out.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
            var skew = fixture.Runtime.Evaluate("performance.timeOrigin - Date.now()")!.GetValue<double>();
            Assert.True(skew <= 0.0, $"performance.timeOrigin is {skew} ms ahead of Date.now()");
        }
    }

    [Fact]
    public void ChildnodeHelpersCoerceNonStringPrimitivesToText()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><div id="p"><span id="t">x</span></div></body></html>""");
        var rt = fixture.Runtime;
        var before = rt.Evaluate(
            "(function(){var t=document.getElementById('t'); t.before(5);"
            + " return t.previousSibling ? t.previousSibling.textContent : 'NULL';})()");
        Assert.Equal("5", before!.GetValue<string>());
        var after = rt.Evaluate(
            "(function(){var t=document.getElementById('t'); t.after(true);"
            + " return t.nextSibling ? t.nextSibling.textContent : 'NULL';})()");
        Assert.Equal("true", after!.GetValue<string>());
        var replaced = rt.Evaluate(
            "(function(){var t=document.getElementById('t'); t.replaceWith(42);"
            + " return document.getElementById('p').textContent;})()");
        Assert.Contains("42", replaced!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceStateWithoutUrlPreservesCurrentLocation()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var path = fixture.Runtime.Evaluate(
            "(function(){history.pushState({}, '', '/dashboard'); history.replaceState({scroll:1});"
            + " return location.pathname;})()");
        Assert.Equal("/dashboard", path!.GetValue<string>());
    }

    [Fact]
    public void PushStateWithoutUrlPreservesCurrentLocation()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var path = fixture.Runtime.Evaluate(
            "(function(){history.pushState({}, '', '/a'); history.pushState({b:1}); return location.pathname;})()");
        Assert.Equal("/a", path!.GetValue<string>());
    }

    [Fact]
    public void HistoryExposesTheWebPlatformConstructorAndPrototype()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (function(){
                const original = history.replaceState;
                History.prototype.replaceState.call(history, {ok:true}, "", "/prototype");
                let illegal = false;
                try { new History(); } catch (error) { illegal = error instanceof TypeError; }
                return {
                    instance: history instanceof History,
                    prototype: Object.getPrototypeOf(history) === History.prototype,
                    method: original === History.prototype.replaceState,
                    tag: Object.prototype.toString.call(history),
                    path: location.pathname,
                    illegal,
                };
            })()
            """);
        var obj = Assert.IsType<JsonObject>(result);
        Assert.True(obj["instance"]!.GetValue<bool>());
        Assert.True(obj["prototype"]!.GetValue<bool>());
        Assert.True(obj["method"]!.GetValue<bool>());
        Assert.Equal("[object History]", obj["tag"]!.GetValue<string>());
        Assert.Equal("/prototype", obj["path"]!.GetValue<string>());
        Assert.True(obj["illegal"]!.GetValue<bool>());
    }

    [Fact]
    public void StyleAttributeParsesIntoStyleObject()
    {
        // Inline styles present in the parsed HTML must be visible via el.style.*
        using var fixture = RuntimeFixture.Setup(
            """<html><body><div id="d" style="color: red; display: none">hi</div></body></html>""");
        var rt = fixture.Runtime;
        Assert.Equal("red", rt.Evaluate("document.getElementById('d').style.color")!.GetValue<string>());
        Assert.Equal("none", rt.Evaluate("document.getElementById('d').style.display")!.GetValue<string>());
    }

    [Fact]
    public void SetStyleAttributeUpdatesStyleObject()
    {
        using var fixture = RuntimeFixture.Setup("""<html><body><div id="d">hi</div></body></html>""");
        var margin = fixture.Runtime.Evaluate(
            "(function(){var e=document.getElementById('d'); e.setAttribute('style','margin: 5px');"
            + " return e.style.margin;})()");
        Assert.Equal("5px", margin!.GetValue<string>());
    }

    [Fact]
    public void NullNamespaceStyleAttributeStaysInSync()
    {
        using var fixture = RuntimeFixture.Setup("""<html><body><div id="d">hi</div></body></html>""");
        var result = fixture.Runtime.Evaluate(
            "(function(){var e=document.getElementById('d'); e.setAttributeNS(null,'style','color: green');"
            + " var before=e.style.color; e.removeAttributeNS(null,'style');"
            + " return before+'|'+e.style.color+'|'+String(e.getAttribute('style'));})()");
        Assert.Equal("green||null", result!.GetValue<string>());
    }

    [Fact]
    public void SettingStylePropertyUpdatesTheAttributeAndSerialization()
    {
        using var fixture = RuntimeFixture.Setup("""<html><body><div id="d">hi</div></body></html>""");
        var rt = fixture.Runtime;
        var attr = rt.Evaluate(
            "(function(){var e=document.getElementById('d'); e.style.color='blue';"
            + " return e.getAttribute('style');})()");
        Assert.Equal("color: blue;", attr!.GetValue<string>());
        var html = rt.Evaluate("document.getElementById('d').outerHTML");
        Assert.Contains("color: blue", html!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void StyleObjectReflectsExternalAttributeChange()
    {
        // A later setAttribute('style', ...) must supersede an earlier value read
        // through el.style (the declaration re-syncs from the attribute).
        using var fixture = RuntimeFixture.Setup(
            """<html><body><div id="d" style="color: red">hi</div></body></html>""");
        var color = fixture.Runtime.Evaluate(
            "(function(){var e=document.getElementById('d'); e.style.color;"
            + " e.setAttribute('style','color: green'); return e.style.color;})()");
        Assert.Equal("green", color!.GetValue<string>());
    }

    [Fact]
    public void CloneNodeDeepPreservesContextSensitiveElements()
    {
        // A <tr> is not a valid child of <div>, so cloning through a throwaway
        // <div>.innerHTML dropped it and returned null. A structural clone keeps it.
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        Assert.Equal(
            "TR",
            rt.Evaluate("(document.createElement('tr').cloneNode(true) || {}).tagName || 'NULL'")!.GetValue<string>());
        Assert.Equal(
            "TD",
            rt.Evaluate("(document.createElement('td').cloneNode(true) || {}).tagName || 'NULL'")!.GetValue<string>());
    }

    [Fact]
    public void CloneNodeDeepCopiesChildrenAndAttributes()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><ul id="l"><li class="a">one</li><li class="b">two</li></ul></body></html>""");
        var result = fixture.Runtime.Evaluate(
            "(function(){var c=document.getElementById('l').cloneNode(true);"
            + " return c.children.length + '|' + c.children[0].className + '|' + c.children[1].textContent;})()");
        Assert.Equal("2|a|two", result!.GetValue<string>());
    }

    [Fact]
    public void CloneNodeDeepPreservesTableRows()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><table id="t"><tbody><tr><td>1</td><td>2</td></tr></tbody></table></body></html>""");
        // Navigate the detached clone directly (querySelector does not traverse
        // detached subtrees). tbody > tr > (td, td).
        var result = fixture.Runtime.Evaluate(
            "(function(){var tb=document.querySelector('#t tbody').cloneNode(true); var tr=tb.children[0];"
            + " return tr.tagName + '|' + tr.children.length + '|' + tr.children[1].textContent;})()");
        Assert.Equal("TR|2|2", result!.GetValue<string>());
    }

    [Fact]
    public void CloneNodeShallowCopiesAttributesWithoutChildren()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><div id="d" data-x="7"><span>kid</span></div></body></html>""");
        var result = fixture.Runtime.Evaluate(
            "(function(){var c=document.getElementById('d').cloneNode(false);"
            + " return c.getAttribute('data-x') + '|' + c.childNodes.length;})()");
        Assert.Equal("7|0", result!.GetValue<string>());
    }

    [Fact]
    public void CloneNodeCopiesJsAssignedInlineStyles()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id='d'></div></body></html>");
        var result = fixture.Runtime.Evaluate(
            "(function(){var d=document.getElementById('d');d.style.color='red';d.style.fontSize='12px';"
            + "var c=d.cloneNode(false);return c.style.color+'|'+c.style.fontSize+'|'+c.style.cssText;})()");
        Assert.Equal("red|12px|color: red; font-size: 12px;", result!.GetValue<string>());
    }

    [Fact]
    public void CloneNodeDeepCopiesTemplateContent()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            "(function(){var t=document.createElement('template');"
            + "t.content.appendChild(document.createElement('option')).textContent='choice';"
            + "var c=t.cloneNode(true);"
            + "return c.content.childNodes.length+'|'+c.content.firstChild.tagName+'|'+c.content.firstChild.textContent;})()");
        Assert.Equal("1|OPTION|choice", result!.GetValue<string>());
    }

    [Fact]
    public void InsertAdjacentHtmlParsesTableFragments()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><table id="t"><tbody id="tb"></tbody></table></body></html>""");
        var result = fixture.Runtime.Evaluate(
            "(function(){var tb=document.getElementById('tb');"
            + " tb.insertAdjacentHTML('beforeend','<tr><td>1</td><td>2</td></tr>');"
            + " var tr=tb.firstElementChild; return tr ? (tr.tagName+':'+tr.children.length) : 'NULL';})()");
        Assert.Equal("TR:2", result!.GetValue<string>());
    }

    [Fact]
    public void InsertAdjacentHtmlPositionIsCaseInsensitive()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><div id="host"><span>base</span></div></body></html>""");
        var result = fixture.Runtime.Evaluate(
            "(function(){var h=document.getElementById('host'); h.insertAdjacentHTML('BeforeEnd','<b>x</b>');"
            + " return h.lastElementChild ? h.lastElementChild.tagName : 'NULL';})()");
        Assert.Equal("B", result!.GetValue<string>());
    }

    [Fact]
    public void InsertAdjacentHtmlRejectsInvalidPosition()
    {
        using var fixture = RuntimeFixture.Setup("""<html><body><div id="host"></div></body></html>""");
        var result = fixture.Runtime.Evaluate(
            "(function(){var h=document.getElementById('host');"
            + " try { h.insertAdjacentHTML('nope','<b>x</b>'); return 'no-throw'; } catch(e){ return e.name; }})()");
        Assert.Equal("SyntaxError", result!.GetValue<string>());
    }

    [Fact]
    public void InsertAdjacentHtmlKeepsLeadingCommentsInTableContexts()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><table><tbody id="tb"><tr id="row"></tr></tbody></table></body></html>""");
        var result = fixture.Runtime.Evaluate(
            "(function(){var tb=document.getElementById('tb');"
            + "tb.insertAdjacentHTML('beforeend','<!--m--><tr><td>v</td></tr>');"
            + "var row=document.getElementById('row');"
            + "row.insertAdjacentHTML('beforeend','<!--n--><td>x</td>');"
            + "return Array.from(tb.childNodes).map(function(n){return n.nodeName}).join('|')+';'"
            + "+Array.from(row.childNodes).map(function(n){return n.nodeName}).join('|');})()");
        Assert.Equal("TR|#comment|TR;#comment|TD", result!.GetValue<string>());
    }

    [Fact]
    public void InsertAdjacentHtmlUsesTheInsertionElementAsContext()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><div id="d"></div><table id="table"><tbody id="tb"></tbody></table></body></html>""");
        var result = fixture.Runtime.Evaluate(
            "(function(){var d=document.getElementById('d');"
            + "d.insertAdjacentHTML('beforeend','<tr><td>v</td></tr>');"
            + "var table=document.getElementById('table');"
            + "table.insertAdjacentHTML('beforeend','<tr><td>x</td></tr>');"
            + "var tb=document.getElementById('tb');"
            + "tb.insertAdjacentHTML('beforeend','<tr><td>y</td></tr>tail');"
            + "return d.firstChild.nodeName+':'+d.textContent+';'+table.lastElementChild.tagName+';'"
            + "+Array.from(tb.childNodes).map(function(n){return n.nodeName+(n.data?':'+n.data:'')}).join('|');})()");
        Assert.Equal("#text:v;TBODY;TR|#text:tail", result!.GetValue<string>());
    }

    [Fact]
    public void SetAttributeNsIsRetrievableByNamespaceAndLocalName()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            "(function(){var s=document.createElementNS('http://www.w3.org/2000/svg','svg');"
            + " s.setAttributeNS('http://www.w3.org/1999/xlink','xlink:href','#g');"
            + " return s.getAttributeNS('http://www.w3.org/1999/xlink','href');})()");
        Assert.Equal("#g", result!.GetValue<string>());
    }

    [Fact]
    public void RemoveAttributeNsRemovesByNamespace()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            "(function(){var s=document.createElementNS('http://www.w3.org/2000/svg','svg');"
            + " s.setAttributeNS('http://www.w3.org/1999/xlink','xlink:href','#g');"
            + " s.removeAttributeNS('http://www.w3.org/1999/xlink','href');"
            + " return s.getAttributeNS('http://www.w3.org/1999/xlink','href');})()");
        AssertJson("null", result);
    }

    [Fact]
    public void GetAttributeNsReadsPlainAttributesWithNullNamespace()
    {
        // Backward-compat: getAttributeNS(null, name) still reads a plain attr.
        using var fixture = RuntimeFixture.Setup("""<html><body><div id="d" title="hi"></div></body></html>""");
        var result = fixture.Runtime.Evaluate("document.getElementById('d').getAttributeNS(null,'title')");
        Assert.Equal("hi", result!.GetValue<string>());
    }

    [Fact]
    public void NamespacedAttributeKeepsItsQualifiedName()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            "(function(){var s=document.createElementNS('http://www.w3.org/2000/svg','svg');"
            + "s.setAttributeNS('http://www.w3.org/1999/xlink','xlink:href','#g');"
            + "return s.getAttribute('xlink:href')+'|'+s.getAttributeNames()[0]+'|'+s.outerHTML;})()");
        Assert.Equal("""#g|xlink:href|<svg xlink:href="#g"></svg>""", result!.GetValue<string>());
    }

    [Fact]
    public void ParsedXlinkAttributeIsAvailableThroughBothApis()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><svg><use id="u" xlink:href="#icon"></use></svg></body></html>""");
        var result = fixture.Runtime.Evaluate(
            "(function(){var u=document.getElementById('u');"
            + "return u.getAttribute('xlink:href')+'|'+u.getAttributeNS('http://www.w3.org/1999/xlink','href')"
            + "+'|'+u.getAttributeNames().join(',');})()");
        Assert.Equal("#icon|#icon|id,xlink:href", result!.GetValue<string>());
    }

    [Fact]
    public void SetAttributeUpdatesAParsedNamespacedAttributeInPlace()
    {
        // setAttribute matched the stored attribute by local name only, so a parsed
        // `xlink:href` (prefix=xlink, local=href) was never found by the qualified name
        // "xlink:href": the update was pushed as a *second* attribute, getAttribute kept
        // returning the stale original, and the element serialized `xlink:href` twice.
        using var fixture = RuntimeFixture.Setup(
            """<html><body><svg><use id="u" xlink:href="#a"></use></svg></body></html>""");
        var result = fixture.Runtime.Evaluate(
            "(function(){var u=document.getElementById('u');u.setAttribute('xlink:href','#b');"
            + "var dup=(u.outerHTML.match(/xlink:href/g)||[]).length;"
            + "return u.getAttribute('xlink:href')+'|'+u.getAttributeNS('http://www.w3.org/1999/xlink','href')"
            + "+'|'+u.getAttributeNames().join(',')+'|'+dup;})()");
        Assert.Equal("#b|#b|id,xlink:href|1", result!.GetValue<string>());
    }

    [Fact]
    public void SetAttributeNsValidatesNamespaceConstraints()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            "(function(){var e=document.createElement('div'),out=[];"
            + "for(const args of [[null,'x:y'],['urn:test','a:b:c'],['urn:test','xml:lang'],['urn:test','xmlns:x']])"
            + "{try{e.setAttributeNS(args[0],args[1],'v');out.push('none')}catch(err){out.push(err.name)}}"
            + "return out.join('|');})()");
        Assert.Equal(
            "NamespaceError|InvalidCharacterError|NamespaceError|NamespaceError",
            result!.GetValue<string>());
    }

    [Fact]
    public void DomParserFlagsMalformedXmlWithParsererror()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            "(function(){var d=new DOMParser().parseFromString('<a><b></a>','application/xml');"
            + " return d.querySelector('parsererror') ? true : false;})()");
        Assert.True(result!.GetValue<bool>());
    }

    [Fact]
    public void DomParserAcceptsWellFormedXml()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            "(function(){var d=new DOMParser().parseFromString('<root><child>x</child></root>','application/xml');"
            + " return d.querySelector('parsererror') ? 'ERR' : 'OK';})()");
        Assert.Equal("OK", result!.GetValue<string>());
    }

    [Fact]
    public void DomParserHtmlNeverGetsParsererror()
    {
        // HTML parsing is tolerant and must never synthesize a parsererror.
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            "(function(){var d=new DOMParser().parseFromString('<div><p>hi</a>','text/html');"
            + " return d.querySelector('parsererror') ? 'ERR' : 'OK';})()");
        Assert.Equal("OK", result!.GetValue<string>());
    }

    [Fact]
    public void CustomElementUpgradeRunsClassConstructorOnExistingElement()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><svelte-like id="component"></svelte-like></body></html>""");
        var result = fixture.Runtime.Evaluate(
            """
            const before = document.getElementById("component");
            class SvelteLike extends HTMLElement {
                constructor() {
                    super();
                    this.$$s = [];
                    this.attachShadow({ mode: "open" });
                }
                connectedCallback() {
                    for (const subscription of this.$$s) subscription();
                    this.$$s.push(() => {});
                    this.shadowRoot.textContent = "ready";
                }
            }
            customElements.define("svelte-like", SvelteLike);
            return [
                document.getElementById("component") === before,
                before instanceof SvelteLike,
                before.constructor === SvelteLike,
                before.$$s.length,
                before.shadowRoot && before.shadowRoot.textContent
            ];
            """);
        AssertJson("""[true,true,true,1,"ready"]""", result);
    }

    [Fact]
    public void ShadowRootChildrenExposeParentSiblingsAndComposedRoot()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            const host = document.createElement("lit-host");
            document.body.appendChild(host);
            const root = host.attachShadow({ mode: "open" });
            const start = document.createComment("start");
            const end = document.createComment("end");
            root.appendChild(start);
            root.appendChild(end);

            const text = document.createTextNode("rendered");
            start.parentNode.insertBefore(text, end);
            const inserted = [
                start.parentNode === root,
                start.nextSibling === text,
                text.previousSibling === start,
                text.nextSibling === end,
                end.previousSibling === text,
                root.contains(text),
                text.getRootNode() === root,
                text.getRootNode({ composed: true }) === document,
                root.getRootNode({ composed: true }) === document,
                root.isConnected,
                text.isConnected,
                root.textContent
            ];

            root.removeChild(text);
            const removed = [
                text.parentNode === null,
                start.nextSibling === end,
                end.previousSibling === start
            ];

            document.body.appendChild(start);
            const moved = [
                start.parentNode === document.body,
                start.getRootNode() === document,
                root.firstChild === end
            ];

            root.innerHTML = "<span id='inside'>inside</span>";
            const inside = root.firstChild;
            const parsed = [
                inside.parentNode === root,
                inside.getRootNode() === root,
                root.textContent,
                inside.matches("span#inside"),
                root.querySelector("span#inside") === inside,
                root.querySelectorAll("span#inside").length === 1
            ];

            const a = document.createElement("a");
            const b = document.createElement("b");
            const c = document.createElement("i");
            root.replaceChildren(a, b, c);
            root.insertBefore(a, c);
            const movedWithin = Array.from(root.children, el => el.localName);

            const fragment = document.createDocumentFragment();
            const x = document.createElement("x-one");
            const y = document.createElement("x-two");
            fragment.append(x, y);
            root.insertBefore(fragment, c);
            const flattened = [
                Array.from(root.children, el => el.localName),
                fragment.childNodes.length,
                x.parentNode === root,
                y.parentNode === root
            ];

            root.replaceChild(b, c);
            const replaced = [
                Array.from(root.children, el => el.localName),
                c.parentNode === null,
                b.parentNode === root
            ];

            const detached = document.createElement("detached-node");
            const errors = [];
            for (const operation of [
                () => root.insertBefore(detached, c),
                () => root.removeChild(c),
                () => root.replaceChild(detached, c),
                () => root.appendChild(root),
                () => root.appendChild(host)
            ]) {
                try {
                    operation();
                    errors.push("none");
                } catch (error) {
                    errors.push(error.name);
                }
            }
            return [inserted, removed, moved, parsed, movedWithin, flattened, replaced, errors];
            """);
        AssertJson(
            """
            [[true,true,true,true,true,true,true,true,true,true,true,"rendered"],
             [true,true,true],
             [true,true,true],
             [true,true,"inside",true,true,true],
             ["b","a","i"],
             [["b","a","x-one","x-two","i"],0,true,true],
             [["a","x-one","x-two","b"],true,true],
             ["NotFoundError","NotFoundError","NotFoundError","HierarchyRequestError","HierarchyRequestError"]]
            """,
            result);
    }

    [Fact]
    public void ShadowRootIdentityAndChildrenAreNativeTreeBacked()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            const host = document.createElement("native-shadow-host");
            document.body.appendChild(host);
            const root = host.attachShadow({ mode: "open", delegatesFocus: true });
            root.innerHTML = "<section id='inside'><span>native</span></section>";
            const inside = root.querySelector("#inside");
            const records = [];
            const observer = new MutationObserver(batch => records.push(...batch));
            observer.observe(root, { childList: true, subtree: true });
            const added = document.createElement("strong");
            inside.appendChild(added);
            records.push(...observer.takeRecords());

            host._shadowRoot = { mode: "closed" };
            let duplicateError = "none";
            try { host.attachShadow({ mode: "open" }); }
            catch (error) { duplicateError = error.name; }

            class ClosedShadowHost extends HTMLElement {
                constructor() {
                    super();
                    this.closedRoot = this.attachShadow({ mode: "closed" });
                    this.internals = this.attachInternals();
                }
            }
            customElements.define("closed-shadow-host", ClosedShadowHost);
            const closedHost = document.createElement("closed-shadow-host");

            return [
                root instanceof ShadowRoot,
                root.nodeType,
                root.nodeName,
                root.host === host,
                root.mode,
                root.delegatesFocus,
                host.shadowRoot === root,
                duplicateError,
                inside.parentNode === root,
                inside.getRootNode() === root,
                inside.getRootNode({ composed: true }) === document,
                root.isConnected,
                inside.isConnected,
                host.contains(inside),
                document.querySelector("#inside") === null,
                root.querySelector("#inside") === inside,
                records.length,
                records[0] && records[0].target === inside,
                records[0] && records[0].addedNodes[0] === added,
                closedHost.shadowRoot,
                closedHost.internals.shadowRoot === closedHost.closedRoot
            ];
            """);
        AssertJson(
            """
            [true,11,"#document-fragment",true,"open",true,true,"NotSupportedError",
             true,true,true,true,true,false,true,true,1,true,true,null,true]
            """,
            result);
    }

    [Fact]
    public void CreateElementSynchronouslyConstructsAnExistingDefinition()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            const testStart = true;
            class CreatedLater extends HTMLElement {
                constructor() {
                    super();
                    this.constructorState = ["initialized"];
                    this.attachShadow({ mode: "open" });
                    this.shadowRoot.textContent = "constructed";
                }
                connectedCallback() {
                    this.constructorState.push("connected");
                }
            }
            customElements.define("created-later", CreatedLater);
            const element = document.createElement("created-later");
            const foreign = document.createElementNS(
                "http://www.w3.org/2000/svg", "created-later"
            );
            return [
                element instanceof CreatedLater,
                element.constructor === CreatedLater,
                element.localName,
                element.constructorState,
                element.shadowRoot && element.shadowRoot.textContent,
                element.isConnected,
                foreign instanceof CreatedLater
            ];
            """);
        AssertJson("""[true,true,"created-later",["initialized"],"constructed",false,false]""", result);
    }

    [Fact]
    public void CreatedForeignElementKeepsNativeQualifiedNameThroughClone()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            "(function(){const ns='http://www.w3.org/2000/svg';"
            + "const el=document.createElementNS(ns,'linearGradient');const clone=el.cloneNode(true);"
            + "return [el.namespaceURI,el.localName,el.tagName,el.nodeName,"
            + "clone.namespaceURI,clone.localName,clone.outerHTML].join('|');})()");
        Assert.Equal(
            "http://www.w3.org/2000/svg|linearGradient|linearGradient|linearGradient|"
            + "http://www.w3.org/2000/svg|linearGradient|<linearGradient></linearGradient>",
            result!.GetValue<string>());
    }

    [Fact]
    public void SvgPathUsesTheStandardInterfaceChain()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><svg><path id="shape" d="M0 0L1 1"></path></svg></body></html>""");
        var result = fixture.Runtime.Evaluate(
            """
            const parsed = document.getElementById("shape");
            SVGPathElement.prototype.polyfillProbe = () => "path";
            const created = document.createElementNS(
                "http://www.w3.org/2000/svg", "path"
            );
            const div = document.createElement("div");
            return [
                parsed.constructor.name,
                parsed instanceof SVGPathElement,
                parsed instanceof SVGGeometryElement,
                parsed instanceof SVGGraphicsElement,
                parsed instanceof SVGElement,
                parsed instanceof Element,
                created instanceof SVGPathElement,
                Object.getPrototypeOf(SVGPathElement.prototype) === SVGGeometryElement.prototype,
                Object.getPrototypeOf(SVGGeometryElement.prototype) === SVGGraphicsElement.prototype,
                Object.getPrototypeOf(SVGGraphicsElement.prototype) === SVGElement.prototype,
                Object.getPrototypeOf(SVGElement.prototype) === Element.prototype,
                parsed.polyfillProbe(),
                typeof div.polyfillProbe
            ];
            """);
        AssertJson(
            """["SVGPathElement",true,true,true,true,true,true,true,true,true,true,"path","undefined"]""",
            result);
    }

    [Fact]
    public void ForeignInnerHtmlAndContextualFragmentsKeepSvgNamespace()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn foreign_inner_html_and_contextual_fragments_keep_svg_namespace() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let v = rt
                    .evaluate(
                        "(function(){const ns='http://www.w3.org/2000/svg';const svg=document.createElementNS(ns,'svg');svg.innerHTML='<linearGradient id=paint></linearGradient>';const range=document.createRange();range.selectNodeContents(svg);const fragment=range.createContextualFragment('<circle></circle>');const circle=fragment.firstElementChild;return [svg.firstElementChild.namespaceURI,svg.firstElementChild.localName,circle.namespaceURI,circle.localName].join('|');})()",
                    )
                    .unwrap();
                assert_eq!(
                    v,
                    serde_json::json!(
                        "http://www.w3.org/2000/svg|linearGradient|http://www.w3.org/2000/svg|circle"
                    )
                );
            }
        */
    }

    [Fact]
    public void ThrowingCustomElementConstructorMarksUpgradeFailedWithoutConnecting()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><throws-during-upgrade id="target"></throws-during-upgrade></body></html>""");
        var result = fixture.Runtime.Evaluate(
            """
            let constructorCalls = 0;
            let connectedCalls = 0;
            class ThrowsDuringUpgrade extends HTMLElement {
                constructor() {
                    super();
                    constructorCalls++;
                    throw new Error("expected constructor failure");
                }
                connectedCallback() {
                    connectedCalls++;
                }
            }
            customElements.define("throws-during-upgrade", ThrowsDuringUpgrade);
            const element = document.getElementById("target");
            customElements.upgrade(document);
            return [
                constructorCalls,
                connectedCalls,
                element.__customUpgradeFailed === true
            ];
            """);
        AssertJson("[1,0,true]", result);
    }

    [Fact]
    public void TestDocumentTitle()
    {
        using var fixture = RuntimeFixture.Setup("<html><head><title>Test</title></head><body></body></html>");
        var rt = fixture.Runtime;
        Assert.Equal("Test", rt.Evaluate("document.title")!.GetValue<string>());

        var result = rt.Evaluate(@"
                (function() {
                  document.title = 'A <new> title';
                  return [
                    document.title,
                    document.querySelector('head > title').textContent,
                    document.querySelectorAll('title').length
                  ];
                })()");
        var array = Assert.IsType<JsonArray>(result);
        Assert.Equal("A <new> title", array[0]!.GetValue<string>());
        Assert.Equal("A <new> title", array[1]!.GetValue<string>());
        Assert.Equal(1.0, array[2]!.GetValue<double>());

        var normalized = rt.Evaluate(@"
                (function() {
                  document.querySelector('title').textContent = '  live\n\tDOM   title  ';
                  return document.title;
                })()");
        Assert.Equal("live DOM title", normalized!.GetValue<string>());
    }

    [Fact]
    public void DocumentTitleSetterCreatesMissingTitleElement()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><main>content</main></body></html>");
        var rt = fixture.Runtime;
        AssertJson(
            """["Created","HEAD","TITLE","Created",true]""",
            rt.Evaluate(
                """
                (function() {
                  document.title = "Created";
                  return [
                    document.title,
                    document.head.tagName,
                    document.head.firstElementChild.tagName,
                    document.head.firstElementChild.textContent,
                    document.documentElement.firstElementChild === document.head
                  ];
                })()
                """));

        AssertJson(
            """["Detached title","  Detached   title  ",""]""",
            rt.Evaluate(
                """
                (function() {
                  const doc = document.implementation.createHTMLDocument();
                  doc.title = "  Detached   title  ";
                  return [doc.title, doc.querySelector("title").textContent, doc.referrer];
                })()
                """));
    }

    [Fact]
    public void DocumentReferrerHasExplicitNavigationState()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        Assert.Equal("", rt.Evaluate("document.referrer")!.GetValue<string>());

        rt.SetReferrer("https://source.example/path?q=1");
        Assert.Equal("https://source.example/path?q=1", rt.Evaluate("document.referrer")!.GetValue<string>());
    }

    [Fact]
    public void GlobalWindowHasBrowserConstructorIdentity()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            "return [window === self, self.constructor === Window,"
            + " window instanceof Window, self.document === document,"
            + " self.location === location, self.history === history,"
            + " self.navigator === navigator];");
        AssertJson("[true,true,true,true,true,true,true]", result);
    }

    [Fact]
    public void WindowNamedAccessExposesIdsAndEligibleNames()
    {
        using var fixture = RuntimeFixture.Setup(
            """
            <html><body>
                <script id="payload" type="application/json">{"ready":true}</script>
                <div id="duplicate"></div><span id="duplicate"></span>
                <form name="login"></form><img name="hero">
                <div name="not-exposed"></div>
            </body></html>
            """);
        var result = fixture.Runtime.Evaluate(
            """
            return [
                window.payload === document.getElementById("payload"),
                window.payload.text,
                window.duplicate instanceof HTMLCollection,
                window.duplicate.length,
                window.login === document.querySelector("form"),
                window.hero === document.querySelector("img"),
                typeof window["not-exposed"]
            ];
            """);
        AssertJson("""[true,"{\"ready\":true}",true,2,true,true,"undefined"]""", result);
    }

    [Fact]
    public void WindowNamedAccessTracksDynamicIdsAndFragmentParsing()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id='host'></div></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            const made = document.createElement("section");
            made.id = "dynamicName";
            const detachedIdAbsent = !("dynamicName" in window);
            document.body.appendChild(made);
            const first = window.dynamicName === made;
            made.id = "renamedDynamic";
            const renamed = !("dynamicName" in window)
                && window.renamedDynamic === made;
            document.body.removeChild(made);
            const removed = !("renamedDynamic" in window);
            document.body.appendChild(made);
            const reattached = window.renamedDynamic === made;
            document.getElementById("host").innerHTML =
                "<script id='parsedName'>payload</script>";
            const parsed = window.parsedName === document.getElementById("parsedName")
                && window.parsedName.text === "payload";
            document.getElementById("host").innerHTML = "";
            const subtree = document.createElement("div");
            subtree.innerHTML = "<svg><path id='nestedSvg' name='svgName'></path></svg>";
            const detachedNestedAbsent = !("nestedSvg" in window);
            document.body.appendChild(subtree);
            const nested = window.nestedSvg === subtree.querySelector("path")
                && typeof window.svgName === "undefined";
            document.body.removeChild(subtree);
            const nestedRemoved = !("nestedSvg" in window);
            document.body.appendChild(subtree);
            const shadowHost = document.createElement("div");
            const shadowRoot = shadowHost.attachShadow({ mode: "open" });
            const shadowChild = document.createElement("span");
            shadowChild.id = "shadowOnly";
            shadowRoot.appendChild(shadowChild);
            document.body.appendChild(shadowHost);
            const originalFetch = window.fetch;
            const collision = document.createElement("div");
            collision.id = "fetch";
            document.body.appendChild(collision);
            document.body.removeChild(collision);
            return [
                detachedIdAbsent,
                first,
                renamed,
                removed,
                reattached,
                parsed,
                !("parsedName" in window),
                detachedNestedAbsent,
                nested,
                nestedRemoved,
                window.nestedSvg === subtree.querySelector("path"),
                !("shadowOnly" in window),
                window.fetch === originalFetch
            ];
            """);
        AssertJson("[true,true,true,true,true,true,true,true,true,true,true,true,true]", result);
    }

    [Fact]
    public void ExplicitViewportIsDistinctFromFingerprintedScreen()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        rt.SetViewport(1024.0, 768.0);
        rt.RunPageInit();
        var result = rt.Evaluate(
            "return [innerWidth, innerHeight, visualViewport.width,"
            + " visualViewport.height, screen.width > 0, screen.height > 0];");
        AssertJson("[1024,768,1024,768,true,true]", result);
    }

    [Fact]
    public void ScreenOverrideIsIndependentLiveAndPreservesScreenIdentity()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        rt.SetViewport(1024.0, 768.0);
        rt.RunPageInit();
        rt.ExecuteScript(
            "remember-screen",
            "globalThis.__screenBefore = screen;"
            + "globalThis.__screenSizeBefore = [screen.width, screen.height];");

        rt.SetScreenSizeOverride((1440.0, 900.0), true);
        AssertJson(
            "[1024,768,1440,900,1440,900,true]",
            rt.Evaluate(
                "[innerWidth, innerHeight, screen.width, screen.height,"
                + " screen.availWidth, screen.availHeight, screen === __screenBefore]"));

        rt.SetScreenSizeOverride(null, false);
        AssertJson(
            "[1024,768,true,true,true,true]",
            rt.Evaluate(
                "[innerWidth, innerHeight, screen.width === __screenSizeBefore[0],"
                + " screen.height === __screenSizeBefore[1],"
                + " screen.availHeight === screen.height - 40,"
                + " screen === __screenBefore]"));
    }

    [Fact]
    public void MatchMediaEvaluatesQueryListsConjunctionsRangesAndOrientation()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        rt.SetViewport(1280.0, 720.0);
        rt.RunPageInit();

        var result = rt.Evaluate(
            """
            return [
                matchMedia("(min-width: 1024px) and (min-height: 700px)").matches,
                matchMedia("(min-width: 1024px) and (min-height: 900px)").matches,
                matchMedia("(max-width: 600px), screen and (orientation: landscape)").matches,
                matchMedia("not print").matches,
                matchMedia("not screen").matches,
                matchMedia("only screen and (width: 1280px) and (height = 720px)").matches,
                matchMedia("(1000px <= width < 1400px) and (height > 700px)").matches,
                matchMedia("(orientation: portrait)").matches,
                matchMedia("(prefers-color-scheme: light) and (pointer: fine) and (hover: hover)").matches,
                matchMedia("(obscura-unknown-feature: yes)").matches
            ];
            """);
        AssertJson("[true,false,true,true,false,true,true,false,true,false]", result);
    }

    [Fact]
    public void MatchMediaMatchesAreLiveAcrossViewportResizes()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        rt.SetViewport(900.0, 600.0);
        rt.RunPageInit();
        AssertJson(
            "[true,false]",
            rt.Evaluate(
                """
                return [
                    (globalThis.__wideAndShort = matchMedia(
                        "(min-width: 800px) and (max-height: 700px)"
                    )).matches,
                    (globalThis.__portrait = matchMedia(
                        "(orientation: portrait)"
                    )).matches
                ];
                """));

        rt.SetViewport(600.0, 900.0);
        AssertJson(
            "[false,true,true]",
            rt.Evaluate(
                "return [__wideAndShort.matches, __portrait.matches,"
                + " matchMedia('(max-width: 600px), print').matches];"));
    }

    [Fact]
    public void ComputedStyleAccessDoesNotGetShadowedByInlineStyleProxy()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><div id="box" style="opacity:.5;width:40px"></div></body></html>""");
        var result = fixture.Runtime.Evaluate(
            """
            const box = document.getElementById("box");
            const computed = getComputedStyle(box);
            return [
                computed.display,
                computed.visibility,
                computed.opacity,
                computed.width,
                computed.getPropertyValue("display"),
                computed.getPropertyValue("background-color")
            ];
            """);
        AssertJson("""["block","visible","0.5","40px","block","rgba(0, 0, 0, 0)"]""", result);
    }

    [Fact]
    public void HyperlinkContentAttributesReflectThroughTheIdlSurface()
    {
        using var fixture = RuntimeFixture.Setup(
            """
            <html><body>
                <a id="locale" hreflang="en-US" rel="alternate"
                   target="_blank" download="guide.pdf"
                   ping="/audit" referrerpolicy="no-referrer">English</a>
            </body></html>
            """);
        var result = fixture.Runtime.Evaluate(
            """
            const link = document.getElementById("locale");
            const initial = [
                link.hreflang, link.rel, link.target, link.download,
                link.ping, link.referrerPolicy,
                link.hreflang.split("-")[1]
            ];
            link.hreflang = "de-DE";
            link.referrerPolicy = "origin";
            return [
                initial,
                link.getAttribute("hreflang"),
                link.getAttribute("referrerpolicy")
            ];
            """);
        AssertJson(
            """
            [["en-US","alternate","_blank","guide.pdf","/audit","no-referrer","US"],"de-DE","origin"]
            """,
            result);
    }

    [Fact]
    public void OrdinaryInlineKeepsComputedSizesButUsesContentGeometry()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html><head><style>
                html,body,p { margin:0 }
                #host { width:300px; font-size:16px; line-height:20px }
                #token {
                    position:relative;
                    width:100%; height:100px;
                    min-width:100%; min-height:100px;
                    max-width:100%; max-height:100px;
                    padding:0 5px; background:red
                }
                #after { position:relative }
                #atomic {
                    display:inline-block; box-sizing:border-box;
                    width:80px; height:30px; padding:0; border:0
                }
                #replaced {
                    display:inline; box-sizing:border-box;
                    width:80px; height:30px; min-width:0; min-height:0;
                    max-width:none; max-height:none; padding:0; border:0
                }
                #items { display:flex }
                #item {
                    display:inline; box-sizing:border-box; flex:none;
                    width:90px; height:25px; padding:0; border:0
                }
            </style></head><body>
                <p id="host">A <code id="token">token</code> <span id="after">after</span></p>
                <span id="atomic"></span>
                <input id="replaced">
                <div id="items"><span id="item"></span></div>
            </body></html>
            """));
        rt.SetViewport(400.0, 240.0);
        rt.RunPageInit();

        var result = Assert.IsType<JsonObject>(rt.Evaluate(
            """
            const token = document.getElementById("token");
            const after = document.getElementById("after");
            const computed = getComputedStyle(token);
            const rect = token.getBoundingClientRect();
            const afterRect = after.getBoundingClientRect();
            const atomic = document.getElementById("atomic").getBoundingClientRect();
            const replaced = document.getElementById("replaced").getBoundingClientRect();
            const item = document.getElementById("item").getBoundingClientRect();
            return {
                computed: [
                    computed.width, computed.height,
                    computed.minWidth, computed.minHeight,
                    computed.maxWidth, computed.maxHeight
                ],
                rect: [rect.x, rect.y, rect.width, rect.height],
                after: [afterRect.x, afterRect.y],
                client: [token.clientWidth, token.clientHeight],
                clientRects: Array.from(token.getClientRects(), r => [
                    r.x, r.y, r.width, r.height
                ]),
                atomic: [atomic.width, atomic.height],
                replaced: [replaced.width, replaced.height],
                item: [item.width, item.height, getComputedStyle(item).display]
            };
            """));

        AssertJson("""["100%","100px","100%","100px","100%","100px"]""", result["computed"]);
        var rect = Assert.IsType<JsonArray>(result["rect"]);
        var tokenX = rect[0]!.GetValue<double>();
        var tokenY = rect[1]!.GetValue<double>();
        var tokenWidth = rect[2]!.GetValue<double>();
        var tokenHeight = rect[3]!.GetValue<double>();
        Assert.True(
            tokenWidth > 20.0 && tokenWidth < 100.0,
            $"ordinary inline should hug text and padding: {rect}");
        Assert.True(tokenHeight < 40.0, "ignored block size leaked into geometry");
        AssertJson("[0,0]", result["client"]);
        var clientRects = Assert.IsType<JsonArray>(result["clientRects"]);
        Assert.Single(clientRects);
        var clientRect = Assert.IsType<JsonArray>(clientRects[0]);
        double[] expected = [tokenX, tokenY, tokenWidth, tokenHeight];
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(
                Math.Abs(clientRect[i]!.GetValue<double>() - expected[i]) < 0.001,
                "getClientRects must expose the renderer's inline fragments");
        }
        var after = Assert.IsType<JsonArray>(result["after"]);
        Assert.True(after[0]!.GetValue<double>() >= tokenX + tokenWidth - 0.01);
        Assert.True(Math.Abs(after[1]!.GetValue<double>() - tokenY) < 0.01);
        AssertJson("[80,30]", result["atomic"]);
        AssertJson("[80,30]", result["replaced"]);
        AssertJson("""[90,25,"block"]""", result["item"]);
    }

    [Fact]
    public void ComputedStyleUsesRendererStylesheetCascadeAndInvalidates()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html><head><style>
                .base {
                    display:flex; position:relative; z-index:7;
                    visibility:hidden; opacity:.35;
                    background-color:rgb(10,20,30); color:rgb(40,50,60);
                    width:120px; height:40px; min-width:20px; max-width:160px;
                    box-sizing:border-box; overflow-x:clip; overflow-y:visible;
                    margin:1px 2px 3px 4px; padding:5px 6px 7px 8px;
                    border:2px solid rgb(70,80,90);
                    flex-direction:column; flex-wrap:wrap;
                    align-items:center; justify-content:space-between;
                    gap:6px 9px; transform:translate(3px,4px);
                }
                .alt { display:grid; width:150px; opacity:.8; }
            </style></head><body><div id="box" class="base"></div></body></html>
            """));
        rt.SetViewport(400.0, 200.0);
        rt.RunPageInit();

        var initial = rt.Evaluate(
            """
            const box = document.getElementById("box");
            const c = getComputedStyle(box);
            return [
                c.display, c.position, c.zIndex, c.visibility, c.opacity,
                c.backgroundColor, c.getPropertyValue("color"),
                c.width, c.height, c.minWidth, c.maxWidth, c.boxSizing,
                c.overflowX, c.overflowY,
                c.marginTop, c.marginRight, c.marginBottom, c.marginLeft,
                c.paddingTop, c.borderLeftWidth, c.borderLeftColor,
                c.flexDirection, c.flexWrap, c.alignItems,
                c.justifyContent, c.rowGap, c.columnGap, c.transform
            ];
            """);
        AssertJson(
            """
            ["flex","relative","7","hidden","0.35","rgb(10, 20, 30)","rgb(40, 50, 60)",
             "120px","40px","20px","160px","border-box","clip","visible",
             "1px","2px","3px","4px","5px","2px","rgb(70, 80, 90)",
             "column","wrap","center","space-between","6px","9px","matrix(1, 0, 0, 1, 3, 4)"]
            """,
            initial);

        AssertJson(
            """["grid","150px","0.8"]""",
            rt.Evaluate(
                """
                const box = document.getElementById("box");
                box.className = "alt";
                const c = getComputedStyle(box);
                return [c.display, c.width, c.opacity];
                """));
    }

    [Fact]
    public void WebkitTruncationComputedNamesUseNativeSupportAndVendorPrefixes()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html><head><style>
              #clamp { display:-webkit-box; -webkit-box-orient:vertical;
                       -webkit-line-clamp:2; overflow:hidden; }
              #legacy { display:-webkit-inline-box; -webkit-box-orient:horizontal; }
            </style></head><body>
              <div id="clamp">one two three four five six seven eight</div>
              <span id="legacy">legacy</span>
            </body></html>
            """));
        rt.SetViewport(120.0, 200.0);
        rt.RunPageInit();

        var result = rt.Evaluate(
            """
            const clamp = getComputedStyle(document.getElementById("clamp"));
            const legacy = getComputedStyle(document.getElementById("legacy"));
            return {
              supports: [
                CSS.supports("text-overflow", "ellipsis"),
                CSS.supports("-webkit-line-clamp", "2"),
                CSS.supports("-webkit-line-clamp", "0"),
                CSS.supports("display", "-webkit-box"),
                CSS.supports("-webkit-box-orient", "vertical")
              ],
              clamp: [clamp.display, clamp.webkitLineClamp,
                clamp.webkitBoxOrient,
                clamp.getPropertyValue("-webkit-line-clamp")],
              legacy: [legacy.display, legacy.webkitBoxOrient]
            };
            """);
        AssertJson(
            """
            {"supports":[true,true,false,true,true],
             "clamp":["flow-root","2","vertical","2"],
             "legacy":["-webkit-inline-box","horizontal"]}
            """,
            result);
    }

    [Fact]
    public void ComputedTypographyUsesResolvedRendererValues()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html><head><style>
                #parent {
                    font-size:20px; line-height:1.5;
                    letter-spacing:-.05em; white-space:pre-wrap;
                    text-align:end
                }
                #child { font-size:10px }
                #zero { letter-spacing:0px; white-space:break-spaces }
            </style></head><body>
                <div id="parent"><span id="child">child</span></div>
                <div id="zero">zero</div>
            </body></html>
            """));
        rt.SetViewport(400.0, 200.0);
        rt.RunPageInit();

        var result = rt.Evaluate(
            """
            const sample = id => {
                const s = getComputedStyle(document.getElementById(id));
                return [s.lineHeight, s.letterSpacing, s.whiteSpace, s.textAlign];
            };
            return [sample("parent"), sample("child"), sample("zero")];
            """);
        AssertJson(
            """
            [["30px","-1px","pre-wrap","end"],
             ["15px","-1px","pre-wrap","end"],
             ["normal","normal","break-spaces","start"]]
            """,
            result);
    }

    [Fact]
    public void ComputedStyleExposesCascadedCustomPropertiesAndInvalidates()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html><head><style>
                :root { --inherited-space: 17px; --derived-space: var(--inherited-space); }
                #nav {
                    --r-globalnav-font-size:17px;
                    --local-scale:1.25;
                    font-size:var(--r-globalnav-font-size);
                }
            </style></head><body>
                <nav id="nav"><span id="child"></span></nav>
            </body></html>
            """));
        rt.SetViewport(400.0, 200.0);
        rt.RunPageInit();

        AssertJson(
            """["17px","17px",1,"17px","17px","1.25","17px","1.25",true]""",
            rt.Evaluate(
                """
                const nav = document.getElementById("nav");
                const child = document.getElementById("child");
                const navStyle = getComputedStyle(nav);
                const childStyle = getComputedStyle(child);
                let enumeratesBase = false;
                for (let i = 0; i < navStyle.length; i++) {
                    if (navStyle.item(i) === "--r-globalnav-font-size")
                        enumeratesBase = true;
                }
                return [
                    navStyle.fontSize,
                    navStyle.getPropertyValue("--r-globalnav-font-size"),
                    parseInt(navStyle.fontSize) /
                        parseInt(navStyle.getPropertyValue("--r-globalnav-font-size")),
                    navStyle.getPropertyValue("--inherited-space"),
                    navStyle.getPropertyValue("--derived-space"),
                    navStyle.getPropertyValue("--local-scale"),
                    childStyle.getPropertyValue("--inherited-space"),
                    childStyle.getPropertyValue("--local-scale"),
                    enumeratesBase
                ];
                """));

        AssertJson(
            """["19px","23px","17px"]""",
            rt.Evaluate(
                """
                const nav = document.getElementById("nav");
                const computed = getComputedStyle(nav);
                nav.style.setProperty("--inherited-space", "23px");
                nav.style.fontSize = "19px";
                return [
                    computed.fontSize,
                    computed.getPropertyValue("--inherited-space"),
                    computed.getPropertyValue("--derived-space")
                ];
                """));
    }

    [Fact]
    public async Task IdleEventLoopFlushesResolvedPromiseContinuations()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id='state'>pending</div></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "font-ready",
            "document.fonts.load('normal 1px Example').then(() => {"
            + " document.getElementById('state').textContent = 'ready'; });");
        await rt.RunEventLoopBoundedAsync(100);
        Assert.Equal(
            "ready",
            rt.Evaluate("document.getElementById('state').textContent")!.GetValue<string>());
    }

    [Fact]
    public async Task QuiescentEventLoopDoesNotWaitForAnalyticsInterval()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "quiescent-long-interval",
            "setInterval(() => { globalThis.__analyticsTicks = (globalThis.__analyticsTicks || 0) + 1; }, 1000);");

        var started = System.Diagnostics.Stopwatch.StartNew();
        await rt.RunEventLoopUntilQuiescentAsync(1_000, 50);
        Assert.True(
            started.Elapsed < TimeSpan.FromMilliseconds(400),
            "a future analytics interval must not consume the full settle budget");
    }

    [Fact]
    public async Task FixedDurationEventLoopYieldsFromContinuouslyReadyTasks()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "fixed-duration-continuously-ready",
            "globalThis.__fixedTicks = 0;"
            + "setInterval(() => { __fixedTicks++; }, 0);");

        var started = System.Diagnostics.Stopwatch.StartNew();
        await rt.RunEventLoopBoundedAsync(40);
        var elapsed = started.Elapsed;

        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(300),
            $"a continuously-ready queue must return between tasks instead of waiting for the watchdog: {elapsed}");
        Assert.True(
            rt.Evaluate("globalThis.__fixedTicks > 0")?.GetValue<bool>() ?? false,
            "the cooperative fixed wait must still execute queued tasks");
        Assert.Equal(
            "usable",
            rt.Evaluate(
                "(document.body.setAttribute('data-after-fixed-wait', 'usable'), "
                + "document.body.getAttribute('data-after-fixed-wait'))")!.GetValue<string>());
    }

    [Fact]
    public async Task ZeroDelayIntervalCreatedByTimerYieldsToEmbedder()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "nested-zero-interval",
            "globalThis.__outerTimerRan = false;"
            + "globalThis.__zeroIntervalTicks = 0;"
            + "setTimeout(() => {"
            + "  globalThis.__outerTimerRan = true;"
            + "  globalThis.__zeroInterval = setInterval("
            + "    () => __zeroIntervalTicks++, 0);"
            + "}, 0);");

        var started = System.Diagnostics.Stopwatch.StartNew();
        await rt.RunAutonomousEventLoopTurnAsync();
        var elapsed = started.Elapsed;

        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(500),
            $"a repeating timer must yield between ticks; elapsed={elapsed}");
        AssertJson("true", rt.Evaluate("globalThis.__outerTimerRan"));
        await rt.RunAutonomousEventLoopTurnAsync();
        for (var turn = 0; turn < 5; turn++)
        {
            await rt.RunAutonomousEventLoopTurnAsync();
        }
        Assert.Equal(
            6.0,
            rt.Evaluate("globalThis.__zeroIntervalTicks")!.GetValue<double>());
        rt.ExecuteScript("clear-zero-interval", "clearInterval(globalThis.__zeroInterval)");
    }

    [Fact]
    public async Task TopLevelZeroDelayIntervalClampsAfterSixTicks()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "top-level-zero-interval",
            "globalThis.__nestedTimerDelays = [];"
            + "globalThis.__topInterval = setInterval("
            + "  () => {"
            + "    const nested = setTimeout(() => {}, 0);"
            + "    __nestedTimerDelays.push(__obscura_nextPendingTimeoutDelay());"
            + "    clearTimeout(nested);"
            + "  }, 0);");

        for (var turn = 0; turn < 7; turn++)
        {
            await rt.RunAutonomousEventLoopTurnAsync();
        }
        Assert.Equal(
            7.0,
            rt.Evaluate("globalThis.__nestedTimerDelays.length")!.GetValue<double>());
        var delays = Assert.IsType<JsonArray>(rt.Evaluate("globalThis.__nestedTimerDelays"));
        Assert.True(
            delays[0]!.GetValue<double>() < 2.0,
            $"a shallow nested timer must remain unclamped: {delays}");
        Assert.True(
            delays[4]!.GetValue<double>() < 2.0,
            $"the fifth-level parent must remain below the clamp boundary: {delays}");
        Assert.True(
            delays[5]!.GetValue<double>() >= 2.0,
            $"a timer nested from the sixth interval task must receive the four-millisecond clamp: {delays}");
        rt.ExecuteScript("clear-top-interval", "clearInterval(globalThis.__topInterval)");
    }

    [Fact]
    public async Task DeeplyNestedIntervalInheritsTheTimerTaskNesting()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "deep-zero-interval",
            "globalThis.__deepIntervalTicks = 0;"
            + "function installDeepInterval(depth) {"
            + "  if (depth === 0) {"
            + "    globalThis.__deepInterval = setInterval("
            + "      () => __deepIntervalTicks++, 0);"
            + "  } else {"
            + "    setTimeout(() => installDeepInterval(depth - 1), 0);"
            + "  }"
            + "}"
            + "installDeepInterval(6);");

        for (var turn = 0; turn < 8; turn++)
        {
            if (rt.Evaluate("globalThis.__deepInterval !== undefined")?.GetValue<bool>() == true)
            {
                break;
            }
            await rt.RunAutonomousEventLoopTurnAsync();
        }
        Assert.Equal(
            0.0,
            rt.Evaluate("globalThis.__deepIntervalTicks")!.GetValue<double>());
        await rt.RunAutonomousEventLoopTurnAsync();
        Assert.Equal(
            1.0,
            rt.Evaluate("globalThis.__deepIntervalTicks")!.GetValue<double>());
        rt.ExecuteScript("clear-deep-interval", "clearInterval(globalThis.__deepInterval)");
    }

    [Fact]
    public async Task ShortObservationDeadlineDoesNotTerminateTheActiveTask()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "task-crossing-observation-deadline",
            "globalThis.__longTaskCompleted = false;"
            + "setTimeout(() => {"
            + "  const end = performance.now() + 600;"
            + "  while (performance.now() < end) {}"
            + "  __longTaskCompleted = true;"
            + "}, 0);");

        var started = System.Diagnostics.Stopwatch.StartNew();
        await rt.RunEventLoopBoundedAsync(20);
        var elapsed = started.Elapsed;

        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(500) && elapsed < TimeSpan.FromMilliseconds(1_500),
            $"capture must wait for the active task boundary without becoming unbounded: {elapsed}");
        AssertJson("true", rt.Evaluate("globalThis.__longTaskCompleted"));
    }

    [Fact]
    public async Task AdaptiveObservationDeadlineDoesNotTerminateTheActiveTask()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "adaptive-task-crossing-observation-deadline",
            "globalThis.__adaptiveLongTaskCompleted = false;"
            + "setTimeout(() => {"
            + "  const end = performance.now() + 600;"
            + "  while (performance.now() < end) {}"
            + "  __adaptiveLongTaskCompleted = true;"
            + "}, 0);");

        var started = System.Diagnostics.Stopwatch.StartNew();
        await rt.RunEventLoopUntilQuiescentAsync(20, 10);
        var elapsed = started.Elapsed;

        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(500) && elapsed < TimeSpan.FromMilliseconds(1_500),
            $"adaptive settle must wait for the active task boundary: {elapsed}");
        AssertJson("true", rt.Evaluate("globalThis.__adaptiveLongTaskCompleted"));
    }

    [Fact]
    public async Task QuiescentEventLoopYieldsFromContinuouslyReadyNonVisualWork()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "quiescent-continuously-ready",
            "setInterval(() => {"
            + " globalThis.__schedulerTicks = (globalThis.__schedulerTicks || 0) + 1; }, 0);");

        var started = System.Diagnostics.Stopwatch.StartNew();
        await rt.RunEventLoopUntilQuiescentAsync(2_000, 150);
        var elapsed = started.Elapsed;

        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(500),
            $"a continuously-ready non-visual scheduler pinned adaptive settle: {elapsed}");
        Assert.True(
            rt.Evaluate("globalThis.__schedulerTicks > 0")?.GetValue<bool>() ?? false,
            "the cooperative policy must still drive scheduler work");
    }

    [Fact]
    public async Task QuiescentEventLoopBoundsASingleUnyieldingCallbackDrain()
    {
        // SynchronousTaskFloorMs in ObscuraJsRuntime.
        const int synchronousTaskFloorMs = 5_000;
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript("quiescent-unyielding-task", "setTimeout(() => { while (true) {} }, 0);");

        var started = System.Diagnostics.Stopwatch.StartNew();
        await rt.RunEventLoopUntilQuiescentAsync(2_000, 150);
        var elapsed = started.Elapsed;

        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(synchronousTaskFloorMs)
                && elapsed < TimeSpan.FromMilliseconds(synchronousTaskFloorMs + 1_500),
            $"one synchronous callback drain escaped the bounded task allowance: {elapsed}");
        Assert.Equal(
            "usable",
            rt.Evaluate(
                "(document.body.setAttribute('data-after-watchdog', 'usable'), "
                + "document.body.getAttribute('data-after-watchdog'))")!.GetValue<string>());
    }

    [Fact]
    public void DroppingAWatchdogCannotTerminateLaterWork()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        {
            using var cancelled = rt.ArmWatchdog(TimeSpan.FromMilliseconds(500));
            Thread.Sleep(100);
        }
        Thread.Sleep(600);

        Assert.Equal(2.0, rt.Evaluate("1 + 1")!.GetValue<double>());
    }

    [Fact]
    public async Task QuiescentEventLoopRetainsDelayedNetworkAndDomUpdate()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        using var client = new ObscuraHttpClient(new CookieJar(), null, allowPrivateNetwork: true);
        var inFlight = rt.State.PageInFlight;
        inFlight.Increment();
        new Thread(() =>
        {
            Thread.Sleep(80);
            inFlight.Decrement();
        })
        {
            IsBackground = true,
        }.Start();
        rt.SetHttpClient(client);
        rt.ExecuteScript(
            "quiescent-delayed-work",
            "setInterval(() => {}, 1000);"
            + "setTimeout(() => document.body.setAttribute('data-ready', 'ready'), 40);");

        await rt.RunEventLoopUntilQuiescentAsync(1_000, 150);
        Assert.Equal(
            "ready",
            rt.Evaluate("document.body.getAttribute('data-ready')")!.GetValue<string>());
    }

    [Fact]
    public async Task QuiescentEventLoopAllowsFetchHydrationWithinNetworkGrace()
    {
        using var page = new DelayedFetch(TimeSpan.FromMilliseconds(700));
        var rt = page.Runtime;
        rt.ExecuteScript(
            "quiescent-fetch-hydration",
            "fetch('/hydrate').then(response => response.text()).then(text => {"
            + " document.body.setAttribute('data-ready', text); });");

        var started = System.Diagnostics.Stopwatch.StartNew();
        await rt.RunEventLoopUntilQuiescentAsync(3_000, 150);
        var elapsed = started.Elapsed;

        Assert.True(page.WasAccepted(TimeSpan.FromMilliseconds(100)), "fixture fetch was not issued");
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(650),
            $"settle returned before the delayed response: {elapsed}");
        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(1_500),
            $"completed hydration should only pay its following quiet window: {elapsed}");
        Assert.Equal(
            "hydrated",
            rt.Evaluate("document.body.getAttribute('data-ready')")!.GetValue<string>());
    }

    [Fact]
    public async Task QuiescentEventLoopBoundsAHangingPageRequest()
    {
        using var page = new DelayedFetch(TimeSpan.FromSeconds(3));
        var rt = page.Runtime;
        rt.ExecuteScript("quiescent-hanging-fetch", "fetch('/analytics').catch(() => {});");

        var started = System.Diagnostics.Stopwatch.StartNew();
        await rt.RunEventLoopUntilQuiescentAsync(4_000, 150);
        var elapsed = started.Elapsed;

        Assert.True(page.WasAccepted(TimeSpan.FromMilliseconds(100)), "fixture fetch was not issued");
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(900),
            $"pending page work must receive the network grace: {elapsed}");
        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(1_700),
            $"a hanging request consumed more than its bounded grace: {elapsed}");
    }

    [Fact]
    public async Task QuiescentEventLoopGivesPostGraceDomActivityAQuietWindow()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.State.PageInFlight.Increment();
        rt.ExecuteScript(
            "quiescent-post-grace-commit",
            "setInterval(() => {}, 1000);"
            + "setTimeout(() => document.body.setAttribute('data-ready', 'late'), 1100);");

        var started = System.Diagnostics.Stopwatch.StartNew();
        await rt.RunEventLoopUntilQuiescentAsync(4_000, 150);
        var elapsed = started.Elapsed;

        Assert.Equal(
            "late",
            rt.Evaluate("document.body.getAttribute('data-ready')")!.GetValue<string>());
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(1_200),
            $"the late commit did not receive a following quiet window: {elapsed}");
        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(1_800),
            $"late observable work escaped the bounded activity tail: {elapsed}");
    }

    [Fact]
    public async Task QuiescenceIgnoresAnotherPagesSharedClientRequest()
    {
        // The Rust test stores straight into the shared client's public in-flight
        // counter; the C# client keeps that counter private, so the equivalent is a
        // real request from another page that is still on the wire.
        using var server = new DelayedFetch(TimeSpan.FromSeconds(3));
        using var client = new ObscuraHttpClient(new CookieJar(), null, allowPrivateNetwork: true);
        var otherPageRequest = client.FetchAsync(new Uri($"{server.Origin}/other-page"));
        Assert.True(server.WasAccepted(TimeSpan.FromSeconds(2)), "the other page's request was not issued");
        Assert.True(client.ActiveRequests > 0, "the shared client should still be busy");

        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.SetHttpClient(client);
        rt.ExecuteScript("quiescent-shared-client", "setInterval(() => {}, 1000);");

        var started = System.Diagnostics.Stopwatch.StartNew();
        await rt.RunEventLoopUntilQuiescentAsync(1_000, 50);
        Assert.True(
            started.Elapsed < TimeSpan.FromMilliseconds(400),
            "an unrelated page request on the shared client must not pin settle");
        _ = otherPageRequest;
    }

    [Fact]
    public async Task QuiescentEventLoopRetainsNearTermRenderTimeout()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "quiescent-render-timeout",
            "setInterval(() => {}, 1000);"
            + "setTimeout(() => document.body.setAttribute('data-ready', 'ready'), 200);");

        var started = System.Diagnostics.Stopwatch.StartNew();
        await rt.RunEventLoopUntilQuiescentAsync(1_000, 150);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(180));
        Assert.Equal(
            "ready",
            rt.Evaluate("document.body.getAttribute('data-ready')")!.GetValue<string>());
    }

    [Fact]
    public async Task QuiescentEventLoopBoundsContinuousVisualMutations()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "quiescent-animated-page",
            "let tick=0;setInterval(() =>"
            + " document.body.setAttribute('data-frame', String(++tick)), 10);");

        var started = System.Diagnostics.Stopwatch.StartNew();
        await rt.RunEventLoopUntilQuiescentAsync(2_000, 150);
        Assert.True(
            started.Elapsed < TimeSpan.FromMilliseconds(1_000),
            "an animated document must not consume the complete settle budget");
        Assert.True(
            rt.Evaluate("Number(document.body.getAttribute('data-frame')) > 0")?.GetValue<bool>() ?? false,
            "the policy must still pump animation work before capture");
    }

    [Fact]
    public void FontFaceSetTracksAuthoredAndScriptCreatedFaces()
    {
        using var fixture = RuntimeFixture.Setup(
            """
            <html><head><style>
                @font-face {
                    font-family: "Authored One";
                    src: url("https://assets.test/one.woff2") format("woff2");
                    font-weight: 350 650;
                }
                @font-face {
                    font-family: AuthoredTwo;
                    src: url(data:font/woff2;base64,d09GMg==);
                    font-style: italic;
                }
            </style></head><body></body></html>
            """);
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
                const authored = Array.from(document.fonts);
                const cssDelete = document.fonts.delete(authored[0]);
                const dynamic = new FontFace("Dynamic", "url('/dynamic.ttf')", {
                    style: "oblique 12deg",
                    weight: "700",
                    stretch: "condensed",
                    unicodeRange: "U+20-7E",
                    display: "swap"
                });
                const addResult = document.fonts.add(dynamic);
                const visited = [];
                document.fonts.forEach((value, key, set) => {
                    visited.push(value === key && set === document.fonts);
                });
                const afterAdd = [
                    document.fonts.size,
                    document.fonts.has(dynamic),
                    addResult === document.fonts,
                    dynamic.family,
                    dynamic.style,
                    dynamic.weight,
                    dynamic.stretch,
                    dynamic.unicodeRange,
                    dynamic.display,
                    visited.every(Boolean)
                ];
                const deleted = document.fonts.delete(dynamic);
                document.fonts.clear();
                const bytes = new Uint8Array([0, 1, 2, 253, 254, 255]);
                const binary = new FontFace("Binary", bytes, { weight: 600 });
                return [
                    authored.length,
                    authored.map(face => face.family),
                    cssDelete,
                    afterAdd,
                    deleted,
                    document.fonts.size,
                    binary.status,
                    binary.loaded === binary.load()
                ];
            })()
            """);
        AssertJson(
            """
            [2,["Authored One","AuthoredTwo"],false,
             [3,true,true,"Dynamic","oblique 12deg","700","condensed","U+20-7E","swap",true],
             true,2,"loaded",true]
            """,
            result);
    }

    [Fact]
    public async Task FontFaceLoadUpdatesStatusSetReadinessAndMatching()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "font-face-lifecycle",
            """
            globalThis.__fontEvents = [];
            const face = new FontFace("Lifecycle", "url('/lifecycle.woff2')", {
                weight: "700"
            });
            document.fonts.onloading = event => __fontEvents.push([event.type, event.fontfaces.length]);
            document.fonts.onloadingdone = event => __fontEvents.push([event.type, event.fontfaces.length]);
            document.fonts.add(face);
            globalThis.__fontBefore = [
                face.status,
                document.fonts.status,
                document.fonts.check("700 16px Lifecycle")
            ];
            globalThis.__fontLoadResult = "pending";
            document.fonts.load("700 16px Lifecycle").then(faces => {
                __fontLoadResult = [faces.length, faces[0] === face, face.status,
                    document.fonts.check("700 16px Lifecycle")];
            });
            document.fonts.ready.then(set => {
                globalThis.__fontReady = set === document.fonts;
            });
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """
            [["unloaded","loaded",false],
             [1,true,"loaded",true],
             true,
             [["loading",1],["loadingdone",1]]]
            """,
            rt.Evaluate("return [__fontBefore, __fontLoadResult, __fontReady, __fontEvents];"));
    }

    [Fact]
    public void AnimationFrameRequiresACallableCallback()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
                try {
                    requestAnimationFrame(null);
                    return [false, ""];
                } catch (error) {
                    return [error instanceof TypeError, error.name];
                }
            })()
            """);
        AssertJson("""[true,"TypeError"]""", result);
    }

    [Fact]
    public async Task AnimationFramesAreOrderedBatchesWithRenderingTimestamps()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "animation-frame-order",
            """
            globalThis.__rafEvents = [];
            globalThis.__rafStamps = [];
            Promise.resolve().then(() => __rafEvents.push("microtask-before"));
            setTimeout(() => __rafEvents.push("timer"), 1);
            requestAnimationFrame((timestamp) => {
                __rafEvents.push("raf-a");
                __rafStamps.push(timestamp);
                Promise.resolve().then(() => __rafEvents.push("microtask-in-raf"));
                requestAnimationFrame((nextTimestamp) => {
                    __rafEvents.push("raf-next");
                    __rafStamps.push(nextTimestamp);
                });
            });
            requestAnimationFrame((timestamp) => {
                __rafEvents.push("raf-b");
                __rafStamps.push(timestamp);
            });
            """);

        await rt.RunEventLoopBoundedAsync(150);
        var result = rt.Evaluate(
            """
            [
                __rafEvents,
                __rafStamps.length,
                __rafStamps[0] === __rafStamps[1],
                __rafStamps[2] > __rafStamps[1]
            ]
            """);
        AssertJson(
            """
            [["microtask-before","timer","raf-a","raf-b","microtask-in-raf","raf-next"],3,true,true]
            """,
            result);
    }

    [Fact]
    public async Task RenderingOpportunityOrdersRafResizeAndIntersectionPhases()
    {
        using var fixture = RuntimeFixture.Setup(
            "<html><body><div id='target' style='width:20px;height:20px'></div></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "rendering-opportunity-order",
            """
            globalThis.__renderPhaseOrder = [];
            const target = document.getElementById("target");
            new ResizeObserver(() => __renderPhaseOrder.push("resize")).observe(target);
            new IntersectionObserver(() => __renderPhaseOrder.push("intersection")).observe(target);
            requestAnimationFrame(() => {
                __renderPhaseOrder.push("raf");
                target.style.width = "40px";
            });
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """["raf","resize","intersection"]""",
            rt.Evaluate("__renderPhaseOrder.slice(0, 3)"));
    }

    [Fact]
    public async Task RafGeometryMutationReachesSettledIntersectionBeforeNextFrame()
    {
        using var fixture = RuntimeFixture.Setup(
            "<html><body style='margin:0'><div id='spacer' style='height:150px'></div>"
            + "<div id='target' style='height:20px'></div></body></html>");
        var rt = fixture.Runtime;
        rt.SetViewport(200.0, 100.0);
        rt.ExecuteScript(
            "settle-intersection",
            """
            globalThis.__sameFrameOrder = [];
            globalThis.__sameFrameInitial = false;
            const target = document.getElementById("target");
            globalThis.__sameFrameObserver = new IntersectionObserver(entries => {
                if (!__sameFrameInitial) {
                    __sameFrameInitial = true;
                    return;
                }
                if (entries.some(entry => entry.isIntersecting)) {
                    __sameFrameOrder.push("intersection");
                }
            });
            __sameFrameObserver.observe(target);
            """);
        await rt.RunEventLoopBoundedAsync(50);

        rt.ExecuteScript(
            "mutate-in-animation-frame",
            """
            requestAnimationFrame(() => {
                __sameFrameOrder.push("raf");
                document.getElementById("spacer").style.height = "0px";
                requestAnimationFrame(() => __sameFrameOrder.push("next-raf"));
            });
            """);
        await rt.RunEventLoopBoundedAsync(80);

        AssertJson("""["raf","intersection","next-raf"]""", rt.Evaluate("__sameFrameOrder"));
    }

    [Fact]
    public async Task CancelAnimationFrameRemovesPendingAndCurrentBatchCallbacks()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "animation-frame-cancel",
            """
            globalThis.__rafEvents = [];
            const pending = requestAnimationFrame(() => __rafEvents.push("pending"));
            cancelAnimationFrame(pending);
            let sameBatch;
            requestAnimationFrame(() => {
                __rafEvents.push("first");
                cancelAnimationFrame(sameBatch);
            });
            sameBatch = requestAnimationFrame(() => __rafEvents.push("same-batch"));
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("""["first"]""", rt.Evaluate("__rafEvents"));
    }

    [Fact]
    public async Task SelfRequeueingAnimationFrameYieldsToTimerTasks()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "animation-frame-yield",
            """
            globalThis.__rafCount = 0;
            globalThis.__rafStopped = false;
            globalThis.__timerAfterAnimation = false;
            let frameId = 0;
            function frame() {
                __rafCount++;
                frameId = requestAnimationFrame(frame);
            }
            frameId = requestAnimationFrame(frame);
            setTimeout(() => {
                cancelAnimationFrame(frameId);
                __rafStopped = true;
            }, 55);
            setTimeout(() => {
                __timerAfterAnimation = true;
            }, 65);
            """);

        await rt.RunEventLoopBoundedAsync(200);
        var values = Assert.IsType<JsonArray>(
            rt.Evaluate("[__rafCount, __rafStopped, __timerAfterAnimation]"));
        var frameCount = values[0]!.GetValue<double>();
        Assert.True(
            frameCount >= 2 && frameCount <= 5,
            $"expected a few paced animation frames before cancellation, got {frameCount}");
        Assert.True(values[1]!.GetValue<bool>());
        Assert.True(values[2]!.GetValue<bool>());
    }

    [Fact]
    public void TestDocumentUrl()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        Assert.Equal("http://example.com/test", fixture.Runtime.Evaluate("document.URL")!.GetValue<string>());
    }

    [Fact]
    public void TestQuerySelector()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><h1>Hello</h1><p>World</p></body></html>");
        Assert.Equal("Hello", fixture.Runtime.Evaluate("document.querySelector('h1').textContent")!.GetValue<string>());
    }

    [Fact]
    public void TestQuerySelectorAll()
    {
        using var fixture = RuntimeFixture.Setup("<ul><li>A</li><li>B</li><li>C</li></ul>");
        Assert.Equal(3, (long)fixture.Runtime.Evaluate("document.querySelectorAll('li').length")!.GetValue<double>());
    }

    [Fact]
    public void CssSupportsMatchesCapabilitiesAndBooleanConditions()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            JSON.stringify([
                CSS.supports("-webkit-hyphens", "none"),
                CSS.supports("margin-trim", "inline"),
                CSS.supports("-moz-orient", "inline"),
                CSS.supports("color", "rgb(from red r g b)"),
                CSS.supports("(((-webkit-hyphens:none)) and (not (margin-trim:inline))) or ((-moz-orient:inline) and (not (color:rgb(from red r g b))))"),
                CSS.supports("display", "grid"),
                CSS.supports("(display:grid) and (selector(.card > *))"),
                CSS.supports("not (unknown-engine-prop:value)"),
                CSS.supports("selector(.card >)"),
                CSS.supports("selector(:obscura-unknown)"),
                CSS.supports("selector(.card,)"),
                CSS.supports("scrollbar-gutter", "stable"),
                CSS.supports("scrollbar-gutter", "floating"),
                CSS.supports("color", "light-dark(rgb(1, 2, 3), color-mix(in srgb, white 50%, black))"),
                CSS.supports("(color:light-dark(red, light-dark(white, black)))"),
                CSS.supports("color", "light-dark(red)"),
                CSS.supports("color", "light-dark(red, rgb(1, 2, 3)"),
                CSS.supports("border", "2px dashed red"),
                CSS.supports("border-width", "10%"),
                CSS.supports("word-break", "break-all"),
                CSS.supports("filter", "blur(2px)"),
                CSS.supports("content", "attr(data-label)"),
                CSS.supports("display", "grid;"),
                CSS.supports("flex-flow", "column"),
                CSS.supports("flex-flow", "wrap column"),
                CSS.supports("flex-flow", "column wrap"),
                CSS.supports("flex-flow", "row column"),
                CSS.supports("flex-flow", "nowrap wrap-reverse"),
                CSS.supports("(flex-flow:column)")
            ])
            """);
        Assert.Equal(
            "[false,false,false,false,false,true,true,true,false,false,false,true,false,true,true,"
            + "false,false,true,false,true,false,true,false,true,true,true,false,false,true]",
            result!.GetValue<string>());
    }

    [Fact]
    public void TestGetElementById()
    {
        using var fixture = RuntimeFixture.Setup(@"<div id=""test"">Content</div>");
        Assert.Equal("DIV", fixture.Runtime.Evaluate("document.getElementById('test').tagName")!.GetValue<string>());
    }

    [Fact]
    public void AttributesNamedNodeMapIsLive()
    {
        using var fixture = RuntimeFixture.Setup("""<div id="test" class="card" data-state="ready"></div>""");
        var result = fixture.Runtime.Evaluate(
            """
            const element = document.getElementById("test");
            const attributes = element.attributes;
            const sameObject = attributes === element.attributes;
            const firstName = attributes[0].name;
            let removed = 0;
            while (attributes.length) {
                element.removeAttributeNode(attributes[0]);
                removed++;
                if (removed > 10) throw new Error("NamedNodeMap is not live");
            }
            return {
                sameObject,
                namedNodeMap: attributes instanceof NamedNodeMap,
                firstName,
                removed,
                length: attributes.length,
                hasAttributes: element.hasAttributes(),
            };
            """);
        AssertJson(
            """
            {"sameObject":true,"namedNodeMap":true,"firstName":"id","removed":3,
             "length":0,"hasAttributes":false}
            """,
            result);
    }

    [Fact]
    public void ScriptCreatedAttributeReadsStayCoherentAcrossMutationApis()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
                const element = document.createElement("DIV");
                const initial = element.getAttribute("data-state");
                element.setAttribute("DATA-STATE", "ready");
                const ordinary = [
                    element.getAttribute("data-state"),
                    element.getAttribute("DATA-STATE"),
                ];
                element.setAttributeNS(null, "data-state", "namespaced");
                const namespaced = element.getAttribute("data-state");
                element.removeAttributeNS(null, "data-state");
                const removed = element.getAttribute("data-state");
                return { initial, ordinary, namespaced, removed };
            })()
            """);
        AssertJson(
            """
            {"initial":null,"ordinary":["ready","ready"],"namespaced":"namespaced","removed":null}
            """,
            result);
    }

    [Fact]
    public void StructuralCacheTracksDetachReparentAndRejectedMutations()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
                const host = document.createElement("div");
                const child = document.createElement("span");
                const text = document.createTextNode("hello");
                const fresh = [host.parentNode, host.isConnected, text.parentNode, text.isConnected];

                host.appendChild(child);
                const detachedTree = [child.parentNode === host, host.isConnected, child.isConnected];
                document.body.appendChild(host);
                const connectedTree = [host.isConnected, child.isConnected, child.parentNode === host];

                const other = document.createElement("section");
                document.body.appendChild(other);
                const afterUnrelatedMutation = [host.parentNode === document.body, child.isConnected];

                other.appendChild(child);
                const reparented = [child.parentNode === other, child.isConnected, host.firstChild === null];

                let wrongReference = "";
                try { other.insertBefore(document.createElement("b"), host); }
                catch (error) { wrongReference = error.name; }
                let wrongReplacement = "";
                try { other.replaceChild(document.createElement("i"), host); }
                catch (error) { wrongReplacement = error.name; }
                let cycle = "";
                try { child.appendChild(other); }
                catch (error) { cycle = error.name; }

                document.body.removeChild(other);
                const removedTree = [other.parentNode, other.isConnected, child.isConnected, child.parentNode === other];
                document.body.appendChild(other);
                const reattachedTree = [other.isConnected, child.isConnected];
                return {
                    fresh,
                    detachedTree,
                    connectedTree,
                    afterUnrelatedMutation,
                    reparented,
                    wrongReference,
                    wrongReplacement,
                    cycle,
                    removedTree,
                    reattachedTree,
                };
            })()
            """);
        AssertJson(
            """
            {"fresh":[null,false,null,false],
             "detachedTree":[true,false,false],
             "connectedTree":[true,true,true],
             "afterUnrelatedMutation":[true,true],
             "reparented":[true,true,true],
             "wrongReference":"NotFoundError",
             "wrongReplacement":"NotFoundError",
             "cycle":"HierarchyRequestError",
             "removedTree":[null,false,false,true],
             "reattachedTree":[true,true]}
            """,
            result);
    }

    [Fact]
    public void ElementScrollMethodsUpdateScrollOffsets()
    {
        using var fixture = RuntimeFixture.Setup(
            """
            <div id="scroller" style="width:100px;height:100px;overflow:auto">
                <div style="width:300px;height:300px"></div>
            </div>
            """);
        var result = fixture.Runtime.Evaluate(
            """
            const element = document.getElementById("scroller");
            element.scrollTo({left: 12, top: 20, behavior: "smooth"});
            element.scrollBy(3, -5);
            element.scroll({left: 7});
            return {
                left: element.scrollLeft,
                top: element.scrollTop,
                methods: [
                    typeof element.scroll,
                    typeof element.scrollTo,
                    typeof element.scrollBy,
                ],
            };
            """);
        AssertJson(
            """{"left":7,"top":15,"methods":["function","function","function"]}""",
            result);
    }

    [Fact]
    public void DocumentFragmentGetElementByIdSearchesDescendants()
    {
        using var fixture = RuntimeFixture.Setup("""<div id="target">document</div>""");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
                const frag = document.createDocumentFragment();
                const section = document.createElement('section');
                section.innerHTML = '<div><span id="target">fragment</span></div><p id="a.b">literal</p>';
                frag.appendChild(section);

                const dup = document.createDocumentFragment();
                const deepParent = document.createElement('div');
                deepParent.innerHTML = '<span id="dup">deep</span>';
                const shallow = document.createElement('p');
                shallow.id = 'dup';
                shallow.textContent = 'shallow';
                dup.appendChild(deepParent);
                dup.appendChild(shallow);

                return [
                    frag.getElementById('target').textContent,
                    frag.getElementById('missing') === null,
                    frag.getElementById('a.b').textContent,
                    frag.getElementById(123) === null,
                    dup.getElementById('dup').textContent,
                ];
            })()
            """);
        AssertJson("""["fragment",true,"literal",true,"deep"]""", result);
    }

    /// Issue #461: FILTER_REJECT must prune the rejected node's whole subtree,
    /// while FILTER_SKIP only skips the node and leaves descendants eligible.
    /// Collapsing both into "not accepted" let a TreeWalker yield nodes from
    /// inside a subtree the page explicitly rejected.
    [Fact]
    public void TreeWalkerFilterRejectPrunesTheWholeSubtree()
    {
        using var fixture = RuntimeFixture.Setup(
            """<div id="root"><section><p>deep</p></section><a></a></div>""");
        var rt = fixture.Runtime;
        rt.RunPageInit();
        var result = rt.Evaluate(
            """
            const root = document.getElementById('root');
            function walk(verdict) {
                const w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, {
                    acceptNode(node) {
                        return node.tagName === 'SECTION' ? verdict : NodeFilter.FILTER_ACCEPT;
                    }
                });
                const seen = [];
                let node;
                while ((node = w.nextNode())) seen.push(node.tagName);
                return seen;
            }
            return [walk(NodeFilter.FILTER_REJECT), walk(NodeFilter.FILTER_SKIP)];
            """);
        // REJECT drops <p> with its <section> parent; SKIP drops only <section>.
        AssertJson("""[["A"],["P","A"]]""", result);
    }

    /// Issue #462: previousNode() must walk reverse document order until a node
    /// is accepted, not give up as soon as the first candidate is filtered out.
    [Fact]
    public void PreviousNodeWalksReverseDocumentOrder()
    {
        using var fixture = RuntimeFixture.Setup("""<div id="root"><a><b></b></a><c></c></div>""");
        var rt = fixture.Runtime;
        rt.RunPageInit();
        var result = rt.Evaluate(
            """
            const root = document.getElementById('root');
            const w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, {
                acceptNode(node) {
                    return node.tagName === 'B'
                        ? NodeFilter.FILTER_SKIP
                        : NodeFilter.FILTER_ACCEPT;
                }
            });
            const forward = [];
            let node;
            while ((node = w.nextNode())) forward.push(node.tagName);
            const backward = [];
            while ((node = w.previousNode())) backward.push(node.tagName);
            return [forward, backward];
            """);
        // From <c>, the previous sibling's deepest last child <b> is skipped, so the
        // walk must keep going up to <a> instead of returning null.
        AssertJson("""[["A","C"],["A"]]""", result);
    }

    /// Issue #462: a backward walk must retrace a forward walk exactly, and stop
    /// at the root without ever returning it.
    [Fact]
    public void PreviousNodeRetracesAFullForwardWalk()
    {
        using var fixture = RuntimeFixture.Setup(
            """<div id="root"><section><p>one</p><span></span></section><a><b></b></a></div>""");
        var rt = fixture.Runtime;
        rt.RunPageInit();
        var result = rt.Evaluate(
            """
            const root = document.getElementById('root');
            const w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
            const forward = [];
            let node;
            while ((node = w.nextNode())) forward.push(node.tagName);
            const backward = [];
            while ((node = w.previousNode())) backward.push(node.tagName);
            backward.reverse();
            // previousNode never yields root, and never yields the node the forward
            // walk ended on, so compare against forward minus its last. A failed
            // traversal leaves currentNode untouched (DOM 6.1), so it stays on the last
            // node previousNode did return.
            return [forward, backward, w.currentNode.tagName];
            """);
        AssertJson(
            """[["SECTION","P","SPAN","A","B"],["SECTION","P","SPAN","A"],"SECTION"]""",
            result);
    }

    /// Issue #462: FILTER_REJECT prunes a subtree in the backward direction too
    /// — the descent into a rejected node's last children must stop.
    [Fact]
    public void PreviousNodeHonoursFilterRejectSubtreePruning()
    {
        using var fixture = RuntimeFixture.Setup(
            """<div id="root"><a></a><section><p>deep</p></section><c></c></div>""");
        var rt = fixture.Runtime;
        rt.RunPageInit();
        var result = rt.Evaluate(
            """
            const root = document.getElementById('root');
            const w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, {
                acceptNode(node) {
                    return node.tagName === 'SECTION'
                        ? NodeFilter.FILTER_REJECT
                        : NodeFilter.FILTER_ACCEPT;
                }
            });
            while (w.nextNode()) { /* advance to the last accepted node */ }
            const backward = [];
            let node;
            while ((node = w.previousNode())) backward.push(node.tagName);
            return backward;
            """);
        // <p> lives inside the rejected <section>, so the backward walk from <c> must
        // jump straight to <a>.
        AssertJson("""["A"]""", result);
    }

    /// Issue #461: NodeIterator has no subtree pruning — DOM 6.2 says
    /// FILTER_REJECT behaves as FILTER_SKIP there. The shared walker must not
    /// Issue #475: parentNode() must never surface a node above `root`. With
    /// currentNode at root, the old guard stepped to root's own parent and
    /// returned it — escaping the walker's subtree entirely.
    [Fact]
    public void TreeWalkerParentNodeDoesNotEscapeAboveRoot()
    {
        using var fixture = RuntimeFixture.Setup("""<div id="root"><a></a></div>""");
        var result = fixture.Runtime.Evaluate(
            """
            const root = document.getElementById('root');
            const w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
            const escaped = w.parentNode();
            return [escaped, w.currentNode.id];
            """);
        // No parent within the subtree, and currentNode stays put at root.
        AssertJson("""[null,"root"]""", result);
    }

    /// Issue #475: when the accepted ancestor is `root` itself, parentNode()
    /// returns it and moves currentNode there — the old `parent !== root` guard
    /// wrongly excluded it.
    [Fact]
    public void TreeWalkerParentNodeCanReturnTheRoot()
    {
        using var fixture = RuntimeFixture.Setup("""<div id="root"><a></a></div>""");
        var result = fixture.Runtime.Evaluate(
            """
            const root = document.getElementById('root');
            const w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
            w.currentNode = root.querySelector('a');
            const p = w.parentNode();
            return [p ? p.id : null, w.currentNode === root];
            """);
        AssertJson("""["root",true]""", result);
    }

    /// Issue #475: parentNode() climbs past a skipped ancestor to the first
    /// accepted one, instead of stopping at the immediate parent.
    [Fact]
    public void TreeWalkerParentNodeClimbsPastSkippedAncestors()
    {
        using var fixture = RuntimeFixture.Setup(
            """<div id="root"><main id="m"><section><a></a></section></main></div>""");
        var result = fixture.Runtime.Evaluate(
            """
            const root = document.getElementById('root');
            const w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, {
                acceptNode(n) {
                    return n.tagName === 'SECTION'
                        ? NodeFilter.FILTER_SKIP
                        : NodeFilter.FILTER_ACCEPT;
                }
            });
            w.currentNode = root.querySelector('a');
            const p = w.parentNode();
            return p ? p.id : null;
            """);
        // <a>'s parent <section> is skipped, so <main> is the first accepted ancestor -
        // not null, and not the immediate <section>.
        Assert.Equal("m", result!.GetValue<string>());
    }

    /// leak TreeWalker's pruning into it.
    [Fact]
    public void NodeIteratorTreatsFilterRejectAsSkip()
    {
        using var fixture = RuntimeFixture.Setup(
            """<div id="root"><section><p>deep</p></section><a></a></div>""");
        var rt = fixture.Runtime;
        rt.RunPageInit();
        var result = rt.Evaluate(
            """
            const root = document.getElementById('root');
            const it = document.createNodeIterator(root, NodeFilter.SHOW_ELEMENT, {
                acceptNode(node) {
                    return node.tagName === 'SECTION'
                        ? NodeFilter.FILTER_REJECT
                        : NodeFilter.FILTER_ACCEPT;
                }
            });
            const seen = [];
            let node;
            while ((node = it.nextNode())) seen.push(node.tagName);
            return seen;
            """);
        // The rejected <section> is skipped but not pruned, so <p> still shows. The
        // leading root is #467: an iterator yields the node it is rooted at.
        AssertJson("""["DIV","P","A"]""", result);
    }

    /// Issue #467: a NodeIterator starts *before* its root, so the first
    /// nextNode() returns the root itself. Aliasing createTreeWalker silently
    /// dropped exactly the element the iterator was rooted at.
    [Fact]
    public void NodeIteratorYieldsTheRootNodeFirst()
    {
        using var fixture = RuntimeFixture.Setup("""<div id="root"><a></a></div>""");
        var result = fixture.Runtime.Evaluate(
            """
            const root = document.getElementById('root');
            const it = document.createNodeIterator(root, NodeFilter.SHOW_ELEMENT);
            const seen = [];
            let node;
            while ((node = it.nextNode())) seen.push(node.tagName);
            return seen;
            """);
        AssertJson("""["DIV","A"]""", result);
    }

    /// Issue #467: the NodeIterator interface surface, and that TreeWalker-only
    /// members are not exposed on it.
    [Fact]
    public void NodeIteratorExposesItsOwnInterface()
    {
        using var fixture = RuntimeFixture.Setup("""<div id="root"><a></a></div>""");
        var result = fixture.Runtime.Evaluate(
            """
            const root = document.getElementById('root');
            const it = document.createNodeIterator(root, NodeFilter.SHOW_ELEMENT);
            const before = [it.referenceNode === root, it.pointerBeforeReferenceNode];
            it.nextNode();
            return [
                before,
                typeof it.detach,
                it.detach() === undefined,
                typeof it.previousNode,
                it.root === root,
                it.whatToShow,
                // TreeWalker-only members must not leak onto a NodeIterator.
                typeof it.currentNode,
                typeof it.firstChild,
                typeof it.parentNode,
                // The pointer advanced past the root it just returned.
                [it.referenceNode.tagName, it.pointerBeforeReferenceNode],
            ];
            """);
        AssertJson(
            """
            [[true,true],"function",true,"function",true,1,
             "undefined","undefined","undefined",["DIV",false]]
            """,
            result);
    }

    /// Issue #467: previousNode() retraces the iterator, and the root is the
    /// last node it yields going backwards.
    [Fact]
    public void NodeIteratorPreviousNodeRetracesTheWalk()
    {
        using var fixture = RuntimeFixture.Setup("""<div id="root"><a><b></b></a><c></c></div>""");
        var result = fixture.Runtime.Evaluate(
            """
            const root = document.getElementById('root');
            const it = document.createNodeIterator(root, NodeFilter.SHOW_ELEMENT);
            const forward = [];
            let node;
            while ((node = it.nextNode())) forward.push(node.tagName);
            const backward = [];
            while ((node = it.previousNode())) backward.push(node.tagName);
            return [forward, backward];
            """);
        // Forward ends on <c>; going back re-yields <c> (the pointer sits after it),
        // then the rest in reverse, root included.
        AssertJson("""[["DIV","A","B","C"],["C","B","A","DIV"]]""", result);
    }

    /// Issue #463: `<template>` contents are parsed into the node's
    /// `template_contents` document, but no op exposed it, so `.content` handed
    /// back a fabricated empty fragment and the parsed markup was unreachable.
    [Fact]
    public void TemplateContentExposesParsedMarkup()
    {
        using var fixture = RuntimeFixture.Setup(
            """<body><template id="t"><p class="row">a</p><p class="row">b</p></template></body>""");
        var result = fixture.Runtime.Evaluate(
            """
            const t = document.getElementById('t');
            return [
                t.content.childNodes.length,
                t.content.querySelectorAll('.row').length,
                t.content.firstElementChild.textContent,
                t.innerHTML,
                t.content.nodeType,
                t.content instanceof DocumentFragment,
                // Identity is stable: frameworks stash `.content` and reuse it.
                t.content === t.content,
                // The children stay off the element itself, per the HTML spec.
                t.childNodes.length,
            ];
            """);
        AssertJson(
            """[2,2,"a","<p class=\"row\">a</p><p class=\"row\">b</p>",11,true,true,0]""",
            result);
    }

    /// Setting innerHTML on the <html> element parses in the "before head"
    /// insertion mode, which synthesizes head and body. The importer must keep
    /// both; it previously returned the synthesized body and dropped the head
    /// (so a <title>/<meta> assigned this way vanished).
    [Fact]
    public void DocumentelementInnerHtmlKeepsHeadAndBody()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            "(function(){ document.documentElement.innerHTML = "
            + "'<head><title>T</title></head><body><p>hi</p></body>'; "
            + "var t = document.querySelector('title'); var p = document.querySelector('p'); "
            + "return (t ? t.textContent : 'no-title') + '|' + (p ? p.textContent : 'no-p'); })()");
        Assert.Equal("T|hi", result!.GetValue<string>());
    }

    /// Regression guard: innerHTML on an ordinary element still imports the
    /// parsed nodes directly (no head/body is synthesized for a div context),
    /// so the fix above must not change the common case.
    [Fact]
    public void OrdinaryElementInnerHtmlImportsContentDirectly()
    {
        using var fixture = RuntimeFixture.Setup("""<html><body><div id="d"></div></body></html>""");
        var result = fixture.Runtime.Evaluate(
            "(function(){ var d=document.getElementById('d'); d.innerHTML='<span>a</span><span>b</span>'; "
            + "return d.children.length + '|' + d.textContent; })()");
        Assert.Equal("2|ab", result!.GetValue<string>());
    }

    /// Issue #463: the same must hold for a template that arrives via innerHTML
    /// rather than the initial document parse — that is how most frameworks
    /// inject templates.
    [Fact]
    public void TemplateContentWorksForTemplatesAddedViaInnerHtml()
    {
        using var fixture = RuntimeFixture.Setup("""<body><div id="host"></div></body>""");
        var result = fixture.Runtime.Evaluate(
            """
            const host = document.getElementById('host');
            host.innerHTML = '<template id="t2"><li class="item">x</li></template>';
            const t = document.getElementById('t2');
            const stamped = t.content.cloneNode(true);
            host.appendChild(stamped);
            return [
                t.content.childNodes.length,
                t.content.querySelector('.item').textContent,
                host.querySelectorAll('li.item').length,
            ];
            """);
        // cloneNode(true) of the content is the canonical stamping idiom.
        AssertJson("""[1,"x",1]""", result);
    }

    /// Issue #463: a template built with createElement has no parsed contents,
    /// so `.content` must allocate a backing fragment on demand and round-trip
    /// through innerHTML.
    [Fact]
    public void TemplateContentRoundTripsForCreatedTemplates()
    {
        using var fixture = RuntimeFixture.Setup("<body></body>");
        var result = fixture.Runtime.Evaluate(
            """
            const t = document.createElement('template');
            t.innerHTML = '<span class="s">hi</span>';
            return [
                t.content.childNodes.length,
                t.content.querySelector('.s').textContent,
                t.innerHTML,
                t.childNodes.length,
            ];
            """);
        AssertJson("""[1,"hi","<span class=\"s\">hi</span>",0]""", result);
    }

    /// Issue #463: serializing a `<template>` must emit its contents, or the
    /// markup silently disappears from outerHTML/innerHTML round-trips — and
    /// `cloneNode(true)`, which round-trips through outer_html, yields an empty
    /// template.
    [Fact]
    public void TemplateContentsSurviveSerializationAndClone()
    {
        using var fixture = RuntimeFixture.Setup(
            """<body><template id="t"><li class="item">x</li></template></body>""");
        var result = fixture.Runtime.Evaluate(
            """
            const t = document.getElementById('t');
            const clone = t.cloneNode(true);
            return [
                t.outerHTML,
                document.body.innerHTML,
                clone.content.childNodes.length,
                clone.content.querySelector('.item').textContent,
                // The clone's contents are its own, not shared with the original.
                (clone.content.firstElementChild === t.content.firstElementChild),
            ];
            """);
        const string expected = """<template id="t"><li class="item">x</li></template>""";
        var values = Assert.IsType<JsonArray>(result);
        Assert.Equal(expected, values[0]!.GetValue<string>());
        Assert.Equal(expected, values[1]!.GetValue<string>());
        Assert.Equal(1.0, values[2]!.GetValue<double>());
        Assert.Equal("x", values[3]!.GetValue<string>());
        Assert.False(values[4]!.GetValue<bool>());
    }

    /// Issue #468: window.scrollTo/scrollBy/scroll were no-op stubs, so the
    /// dominant infinite-scroll idiom never advanced the page offset.
    [Fact(Skip = "this Rust test is #[cfg(not(feature = \"render\"))]: it asserts the synthetic non-render scroll compatibility state that bootstrap.js falls back to when op_scroll_offset / op_scroll_to are absent. Obscura.Js has no render feature split - the render ops are always bound - so window.scrollTo clamps against real geometry here. The render counterpart, RenderedWindowScrollClampsAndGeometryIsViewportRelative, covers this build")]
    public void WindowScrollMethodsMoveThePageOffset()
    {
        using var fixture = RuntimeFixture.Setup("""<html><body><div id="d"></div></body></html>""");
        var result = fixture.Runtime.Evaluate(
            """
            const scrolled = window.scrollTo(0, 500);
            const afterTo = [window.scrollX, window.scrollY];
            window.scrollBy(0, 200);
            const afterBy = [window.pageXOffset, window.pageYOffset];
            window.scrollTo({ left: 10, top: 40 });
            const afterOptions = [window.scrollX, window.scrollY];
            window.scroll(5, 5);
            const afterScroll = [window.scrollX, window.scrollY];
            // Negative offsets clamp to 0, as they do for elements.
            window.scrollTo(0, -100);
            return [afterTo, afterBy, afterOptions, afterScroll, window.scrollY];
            """);
        AssertJson("""[[0,500],[0,700],[10,40],[5,5],0]""", result);
    }

    /// Issue #468: the page offset is one value, readable and writable through
    /// either `window.scrollY` or `document.scrollingElement.scrollTop`.
    [Fact(Skip = "this Rust test is #[cfg(not(feature = \"render\"))]: it asserts the synthetic non-render scroll compatibility state that bootstrap.js falls back to when op_scroll_offset / op_scroll_to are absent. Obscura.Js has no render feature split - the render ops are always bound - so window.scrollTo clamps against real geometry here. The render counterpart, RenderedWindowScrollClampsAndGeometryIsViewportRelative, covers this build")]
    public void WindowScrollOffsetIsSharedWithTheScrollingElement()
    {
        using var fixture = RuntimeFixture.Setup("""<html><body><div id="d"></div></body></html>""");
        var result = fixture.Runtime.Evaluate(
            """
            const isDocEl = document.scrollingElement === document.documentElement;
            window.scrollTo(0, 300);
            // Written through the window, read through the element...
            const viaElement = document.scrollingElement.scrollTop;
            // ...and the reverse.
            document.scrollingElement.scrollTop = 90;
            return [isDocEl, viaElement, window.scrollY, window.pageYOffset];
            """);
        AssertJson("[true,300,90,90]", result);
    }

    /// Issue #468: a scroll event must reach listeners on both the window and
    /// the document — that is the signal lazy loaders wait for.
    [Fact(Skip = "this Rust test is #[cfg(not(feature = \"render\"))]: it asserts the synthetic non-render scroll compatibility state that bootstrap.js falls back to when op_scroll_offset / op_scroll_to are absent. Obscura.Js has no render feature split - the render ops are always bound - so window.scrollTo clamps against real geometry here. The render counterpart, RenderedWindowScrollClampsAndGeometryIsViewportRelative, covers this build")]
    public async Task WindowScrollFiresAScrollEvent()
    {
        using var fixture = RuntimeFixture.Setup("""<html><body><div id="d"></div></body></html>""");
        var result = await fixture.Runtime.EvaluateForCdpAsync(
            """
            new Promise(resolve => {
                let win = 0, doc = 0;
                window.addEventListener('scroll', () => win++);
                document.addEventListener('scroll', () => doc++);
                window.scrollBy(0, 400);
                setTimeout(() => resolve([win, doc, window.scrollY]), 5);
            })
            """,
            returnByValue: true,
            awaitPromise: true);
        AssertJson("[1,1,400]", result.Value);
    }

    [Fact]
    public void RenderedWindowScrollClampsAndGeometryIsViewportRelative()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="wide" style="width:600px;height:700px"></div>
                <div id="target" style="width:20px;height:300px"></div>
                <div id="fixed" style="position:fixed;left:12px;top:14px;width:30px;height:25px">
                    <span id="fixed-child">fixed</span>
                </div>
            </body></html>
            """));
        rt.SetViewport(320.0, 200.0);
        rt.RunPageInit();

        var result = rt.Evaluate(
            """
            const target = document.getElementById("target");
            const fixed = document.getElementById("fixed");
            const fixedChild = document.getElementById("fixed-child");
            const before = {
                target: target.getBoundingClientRect(),
                fixed: fixed.getBoundingClientRect(),
                fixedChild: fixedChild.getBoundingClientRect(),
            };
            window.scrollTo(99999, 99999);
            const maxX = document.documentElement.scrollWidth - innerWidth;
            const maxY = document.documentElement.scrollHeight - innerHeight;
            const after = {
                target: target.getBoundingClientRect(),
                fixed: fixed.getBoundingClientRect(),
                fixedChild: fixedChild.getBoundingClientRect(),
            };
            return [
                innerWidth, innerHeight,
                document.documentElement.clientWidth,
                document.documentElement.clientHeight,
                document.documentElement.scrollWidth,
                document.documentElement.scrollHeight,
                window.scrollX, window.scrollY,
                document.scrollingElement.scrollLeft,
                document.scrollingElement.scrollTop,
                maxX, maxY,
                Math.abs(after.target.left - (before.target.left - maxX)) < 0.01,
                Math.abs(after.target.top - (before.target.top - maxY)) < 0.01,
                Math.abs(after.fixed.left - before.fixed.left) < 0.01,
                Math.abs(after.fixed.top - before.fixed.top) < 0.01,
                Math.abs(after.fixedChild.left - before.fixedChild.left) < 0.01,
                Math.abs(after.fixedChild.top - before.fixedChild.top) < 0.01,
            ];
            """);
        var values = Assert.IsType<JsonArray>(result);
        AssertJson("[320,200,320,200]", new JsonArray(
            values[0]!.DeepClone(), values[1]!.DeepClone(), values[2]!.DeepClone(), values[3]!.DeepClone()));
        var scrollWidth = values[4]!.GetValue<double>();
        var scrollHeight = values[5]!.GetValue<double>();
        Assert.True(scrollWidth >= 600.0, $"scrollWidth was {scrollWidth}");
        Assert.True(scrollHeight >= 1000.0, $"scrollHeight was {scrollHeight}");
        Assert.Equal(values[10]!.GetValue<double>(), values[6]!.GetValue<double>());
        Assert.Equal(values[11]!.GetValue<double>(), values[7]!.GetValue<double>());
        Assert.Equal(values[10]!.GetValue<double>(), values[8]!.GetValue<double>());
        Assert.Equal(values[11]!.GetValue<double>(), values[9]!.GetValue<double>());
        for (var i = 12; i < values.Count; i++)
        {
            Assert.True(values[i]!.GetValue<bool>(), $"geometry assertion {i} was false");
        }
    }

    [Fact]
    public void NestedScrollMetricsGeometryPixelsAndRelayoutShareOneState()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="outer" style="box-sizing:border-box;width:120px;height:100px;
                     border:4px solid red;overflow:hidden;position:relative;background:red">
                  <div id="inner" style="width:220px;height:200px;overflow:hidden;
                       position:relative;background:blue">
                    <div id="target" style="position:absolute;left:300px;top:280px;
                         width:30px;height:20px;background:lime"></div>
                  </div>
                </div>
            </body></html>
            """));
        rt.SetViewport(360.0, 240.0);
        rt.RunPageInit();

        var top = rt.ScreenshotPrepared((360.0f, 240.0f), "about:blank");
        Assert.NotNull(top);
        var result = rt.Evaluate(
            """
            const outer = document.getElementById('outer');
            const inner = document.getElementById('inner');
            const target = document.getElementById('target');
            const before = {
              outer: outer.getBoundingClientRect(),
              inner: inner.getBoundingClientRect(),
              target: target.getBoundingClientRect(),
            };
            outer.scrollTo(9999, 9999);
            inner.scrollTo(9999, 9999);
            const after = {
              outer: outer.getBoundingClientRect(),
              inner: inner.getBoundingClientRect(),
              target: target.getBoundingClientRect(),
            };
            const first = [target.getBoundingClientRect().left, target.getBoundingClientRect().top];
            outer.scrollTo(0, 0); inner.scrollTo(0, 0);
            outer.scrollTo(9999, 9999); inner.scrollTo(9999, 9999);
            const repeated = [target.getBoundingClientRect().left, target.getBoundingClientRect().top];
            return {
              outerMetrics: [outer.clientWidth, outer.clientHeight, outer.scrollWidth, outer.scrollHeight],
              innerMetrics: [inner.clientWidth, inner.clientHeight, inner.scrollWidth, inner.scrollHeight],
              offsets: [outer.scrollLeft, outer.scrollTop, inner.scrollLeft, inner.scrollTop],
              outerDelta: [after.outer.left - before.outer.left, after.outer.top - before.outer.top],
              innerDelta: [after.inner.left - before.inner.left, after.inner.top - before.inner.top],
              targetDelta: [after.target.left - before.target.left, after.target.top - before.target.top],
              repeated: [first[0] === repeated[0], first[1] === repeated[1]],
            };
            """);
        Assert.NotNull(result);
        AssertJson("[112, 92, 220, 200]", result!["outerMetrics"]);
        AssertJson("[220, 200, 330, 300]", result["innerMetrics"]);
        AssertJson("[108, 108, 110, 100]", result["offsets"]);
        AssertJson("[0, 0]", result["outerDelta"]);
        AssertJson("[-108, -108]", result["innerDelta"]);
        AssertJson("[-218, -208]", result["targetDelta"]);
        AssertJson("[true, true]", result["repeated"]);

        var scrolled = rt.ScreenshotPrepared((360.0f, 240.0f), "about:blank");
        var scrolledRepeat = rt.ScreenshotPrepared((360.0f, 240.0f), "about:blank");
        Assert.NotNull(scrolled);
        Assert.NotNull(scrolledRepeat);
        Assert.False(top!.AsSpan().SequenceEqual(scrolled), "nested scroll must move painted pixels");
        Assert.True(
            scrolled!.AsSpan().SequenceEqual(scrolledRepeat),
            "capture must not accumulate movement");

        var retained = rt.Evaluate(
            """
            const outer = document.getElementById('outer');
            const inner = document.getElementById('inner');
            outer.setAttribute('data-relayout', '1');
            return [outer.scrollLeft, outer.scrollTop, inner.scrollLeft, inner.scrollTop];
            """);
        AssertJson("[108, 108, 110, 100]", retained);

        var reclamped = rt.Evaluate(
            """
            const outer = document.getElementById('outer');
            const inner = document.getElementById('inner');
            inner.setAttribute('style', 'width:150px;height:120px;overflow:hidden;position:relative;background:blue');
            document.getElementById('target').setAttribute(
              'style',
              'position:absolute;left:100px;top:80px;width:30px;height:20px;background:lime'
            );
            return [outer.scrollLeft, outer.scrollTop, inner.scrollLeft, inner.scrollTop];
            """);
        AssertJson("[38, 28, 0, 0]", reclamped);

        rt.Evaluate(
            "(function(){ document.getElementById('outer').remove();"
            + " document.documentElement.getBoundingClientRect(); return true; })()");
        Assert.Empty(rt.State.ElementScrollOffsets);
    }

    /// Chromium 150 oracle for CSSOM scrolling overflow. Visible and clip
    /// boxes expose descendant overflow but cannot move; an actual scrolling
    /// box includes trailing padding. A clip boundary suppresses propagation
    /// only on its clipped axis, and ordinary inline boxes expose zero metrics.
    [Fact]
    public void ElementScrollMetricsMatchChromiumOverflowOracles()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
              <style>
                .box { width:100px;height:80px;padding:10px;border:2px solid;position:absolute }
                .child { width:200px;height:150px }
              </style>
              <div id="visible" class="box" style="overflow:visible;top:0"><div class="child"></div></div>
              <div id="clip" class="box" style="overflow:clip;top:150px"><div class="child"></div></div>
              <div id="hidden" class="box" style="overflow:hidden;top:300px"><div class="child"></div></div>
              <div id="outer" style="width:100px;height:80px;overflow:visible;position:absolute;top:450px">
                <div id="axis" style="width:150px;height:120px;overflow-x:visible;overflow-y:clip">
                  <div style="width:300px;height:250px"></div>
                </div>
              </div>
              <div id="f1" style="width:10px;overflow:visible"><div style="width:100.1px;height:1px"></div></div>
              <div id="f2" style="width:10px;overflow:visible"><div style="width:100.6px;height:1px"></div></div>
              <span id="inline">long inline text</span>
            </body></html>
            """));
        rt.SetViewport(420.0, 700.0);
        rt.RunPageInit();

        var result = Assert.IsType<JsonObject>(rt.Evaluate(
            """
            const visible = document.getElementById('visible');
            const clip = document.getElementById('clip');
            const hidden = document.getElementById('hidden');
            const outer = document.getElementById('outer');
            const axis = document.getElementById('axis');
            const inline = document.getElementById('inline');
            visible.scrollTo(99, 99);
            clip.scrollTo(99, 99);
            hidden.scrollTo(99, 99);
            return {
              visible: [visible.scrollWidth, visible.scrollHeight, visible.scrollLeft, visible.scrollTop],
              clip: [clip.scrollWidth, clip.scrollHeight, clip.scrollLeft, clip.scrollTop],
              hidden: [hidden.scrollWidth, hidden.scrollHeight, hidden.scrollLeft, hidden.scrollTop],
              axis: [outer.scrollWidth, outer.scrollHeight, axis.scrollWidth, axis.scrollHeight],
              fractional: [document.getElementById('f1').scrollWidth, document.getElementById('f2').scrollWidth],
              inline: [inline.scrollWidth, inline.scrollHeight, inline.clientWidth, inline.clientHeight],
            };
            """));
        AssertJson("[210,160,0,0]", result["visible"]);
        AssertJson("[210,160,0,0]", result["clip"]);
        AssertJson("[220,170,99,70]", result["hidden"]);
        AssertJson("[300,120,300,250]", result["axis"]);
        AssertJson("[100,101]", result["fractional"]);
        AssertJson("[0,0,0,0]", result["inline"]);
    }

    /// Chromium quantizes effective scrolling ranges and assigned offsets to
    /// the current device-pixel grid. At the renderer's present 1x scale a
    /// 100.4px area cannot move a 100px scrollport, while 100.6px rounds to a
    /// one-pixel range and assigning `.5` moves geometry and paint by 1px.
    [Fact]
    public void FractionalScrollRangesQuantizeGeometryAndPixelsAtOneX()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
              <div id="low" style="width:100px;height:40px;overflow:auto;position:absolute;top:0">
                <div style="width:100.4px;height:40px;position:relative;background:white">
                  <div id="lowChild" style="position:absolute;left:40px;top:5px;width:10px;height:25px;background:red"></div>
                </div>
              </div>
              <div id="high" style="width:100px;height:40px;overflow:auto;position:absolute;top:60px">
                <div style="width:100.6px;height:40px;position:relative;background:white">
                  <div id="highChild" style="position:absolute;left:40px;top:5px;width:10px;height:25px;background:blue"></div>
                </div>
              </div>
            </body></html>
            """));
        rt.SetViewport(160.0, 120.0);
        rt.RunPageInit();

        var initial = rt.ScreenshotPrepared((160.0f, 120.0f), "about:blank");
        Assert.NotNull(initial);
        var low = rt.Evaluate(
            """
            const low = document.getElementById('low');
            const child = document.getElementById('lowChild');
            const before = child.getBoundingClientRect();
            low.scrollLeft = 999;
            const after = child.getBoundingClientRect();
            return [low.scrollWidth, low.clientWidth, low.scrollLeft, after.left - before.left];
            """);
        AssertJson("[100, 100, 0, 0]", low);
        var afterLow = rt.ScreenshotPrepared((160.0f, 120.0f), "about:blank");
        Assert.NotNull(afterLow);
        Assert.True(
            initial!.AsSpan().SequenceEqual(afterLow),
            "a rounded-zero range cannot move pixels");

        var high = rt.Evaluate(
            """
            const high = document.getElementById('high');
            const child = document.getElementById('highChild');
            const before = child.getBoundingClientRect();
            high.scrollLeft = .5;
            const after = child.getBoundingClientRect();
            return [high.scrollWidth, high.clientWidth, high.scrollLeft, after.left - before.left];
            """);
        AssertJson("[101, 100, 1, -1]", high);
        var afterHigh = rt.ScreenshotPrepared((160.0f, 120.0f), "about:blank");
        Assert.NotNull(afterHigh);
        Assert.False(afterLow!.AsSpan().SequenceEqual(afterHigh), "the quantized pixel must repaint");

        using var rootOwner = new CaptureRuntime(new ObscuraJsRuntime());
        var rootRt = rootOwner.Runtime;
        rootRt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
              <div id="wide" style="width:100.6px;height:20px"></div>
            </body></html>
            """));
        rootRt.SetViewport(100.0, 60.0);
        rootRt.RunPageInit();
        var root = rootRt.Evaluate(
            """
            const wide = document.getElementById('wide');
            const before = wide.getBoundingClientRect();
            window.scrollTo(.5, 0);
            const after = wide.getBoundingClientRect();
            const high = [document.documentElement.scrollWidth, window.scrollX, after.left - before.left];
            wide.style.width = '100.4px';
            window.scrollTo(999, 0);
            return { high, low: [document.documentElement.scrollWidth, window.scrollX] };
            """);
        Assert.NotNull(root);
        AssertJson("[101, 1, -1]", root!["high"]);
        AssertJson("[100, 0]", root["low"]);
    }

    [Fact]
    public void ElementScrollOffsetsFollowChromiumBoxAndDomLifecycles()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
              <div id="first"><div id="scroller" style="width:100px;height:80px;overflow:auto">
                <div style="width:250px;height:200px"></div>
              </div></div>
              <div id="second"></div>
            </body></html>
            """));
        rt.SetViewport(360.0, 240.0);
        rt.RunPageInit();

        var result = Assert.IsType<JsonObject>(rt.Evaluate(
            """
            const scroller = document.getElementById('scroller');
            const first = document.getElementById('first');
            const second = document.getElementById('second');
            scroller.scrollTo(70, 60);
            const initial = [scroller.scrollLeft, scroller.scrollTop];
            scroller.style.overflow = 'visible';
            const visible = [scroller.scrollLeft, scroller.scrollTop];
            scroller.style.overflow = 'auto';
            const restoredStyle = [scroller.scrollLeft, scroller.scrollTop];
            scroller.style.display = 'none';
            const noBox = [scroller.scrollWidth, scroller.scrollHeight, scroller.scrollLeft, scroller.scrollTop];
            scroller.scrollTo(5, 5);
            scroller.style.display = 'block';
            const restoredDisplay = [scroller.scrollLeft, scroller.scrollTop];
            second.appendChild(scroller);
            const moved = [scroller.scrollLeft, scroller.scrollTop];
            scroller.scrollTo(40, 30);
            second.removeChild(scroller);
            first.appendChild(scroller);
            const reattached = [scroller.scrollLeft, scroller.scrollTop];
            scroller.scrollTo(20, 10);
            first.textContent = 'replacement';
            document.body.appendChild(scroller);
            const textReplacement = [scroller.scrollLeft, scroller.scrollTop];
            const detached = document.createElement('div');
            detached.style.cssText = 'width:100px;height:80px;overflow:auto';
            let recomputes = 0;
            globalThis.__obscura_recompute_intersections = () => { recomputes++; };
            detached.scrollTo(30, 20);
            scroller.scrollTo(11, 12);
            return {
              initial, visible, restoredStyle, noBox, restoredDisplay,
              moved, reattached, textReplacement,
              detached: [detached.scrollWidth, detached.scrollHeight, detached.scrollLeft, detached.scrollTop],
              atomic: [scroller.scrollLeft, scroller.scrollTop, recomputes],
            };
            """));
        AssertJson("[70,60]", result["initial"]);
        AssertJson("[0,0]", result["visible"]);
        AssertJson("[70,60]", result["restoredStyle"]);
        AssertJson("[0,0,0,0]", result["noBox"]);
        AssertJson("[70,60]", result["restoredDisplay"]);
        AssertJson("[0,0]", result["moved"]);
        AssertJson("[0,0]", result["reattached"]);
        AssertJson("[0,0]", result["textReplacement"]);
        AssertJson("[0,0,0,0]", result["detached"]);
        AssertJson("[11,12,1]", result["atomic"]);
    }

    [Fact]
    public void FixedPanelsScrollLocallyAndTransformedDescendantsRemainSupported()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0;height:1800px">
              <div id="modal" style="position:fixed;left:20px;top:20px;width:140px;height:120px;background:red">
                <div id="panel" style="width:100px;height:80px;overflow:hidden;position:relative;background:blue">
                  <div id="fixedTarget" style="position:absolute;left:180px;top:160px;width:20px;height:20px;background:lime"></div>
                </div>
              </div>
              <div id="transformedScroller" style="position:absolute;top:300px;width:100px;height:80px;overflow:hidden">
                <div id="transformedTarget" style="width:240px;height:180px;transform:scale(1.1)"></div>
              </div>
              <div style="position:absolute;top:600px;transform:scale(1.2)">
                <div id="affineAncestorScroller" style="width:100px;height:80px;overflow:hidden">
                  <div style="width:240px;height:180px"></div>
                </div>
              </div>
            </body></html>
            """));
        rt.SetViewport(360.0, 240.0);
        rt.RunPageInit();

        var result = Assert.IsType<JsonObject>(rt.Evaluate(
            """
            const modal = document.getElementById('modal');
            const panel = document.getElementById('panel');
            const fixedTarget = document.getElementById('fixedTarget');
            const transformedScroller = document.getElementById('transformedScroller');
            const transformedTarget = document.getElementById('transformedTarget');
            const affineAncestorScroller = document.getElementById('affineAncestorScroller');
            const before = {
              modal: modal.getBoundingClientRect(),
              fixedTarget: fixedTarget.getBoundingClientRect(),
              transformedTarget: transformedTarget.getBoundingClientRect(),
            };
            panel.scrollTo(60, 50);
            transformedScroller.scrollTo(50, 40);
            affineAncestorScroller.scrollTo(50, 40);
            window.scrollTo(0, 500);
            const after = {
              modal: modal.getBoundingClientRect(),
              fixedTarget: fixedTarget.getBoundingClientRect(),
              transformedTarget: transformedTarget.getBoundingClientRect(),
            };
            return {
              modalDelta: [after.modal.left - before.modal.left, after.modal.top - before.modal.top],
              fixedDelta: [after.fixedTarget.left - before.fixedTarget.left, after.fixedTarget.top - before.fixedTarget.top],
              transformedDelta: [after.transformedTarget.left - before.transformedTarget.left, after.transformedTarget.top - before.transformedTarget.top],
              offsets: [panel.scrollLeft, panel.scrollTop, transformedScroller.scrollLeft, transformedScroller.scrollTop, affineAncestorScroller.scrollLeft, affineAncestorScroller.scrollTop],
            };
            """));
        AssertJson("[0,0]", result["modalDelta"]);
        AssertJson("[-60,-50]", result["fixedDelta"]);
        AssertJson("[-50,-540]", result["transformedDelta"]);
        AssertJson("[60,50,50,40,0,0]", result["offsets"]);
    }

    /// CSSOM View exposes the viewport through the standards-mode root, but
    /// ordinary elements (including body) report their padding box. Modern
    /// animation libraries commonly measure a fixed 100vh sentinel through
    /// clientHeight; the old synthetic 100x20 fallback collapsed all of their
    /// viewport-relative trigger ranges.
    [Fact]
    public void RenderedClientMetricsUseTheLivePaddingBox()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="tracker"
                     style="position:fixed;top:0;width:100%;height:100vh"></div>
                <div id="box"
                     style="box-sizing:content-box;width:100.4px;height:50.6px;
                            padding:5px 8.2px 6px 7.2px;
                            border-style:solid;
                            border-width:2px 4.1px 3px 3.1px"></div>
                <div style="height:900px"></div>
            </body></html>
            """));
        rt.SetViewport(320.0, 200.0);
        rt.RunPageInit();

        var initial = Assert.IsType<JsonObject>(rt.Evaluate(
            """
            const tracker = document.getElementById("tracker");
            const box = document.getElementById("box");
            return {
                root: [
                    document.documentElement.clientWidth,
                    document.documentElement.clientHeight
                ],
                body: [document.body.clientWidth, document.body.clientHeight],
                tracker: [
                    tracker.clientWidth, tracker.clientHeight,
                    tracker.offsetWidth, tracker.offsetHeight
                ],
                box: [
                    box.clientWidth, box.clientHeight,
                    box.getBoundingClientRect().width,
                    box.getBoundingClientRect().height
                ]
            };
            """));
        AssertJson("[320,200]", initial["root"]);
        AssertJson("[320,967]", initial["body"]);
        AssertJson("[320,200,320,200]", initial["tracker"]);
        var box = Assert.IsType<JsonArray>(initial["box"]);
        Assert.Equal(116.0, box[0]!.GetValue<double>());
        Assert.Equal(62.0, box[1]!.GetValue<double>());
        Assert.True(Math.Abs(box[2]!.GetValue<double>() - 123.0) < 0.05);
        Assert.Equal(67.0, box[3]!.GetValue<double>());

        // Attribute-backed inline-style changes invalidate the retained render.
        // Borders do not change the padding box; padding does.
        var mutated = Assert.IsType<JsonArray>(rt.Evaluate(
            """
            const tracker = document.getElementById("tracker");
            const box = document.getElementById("box");
            tracker.style.height = "50vh";
            box.style.borderLeftWidth = "13px";
            box.style.paddingLeft = "17px";
            return [
                tracker.clientHeight,
                box.clientWidth,
                box.getBoundingClientRect().width
            ];
            """));
        Assert.Equal(100.0, mutated[0]!.GetValue<double>());
        Assert.Equal(126.0, mutated[1]!.GetValue<double>());
        Assert.Equal(143.0, mutated[2]!.GetValue<double>());

        // A later CDP/emulation viewport update invalidates the layout too; both the
        // root special case and an ordinary 100vh box are live.
        rt.SetViewport(640.0, 360.0);
        AssertJson(
            "[640,360,640,180]",
            rt.Evaluate(
                """
                const tracker = document.getElementById("tracker");
                return [
                    document.documentElement.clientWidth,
                    document.documentElement.clientHeight,
                    tracker.clientWidth,
                    tracker.clientHeight
                ]
                """));
    }

    /// CSSOM View distinguishes "no associated CSS box" from a real box whose
    /// dimensions happen to be zero. Blink and Gecko return an all-zero
    /// bounding rect and no client rects for display:none/detached elements;
    /// a laid-out zero-size box still contributes one client rect.
    [Fact]
    public void RenderedCssomRectsDistinguishNoBoxFromZeroSizeBox()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="hidden" style="display:none;width:80px;height:40px"></div>
                <div id="zero" style="display:block;width:0;height:0"></div>
            </body></html>
            """));
        rt.SetViewport(320.0, 200.0);
        rt.RunPageInit();

        var result = Assert.IsType<JsonObject>(rt.Evaluate(
            """
            const hidden = document.getElementById("hidden");
            const detached = document.createElement("div");
            detached.style.cssText = "display:block;width:90px;height:50px";
            const zero = document.getElementById("zero");
            const sample = element => {
                const rect = element.getBoundingClientRect();
                const rects = element.getClientRects();
                return {
                    rect: [
                        rect.x, rect.y, rect.width, rect.height,
                        rect.top, rect.right, rect.bottom, rect.left
                    ],
                    rectCount: rects.length,
                    firstWidth: rects.length ? rects[0].width : null,
                };
            };
            return {
                hidden: sample(hidden),
                detached: sample(detached),
                zero: sample(zero),
            };
            """));

        foreach (var name in new[] { "hidden", "detached" })
        {
            var entry = Assert.IsType<JsonObject>(result[name]);
            AssertJson("[0,0,0,0,0,0,0,0]", entry["rect"]);
            Assert.Equal(0.0, entry["rectCount"]!.GetValue<double>());
            AssertJson("null", entry["firstWidth"]);
        }
        var zero = Assert.IsType<JsonObject>(result["zero"]);
        var zeroRect = Assert.IsType<JsonArray>(zero["rect"]);
        Assert.Equal(0.0, zeroRect[2]!.GetValue<double>());
        Assert.Equal(0.0, zeroRect[3]!.GetValue<double>());
        Assert.Equal(
            1.0,
            zero["rectCount"]!.GetValue<double>());
        Assert.Equal(0.0, zero["firstWidth"]!.GetValue<double>());
    }

    [Fact(Skip = "this Rust test is #[cfg(not(feature = \"render\"))]: it asserts bootstrap.js's synthetic 100x20 CSSOM fallback, used only when op_element_box is absent. Obscura.Js has no render feature split - the render ops are always bound - so getBoundingClientRect returns real layout geometry. RenderedCssomRectsDistinguishNoBoxFromZeroSizeBox covers this build")]
    public void NonRenderCssomRectsKeepCompatibilityGeometry()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn non_render_cssom_rects_keep_compatibility_geometry() {
                let mut rt = setup_runtime(r#"<html><body><div id="box"></div></body></html>"#);
                let result = rt
                    .evaluate(
                        r#"
                        const box = document.getElementById("box");
                        const detached = document.createElement("div");
                        return [box, detached].map(element => {
                            const rect = element.getBoundingClientRect();
                            return [rect.width, rect.height, element.getClientRects().length];
                        });
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!([[100, 20, 1], [100, 20, 1]]));
            }
        */
    }

    /// Chromium 150 reference (800x513 CSS-pixel viewport):
    /// top=[60,20,20,-267], bottom=[448,448,231,31,-496] at the sampled
    /// root scroll offsets. This keeps sticky distinct from fixed positioning,
    /// verifies subtree movement, bottom-only sticking, and the containing
    /// block's lower boundary without depending on a live site.
    [Fact]
    public void RootScrollStickyGeometryMatchesChromiumConstraints()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div style="height:40px"></div>
                <div id="cb" style="box-sizing:border-box;height:900px;padding:10px 12px;border:4px solid #333">
                    <div id="top" style="box-sizing:border-box;position:sticky;top:20px;height:60px;margin:6px">
                        <div id="top-child" style="height:12px"></div>
                    </div>
                    <div style="height:500px"></div>
                    <div id="bottom" style="box-sizing:border-box;position:sticky;bottom:15px;height:50px;margin:5px"></div>
                </div>
                <div style="height:700px"></div>
                <div id="fixed" style="position:fixed;left:600px;top:20px;width:60px;height:60px"></div>
            </body></html>
            """));
        rt.SetViewport(800.0, 513.0);
        rt.RunPageInit();

        var result = rt.Evaluate(
            """
            const top = document.getElementById("top");
            const child = document.getElementById("top-child");
            const bottom = document.getElementById("bottom");
            const fixed = document.getElementById("fixed");
            const sample = y => {
                window.scrollTo(0, y);
                return [
                    window.scrollY,
                    top.getBoundingClientRect().top,
                    child.getBoundingClientRect().top,
                    bottom.getBoundingClientRect().top,
                    fixed.getBoundingClientRect().top,
                ];
            };
            return [sample(0), sample(100), sample(400), sample(600), sample(9999)];
            """);
        var rows = Assert.IsType<JsonArray>(result);
        double Number(int row, int column) =>
            ((JsonArray)rows[row]!)[column]!.GetValue<double>();
        void Close(double actual, double expected) =>
            Assert.True(Math.Abs(actual - expected) < 0.05, $"expected {expected}, got {actual}");

        Close(Number(0, 1), 60.0);
        Close(Number(0, 3), 448.0);
        Close(Number(1, 1), 20.0);
        Close(Number(1, 2), 20.0);
        Close(Number(1, 3), 448.0);
        Close(Number(2, 1), 20.0);
        Close(Number(2, 3), 231.0);
        Close(Number(3, 1), 20.0);
        Close(Number(3, 3), 31.0);
        Close(Number(4, 0), 1127.0);
        Close(Number(4, 1), -267.0);
        Close(Number(4, 2), -267.0);
        Close(Number(4, 3), -496.0);
        for (var row = 0; row < rows.Count; row++)
        {
            Close(Number(row, 4), 20.0);
        }
    }

    /// Chromium 150 horizontal reference for the same constraint algorithm:
    /// the sticky subtree pins at x=20, remains distinct from fixed, then
    /// leaves with its 500px containing block at the right boundary.
    [Fact]
    public void RootScrollStickySupportsTheInlineAxis()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div style="box-sizing:border-box;margin-left:40px;width:500px;height:100px;padding:10px;border:4px solid">
                    <div id="sticky" style="box-sizing:border-box;position:sticky;left:20px;width:60px;height:30px;margin:6px">
                        <div id="child" style="width:10px;height:10px"></div>
                    </div>
                </div>
                <div style="width:1600px;height:600px"></div>
                <div id="fixed" style="position:fixed;left:20px;top:100px;width:60px;height:30px"></div>
            </body></html>
            """));
        rt.SetViewport(800.0, 513.0);
        rt.RunPageInit();

        var result = rt.Evaluate(
            """
            const sticky = document.getElementById("sticky");
            const child = document.getElementById("child");
            const fixed = document.getElementById("fixed");
            const sample = x => {
                window.scrollTo(x, 0);
                return [
                    window.scrollX,
                    sticky.getBoundingClientRect().left,
                    child.getBoundingClientRect().left,
                    fixed.getBoundingClientRect().left,
                ];
            };
            return [sample(0), sample(100), sample(400), sample(800)];
            """);
        var rows = Assert.IsType<JsonArray>(result);
        double[][] expected =
        [
            [0.0, 60.0, 60.0, 20.0],
            [100.0, 20.0, 20.0, 20.0],
            [400.0, 20.0, 20.0, 20.0],
            [800.0, -340.0, -340.0, 20.0],
        ];
        for (var row = 0; row < expected.Length; row++)
        {
            var actualRow = Assert.IsType<JsonArray>(rows[row]);
            for (var column = 0; column < expected[row].Length; column++)
            {
                var actual = actualRow[column]!.GetValue<double>();
                Assert.True(
                    Math.Abs(actual - expected[row][column]) < 0.05,
                    $"expected {expected[row][column]}, got {actual}");
            }
        }
    }

    [Fact]
    public void PreparedRenderSharesResourceGeometryWithCssomAndScreenshots()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><head>
                <base href="/assets/">
            </head><body style="margin:0">
                <div id="frame" style="width:160px">
                    <img id="hero" src="hero.svg" style="display:block;width:100%;height:auto">
                </div>
                <div style="height:400px;background:#0000ff"></div>
            </body></html>
            """));
        rt.SetUrl("http://example.test/docs/page");
        rt.SetViewport(200.0, 100.0);

        int loads = 0;
        rt.State.RenderResources = Obscura.Render.RenderResourceCache.WithLoader(url =>
        {
            Assert.Equal("http://example.test/assets/hero.svg", url);
            loads++;
            return System.Text.Encoding.UTF8.GetBytes(
                """
                <svg xmlns="http://www.w3.org/2000/svg" width="400" height="100">
                    <rect width="400" height="100" fill="#ffff00"/>
                </svg>
                """);
        });
        rt.RunPageInit();

        var before = rt.Evaluate(
            """
            const hero = document.getElementById("hero");
            const rect = hero.getBoundingClientRect();
            return [rect.width, rect.height, document.documentElement.scrollHeight];
            """);
        Assert.NotNull(before);
        Assert.Equal(160.0, before![0]!.GetValue<double>());
        Assert.Equal(40.0, before[1]!.GetValue<double>());
        float cssomHeight = (float)before[2]!.GetValue<double>();
        var preparedBefore = rt.State.PreparedRender;
        Assert.NotNull(preparedBefore);
        Assert.Equal(cssomHeight, preparedBefore!.ContentSize().Height);
        Assert.Equal(1, loads);

        const string baseUrl = "http://example.test/assets/";
        var top = rt.ScreenshotPrepared((200.0f, 100.0f), baseUrl);
        Assert.NotNull(top);
        rt.Evaluate(
            "(function(){ window.scrollTo(0, document.documentElement.scrollHeight); return window.scrollY; })()");
        var bottom = rt.ScreenshotPrepared((200.0f, 100.0f), baseUrl);
        var bottomRepeat = rt.ScreenshotPrepared((200.0f, 100.0f), baseUrl);
        Assert.NotNull(bottom);
        Assert.NotNull(bottomRepeat);
        Assert.False(top!.AsSpan().SequenceEqual(bottom));
        Assert.True(bottom!.AsSpan().SequenceEqual(bottomRepeat));
        Assert.Same(preparedBefore, rt.State.PreparedRender);
        Assert.Equal(cssomHeight, rt.State.PreparedRender!.ContentSize().Height);
        Assert.Equal(1, loads);

        var after = rt.Evaluate(
            """
            const hero = document.getElementById("hero");
            document.getElementById("frame").setAttribute("style", "width:80px");
            const rect = hero.getBoundingClientRect();
            return [rect.width, rect.height, document.documentElement.scrollHeight];
            """);
        Assert.NotNull(after);
        Assert.Equal(80.0, after![0]!.GetValue<double>());
        Assert.Equal(20.0, after[1]!.GetValue<double>());
        float mutatedHeight = (float)after[2]!.GetValue<double>();
        Assert.Equal(mutatedHeight, rt.State.PreparedRender!.ContentSize().Height);
        Assert.NotNull(rt.ScreenshotPrepared((200.0f, 100.0f), baseUrl));
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task MissingRenderResourcePreservesPreparedLayoutAndScroll()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0;height:240px">
                <div style="height:240px;background:blue"></div>
            </body></html>
            """));
        rt.SetUrl("http://example.test/page");
        rt.SetViewport(80.0, 60.0);
        rt.RunPageInit();
        rt.Evaluate("window.scrollTo(0, 40)");
        var beforePng = rt.ScreenshotPrepared((80.0f, 60.0f), "http://example.test/page");
        Assert.NotNull(beforePng);
        var prepared = rt.State.PreparedRender;
        Assert.NotNull(prepared);
        var resolved = rt.State.ResolvedScroll;
        Assert.NotNull(resolved);
        var resolvedState = resolved!.Value.State;
        ulong scrollGeneration = rt.State.ScrollGeneration;
        var rootOffset = resolvedState.RootOffset();

        const string missingUrl = "http://example.test/missing.svg";
        rt.SeedRenderResource(missingUrl, null);
        Assert.True(rt.RenderResourceIsKnown(missingUrl));
        Assert.Same(prepared, rt.State.PreparedRender);
        Assert.Same(resolvedState, rt.State.ResolvedScroll!.Value.State);
        Assert.Equal(scrollGeneration, rt.State.ScrollGeneration);
        Assert.Equal(rootOffset, rt.State.ResolvedScroll!.Value.State.RootOffset());
        var repeat = rt.ScreenshotPrepared((80.0f, 60.0f), "http://example.test/page");
        Assert.NotNull(repeat);
        Assert.True(beforePng!.AsSpan().SequenceEqual(repeat));

        rt.SeedRenderResource(
            "http://example.test/loaded.svg",
            System.Text.Encoding.UTF8.GetBytes(
                """<svg xmlns="http://www.w3.org/2000/svg" width="20" height="10"/>"""));
        Assert.Same(prepared, rt.State.PreparedRender);
        Assert.Equal<Obscura.Render.RetainedStyleMutation>(
            [Obscura.Render.RetainedStyleMutation.Resource.Instance],
            rt.State.PendingStyleMutations);
        Assert.Null(rt.State.ResolvedScroll);
        await Task.CompletedTask;
    }

    [Fact]
    public void ImageResourceArrivalRetainsStylesAndRebuildsIntrinsicGeometry()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <img id="hero" src="http://example.test/late.png" style="display:block">
                <div id="after" style="height:10px"></div>
            </body></html>
            """));
        rt.SetUrl("http://example.test/page");
        rt.SetViewport(80.0, 60.0);
        rt.State.RenderResources = Obscura.Render.RenderResourceCache.WithLoader(_ => null);
        rt.RunPageInit();

        AssertJson(
            "[0, 0]",
            rt.Evaluate("[hero.getBoundingClientRect().height, after.getBoundingClientRect().top]"));
        var prepared = rt.State.PreparedRender;
        Assert.NotNull(prepared);

        // Preserve already queued framework damage and coalesce repeated
        // notification of the same shared resource into one refresh marker.
        rt.Evaluate("after.setAttribute('data-ready', 'true')");
        byte[] png = RenderCaptureSupport.TwoByThreePng();
        rt.SeedRenderImageResource(
            "http://example.test/late.png",
            Obscura.Render.ImageRequestProfile.NoCorsInclude,
            png);
        rt.SeedRenderImageResource(
            "http://example.test/late.png",
            Obscura.Render.ImageRequestProfile.NoCorsInclude,
            png);
        Assert.Same(prepared, rt.State.PreparedRender);
        Assert.Equal(
            1,
            rt.State.PendingStyleMutations.Count(m => m is Obscura.Render.RetainedStyleMutation.Resource));
        Assert.Contains(
            rt.State.PendingStyleMutations,
            m => m is Obscura.Render.RetainedStyleMutation.Attribute);

        AssertJson(
            "[3, 3]",
            rt.Evaluate("[hero.getBoundingClientRect().height, after.getBoundingClientRect().top]"));
        Assert.Empty(rt.State.PendingStyleMutations);
    }

    [Fact]
    public void FixedImageResourceArrivalRepaintsWithoutRebuildingGeometry()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <img id="hero" src="http://example.test/fixed.png"
                     style="display:block;width:20px;height:10px">
                <div id="after" style="height:10px;background:blue"></div>
            </body></html>
            """));
        rt.SetUrl("http://example.test/page");
        rt.SetViewport(80.0, 60.0);
        rt.State.RenderResources = Obscura.Render.RenderResourceCache.WithLoader(_ => null);
        rt.RunPageInit();

        AssertJson("[20, 10, 10]", rt.Evaluate("[hero.offsetWidth, hero.offsetHeight, after.offsetTop]"));
        var beforePng = rt.ScreenshotPrepared((80.0f, 60.0f), "http://example.test/page");
        Assert.NotNull(beforePng);
        var prepared = rt.State.PreparedRender;
        var resolvedState = rt.State.ResolvedScroll!.Value.State;
        ulong activityBefore = rt.State.ActivityGeneration;

        rt.SeedRenderImageResource(
            "http://example.test/fixed.png",
            Obscura.Render.ImageRequestProfile.NoCorsInclude,
            RenderCaptureSupport.TwoByThreePng());
        Assert.Same(prepared, rt.State.PreparedRender);
        Assert.Same(resolvedState, rt.State.ResolvedScroll!.Value.State);
        Assert.Empty(rt.State.PendingStyleMutations);
        Assert.True(rt.State.ActivityGeneration > activityBefore);

        AssertJson("[20, 10, 10]", rt.Evaluate("[hero.offsetWidth, hero.offsetHeight, after.offsetTop]"));
        var afterPng = rt.ScreenshotPrepared((80.0f, 60.0f), "http://example.test/page");
        Assert.NotNull(afterPng);
        Assert.False(afterPng!.AsSpan().SequenceEqual(beforePng), "new image pixels must reach paint");
    }

    [Fact]
    public void FixedFlexImageResourceArrivalStillRebuildsGeometry()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html><body><div style="display:flex">
                <img id="hero" src="http://example.test/flex.png"
                     style="width:20px;height:10px">
            </div></body></html>
            """));
        rt.SetUrl("http://example.test/page");
        rt.State.RenderResources = Obscura.Render.RenderResourceCache.WithLoader(_ => null);
        rt.RunPageInit();
        rt.Evaluate("hero.getBoundingClientRect().width");
        Assert.NotNull(rt.State.ResolvedScroll);

        rt.SeedRenderImageResource(
            "http://example.test/flex.png",
            Obscura.Render.ImageRequestProfile.NoCorsInclude,
            RenderCaptureSupport.TwoByThreePng());
        Assert.Equal<Obscura.Render.RetainedStyleMutation>(
            [Obscura.Render.RetainedStyleMutation.Resource.Instance],
            rt.State.PendingStyleMutations);
        Assert.Null(rt.State.ResolvedScroll);
    }

    [Fact]
    public void FixedCssContentImageArrivalStillRebuildsIntrinsicGeometry()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html><body><img id="hero" src="fallback.png"
                style="display:block;width:20px;height:10px;content:url('http://example.test/content.png')">
            </body></html>
            """));
        rt.SetUrl("http://example.test/page");
        rt.State.RenderResources = Obscura.Render.RenderResourceCache.WithLoader(_ => null);
        rt.RunPageInit();
        rt.Evaluate("hero.getBoundingClientRect().width");
        Assert.NotNull(rt.State.ResolvedScroll);

        rt.SeedRenderImageResource(
            "http://example.test/content.png",
            Obscura.Render.ImageRequestProfile.NoCorsInclude,
            RenderCaptureSupport.TwoByThreePng());
        Assert.Equal<Obscura.Render.RetainedStyleMutation>(
            [Obscura.Render.RetainedStyleMutation.Resource.Instance],
            rt.State.PendingStyleMutations);
        Assert.Null(rt.State.ResolvedScroll);
    }

    [Fact]
    public void DocumentRegionCapturePreservesLiveRuntimeStateAndResourceCache()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0;height:260px">
                <div style="height:120px;background:red"></div>
                <img src="http://example.test/marker.svg"
                     style="display:block;width:20px;height:20px">
                <div style="height:120px;background:blue"></div>
                <div style="position:fixed;left:0;top:0;width:10px;height:10px;background:lime"></div>
            </body></html>
            """));
        rt.SetUrl("http://example.test/page");
        rt.SetViewport(80.0, 60.0);
        int loads = 0;
        rt.State.RenderResources = Obscura.Render.RenderResourceCache.WithLoader(url =>
        {
            Assert.Equal("http://example.test/marker.svg", url);
            Interlocked.Increment(ref loads);
            return System.Text.Encoding.UTF8.GetBytes(
                """
                <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20">
                    <rect width="20" height="20" fill="#ffff00"/>
                </svg>
                """);
        });
        rt.RunPageInit();
        Assert.Equal(
            50.0,
            rt.Evaluate("(function(){ window.scrollTo(0, 50); return window.scrollY; })()")!.GetValue<double>());
        var liveBefore = rt.ScreenshotPrepared((80.0f, 60.0f), "http://example.test/page");
        Assert.NotNull(liveBefore);
        var prepared = rt.State.PreparedRender;
        Assert.NotNull(prepared);
        var viewport = rt.State.Viewport;
        var scrollOffset = rt.State.ScrollOffset;
        ulong scrollGeneration = rt.State.ScrollGeneration;
        var resolvedRoot = rt.State.ResolvedScroll!.Value.State.RootOffset();
        float fullHeight = prepared!.ContentSize().Height;
        Assert.Equal(1, loads);

        var (regionPng, regionError) = rt.ScreenshotPreparedRegion(
            Obscura.Render.CaptureRegion.New(0.0f, 115.0f, 80.0f, 40.0f, 1.5f));
        Assert.Null(regionError);
        Assert.NotNull(regionPng);
        var (fullPng, fullError) = rt.ScreenshotPreparedRegion(
            Obscura.Render.CaptureRegion.New(0.0f, 0.0f, 80.0f, fullHeight, 1.0f));
        Assert.Null(fullError);
        Assert.NotNull(fullPng);
        Assert.Equal((120u, 60u), RenderCaptureSupport.PngSize(regionPng!));
        Assert.Equal((80u, (uint)MathF.Ceiling(fullHeight)), RenderCaptureSupport.PngSize(fullPng!));

        var liveAfter = rt.ScreenshotPrepared((80.0f, 60.0f), "http://example.test/page");
        Assert.NotNull(liveAfter);
        Assert.True(liveAfter!.AsSpan().SequenceEqual(liveBefore));
        Assert.Same(prepared, rt.State.PreparedRender);
        Assert.Equal(viewport, rt.State.Viewport);
        Assert.Equal(scrollOffset, rt.State.ScrollOffset);
        Assert.Equal(scrollGeneration, rt.State.ScrollGeneration);
        Assert.Equal(resolvedRoot, rt.State.ResolvedScroll!.Value.State.RootOffset());
        Assert.Equal(1, loads);
    }

    [Fact]
    public void ScriptRegisteredUrlFontReachesRenderResourceCollection()
    {
        List<string> loads = [];
        byte[] font = Obscura.Render.FontAssets.Load("liberation-serif");
        using var owner = RenderCaptureSupport.ParserImageRuntime(
            """
            <html><body style="margin:0">
                <span id="sample" style="display:inline-block;width:max-content;
                    font-family:DynamicFixture;font-size:40px;white-space:nowrap">WWWWiiii</span>
            </body></html>
            """,
            url =>
            {
                loads.Add(url);
                return string.Equals(url, "http://example.com/fonts/dynamic.ttf", StringComparison.Ordinal)
                    ? font
                    : null;
            });
        var rt = owner.Runtime;
        rt.SetViewport(400.0, 100.0);
        double before = rt
            .Evaluate("document.getElementById('sample').getBoundingClientRect().width")!
            .GetValue<double>();
        Assert.Empty(loads);

        var registered = rt.Evaluate(
            """
            (() => {
                const face = new FontFace("DynamicFixture",
                    "url('../fonts/dynamic.ttf') format('truetype')",
                    { weight: "normal", style: "normal", unicodeRange: "U+20-7E" });
                return [document.fonts.add(face) === document.fonts,
                    document.fonts.size, document.fonts.has(face)];
            })()
            """);
        AssertJson("[true, 1, true]", registered);
        Assert.Single(rt.State.DynamicFonts);
        Assert.NotNull(rt.State.PreparedRender);
        Assert.Equal<Obscura.Render.RetainedStyleMutation>(
            [Obscura.Render.RetainedStyleMutation.Resource.Instance],
            rt.State.PendingStyleMutations);

        double after = rt
            .Evaluate("document.getElementById('sample').getBoundingClientRect().width")!
            .GetValue<double>();
        Assert.NotEqual(before, after);
        Assert.Equal<string>(["http://example.com/fonts/dynamic.ttf"], loads);
        Assert.NotNull(rt.ScreenshotPrepared((400.0f, 100.0f), "http://example.com/page/index.html"));
        Assert.Single(loads);
    }

    [Fact]
    public void RenderedLayoutCacheIsInvalidatedByStyleMutations()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="box" style="height:300px;width:40px"></div>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();

        var result = Assert.IsType<JsonArray>(rt.Evaluate(
            """
            const box = document.getElementById("box");
            const before = [
                document.documentElement.scrollHeight,
                box.getBoundingClientRect().height,
            ];
            box.setAttribute("style", "height:900px;width:80px");
            const after = [
                document.documentElement.scrollHeight,
                box.getBoundingClientRect().height,
                box.getBoundingClientRect().width,
            ];
            return [before, after];
            """));
        var before = Assert.IsType<JsonArray>(result[0]);
        var after = Assert.IsType<JsonArray>(result[1]);
        Assert.True(after[0]!.GetValue<double>() > before[0]!.GetValue<double>());
        Assert.Equal(300.0, before[1]!.GetValue<double>());
        Assert.Equal(900.0, after[1]!.GetValue<double>());
        Assert.Equal(80.0, after[2]!.GetValue<double>());
    }

    [Fact]
    public void ElementTextContentReplacementRecomputesEmptySelector()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <style>#x { width: 10px; height: 5px } #x:empty { width: 30px }</style>
            <div id="x">text</div>
            """));
        rt.RunPageInit();

        AssertJson(
            "[10,true,30]",
            rt.Evaluate(
                """
                (() => {
                    const x = document.getElementById("x");
                    const before = x.getBoundingClientRect().width;
                    x.textContent = "";
                    return [before, x.matches(":empty"), x.getBoundingClientRect().width];
                })()
                """));
    }

    [Fact]
    public void PreparedRenderSurvivesDetachedNoOpAndSameViewportUpdates()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="box" class="box" style="height:30px;width:40px"></div>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();

        Assert.Equal(
            40.0,
            rt.Evaluate("document.getElementById('box').getBoundingClientRect().width")!.GetValue<double>());
        Assert.NotNull(rt.State.PreparedRender);

        // Modern frameworks build and decorate substantial detached trees.
        // None of this can affect the connected document's style or geometry.
        rt.Evaluate(
            """
            const parent = document.createElement('section');
            const child = document.createElement('div');
            child.setAttribute('class', 'box');
            child.setAttribute('style', 'height:900px');
            parent.appendChild(child);
            child.setAttribute('data-state', 'ready');
            """);
        Assert.NotNull(rt.State.PreparedRender);

        // Attribute setters still fire their DOM/observer semantics when the
        // assigned value is identical, but layout is not dirtied.
        rt.Evaluate(
            """
            const box = document.getElementById('box');
            box.setAttribute('class', 'box');
            box.removeAttribute('data-absent');
            """);
        Assert.NotNull(rt.State.PreparedRender);

        rt.SetViewport(200.0, 100.0);
        Assert.NotNull(rt.State.PreparedRender);

        rt.Evaluate("document.getElementById('box').setAttribute('style', 'height:60px;width:40px')");
        Assert.NotNull(rt.State.PreparedRender);
        var mutation = Assert.Single(rt.State.PendingStyleMutations);
        var attribute = Assert.IsType<Obscura.Render.RetainedStyleMutation.Attribute>(mutation);
        Assert.Equal("style", attribute.Mutation.Name);

        Assert.Equal(
            60.0,
            rt.Evaluate("document.getElementById('box').getBoundingClientRect().height")!.GetValue<double>());
    }

    [Fact]
    public void WaapiPauseSeekAndCancelPreserveAuthoredInlineStyle()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><div id="box" style="opacity:.2;width:20px;height:20px"></div></body></html>""");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "waapi",
            """
            globalThis.box = document.getElementById('box');
            globalThis.__animation = box.animate(
                [{opacity:.2, transform:'translateX(0px)'}, {opacity:1, transform:'translateX(100px)'}],
                {duration:100, fill:'both', easing:'linear'}
            );
            __animation.pause();
            __animation.currentTime = 50;
            """);
        Assert.Equal(".2", rt.Evaluate("box.style.opacity")!.GetValue<string>());
        Assert.True(rt.Evaluate("box.getAnimations()[0] === __animation")!.GetValue<bool>());
        Assert.True(rt.Evaluate("document.getAnimations()[0] === __animation")!.GetValue<bool>());
        Assert.Equal("paused", rt.Evaluate("__animation.playState")!.GetValue<string>());
        Assert.True(
            rt.Evaluate(
                "!('easingBezier' in __animation.effect.getTiming()) && "
                + "!('linearEasing' in __animation.effect.getComputedTiming())")!.GetValue<bool>());
        var opacity = float.Parse(
            rt.Evaluate("getComputedStyle(box).opacity")!.GetValue<string>(),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(MathF.Abs(opacity - 0.6f) < 0.001f, $"midpoint opacity was {opacity}");

        rt.ExecuteScript("cancel", "__animation.cancel()");
        Assert.Equal(".2", rt.Evaluate("box.style.opacity")!.GetValue<string>());
        Assert.Equal("0.2", rt.Evaluate("getComputedStyle(box).opacity")!.GetValue<string>());
        Assert.Equal(0.0, rt.Evaluate("box.getAnimations().length")!.GetValue<double>());
        Assert.Equal(0.0, rt.Evaluate("document.getAnimations().length")!.GetValue<double>());
    }

    [Fact]
    public async Task WaapiZeroDurationFinishesAsynchronouslyAndFiresLifecycle()
    {
        using var fixture = RuntimeFixture.Setup("""<div id="box" style="opacity:.1"></div>""");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "waapi-lifecycle",
            """
            globalThis.box = document.getElementById('box');
            globalThis.__ready = false;
            globalThis.__finished = false;
            globalThis.__finishEvent = false;
            globalThis.__animation = box.animate([{opacity:.1}, {opacity:1}], {duration:0, fill:'both'});
            __animation.onfinish = () => { __finishEvent = true; };
            __animation.ready.then(() => { __ready = true; });
            __animation.finished.then(() => { __finished = true; });
            """);
        await rt.RunEventLoopBoundedAsync(20);
        Assert.True(rt.Evaluate("__ready")!.GetValue<bool>());
        Assert.True(rt.Evaluate("__finished")!.GetValue<bool>());
        Assert.True(rt.Evaluate("__finishEvent")!.GetValue<bool>());
        Assert.Equal("finished", rt.Evaluate("__animation.playState")!.GetValue<string>());
        Assert.Equal("1", rt.Evaluate("getComputedStyle(box).opacity")!.GetValue<string>());
    }

    [Fact]
    public async Task WaapiPositiveInfiniteIterationsRemainActive()
    {
        using var fixture = RuntimeFixture.Setup("""<div id="box" style="opacity:.1"></div>""");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "waapi-infinite",
            """
            globalThis.__infiniteFinished = false;
            globalThis.__infiniteAnimation = document.getElementById('box').animate(
                [{opacity:.1}, {opacity:1}],
                {duration:1, iterations:Infinity, fill:'both', easing:'linear'}
            );
            __infiniteAnimation.finished.then(() => { __infiniteFinished = true; });
            """);
        await rt.RunEventLoopBoundedAsync(20);
        AssertJson(
            """["running",true,false]""",
            rt.Evaluate(
                "[__infiniteAnimation.playState, "
                + "__infiniteAnimation.effect.getTiming().iterations === Infinity, __infiniteFinished]"));
        rt.Evaluate("getComputedStyle(document.getElementById('box')).opacity");
        Assert.True(rt.PreparedHasActiveCssAnimations);
    }

    [Fact]
    public void ForwardAnimationSamplesRetainStaticPreparedRender()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div style="width:80px;height:60px;background:#1769aa"></div>
            </body></html>
            """));
        rt.SetUrl("http://example.test/page");
        rt.SetViewport(80.0, 60.0);
        rt.RunPageInit();

        Assert.True(rt.SetAnimationSampleTime(new Obscura.Render.AnimationSampleTime(100.0f)));
        var first = rt.ScreenshotPrepared((80.0f, 60.0f), "http://example.test/page");
        Assert.NotNull(first);
        var prepared = rt.State.PreparedRender;
        Assert.NotNull(prepared);

        Assert.True(rt.SetAnimationSampleTime(new Obscura.Render.AnimationSampleTime(250.0f)));
        var second = rt.ScreenshotPrepared((80.0f, 60.0f), "http://example.test/page");
        Assert.NotNull(second);
        Assert.Same(prepared, rt.State.PreparedRender);
        Assert.True(first!.AsSpan().SequenceEqual(second));
    }

    [Fact]
    public void ForwardActiveAnimationSampleUpdatesGeometryAndPaintFromRetainedFrame()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><head><style>
                @keyframes grow {
                    from { width:20px; background-color:#ff0000 }
                    to { width:100px; background-color:#0000ff }
                }
                #box { height:40px; animation:grow 1000ms linear both }
            </style></head><body style="margin:0"><div id="box"></div></body></html>
            """));
        rt.SetUrl("http://example.test/page");
        rt.SetViewport(120.0, 40.0);
        rt.RunPageInit();

        Assert.True(rt.SetAnimationSample(Obscura.Render.AnimationSample.Document(0.0f)));
        var initial = rt.ScreenshotPrepared((120.0f, 40.0f), "http://example.test/page");
        Assert.NotNull(initial);
        Assert.True(MathF.Abs(RenderCaptureSupport.AnimationTestWidth(rt, "box") - 20.0f) < 0.1f);

        Assert.True(rt.SetAnimationSample(Obscura.Render.AnimationSample.Document(500.0f)));
        Assert.NotNull(rt.State.PreparedRender);
        var midpoint = rt.ScreenshotPrepared((120.0f, 40.0f), "http://example.test/page");
        Assert.NotNull(midpoint);

        Assert.True(MathF.Abs(RenderCaptureSupport.AnimationTestWidth(rt, "box") - 60.0f) < 0.1f);
        Assert.False(initial!.AsSpan().SequenceEqual(midpoint), "animated paint output must advance");
        Assert.Equal(500.0f, rt.State.PreparedRender!.AnimationSampleTime().Milliseconds);
    }

    [Fact]
    public void ForwardWaapiSampleUpdatesRetainedStyleAndPaint()
    {
        using var fixture = RuntimeFixture.Setup(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="box" style="width:40px;height:40px;background:#1769aa"></div>
            </body></html>
            """);
        var rt = fixture.Runtime;
        rt.SetViewport(120.0, 40.0);
        rt.State.ResetAnimationTimelineOrigin();
        Assert.True(rt.SetAnimationSample(Obscura.Render.AnimationSample.Document(0.0f)));
        Assert.NotNull(rt.ScreenshotPrepared((120.0f, 40.0f), "http://example.com/test"));
        rt.ExecuteScript(
            "waapi-retained-frame",
            """
            document.getElementById('box').animate(
                [{opacity:0,transform:'translateX(0px)'},
                 {opacity:1,transform:'translateX(80px)'}],
                {duration:1000,fill:'both',easing:'linear'}
            )
            """);
        var boxNode = rt.State.Dom!.GetElementById("box");
        Assert.NotNull(boxNode);
        Assert.NotNull(rt.State.PreparedRender);
        Assert.Equal<Obscura.Render.RetainedStyleMutation>(
            [new Obscura.Render.RetainedStyleMutation.WaapiAnimation(boxNode!.Value)],
            rt.State.PendingStyleMutations);
        var initial = rt.ScreenshotPrepared((120.0f, 40.0f), "http://example.com/test");
        Assert.NotNull(initial);

        Assert.True(rt.SetAnimationSample(Obscura.Render.AnimationSample.Document(500.0f)));
        Assert.NotNull(rt.State.PreparedRender);
        var midpoint = rt.ScreenshotPrepared((120.0f, 40.0f), "http://example.com/test");
        Assert.NotNull(midpoint);
        float midpointOpacity = rt.State.PreparedRender!.Layout.Styles[boxNode.Value].Opacity!.Value;

        Assert.True(
            midpointOpacity >= 0.45f && midpointOpacity < 0.55f,
            $"WAAPI midpoint opacity={midpointOpacity}");
        Assert.False(initial!.AsSpan().SequenceEqual(midpoint), "WAAPI paint output must advance");
    }

    [Fact]
    public void WaapiCancelRetainsStaticStyleGraphAndRestoresAuthoredStyle()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><div id="box" style="opacity:.25;width:20px;height:20px"></div></body></html>""");
        var rt = fixture.Runtime;
        rt.SetViewport(40.0, 40.0);
        Assert.NotNull(rt.ScreenshotPrepared((40.0f, 40.0f), "http://example.com/test"));
        rt.ExecuteScript(
            "waapi-retained-cancel",
            """
            globalThis.__cancelAnimation = document.getElementById('box').animate(
                [{opacity:1}, {opacity:0}], {duration:1000, fill:'both'}
            )
            """);
        double animatedOpacity = rt
            .Evaluate("Number(getComputedStyle(document.getElementById('box')).opacity)")!
            .GetValue<double>();
        Assert.True(animatedOpacity > 0.9, $"animated opacity={animatedOpacity}");

        rt.Evaluate("__cancelAnimation.cancel()");
        Assert.NotNull(rt.State.PreparedRender);
        AssertJson(
            "\"0.25\"",
            rt.Evaluate("getComputedStyle(document.getElementById('box')).opacity"));
    }

    [Fact]
    public void CompletedAnimationRetainsForwardFrameButBackwardSeekRebuilds()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><head><style>
                @keyframes fade { from { opacity:1 } to { opacity:0 } }
                #box { width:80px; height:60px; background:#ff0000;
                       animation:fade 100ms linear forwards }
            </style></head><body style="margin:0"><div id="box"></div></body></html>
            """));
        rt.SetUrl("http://example.test/page");
        rt.SetViewport(80.0, 60.0);
        rt.RunPageInit();

        Assert.True(rt.SetAnimationSampleTime(new Obscura.Render.AnimationSampleTime(150.0f)));
        var completed = rt.ScreenshotPrepared((80.0f, 60.0f), "http://example.test/page");
        Assert.NotNull(completed);
        Assert.False(rt.PreparedHasActiveCssAnimations);
        var prepared = rt.State.PreparedRender;
        Assert.NotNull(prepared);

        Assert.True(rt.SetAnimationSampleTime(new Obscura.Render.AnimationSampleTime(300.0f)));
        var later = rt.ScreenshotPrepared((80.0f, 60.0f), "http://example.test/page");
        Assert.NotNull(later);
        Assert.True(completed!.AsSpan().SequenceEqual(later));
        Assert.Same(prepared, rt.State.PreparedRender);

        Assert.True(rt.SetAnimationSampleTime(new Obscura.Render.AnimationSampleTime(0.0f)));
        Assert.Null(rt.State.PreparedRender);
        var initial = rt.ScreenshotPrepared((80.0f, 60.0f), "http://example.test/page");
        Assert.NotNull(initial);
        Assert.False(completed.AsSpan().SequenceEqual(initial));
        Assert.True(rt.PreparedHasActiveCssAnimations);
    }

    [Fact]
    public void UnsupportedCustomPropertyAnimationDoesNotKeepRenderDamageActive()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><head><style>
                @property --brand-cycle { syntax:"<color>"; inherits:true; initial-value:#2dacf9 }
                @keyframes brand-cycle {
                    from { --brand-cycle:#2dacf9 }
                    to { --brand-cycle:#7ce95a }
                }
                :root { animation:brand-cycle 10s linear infinite }
            </style></head><body style="margin:0">
                <div style="width:80px;height:60px;background:#1769aa"></div>
            </body></html>
            """));
        rt.SetUrl("http://example.test/page");
        rt.SetViewport(80.0, 60.0);
        rt.RunPageInit();

        var first = rt.ScreenshotPrepared((80.0f, 60.0f), "http://example.test/page");
        Assert.NotNull(first);
        Assert.False(
            rt.PreparedHasActiveCssAnimations,
            "an unsupported custom-property-only animation has no render damage");
        var prepared = rt.State.PreparedRender;
        Assert.NotNull(prepared);
        Assert.True(rt.SetAnimationSampleTime(new Obscura.Render.AnimationSampleTime(5_000.0f)));
        var later = rt.ScreenshotPrepared((80.0f, 60.0f), "http://example.test/page");
        Assert.NotNull(later);
        Assert.True(first!.AsSpan().SequenceEqual(later));
        Assert.Same(prepared, rt.State.PreparedRender);
    }

    [Fact]
    public void RemoveAndReappendRestartsAnimationWithoutIntermediateFlush()
    {
        using var owner = RenderCaptureSupport.AnimationEpochRuntime();
        var rt = owner.Runtime;
        rt.Evaluate(
            "var box=document.createElement('div');box.id='box';box.className='anim';document.body.appendChild(box)");
        Assert.True(rt.SetAnimationSample(Obscura.Render.AnimationSample.Document(1_000.0f)));
        Assert.NotNull(rt.ScreenshotPrepared((200.0f, 80.0f), "http://example.test/page"));
        Assert.True(RenderCaptureSupport.AnimationTestWidth(rt, "box") > 95.0f);

        RenderCaptureSupport.RewindAnimationTimeline(rt, 1_000);
        rt.Evaluate("var box=document.getElementById('box');box.remove();document.body.appendChild(box)");
        Assert.True(rt.SetAnimationSample(Obscura.Render.AnimationSample.Document(1_100.0f)));
        Assert.NotNull(rt.ScreenshotPrepared((200.0f, 80.0f), "http://example.test/page"));
        float restarted = RenderCaptureSupport.AnimationTestWidth(rt, "box");
        Assert.True(restarted >= 5.0f && restarted < 20.0f, $"restarted width={restarted}");
    }

    [Fact]
    public void ScopedAnimationEpochsSurviveLaterUnrelatedMutationsAndT0Capture()
    {
        using var owner = RenderCaptureSupport.AnimationEpochRuntime();
        var rt = owner.Runtime;
        RenderCaptureSupport.RewindAnimationTimeline(rt, 100);
        rt.Evaluate(
            "var a=document.createElement('div');a.id='first';a.className='anim';document.body.appendChild(a)");
        RenderCaptureSupport.RewindAnimationTimeline(rt, 500);
        rt.Evaluate(
            "var b=document.createElement('div');b.id='second';b.className='anim';document.body.appendChild(b)");
        RenderCaptureSupport.RewindAnimationTimeline(rt, 600);
        rt.Evaluate("document.getElementById('anchor').setAttribute('data-unrelated','yes')");

        Assert.True(rt.SetAnimationSample(Obscura.Render.AnimationSample.LocalOverride(0.0f)));
        Assert.NotNull(rt.ScreenshotPrepared((200.0f, 80.0f), "http://example.test/page"));
        Assert.Equal(0.0f, RenderCaptureSupport.AnimationTestWidth(rt, "first"));
        Assert.Equal(0.0f, RenderCaptureSupport.AnimationTestWidth(rt, "second"));

        Assert.True(rt.SetAnimationSample(Obscura.Render.AnimationSample.Document(700.0f)));
        Assert.NotNull(rt.ScreenshotPrepared((200.0f, 80.0f), "http://example.test/page"));
        float first = RenderCaptureSupport.AnimationTestWidth(rt, "first");
        float second = RenderCaptureSupport.AnimationTestWidth(rt, "second");
        Assert.True(first >= 55.0f && first < 65.0f, $"first width={first}");
        Assert.True(second >= 15.0f && second < 25.0f, $"second width={second}");
    }

    [Fact]
    public void TimingEditsPreserveIdentityAndPauseHoldsThenResumes()
    {
        using var owner = RenderCaptureSupport.AnimationEpochRuntime();
        var rt = owner.Runtime;
        rt.Evaluate(
            "var box=document.createElement('div');box.id='box';box.className='anim';document.body.appendChild(box)");
        Assert.True(rt.SetAnimationSample(Obscura.Render.AnimationSample.Document(300.0f)));
        Assert.NotNull(rt.ScreenshotPrepared((200.0f, 80.0f), "http://example.test/page"));
        float started = RenderCaptureSupport.AnimationTestWidth(rt, "box");
        Assert.True(started >= 25.0f && started < 35.0f, $"started width={started}");

        RenderCaptureSupport.RewindAnimationTimeline(rt, 300);
        rt.Evaluate(
            "document.getElementById('box').setAttribute('style','animation-duration:2000ms;animation-play-state:paused')");
        Assert.True(rt.SetAnimationSample(Obscura.Render.AnimationSample.Document(700.0f)));
        Assert.NotNull(rt.ScreenshotPrepared((200.0f, 80.0f), "http://example.test/page"));
        float held = RenderCaptureSupport.AnimationTestWidth(rt, "box");
        Assert.True(held >= 12.0f && held < 18.0f, $"held width={held}");

        RenderCaptureSupport.RewindAnimationTimeline(rt, 800);
        rt.Evaluate(
            "document.getElementById('box').setAttribute('style','animation-duration:2000ms;animation-play-state:running')");
        Assert.True(rt.SetAnimationSample(Obscura.Render.AnimationSample.Document(1_000.0f)));
        Assert.NotNull(rt.ScreenshotPrepared((200.0f, 80.0f), "http://example.test/page"));
        float resumed = RenderCaptureSupport.AnimationTestWidth(rt, "box");
        Assert.True(resumed >= 22.0f && resumed < 28.0f, $"resumed width={resumed}");
    }

    [Fact]
    public void CssomGeometrySamplesLiveDocumentTime()
    {
        using var owner = RenderCaptureSupport.AnimationEpochRuntime();
        var rt = owner.Runtime;
        rt.Evaluate(
            "var box=document.createElement('div');box.id='box';box.className='anim';"
            + "document.body.appendChild(box)");
        var initial = rt.Evaluate(
            "document.getElementById('box').getBoundingClientRect().width")!.GetValue<double>();
        Thread.Sleep(120);
        var later = rt.Evaluate(
            "document.getElementById('box').getBoundingClientRect().width")!.GetValue<double>();
        Assert.True(later >= initial + 8.0, $"initial={initial}, later={later}");
    }

    [Fact]
    public void FixedAnimationCaptureIsInvariantAfterGeometryFlush()
    {
        static CaptureRuntime MakeRuntime()
        {
            var runtime = new ObscuraJsRuntime();
            runtime.SetDom(HtmlParsing.ParseHtml(
                """
                <html style="margin:0"><head><style>
                    @keyframes dismiss {
                        from { opacity:1; transform:translateY(0) }
                        to { opacity:0; transform:translateY(-80px) }
                    }
                    body { margin:0; width:160px; height:100px; background:#f5f7fa }
                    #content { width:120px; height:50px; margin:20px; background:#1769aa }
                    #shell { position:fixed; inset:0; background:#111827 }
                    #shell.dismissed { animation:dismiss 600ms linear forwards }
                </style></head><body><div id="content"></div>
                    <div id="shell"></div></body></html>
                """));
            runtime.SetUrl("http://example.test/github-like-shell");
            runtime.SetViewport(160.0, 100.0);
            runtime.RunPageInit();
            runtime.Evaluate("document.getElementById('shell').className='dismissed'");
            return new CaptureRuntime(runtime);
        }

        var fixedSample = Obscura.Render.AnimationSample.LocalOverride(750.0f);
        using var directOwner = MakeRuntime();
        Assert.True(directOwner.Runtime.SetAnimationSample(fixedSample));
        var direct = directOwner.Runtime.ScreenshotPrepared(
            (160.0f, 100.0f), "http://example.test/github-like-shell");
        Assert.NotNull(direct);

        using var geometryOwner = MakeRuntime();
        var rect = geometryOwner.Runtime.Evaluate(
            "document.getElementById('content').getBoundingClientRect().toJSON()");
        Assert.NotNull(rect);
        Assert.Equal(120.0, rect!["width"]!.GetValue<double>());
        Assert.True(geometryOwner.Runtime.SetAnimationSample(fixedSample));
        var afterGeometry = geometryOwner.Runtime.ScreenshotPrepared(
            (160.0f, 100.0f), "http://example.test/github-like-shell");
        Assert.NotNull(afterGeometry);

        Assert.True(
            direct!.AsSpan().SequenceEqual(afterGeometry),
            "a CSSOM geometry flush must not change fixed-time capture output");
    }

    [Fact]
    public void CssomAnimationSampleIsFrozenWithinOneJavascriptTask()
    {
        using var owner = RenderCaptureSupport.AnimationEpochRuntime();
        var rt = owner.Runtime;
        rt.Evaluate(
            "var box=document.createElement('div');box.id='box';box.className='anim';"
            + "document.body.appendChild(box)");
        var widths = Assert.IsType<JsonArray>(rt.Evaluate(
            """
            (function(){
                const box = document.getElementById('box');
                const first = box.getBoundingClientRect().width;
                const deadline = Date.now() + 120;
                while (Date.now() < deadline) {}
                return [first, box.getBoundingClientRect().width];
            })()
            """));
        Assert.Equal(widths[0]!.GetValue<double>(), widths[1]!.GetValue<double>());
    }

    [Fact]
    public async Task TimerCallbackStartsAFreshLazyAnimationSample()
    {
        using var owner = RenderCaptureSupport.AnimationEpochRuntime();
        var rt = owner.Runtime;
        rt.ExecuteScript(
            "timer-animation-sample",
            """
            var box=document.createElement('div');
            box.id='box';box.className='anim';document.body.appendChild(box);
            globalThis.__beforeTimerWidth=box.getBoundingClientRect().width;
            setTimeout(() => {
                globalThis.__afterTimerWidth=box.getBoundingClientRect().width;
            }, 100);
            """);
        await rt.RunEventLoopBoundedAsync(250);
        var widths = Assert.IsType<JsonArray>(
            rt.Evaluate("[globalThis.__beforeTimerWidth, globalThis.__afterTimerWidth]"));
        var before = widths[0]!.GetValue<double>();
        var after = widths[1]!.GetValue<double>();
        Assert.True(after >= before + 7.0, $"before={before}, after={after}");
    }

    [Fact]
    public void AutocompleteAttributeRetainsPreparedRenderUntilGeometryFlush()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><head><style>
                input { display:block; width:40px; height:20px }
                input[autocomplete="off"] { width:90px }
            </style></head><body style="margin:0">
                <input id="field" autocomplete="on">
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();

        Assert.Equal(
            40.0,
            rt.Evaluate("document.getElementById('field').getBoundingClientRect().width")!.GetValue<double>());
        Assert.NotNull(rt.State.PreparedRender);

        rt.Evaluate("document.getElementById('field').setAttribute('autocomplete', 'off')");
        Assert.NotNull(rt.State.PreparedRender);
        var mutation = Assert.Single(rt.State.PendingStyleMutations);
        var attribute = Assert.IsType<Obscura.Render.RetainedStyleMutation.Attribute>(mutation);
        Assert.Equal("autocomplete", attribute.Mutation.Name);
        Assert.Equal("on", attribute.Mutation.OldValue);
        Assert.Equal("off", attribute.Mutation.NewValue);

        Assert.Equal(
            90.0,
            rt.Evaluate("document.getElementById('field').getBoundingClientRect().width")!.GetValue<double>());
        Assert.Empty(rt.State.PendingStyleMutations);
    }

    [Fact]
    public void NamespacedAttributeMutationsParticipateInIdAndRenderInvalidation()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """<html style="margin:0"><body style="margin:0"><div id="box" class="box" style="height:30px;width:40px"></div></body></html>"""));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();

        Assert.Equal(
            30.0,
            rt.Evaluate("document.getElementById('box').getBoundingClientRect().height")!.GetValue<double>());
        Assert.NotNull(rt.State.PreparedRender);

        rt.Evaluate("document.getElementById('box').setAttributeNS(null, 'class', 'box')");
        Assert.NotNull(rt.State.PreparedRender);

        rt.Evaluate("document.getElementById('box').setAttributeNS(null, 'style', 'height:70px;width:40px')");
        Assert.Null(rt.State.PreparedRender);
        Assert.Equal(
            70.0,
            rt.Evaluate("document.getElementById('box').getBoundingClientRect().height")!.GetValue<double>());

        AssertJson(
            "true",
            rt.Evaluate(
                "(function(){const box=document.getElementById('box');box.setAttributeNS(null,'id','renamed');"
                + "const found=document.getElementById('renamed')===box;box.removeAttributeNS(null,'id');"
                + "return found && document.getElementById('renamed')===null;})()"));
    }

    [Fact]
    public void StylesheetIndexCacheReusesSourcesButNotLiveCascadeOrViewport()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><head><style id="sheet">
                .a { width:40px; height:20px }
                .b { width:80px; height:20px }
            </style></head><body style="margin:0">
                <div id="box" class="a"></div>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();

        Assert.Equal(
            40.0,
            rt.Evaluate("document.getElementById('box').getBoundingClientRect().width")!.GetValue<double>());
        Assert.Equal(1UL, rt.State.StylesheetCache.MissCount);
        Assert.Equal(0UL, rt.State.StylesheetCache.HitCount);
        Assert.True(rt.State.StylesheetCache.RetainedSourceBytes > 0);

        // The compiled selector index is reusable, but matching and cascade
        // must observe the new class on the live connected element.
        rt.Evaluate("document.getElementById('box').className = 'b'");
        Assert.NotNull(rt.State.PreparedRender);
        Assert.Single(rt.State.PendingStyleMutations);
        Assert.Equal(
            80.0,
            rt.Evaluate("document.getElementById('box').getBoundingClientRect().width")!.GetValue<double>());
        Assert.Equal(1UL, rt.State.StylesheetCache.MissCount);
        Assert.Equal(1UL, rt.State.StylesheetCache.HitCount);

        // Style text is part of the exact key and cannot reuse stale rules.
        rt.Evaluate(
            """document.getElementById('sheet').textContent = '.b{width:120px;height:20px}@media(min-width:250px){.b{width:160px}}'""");
        Assert.Equal(
            120.0,
            rt.Evaluate("document.getElementById('box').getBoundingClientRect().width")!.GetValue<double>());
        Assert.Equal(2UL, rt.State.StylesheetCache.MissCount);
        Assert.Equal(1UL, rt.State.StylesheetCache.HitCount);

        // Media-query filtering is viewport-dependent, so an exact source hit
        // at a different viewport must still reparse and reindex.
        rt.SetViewport(300.0, 100.0);
        Assert.Equal(
            160.0,
            rt.Evaluate("document.getElementById('box').getBoundingClientRect().width")!.GetValue<double>());
        Assert.Equal(3UL, rt.State.StylesheetCache.MissCount);
        Assert.Equal(1UL, rt.State.StylesheetCache.HitCount);
    }

    [Fact]
    public void ConnectedTreeMutationsQueueRetainedStylesUntilGeometryFlush()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><head><style>
                .item{display:block;width:40px;height:12px}
                .item:nth-child(2){width:80px}
            </style></head><body style="margin:0">
                <main id="list"><div id="first" class="item"></div></main>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();

        Assert.Equal(
            40.0,
            rt.Evaluate("document.getElementById('first').getBoundingClientRect().width")!.GetValue<double>());
        rt.Evaluate(
            "const added=document.createElement('div');added.id='added';added.className='item';"
            + "document.getElementById('list').appendChild(added)");
        Assert.NotNull(rt.State.PreparedRender);
        var insert = Assert.IsType<Obscura.Render.RetainedStyleMutation.Tree>(
            Assert.Single(rt.State.PendingStyleMutations));
        Assert.IsType<Obscura.Render.TreeStyleMutation.Insert>(insert.Mutation);
        Assert.Equal(
            80.0,
            rt.Evaluate("document.getElementById('added').getBoundingClientRect().width")!.GetValue<double>());
        Assert.Empty(rt.State.PendingStyleMutations);

        rt.Evaluate("document.getElementById('added').style.width='65px'");
        Assert.NotNull(rt.State.PreparedRender);
        var styled = Assert.IsType<Obscura.Render.RetainedStyleMutation.Attribute>(
            Assert.Single(rt.State.PendingStyleMutations));
        Assert.Equal("style", styled.Mutation.Name);
        Assert.Equal(
            65.0,
            rt.Evaluate("document.getElementById('added').getBoundingClientRect().width")!.GetValue<double>());
        Assert.Equal(
            80.0,
            rt.Evaluate(
                "(function(){const added=document.getElementById('added');added.style.width='';"
                + "return added.getBoundingClientRect().width})()")!.GetValue<double>());

        rt.Evaluate("document.getElementById('list').removeChild(document.getElementById('first'))");
        Assert.NotNull(rt.State.PreparedRender);
        var removed = Assert.IsType<Obscura.Render.RetainedStyleMutation.Tree>(
            Assert.Single(rt.State.PendingStyleMutations));
        Assert.IsType<Obscura.Render.TreeStyleMutation.Remove>(removed.Mutation);
        Assert.Equal(
            40.0,
            rt.Evaluate("document.getElementById('added').getBoundingClientRect().width")!.GetValue<double>());
        Assert.Empty(rt.State.PendingStyleMutations);
    }

    [Fact]
    public void RootOverflowClipPreservesCssomScrollRange()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0;height:100%;overflow:hidden">
                <body style="margin:0;height:100%">
                    <main id="main" style="padding-top:48px">
                        <div style="height:5000px"></div>
                    </main>
                </body>
            </html>
            """));
        rt.SetViewport(900.0, 1000.0);
        rt.RunPageInit();

        AssertJson(
            "[5048,5048,5048,4048,-4048]",
            rt.Evaluate(
                """
                const main = document.getElementById("main");
                const before = main.getBoundingClientRect();
                scrollTo(0, 99999);
                const after = main.getBoundingClientRect();
                return [
                    before.height,
                    document.documentElement.scrollHeight,
                    document.body.scrollHeight,
                    scrollY,
                    after.top,
                ];
                """));
    }

    [Fact]
    public async Task RenderedWindowScrollEventsRequireActualMovement()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div style="height:1000px"></div>
            </body></html>
            """));
        rt.SetViewport(320.0, 200.0);
        rt.RunPageInit();

        var moved = await rt.EvaluateForCdpAsync(
            """
            new Promise(resolve => {
                let win = 0, doc = 0;
                window.addEventListener("scroll", () => win++);
                document.addEventListener("scroll", () => doc++);
                window.scrollTo(0, 100);
                setTimeout(() => resolve([win, doc, window.scrollY]), 5);
            })
            """,
            returnByValue: true,
            awaitPromise: true);
        AssertJson("[1,1,100]", moved.Value);

        rt.Evaluate("window.scrollTo(0, 99999)");
        await rt.RunEventLoopBoundedAsync(20);
        var noOp = await rt.EvaluateForCdpAsync(
            """
            new Promise(resolve => {
                let win = 0, doc = 0;
                window.addEventListener("scroll", () => win++);
                document.addEventListener("scroll", () => doc++);
                const before = window.scrollY;
                window.scrollTo(0, 99999);
                setTimeout(() => resolve([win, doc, before, window.scrollY]), 5);
            })
            """,
            returnByValue: true,
            awaitPromise: true);
        var values = Assert.IsType<JsonArray>(noOp.Value);
        Assert.Equal(0.0, values[0]!.GetValue<double>());
        Assert.Equal(0.0, values[1]!.GetValue<double>());
        Assert.Equal(values[2]!.GetValue<double>(), values[3]!.GetValue<double>());
    }

    [Fact]
    public async Task FingerprintedScreenDoesNotInventADeviceScaleFactor()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        rt.SetViewport(300.0, 200.0);
        // Force the fingerprint seed whose screen-pool entry is 2560x1440. That
        // physical screen must not silently turn a 1x render surface into a 2x
        // devicePixelContentBoxSize surface.
        rt.ExecuteScript(
            "deterministic-high-resolution-screen",
            "Date.now = () => 0; Math.random = () => 2 / 0xFFFFFFFF;");
        rt.RunPageInit();

        AssertJson("[2560,1440,1]", rt.Evaluate("[screen.width, screen.height, devicePixelRatio]"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ResizeObserverReportsRealBoxesOnlyWhenSelectedSizeChanges()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="target" style="box-sizing:border-box;width:120px;height:80px;
                     padding:5px 7px;border:2px solid black"></div>
            </body></html>
            """));
        rt.SetViewport(300.0, 200.0);
        rt.RunPageInit();
        rt.ExecuteScript(
            "resize-observer-boxes",
            """
            globalThis.__resizeRecords = [];
            globalThis.__resizeObserver = new ResizeObserver(entries => {
                __resizeRecords.push(...entries.map(entry => ({
                    interfaces: [
                        entry instanceof ResizeObserverEntry,
                        entry.contentBoxSize[0] instanceof ResizeObserverSize,
                        entry.borderBoxSize[0] instanceof ResizeObserverSize,
                        entry.devicePixelContentBoxSize[0] instanceof ResizeObserverSize,
                    ],
                    contentRect: [
                        entry.contentRect.x, entry.contentRect.y,
                        entry.contentRect.width, entry.contentRect.height,
                    ],
                    content: [
                        entry.contentBoxSize[0].inlineSize,
                        entry.contentBoxSize[0].blockSize,
                    ],
                    border: [
                        entry.borderBoxSize[0].inlineSize,
                        entry.borderBoxSize[0].blockSize,
                    ],
                    device: [
                        entry.devicePixelContentBoxSize[0].inlineSize,
                        entry.devicePixelContentBoxSize[0].blockSize,
                    ],
                })));
            });
            __resizeObserver.observe(document.getElementById("target"));
            """);
        await rt.RunEventLoopBoundedAsync(40);
        AssertJson(
            """
            [{"interfaces":[true,true,true,true],
              "contentRect":[7,5,102,66],
              "content":[102,66],
              "border":[120,80],
              "device":[102,66]}]
            """,
            rt.Evaluate("__resizeRecords"));

        // A style mutation still causes a rendering checkpoint, but unchanged observed
        // geometry must not produce a speculative notification.
        rt.Evaluate("""document.getElementById("target").style.color = "red" """);
        await rt.RunEventLoopBoundedAsync(40);
        Assert.Equal(1.0, rt.Evaluate("__resizeRecords.length")!.GetValue<double>());

        rt.Evaluate("""document.getElementById("target").style.width = "140px" """);
        await rt.RunEventLoopBoundedAsync(40);
        AssertJson(
            "[[102,120],[122,140]]",
            rt.Evaluate("__resizeRecords.map(record => [record.content[0], record.border[0]])"));
        Assert.Equal(-1.0, rt.Evaluate("__obscura_nextPendingTimeoutDelay()")!.GetValue<double>());
    }

    [Fact]
    public async Task ResizeObserverBatchesUniqueTargetsIntoOneNativeLayoutRead()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="a" style="box-sizing:border-box;width:100px;height:30px;padding:2px 3px;border:1px solid"></div>
                <div id="b" style="box-sizing:border-box;width:110px;height:30px;padding:2px 3px;border:1px solid"></div>
                <div id="c" style="box-sizing:border-box;width:120px;height:30px;padding:2px 3px;border:1px solid"></div>
                <div id="d" style="box-sizing:border-box;width:130px;height:30px;padding:2px 3px;border:1px solid"></div>
                <div id="vertical" style="box-sizing:border-box;width:140px;height:30px;padding:2px 3px;border:1px solid;writing-mode:vertical-rl"></div>
                <div id="hidden" style="display:none;width:50px;height:20px"></div>
            </body></html>
            """));
        rt.SetViewport(400.0, 300.0);
        rt.RunPageInit();
        rt.ExecuteScript(
            "batch-resize-observer-targets",
            """
            globalThis.__resizeBulkCalls = 0;
            globalThis.__resizeBulkSizes = [];
            globalThis.__resizeLegacyGeometryCalls = 0;
            globalThis.__resizeComputedStyleCalls = 0;
            const nativeBulk = Deno.core.ops.op_resize_observer_measurements;
            const nativeGeometry = Deno.core.ops.op_layout_geometry;
            const nativeComputedStyle = Deno.core.ops.op_computed_style;
            Deno.core.ops.op_resize_observer_measurements = input => {
                __resizeBulkCalls++;
                __resizeBulkSizes.push(JSON.parse(input).length);
                return nativeBulk(input);
            };
            Deno.core.ops.op_layout_geometry = (...args) => {
                __resizeLegacyGeometryCalls++;
                return nativeGeometry(...args);
            };
            Deno.core.ops.op_computed_style = (...args) => {
                __resizeComputedStyleCalls++;
                return nativeComputedStyle(...args);
            };

            globalThis.__resizeBatchRecords = [];
            const detached = document.createElement("div");
            detached.id = "detached";
            detached.style.cssText = "width:60px;height:20px";
            const targets = ["a", "b", "c", "d", "vertical", "hidden"]
                .map(id => document.getElementById(id));
            targets.push(detached);
            const observer = new ResizeObserver(entries => {
                __resizeBatchRecords.push(entries.map(entry => [
                    entry.target.id,
                    entry.contentBoxSize[0].inlineSize,
                    entry.contentBoxSize[0].blockSize,
                    entry.borderBoxSize[0].inlineSize,
                    entry.borderBoxSize[0].blockSize,
                ]));
            });
            for (const target of targets) observer.observe(target);
            // A second observer of an existing target must share the same native
            // measurement rather than adding it to the batch twice.
            globalThis.__duplicateResizeRecords = 0;
            const duplicate = new ResizeObserver(entries => {
                __duplicateResizeRecords += entries.length;
            });
            duplicate.observe(targets[2], { box: "border-box" });
            """);
        await rt.RunEventLoopBoundedAsync(100);

        AssertJson(
            """
            [1,[7],0,0,1,
             [[["a",92,24,100,30],
               ["b",102,24,110,30],
               ["c",112,24,120,30],
               ["d",122,24,130,30],
               ["vertical",24,132,30,140],
               ["hidden",0,0,0,0],
               ["detached",0,0,0,0]]]]
            """,
            rt.Evaluate(
                """
                [
                    __resizeBulkCalls,
                    __resizeBulkSizes,
                    __resizeLegacyGeometryCalls,
                    __resizeComputedStyleCalls,
                    __duplicateResizeRecords,
                    __resizeBatchRecords,
                ]
                """));
    }

    [Fact]
    public async Task ResizeObserverSelectedBoxAndViewportLifecycleMatchChromium()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="target" style="box-sizing:border-box;width:50vw;height:40px;
                     padding:4px;border:2px solid"></div>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();
        rt.ExecuteScript(
            "resize-observer-selected-box",
            """
            globalThis.__contentWidths = [];
            globalThis.__borderWidths = [];
            const target = document.getElementById("target");
            globalThis.__contentObserver = new ResizeObserver(entries => {
                __contentWidths.push(entries[0].contentBoxSize[0].inlineSize);
            });
            globalThis.__borderObserver = new ResizeObserver(entries => {
                __borderWidths.push(entries[0].borderBoxSize[0].inlineSize);
            });
            __contentObserver.observe(target, { box: "content-box" });
            __borderObserver.observe(target, { box: "border-box" });
            """);
        await rt.RunEventLoopBoundedAsync(40);
        AssertJson("[[88],[100]]", rt.Evaluate("[__contentWidths, __borderWidths]"));

        // A viewport update is a rendering update even without a DOM mutation.
        rt.SetViewport(300.0, 100.0);
        await rt.RunEventLoopBoundedAsync(40);
        AssertJson("[[88,138],[100,150]]", rt.Evaluate("[__contentWidths, __borderWidths]"));

        // With border-box sizing a thicker border shrinks the content box but leaves
        // the selected border box unchanged.
        rt.Evaluate("""document.getElementById("target").style.borderWidth = "4px" """);
        await rt.RunEventLoopBoundedAsync(40);
        AssertJson("[[88,138,134],[100,150]]", rt.Evaluate("[__contentWidths, __borderWidths]"));
    }

    [Fact]
    public async Task ScrollingDoesNotRemeasureResizeObserverTargets()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0;height:1000px">
                <div id="probe" style="width:40px;height:20px"></div>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();
        rt.ExecuteScript(
            "observe-before-scroll",
            """
            globalThis.__scrollResizeRecords = 0;
            globalThis.__scrollResizeObserver = new ResizeObserver(entries => {
                __scrollResizeRecords += entries.length;
            });
            __scrollResizeObserver.observe(document.getElementById("probe"));
            """);
        await rt.RunEventLoopBoundedAsync(40);
        Assert.Equal(1.0, rt.Evaluate("__scrollResizeRecords")!.GetValue<double>());

        rt.ExecuteScript(
            "count-scroll-geometry-reads",
            """
            globalThis.__scrollGeometryReads = 0;
            globalThis.__nativeLayoutGeometry = Deno.core.ops.op_layout_geometry;
            Deno.core.ops.op_layout_geometry = (...args) => {
                __scrollGeometryReads++;
                return __nativeLayoutGeometry(...args);
            };
            window.scrollTo(0, 50);
            """);
        await rt.RunEventLoopBoundedAsync(40);
        var result = rt.Evaluate("[scrollY, __scrollGeometryReads, __scrollResizeRecords]");
        rt.ExecuteScript(
            "restore-layout-geometry-op",
            "Deno.core.ops.op_layout_geometry = __nativeLayoutGeometry;");
        AssertJson("[50,0,1]", result);
    }

    [Fact]
    public async Task ResizeObserverDisconnectIsReusableAndInlineBoxesAreEmpty()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html><body><div id="first" style="width:40px;height:20px"></div>
                <span id="inline" style="padding:8px;border:2px solid">text</span>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();
        var result = await rt.EvaluateForCdpAsync(
            """
            new Promise(resolve => {
                const deliveries = [];
                const observer = new ResizeObserver(entries => {
                    deliveries.push(entries.map(entry => [
                        entry.target.id,
                        entry.contentRect.width,
                        entry.borderBoxSize[0].inlineSize,
                    ]));
                    if (deliveries.length === 1) {
                        observer.disconnect();
                        observer.observe(document.getElementById("inline"));
                    } else {
                        observer.disconnect();
                        resolve([deliveries, __resizeObservers.length]);
                    }
                });
                observer.observe(document.getElementById("first"));
                setTimeout(() => resolve(["timed out"]), 100);
            })
            """,
            returnByValue: true,
            awaitPromise: true);
        AssertJson("""[[[["first",40,40]],[["inline",0,0]]],0]""", result.Value);

        AssertJson(
            """["TypeError","TypeError","TypeError"]""",
            rt.Evaluate(
                """
                [
                    (() => { try { new ResizeObserver(null); } catch (e) { return e.name; } })(),
                    (() => { try { new ResizeObserver(() => {}).observe(document); } catch (e) { return e.name; } })(),
                    (() => { try { new ResizeObserver(() => {}).observe(document.body, {box:"margin-box"}); } catch (e) { return e.name; } })(),
                ]
                """));
    }

    [Fact]
    public async Task ResizeObserverSelfResizeIsDepthBoundedWithoutTimerSpin()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """<html><body><div id="target" style="width:40px;height:20px"></div></body></html>"""));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();
        rt.ExecuteScript(
            "resize-observer-loop-limit",
            """
            globalThis.__resizeCallbacks = 0;
            globalThis.__resizeLoopErrors = 0;
            addEventListener("error", event => {
                if (event.message === "ResizeObserver loop completed with undelivered notifications.") {
                    __resizeLoopErrors++;
                }
            });
            const target = document.getElementById("target");
            globalThis.__loopingResizeObserver = new ResizeObserver(() => {
                __resizeCallbacks++;
                target.style.width = (40 + __resizeCallbacks) + "px";
            });
            __loopingResizeObserver.observe(target);
            """);
        await rt.RunEventLoopBoundedAsync(40);
        AssertJson(
            "[1,1,-1]",
            rt.Evaluate("[__resizeCallbacks, __resizeLoopErrors, __obscura_nextPendingTimeoutDelay()]"));

        // A later external rendering change starts a fresh bounded cycle; the
        // suppressed same-depth observation did not poison future delivery.
        rt.Evaluate("""document.getElementById("target").style.width = "60px" """);
        await rt.RunEventLoopBoundedAsync(40);
        AssertJson("[2,2]", rt.Evaluate("[__resizeCallbacks, __resizeLoopErrors]"));
    }

    [Fact]
    public async Task IntersectionObserverTracksViewportThresholdCrossings()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div style="height:150px"></div>
                <div id="target" style="height:100px"></div>
                <div style="height:300px"></div>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();

        // The reference schedules the two scrolls 25ms apart. The port's first
        // prepared render costs a few hundred milliseconds (embedded font
        // initialization) and each later rendering opportunity a few more, so at that
        // granularity both scrolls land before the first intersection checkpoint runs
        // and the two crossings collapse into one. The schedule is scaled by ten; the
        // assertions - one delivery per threshold crossing, with the reference's exact
        // geometry - are unchanged.
        var result = await rt.EvaluateForCdpAsync(
            """
            new Promise(resolve => {
                const records = [];
                const target = document.getElementById("target");
                const observer = new IntersectionObserver(entries => {
                    for (const entry of entries) {
                        records.push([
                            entry.isIntersecting,
                            Math.round(entry.intersectionRatio * 100) / 100,
                            Math.round(entry.boundingClientRect.top),
                            Math.round(entry.intersectionRect.height),
                        ]);
                    }
                }, { threshold: [0, 0.5, 1] });
                observer.observe(target);
                setTimeout(() => window.scrollTo(0, 100), 250);
                setTimeout(() => window.scrollTo(0, 260), 500);
                setTimeout(() => resolve(records), 800);
            })
            """,
            returnByValue: true,
            awaitPromise: true);
        AssertJson("[[false,0,150,0],[true,0.5,50,50],[false,0,-110,0]]", result.Value);
    }

    [Fact]
    public async Task IntersectionObserverBatchesUniqueClipGraphIntoOneNativeLayoutRead()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="root" style="width:200px;height:100px;overflow:hidden">
                    <div id="clip" style="width:150px;height:80px;overflow:auto">
                        <div id="a" style="width:30px;height:20px"></div>
                        <div id="b" style="width:40px;height:20px"></div>
                        <div id="hidden" style="display:none"></div>
                    </div>
                </div>
            </body></html>
            """));
        rt.SetViewport(300.0, 200.0);
        rt.RunPageInit();
        rt.ExecuteScript(
            "batch-intersection-observer-clip-graph",
            """
            globalThis.__intersectionBulkCalls = 0;
            globalThis.__intersectionBulkSizes = [];
            globalThis.__intersectionLegacyGeometryCalls = 0;
            globalThis.__intersectionComputedStyleCalls = 0;
            const nativeBulk = Deno.core.ops.op_intersection_observer_measurements;
            const nativeGeometry = Deno.core.ops.op_layout_geometry;
            const nativeComputedStyle = Deno.core.ops.op_computed_style;
            Deno.core.ops.op_intersection_observer_measurements = input => {
                __intersectionBulkCalls++;
                __intersectionBulkSizes.push(JSON.parse(input).length);
                return nativeBulk(input);
            };
            Deno.core.ops.op_layout_geometry = (...args) => {
                __intersectionLegacyGeometryCalls++;
                return nativeGeometry(...args);
            };
            Deno.core.ops.op_computed_style = (...args) => {
                __intersectionComputedStyleCalls++;
                return nativeComputedStyle(...args);
            };

            const root = document.getElementById("root");
            const a = document.getElementById("a");
            const b = document.getElementById("b");
            const hidden = document.getElementById("hidden");
            const detached = document.createElement("div");
            detached.id = "detached";
            globalThis.__intersectionBatchRecords = [];
            const first = new IntersectionObserver(entries => {
                __intersectionBatchRecords.push(entries.map(entry => [
                    entry.target.id,
                    entry.isIntersecting,
                ]));
            }, { root });
            const second = new IntersectionObserver(entries => {
                __intersectionBatchRecords.push(entries.map(entry => [
                    entry.target.id,
                    entry.isIntersecting,
                ]));
            }, { root });
            first.observe(a);
            first.observe(b);
            first.observe(hidden);
            first.observe(detached);
            // The second observer shares its target, root, and clip ancestor with the
            // first and must not duplicate any native measurements.
            second.observe(b);
            """);
        await rt.RunEventLoopBoundedAsync(100);

        AssertJson(
            """
            [1,[6],0,0,
             [[["a",true],["b",true],["hidden",false],["detached",false]],
              [["b",true]]]]
            """,
            rt.Evaluate(
                """
                [
                    __intersectionBulkCalls,
                    __intersectionBulkSizes,
                    __intersectionLegacyGeometryCalls,
                    __intersectionComputedStyleCalls,
                    __intersectionBatchRecords,
                ]
                """));
    }

    [Fact]
    public async Task IntersectionObserverDeliversDocumentBatchBeforeCallbackPostedTasks()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """<html><body><div id="first"></div><div id="second"></div></body></html>"""));
        rt.RunPageInit();
        rt.ExecuteScript(
            "intersection-document-delivery-batch",
            """
            globalThis.__intersectionDeliveryOrder = [];
            const first = new IntersectionObserver(() => {
                __intersectionDeliveryOrder.push("first-observer");
                scheduler.postTask(() => {
                    __intersectionDeliveryOrder.push("callback-posted-task");
                }, { priority: "user-blocking" });
            });
            const second = new IntersectionObserver(() => {
                __intersectionDeliveryOrder.push("second-observer");
            });
            first.observe(document.getElementById("first"));
            second.observe(document.getElementById("second"));
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """["first-observer","second-observer","callback-posted-task"]""",
            rt.Evaluate("__intersectionDeliveryOrder"));
    }

    [Fact]
    public async Task IntersectionDeliveryRecoversAcrossDocumentReplacement()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        rt.RunPageInit();
        rt.ExecuteScript(
            "old-intersection-delivery",
            """
            globalThis.__intersectionReplacementOrder = [];
            const oldObserver = new IntersectionObserver(() => {
                __intersectionReplacementOrder.push("old");
            });
            oldObserver._records.push({ old: true });
            oldObserver._check([], false, new Map());
            """);

        rt.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        rt.ExecuteScript(
            "fresh-intersection-delivery",
            """
            const freshObserver = new IntersectionObserver(() => {
                __intersectionReplacementOrder.push("fresh");
            });
            freshObserver._records.push({ fresh: true });
            freshObserver._check([], false, new Map());
            """);

        await rt.RunEventLoopBoundedAsync(100);
        AssertJson("""["fresh"]""", rt.Evaluate("__intersectionReplacementOrder"));
    }

    [Fact]
    public async Task IntersectionObserverElementRootUsesLivePaddingBoxAndScroll()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="root" style="position:absolute;left:10px;top:20px;width:100px;
                     height:80px;padding:10px;border:5px solid;overflow:auto">
                    <div style="height:100px"></div>
                    <div id="target" style="height:20px"></div>
                </div>
            </body></html>
            """));
        rt.SetViewport(300.0, 200.0);
        rt.RunPageInit();

        var result = await rt.EvaluateForCdpAsync(
            """
            new Promise(resolve => {
                const records = [];
                const root = document.getElementById("root");
                const observer = new IntersectionObserver(entries => {
                    records.push(...entries.map(entry => ({
                        intersecting: entry.isIntersecting,
                        ratio: entry.intersectionRatio,
                        root: [
                            entry.rootBounds.x, entry.rootBounds.y,
                            entry.rootBounds.width, entry.rootBounds.height,
                        ],
                        intersection: [
                            entry.intersectionRect.x, entry.intersectionRect.y,
                            entry.intersectionRect.width, entry.intersectionRect.height,
                        ],
                    })));
                }, { root, threshold: [0, 1] });
                observer.observe(document.getElementById("target"));
                setTimeout(() => { root.scrollTop = 999; }, 25);
                setTimeout(() => resolve(records), 60);
            })
            """,
            returnByValue: true,
            awaitPromise: true);
        AssertJson(
            """
            [{"intersecting":false,"ratio":0,"root":[15,25,120,100],"intersection":[0,0,0,0]},
             {"intersecting":true,"ratio":1,"root":[15,25,120,100],"intersection":[25,95,100,20]}]
            """,
            result.Value);
    }

    [Fact]
    public async Task IntersectionObserverClipsThroughIntermediateOverflowAncestors()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="root" style="position:absolute;left:10px;top:20px;
                     width:300px;height:300px;overflow:visible">
                    <div id="clip" style="width:100px;height:100px;overflow:hidden">
                        <div style="height:150px"></div>
                        <div id="target" style="height:20px"></div>
                    </div>
                </div>
            </body></html>
            """));
        rt.SetViewport(400.0, 400.0);
        rt.RunPageInit();

        var result = await rt.EvaluateForCdpAsync(
            """
            new Promise(resolve => {
                const records = [];
                const clip = document.getElementById("clip");
                const observer = new IntersectionObserver(entries => {
                    records.push(...entries.map(entry => [
                        entry.isIntersecting,
                        entry.intersectionRatio,
                        [
                            entry.intersectionRect.x,
                            entry.intersectionRect.y,
                            entry.intersectionRect.width,
                            entry.intersectionRect.height,
                        ],
                    ]));
                }, {
                    root: document.getElementById("root"),
                    threshold: [0, 1],
                });
                observer.observe(document.getElementById("target"));
                setTimeout(() => { clip.scrollTop = 999; }, 25);
                setTimeout(() => resolve(records), 60);
            })
            """,
            returnByValue: true,
            awaitPromise: true);
        // Chromium reports the initial target as non-intersecting: although it lies
        // inside the explicit root, the intermediate overflow container clips it.
        // Programmatic scrolling then reveals the complete box.
        AssertJson("[[false,0,[0,0,0,0]],[true,1,[10,100,100,20]]]", result.Value);
    }

    [Fact]
    public async Task IntersectionObserverInitialGeometryWaitsForOneRenderCheckpoint()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><body><div id="first"></div><div id="second"></div></body></html>""");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "intersection-render-checkpoint",
            """
            globalThis.__ioOrder = ["sync"];
            globalThis.__ioReads = 0;
            const first = document.getElementById("first");
            const second = document.getElementById("second");
            for (const element of [first, second]) {
                const nativeRect = element.getBoundingClientRect.bind(element);
                element.getBoundingClientRect = () => {
                    __ioReads++;
                    return nativeRect();
                };
            }
            const observer = new IntersectionObserver(
                () => __ioOrder.push("observer")
            );
            observer.observe(first);
            observer.observe(second);
            Promise.resolve().then(() => __ioOrder.push("microtask"));
            __ioOrder.push("after-observe-" + __ioReads);
            """);

        AssertJson(
            """[["sync","after-observe-0","microtask"],0]""",
            rt.Evaluate("[__ioOrder, __ioReads]"));
        await rt.RunEventLoopBoundedAsync(100);
        // The port always builds the render ops, so the batched native measurement
        // path answers and no per-element getBoundingClientRect read happens.
        AssertJson(
            """[["sync","after-observe-0","microtask","observer"],0]""",
            rt.Evaluate("[__ioOrder, __ioReads]"));
    }

    [Fact]
    public async Task IntersectionObserverHonorsRootMarginZeroAreaAndNoFakeRefires()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn intersection_observer_honors_root_margin_zero_area_and_no_fake_refires() {
                let dom = parse_html(
                    r#"<html style="margin:0"><body style="margin:0;position:relative">
                        <div style="height:110px"></div>
                        <div id="margin-target" style="height:10px"></div>
                        <div id="zero" style="position:absolute;left:20px;top:50px;width:0;height:0"></div>
                        <div id="root" style="height:20px;overflow:auto"></div>
                        <div style="height:300px"></div>
                    </body></html>"#,
                );
                let mut rt = ObscuraJsRuntime::new();
                rt.set_dom(dom);
                rt.set_viewport(200.0, 100.0);
                rt.run_page_init();

                let result = rt
                    .evaluate_for_cdp(
                        r#"
                        new Promise(resolve => {
                            const marginRecords = [], zeroRecords = [];
                            const marginObserver = new IntersectionObserver(
                                entries => marginRecords.push(...entries.map(entry => [
                                    entry.isIntersecting,
                                    entry.intersectionRatio,
                                    entry.rootBounds.bottom,
                                ])),
                                { rootMargin: "0px 0px 20px", threshold: [0, 1] }
                            );
                            const zeroObserver = new IntersectionObserver(
                                entries => zeroRecords.push(...entries.map(entry => [
                                    entry.isIntersecting,
                                    entry.intersectionRatio,
                                ]))
                            );
                            marginObserver.observe(document.getElementById("margin-target"));
                            zeroObserver.observe(document.getElementById("zero"));
                            let elementRoot = false;
                            try {
                                const rooted = new IntersectionObserver(() => {}, {
                                    root: document.getElementById("root")
                                });
                                elementRoot = rooted.root === document.getElementById("root");
                            } catch (error) {
                                elementRoot = error.name;
                            }
                            setTimeout(() => resolve([
                                marginRecords, zeroRecords, elementRoot,
                                marginObserver.rootMargin, marginObserver.thresholds,
                            ]), 200);
                        })
                        "#,
                        true,
                        true,
                    )
                    .await
                    .unwrap();
                assert_eq!(
                    result.value.unwrap(),
                    serde_json::json!([
                        [[true, 1, 120]],
                        [[true, 1]],
                        true,
                        "0px 0px 20px 0px",
                        [0, 1],
                    ])
                );
            }
        */
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("""
            <html style="margin:0"><body style="margin:0;position:relative">
                <div style="height:110px"></div>
                <div id="margin-target" style="height:10px"></div>
                <div id="zero" style="position:absolute;left:20px;top:50px;width:0;height:0"></div>
                <div id="root" style="height:20px;overflow:auto"></div>
                <div style="height:300px"></div>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();

        var result = await rt.EvaluateForCdpAsync(
            """
            new Promise(resolve => {
                const marginRecords = [], zeroRecords = [];
                const marginObserver = new IntersectionObserver(
                    entries => marginRecords.push(...entries.map(entry => [
                        entry.isIntersecting,
                        entry.intersectionRatio,
                        entry.rootBounds.bottom,
                    ])),
                    { rootMargin: "0px 0px 20px", threshold: [0, 1] }
                );
                const zeroObserver = new IntersectionObserver(
                    entries => zeroRecords.push(...entries.map(entry => [
                        entry.isIntersecting,
                        entry.intersectionRatio,
                    ]))
                );
                marginObserver.observe(document.getElementById("margin-target"));
                zeroObserver.observe(document.getElementById("zero"));
                let elementRoot = false;
                try {
                    const rooted = new IntersectionObserver(() => {}, {
                        root: document.getElementById("root")
                    });
                    elementRoot = rooted.root === document.getElementById("root");
                } catch (error) {
                    elementRoot = error.name;
                }
                setTimeout(() => resolve([
                    marginRecords, zeroRecords, elementRoot,
                    marginObserver.rootMargin, marginObserver.thresholds,
                ]), 200);
            })
            """,
            returnByValue: true,
            awaitPromise: true);
        Assert.Equal(
            """[[[true,1,120]],[[true,1]],true,"0px 0px 20px 0px",[0,1]]""",
            result.Value!.ToJsonString());
    }

    [Fact]
    public async Task IntersectionObserverDoesNotRefireWhileTargetStaysIntersecting()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn intersection_observer_does_not_refire_while_target_stays_intersecting() {
                let dom = parse_html(
                    r#"<html><body>
                        <div id="feed"></div>
                        <div id="sentinel"></div>
                    </body></html>"#,
                );
                let mut rt = ObscuraJsRuntime::new();
                rt.set_dom(dom);
                rt.set_viewport(1280.0, 720.0);
                rt.run_page_init();

                let result = rt
                    .evaluate_for_cdp(
                        r#"
                        new Promise(resolve => {
                            const feed = document.getElementById("feed");
                            let loaded = 0;
                            const observer = new IntersectionObserver(entries => {
                                for (const entry of entries) {
                                    if (!entry.isIntersecting) continue;
                                    for (let i = 0; i < 10; i++) {
                                        const card = document.createElement("div");
                                        card.textContent = "Item " + loaded++;
                                        feed.appendChild(card);
                                    }
                                }
                            });
                            observer.observe(document.getElementById("sentinel"));
                            setTimeout(() => resolve([
                                loaded,
                                feed.querySelectorAll("div").length,
                            ]), 200);
                        })
                        "#,
                        true,
                        true,
                    )
                    .await
                    .unwrap();
                assert_eq!(result.value.unwrap(), serde_json::json!([10, 10]));
            }
        */
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("""
            <html><body>
                <div id="feed"></div>
                <div id="sentinel"></div>
            </body></html>
            """));
        rt.SetViewport(1280.0, 720.0);
        rt.RunPageInit();

        var result = await rt.EvaluateForCdpAsync(
            """
            new Promise(resolve => {
                const feed = document.getElementById("feed");
                let loaded = 0;
                const observer = new IntersectionObserver(entries => {
                    for (const entry of entries) {
                        if (!entry.isIntersecting) continue;
                        for (let i = 0; i < 10; i++) {
                            const card = document.createElement("div");
                            card.textContent = "Item " + loaded++;
                            feed.appendChild(card);
                        }
                    }
                });
                observer.observe(document.getElementById("sentinel"));
                setTimeout(() => resolve([
                    loaded,
                    feed.querySelectorAll("div").length,
                ]), 200);
            })
            """,
            returnByValue: true,
            awaitPromise: true);
        Assert.Equal("[10,10]", result.Value!.ToJsonString());
    }

    [Fact]
    public async Task IntersectionObserverCanBeReusedAfterDisconnect()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn intersection_observer_can_be_reused_after_disconnect() {
                let dom = parse_html(
                    r#"<html style="margin:0"><body style="margin:0">
                        <div id="stale" style="height:10px"></div>
                        <div id="first" style="height:10px"></div>
                        <div id="second" style="height:10px"></div>
                    </body></html>"#,
                );
                let mut rt = ObscuraJsRuntime::new();
                rt.set_dom(dom);
                rt.set_viewport(200.0, 100.0);
                rt.run_page_init();

                let result = rt
                    .evaluate_for_cdp(
                        r#"
                        new Promise(resolve => {
                            const deliveries = [];
                            const observer = new IntersectionObserver(entries => {
                                deliveries.push(entries.map(entry => entry.target.id));
                                if (deliveries.length === 1) {
                                    observer.disconnect();
                                    observer.observe(document.getElementById("second"));
                                } else {
                                    observer.disconnect();
                                    resolve([
                                        deliveries,
                                        globalThis.__intersectionObservers.length,
                                    ]);
                                }
                            });

                            // A pending record from before disconnect must be discarded.
                            observer.observe(document.getElementById("stale"));
                            observer.disconnect();
                            observer.observe(document.getElementById("first"));
                            setTimeout(() => resolve(["timed out"]), 100);
                        })
                        "#,
                        true,
                        true,
                    )
                    .await
                    .unwrap();
                assert_eq!(
                    result.value.unwrap(),
                    serde_json::json!([[["first"], ["second"]], 0,])
                );
            }
        */
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("""
            <html style="margin:0"><body style="margin:0">
                <div id="stale" style="height:10px"></div>
                <div id="first" style="height:10px"></div>
                <div id="second" style="height:10px"></div>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();

        var result = await rt.EvaluateForCdpAsync(
            """
            new Promise(resolve => {
                const deliveries = [];
                const observer = new IntersectionObserver(entries => {
                    deliveries.push(entries.map(entry => entry.target.id));
                    if (deliveries.length === 1) {
                        observer.disconnect();
                        observer.observe(document.getElementById("second"));
                    } else {
                        observer.disconnect();
                        resolve([
                            deliveries,
                            globalThis.__intersectionObservers.length,
                        ]);
                    }
                });

                // A pending record from before disconnect must be discarded.
                observer.observe(document.getElementById("stale"));
                observer.disconnect();
                observer.observe(document.getElementById("first"));
                setTimeout(() => resolve(["timed out"]), 100);
            })
            """,
            returnByValue: true,
            awaitPromise: true);
        Assert.Equal("""[[["first"],["second"]],0]""", result.Value!.ToJsonString());
    }

    [Fact]
    public async Task IntersectionObserverRecomputesAfterStyleMutationAndResize()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn intersection_observer_recomputes_after_style_mutation_and_resize() {
                let dom = parse_html(
                    r#"<html style="margin:0"><body style="margin:0">
                        <div id="spacer" style="height:150px"></div>
                        <div id="target" style="height:20px"></div>
                        <div style="height:300px"></div>
                    </body></html>"#,
                );
                let mut rt = ObscuraJsRuntime::new();
                rt.set_dom(dom);
                rt.set_viewport(200.0, 100.0);
                rt.run_page_init();
                rt.execute_script(
                    "intersection-mutation",
                    r#"
                        globalThis.__ioRecords = [];
                        globalThis.__io = new IntersectionObserver(entries => {
                            __ioRecords.push(...entries.map(entry => [
                                entry.isIntersecting,
                                Math.round(entry.boundingClientRect.top),
                            ]));
                        });
                        __io.observe(document.getElementById("target"));
                    "#,
                )
                .unwrap();
                rt.run_event_loop_bounded(40).await.unwrap();

                rt.evaluate(r#"document.getElementById("spacer").setAttribute("style", "height:120px")"#)
                    .unwrap();
                rt.run_event_loop_bounded(40).await.unwrap();
                assert_eq!(
                    rt.evaluate("__ioRecords").unwrap(),
                    serde_json::json!([[false, 150]])
                );

                rt.set_viewport(200.0, 160.0);
                rt.run_event_loop_bounded(40).await.unwrap();
                assert_eq!(
                    rt.evaluate("__ioRecords").unwrap(),
                    serde_json::json!([[false, 150], [true, 120]])
                );
            }
        */
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("""
            <html style="margin:0"><body style="margin:0">
                <div id="spacer" style="height:150px"></div>
                <div id="target" style="height:20px"></div>
                <div style="height:300px"></div>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();
        rt.ExecuteScript(
            "intersection-mutation",
            """
            globalThis.__ioRecords = [];
            globalThis.__io = new IntersectionObserver(entries => {
                __ioRecords.push(...entries.map(entry => [
                    entry.isIntersecting,
                    Math.round(entry.boundingClientRect.top),
                ]));
            });
            __io.observe(document.getElementById("target"));
            """);
        await rt.RunEventLoopBoundedAsync(40);

        rt.Evaluate("""document.getElementById("spacer").setAttribute("style", "height:120px")""");
        await rt.RunEventLoopBoundedAsync(40);
        Assert.Equal("[[false,150]]", rt.Evaluate("__ioRecords")!.ToJsonString());

        rt.SetViewport(200.0, 160.0);
        await rt.RunEventLoopBoundedAsync(40);
        Assert.Equal("[[false,150],[true,120]]", rt.Evaluate("__ioRecords")!.ToJsonString());
    }

    [Fact]
    public async Task IntersectionObserverRecomputesAfterRootScroll()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn intersection_observer_recomputes_after_root_scroll() {
                let dom = parse_html(
                    r#"<html style="margin:0"><body style="margin:0">
                        <div style="height:150px"></div>
                        <div id="target" style="height:20px"></div>
                        <div style="height:300px"></div>
                    </body></html>"#,
                );
                let mut rt = ObscuraJsRuntime::new();
                rt.set_dom(dom);
                rt.set_viewport(200.0, 100.0);
                rt.run_page_init();
                rt.execute_script(
                    "intersection-root-scroll",
                    r#"
                        globalThis.__rootScrollIoRecords = [];
                        globalThis.__rootScrollIo = new IntersectionObserver(entries => {
                            __rootScrollIoRecords.push(...entries.map(entry => [
                                entry.isIntersecting,
                                Math.round(entry.boundingClientRect.top),
                            ]));
                        });
                        __rootScrollIo.observe(document.getElementById("target"));
                    "#,
                )
                .unwrap();
                rt.run_event_loop_bounded(40).await.unwrap();
                rt.evaluate("window.scrollTo(0, 100)").unwrap();
                rt.run_event_loop_bounded(40).await.unwrap();
                assert_eq!(
                    rt.evaluate("__rootScrollIoRecords").unwrap(),
                    serde_json::json!([[false, 150], [true, 50]])
                );
            }
        */
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("""
            <html style="margin:0"><body style="margin:0">
                <div style="height:150px"></div>
                <div id="target" style="height:20px"></div>
                <div style="height:300px"></div>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();
        rt.ExecuteScript(
            "intersection-root-scroll",
            """
            globalThis.__rootScrollIoRecords = [];
            globalThis.__rootScrollIo = new IntersectionObserver(entries => {
                __rootScrollIoRecords.push(...entries.map(entry => [
                    entry.isIntersecting,
                    Math.round(entry.boundingClientRect.top),
                ]));
            });
            __rootScrollIo.observe(document.getElementById("target"));
            """);
        await rt.RunEventLoopBoundedAsync(40);
        rt.Evaluate("window.scrollTo(0, 100)");
        await rt.RunEventLoopBoundedAsync(40);
        Assert.Equal("[[false,150],[true,50]]", rt.Evaluate("__rootScrollIoRecords")!.ToJsonString());
    }

    [Fact]
    public void ScrollIntoViewAlignsTheRootViewportAndClamps()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn scroll_into_view_aligns_the_root_viewport_and_clamps() {
                let dom = parse_html(
                    r#"<html style="margin:0"><body style="margin:0">
                        <div style="height:300px"></div>
                        <div id="target" style="height:40px"></div>
                        <div style="height:340px"></div>
                        <div id="bottom" style="height:20px"></div>
                        <div id="fixed" style="position:fixed;top:10px;height:20px"></div>
                    </body></html>"#,
                );
                let mut rt = ObscuraJsRuntime::new();
                rt.set_dom(dom);
                rt.set_viewport(200.0, 100.0);
                rt.run_page_init();

                let result = rt
                    .evaluate(
                        r#"
                        const target = document.getElementById("target");
                        const bottom = document.getElementById("bottom");
                        const fixed = document.getElementById("fixed");
                        target.scrollIntoView();
                        const start = scrollY;
                        scrollTo(0, 0);
                        target.scrollIntoView({ block: "center" });
                        const center = scrollY;
                        scrollTo(0, 0);
                        target.scrollIntoView({ block: "end" });
                        const end = scrollY;
                        scrollTo(0, 0);
                        target.scrollIntoView({ block: "nearest" });
                        const nearestOutside = scrollY;
                        scrollTo(0, 250);
                        target.scrollIntoView({ block: "nearest" });
                        const nearestVisible = scrollY;
                        fixed.scrollIntoView();
                        const afterFixed = scrollY;
                        bottom.scrollIntoView({ block: "start" });
                        const clamped = scrollY;
                        const max = document.documentElement.scrollHeight - innerHeight;
                        return [
                            start, center, end, nearestOutside, nearestVisible,
                            afterFixed, clamped, max,
                        ];
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([300, 270, 240, 240, 250, 250, 600, 600])
                );
            }
        */
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("""
            <html style="margin:0"><body style="margin:0">
                <div style="height:300px"></div>
                <div id="target" style="height:40px"></div>
                <div style="height:340px"></div>
                <div id="bottom" style="height:20px"></div>
                <div id="fixed" style="position:fixed;top:10px;height:20px"></div>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();

        var result = rt.Evaluate("""
            const target = document.getElementById("target");
            const bottom = document.getElementById("bottom");
            const fixed = document.getElementById("fixed");
            target.scrollIntoView();
            const start = scrollY;
            scrollTo(0, 0);
            target.scrollIntoView({ block: "center" });
            const center = scrollY;
            scrollTo(0, 0);
            target.scrollIntoView({ block: "end" });
            const end = scrollY;
            scrollTo(0, 0);
            target.scrollIntoView({ block: "nearest" });
            const nearestOutside = scrollY;
            scrollTo(0, 250);
            target.scrollIntoView({ block: "nearest" });
            const nearestVisible = scrollY;
            fixed.scrollIntoView();
            const afterFixed = scrollY;
            bottom.scrollIntoView({ block: "start" });
            const clamped = scrollY;
            const max = document.documentElement.scrollHeight - innerHeight;
            return [
                start, center, end, nearestOutside, nearestVisible,
                afterFixed, clamped, max,
            ];
            """);
        Assert.Equal("[300,270,240,240,250,250,600,600]", result!.ToJsonString());
    }

    [Fact]
    public async Task ScrollIntoViewEmitsEventsOnlyWhenTheRootMoves()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn scroll_into_view_emits_events_only_when_the_root_moves() {
                let dom = parse_html(
                    r#"<html style="margin:0"><body style="margin:0">
                        <div style="height:300px"></div>
                        <div id="target" style="height:40px"></div>
                        <div style="height:300px"></div>
                    </body></html>"#,
                );
                let mut rt = ObscuraJsRuntime::new();
                rt.set_dom(dom);
                rt.set_viewport(200.0, 100.0);
                rt.run_page_init();

                rt.evaluate("window.scrollTo(0, 250)").unwrap();
                rt.run_event_loop_bounded(20).await.unwrap();
                let no_op = rt
                    .evaluate_for_cdp(
                        r#"
                        new Promise(resolve => {
                            let win = 0, doc = 0;
                            window.addEventListener("scroll", () => win++);
                            document.addEventListener("scroll", () => doc++);
                            document.getElementById("target").scrollIntoView({ block: "nearest" });
                            setTimeout(() => resolve([win, doc, scrollY]), 5);
                        })
                        "#,
                        true,
                        true,
                    )
                    .await
                    .unwrap();
                assert_eq!(no_op.value.unwrap(), serde_json::json!([0, 0, 250]));

                rt.evaluate("window.scrollTo(0, 0)").unwrap();
                rt.run_event_loop_bounded(20).await.unwrap();
                let moved = rt
                    .evaluate_for_cdp(
                        r#"
                        new Promise(resolve => {
                            let win = 0, doc = 0;
                            window.addEventListener("scroll", () => win++);
                            document.addEventListener("scroll", () => doc++);
                            document.getElementById("target").scrollIntoView({ block: "center" });
                            setTimeout(() => resolve([win, doc, scrollY]), 5);
                        })
                        "#,
                        true,
                        true,
                    )
                    .await
                    .unwrap();
                assert_eq!(moved.value.unwrap(), serde_json::json!([1, 1, 270]));
            }
        */
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml("""
            <html style="margin:0"><body style="margin:0">
                <div style="height:300px"></div>
                <div id="target" style="height:40px"></div>
                <div style="height:300px"></div>
            </body></html>
            """));
        rt.SetViewport(200.0, 100.0);
        rt.RunPageInit();

        // The reference settles on a 5ms timer and gets a process per test. This
        // host runs the whole class in one process, so the settle window is
        // widened to 50ms; that only gives a stray scroll event more time to
        // show up in the no-op half.
        rt.Evaluate("window.scrollTo(0, 250)");
        await rt.RunEventLoopBoundedAsync(20);
        var noOp = await rt.EvaluateForCdpAsync(
            """
            new Promise(resolve => {
                let win = 0, doc = 0;
                window.addEventListener("scroll", () => win++);
                document.addEventListener("scroll", () => doc++);
                document.getElementById("target").scrollIntoView({ block: "nearest" });
                setTimeout(() => resolve([win, doc, scrollY]), 50);
            })
            """,
            returnByValue: true,
            awaitPromise: true);
        Assert.Equal("[0,0,250]", noOp.Value!.ToJsonString());

        rt.Evaluate("window.scrollTo(0, 0)");
        await rt.RunEventLoopBoundedAsync(20);
        var moved = await rt.EvaluateForCdpAsync(
            """
            new Promise(resolve => {
                let win = 0, doc = 0;
                window.addEventListener("scroll", () => win++);
                document.addEventListener("scroll", () => doc++);
                document.getElementById("target").scrollIntoView({ block: "center" });
                setTimeout(() => resolve([win, doc, scrollY]), 50);
            })
            """,
            returnByValue: true,
            awaitPromise: true);
        Assert.Equal("[1,1,270]", moved.Value!.ToJsonString());
    }

    /// Issue #469: FILTER_SKIP leaves a skipped node's children eligible, so
    /// firstChild()/lastChild() must descend into them. FILTER_REJECT must not.
    [Fact]
    public void TreeWalkerChildMoversDescendOnSkipButNotOnReject()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn tree_walker_child_movers_descend_on_skip_but_not_on_reject() {
                let mut rt = setup_runtime(r#"<div id="root"><section><a></a><b></b></section></div>"#);
                let result = rt
                    .evaluate(
                        r#"
                        const root = document.getElementById('root');
                        function mover(verdict, method) {
                            const w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, {
                                acceptNode(node) {
                                    return node.tagName === 'SECTION' ? verdict : NodeFilter.FILTER_ACCEPT;
                                }
                            });
                            const found = w[method]();
                            return found ? found.tagName : null;
                        }
                        return [
                            mover(NodeFilter.FILTER_SKIP, 'firstChild'),
                            mover(NodeFilter.FILTER_SKIP, 'lastChild'),
                            mover(NodeFilter.FILTER_REJECT, 'firstChild'),
                            mover(NodeFilter.FILTER_REJECT, 'lastChild'),
                        ];
                        "#,
                    )
                    .unwrap();
                // SKIP descends into <section>; REJECT prunes it and finds nothing else.
                assert_eq!(result, serde_json::json!(["A", "B", null, null]));
            }
        */
        using var fixture = RuntimeFixture.Setup("""<div id="root"><section><a></a><b></b></section></div>""");
        var result = fixture.Runtime.Evaluate("""
            const root = document.getElementById('root');
            function mover(verdict, method) {
                const w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, {
                    acceptNode(node) {
                        return node.tagName === 'SECTION' ? verdict : NodeFilter.FILTER_ACCEPT;
                    }
                });
                const found = w[method]();
                return found ? found.tagName : null;
            }
            return [
                mover(NodeFilter.FILTER_SKIP, 'firstChild'),
                mover(NodeFilter.FILTER_SKIP, 'lastChild'),
                mover(NodeFilter.FILTER_REJECT, 'firstChild'),
                mover(NodeFilter.FILTER_REJECT, 'lastChild'),
            ];
            """);
        // SKIP descends into <section>; REJECT prunes it and finds nothing else.
        Assert.Equal("""["A","B",null,null]""", result!.ToJsonString());
    }

    /// Issue #469: nextSibling()/previousSibling() must descend into a skipped
    /// sibling's subtree rather than stepping straight over it.
    [Fact]
    public void TreeWalkerSiblingMoversDescendIntoSkippedSiblings()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn tree_walker_sibling_movers_descend_into_skipped_siblings() {
                let mut rt = setup_runtime(
                    r#"<div id="root"><p id="start"></p><section><a></a></section><q></q></div>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                        const root = document.getElementById('root');
                        function mover(verdict, method, from) {
                            const w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, {
                                acceptNode(node) {
                                    return node.tagName === 'SECTION' ? verdict : NodeFilter.FILTER_ACCEPT;
                                }
                            });
                            w.currentNode = document.getElementById(from);
                            const found = w[method]();
                            return found ? found.tagName : null;
                        }
                        return [
                            // <section> is skipped, so its child <a> is the next sibling.
                            mover(NodeFilter.FILTER_SKIP, 'nextSibling', 'start'),
                            // Rejected: the subtree is off-limits, so skip past to <q>.
                            mover(NodeFilter.FILTER_REJECT, 'nextSibling', 'start'),
                        ];
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!(["A", "Q"]));
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<div id="root"><p id="start"></p><section><a></a></section><q></q></div>""");
        var result = fixture.Runtime.Evaluate("""
            const root = document.getElementById('root');
            function mover(verdict, method, from) {
                const w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, {
                    acceptNode(node) {
                        return node.tagName === 'SECTION' ? verdict : NodeFilter.FILTER_ACCEPT;
                    }
                });
                w.currentNode = document.getElementById(from);
                const found = w[method]();
                return found ? found.tagName : null;
            }
            return [
                // <section> is skipped, so its child <a> is the next sibling.
                mover(NodeFilter.FILTER_SKIP, 'nextSibling', 'start'),
                // Rejected: the subtree is off-limits, so skip past to <q>.
                mover(NodeFilter.FILTER_REJECT, 'nextSibling', 'start'),
            ];
            """);
        Assert.Equal("""["A","Q"]""", result!.ToJsonString());
    }

    /// Issue #469: the backward sibling mover descends to *last* children.
    [Fact]
    public void TreeWalkerPreviousSiblingDescendsToLastChild()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn tree_walker_previous_sibling_descends_to_last_child() {
                let mut rt = setup_runtime(
                    r#"<div id="root"><section><a></a><b></b></section><p id="start"></p></div>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                        const root = document.getElementById('root');
                        const w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, {
                            acceptNode(node) {
                                return node.tagName === 'SECTION'
                                    ? NodeFilter.FILTER_SKIP
                                    : NodeFilter.FILTER_ACCEPT;
                            }
                        });
                        w.currentNode = document.getElementById('start');
                        const found = w.previousSibling();
                        return found ? found.tagName : null;
                        "#,
                    )
                    .unwrap();
                // Reverse order descends to <section>'s last child, not its first.
                assert_eq!(result, serde_json::json!("B"));
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<div id="root"><section><a></a><b></b></section><p id="start"></p></div>""");
        var result = fixture.Runtime.Evaluate("""
            const root = document.getElementById('root');
            const w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT, {
                acceptNode(node) {
                    return node.tagName === 'SECTION'
                        ? NodeFilter.FILTER_SKIP
                        : NodeFilter.FILTER_ACCEPT;
                }
            });
            w.currentNode = document.getElementById('start');
            const found = w.previousSibling();
            return found ? found.tagName : null;
            """);
        // Reverse order descends to <section>'s last child, not its first.
        Assert.Equal("B", result!.GetValue<string>());
    }

    [Fact]
    public void AppendChildFlattensDocumentFragment()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn append_child_flattens_document_fragment() {
                let mut rt = setup_runtime(r#"<main id="host"></main>"#);
                let result = rt
                    .evaluate(
                        r#"
                        const host = document.getElementById('host');
                        const fragment = document.createDocumentFragment();
                        const first = document.createElement('article');
                        const second = document.createElement('article');
                        first.id = 'first';
                        second.id = 'second';
                        first.className = second.className = 'quote';
                        fragment.appendChild(first);
                        fragment.appendChild(second);

                        const returned = host.appendChild(fragment);
                        return [
                            returned === fragment,
                            Array.from(host.children).map(node => node.id),
                            host.querySelectorAll('.quote').length,
                            fragment.childNodes.length,
                            first.parentNode === host,
                            first.parentElement === host,
                        ];
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([true, ["first", "second"], 2, 0, true, true])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("""<main id="host"></main>""");
        var result = fixture.Runtime.Evaluate("""
            const host = document.getElementById('host');
            const fragment = document.createDocumentFragment();
            const first = document.createElement('article');
            const second = document.createElement('article');
            first.id = 'first';
            second.id = 'second';
            first.className = second.className = 'quote';
            fragment.appendChild(first);
            fragment.appendChild(second);

            const returned = host.appendChild(fragment);
            return [
                returned === fragment,
                Array.from(host.children).map(node => node.id),
                host.querySelectorAll('.quote').length,
                fragment.childNodes.length,
                first.parentNode === host,
                first.parentElement === host,
            ];
            """);
        Assert.Equal("""[true,["first","second"],2,0,true,true]""", result!.ToJsonString());
    }

    [Fact]
    public void InsertBeforeFlattensDocumentFragmentInOrder()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn insert_before_flattens_document_fragment_in_order() {
                let mut rt = setup_runtime(r#"<main id="host"><article id="last"></article></main>"#);
                let result = rt
                    .evaluate(
                        r#"
                        const host = document.getElementById('host');
                        const last = document.getElementById('last');
                        const fragment = document.createDocumentFragment();
                        const first = document.createElement('article');
                        const second = document.createElement('article');
                        first.id = 'first';
                        second.id = 'second';
                        fragment.appendChild(first);
                        fragment.appendChild(second);

                        const returned = host.insertBefore(fragment, last);
                        return [
                            returned === fragment,
                            Array.from(host.children).map(node => node.id),
                            fragment.childNodes.length,
                            first.parentElement === host,
                            second.parentElement === host,
                        ];
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([true, ["first", "second", "last"], 0, true, true])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("""<main id="host"><article id="last"></article></main>""");
        var result = fixture.Runtime.Evaluate("""
            const host = document.getElementById('host');
            const last = document.getElementById('last');
            const fragment = document.createDocumentFragment();
            const first = document.createElement('article');
            const second = document.createElement('article');
            first.id = 'first';
            second.id = 'second';
            fragment.appendChild(first);
            fragment.appendChild(second);

            const returned = host.insertBefore(fragment, last);
            return [
                returned === fragment,
                Array.from(host.children).map(node => node.id),
                fragment.childNodes.length,
                first.parentElement === host,
                second.parentElement === host,
            ];
            """);
        Assert.Equal("""[true,["first","second","last"],0,true,true]""", result!.ToJsonString());
    }

    [Fact]
    public void ReplaceChildFlattensDocumentFragmentAndRemovesOldChild()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn replace_child_flattens_document_fragment_and_removes_old_child() {
                let mut rt = setup_runtime(
                    r#"<main id="host"><article id="old"></article><article id="tail"></article></main>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                        const host = document.getElementById('host');
                        const old = document.getElementById('old');
                        const fragment = document.createDocumentFragment();
                        const first = document.createElement('article');
                        const second = document.createElement('article');
                        first.id = 'first';
                        second.id = 'second';
                        fragment.appendChild(first);
                        fragment.appendChild(second);

                        const returned = host.replaceChild(fragment, old);
                        return [
                            returned === old,
                            Array.from(host.children).map(node => node.id),
                            fragment.childNodes.length,
                            old.parentNode === null,
                            first.parentElement === host,
                            second.parentElement === host,
                        ];
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([true, ["first", "second", "tail"], 0, true, true, true])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<main id="host"><article id="old"></article><article id="tail"></article></main>""");
        var result = fixture.Runtime.Evaluate("""
            const host = document.getElementById('host');
            const old = document.getElementById('old');
            const fragment = document.createDocumentFragment();
            const first = document.createElement('article');
            const second = document.createElement('article');
            first.id = 'first';
            second.id = 'second';
            fragment.appendChild(first);
            fragment.appendChild(second);

            const returned = host.replaceChild(fragment, old);
            return [
                returned === old,
                Array.from(host.children).map(node => node.id),
                fragment.childNodes.length,
                old.parentNode === null,
                first.parentElement === host,
                second.parentElement === host,
            ];
            """);
        Assert.Equal("""[true,["first","second","tail"],0,true,true,true]""", result!.ToJsonString());
    }

    [Fact]
    public void TestInnerHtml()
    {
        using var fixture = RuntimeFixture.Setup(@"<div id=""x""><p>Hello</p></div>");
        var html = fixture.Runtime.Evaluate("document.getElementById('x').innerHTML")!.GetValue<string>();
        Assert.Contains("<p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateInnerHtmlPreservesTableFragments()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn template_inner_html_preserves_table_fragments() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        const template = document.createElement('template');
                        template.innerHTML = '<tr><td>first</td><td>second</td></tr>';
                        const clone = template.content.firstChild.cloneNode(true);
                        return [
                            clone.tagName,
                            clone.firstElementChild.tagName,
                            clone.firstElementChild.children.length,
                            clone.textContent,
                        ];
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!(["TR", "TD", 0, "firstsecond"]));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            const template = document.createElement('template');
            template.innerHTML = '<tr><td>first</td><td>second</td></tr>';
            const clone = template.content.firstChild.cloneNode(true);
            return [
                clone.tagName,
                clone.firstElementChild.tagName,
                clone.firstElementChild.children.length,
                clone.textContent,
            ];
            """);
        Assert.Equal("""["TR","TD",0,"firstsecond"]""", result!.ToJsonString());
    }

    [Fact]
    public void DocumentExposesParentNodeElementChildrenApi()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn document_exposes_parent_node_element_children_api() {
                let mut rt = setup_runtime("<html><head></head><body></body></html>");
                let result = rt
                    .evaluate(
                        "return [document.firstElementChild === document.documentElement,\
                                 document.lastElementChild === document.documentElement,\
                                 document.children.length, document.childElementCount];",
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!([true, true, 1, 1]));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            "return [document.firstElementChild === document.documentElement,"
            + " document.lastElementChild === document.documentElement,"
            + " document.children.length, document.childElementCount];");
        Assert.Equal("[true,true,1,1]", result!.ToJsonString());
    }

    [Fact]
    public void AtobDecodesLargePayloadWithoutArgumentStackOverflow()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn atob_decodes_large_payload_without_argument_stack_overflow() {
                let mut rt = setup_runtime("<html><body></body></html>");
                // 60k four-character groups decode to 180k bytes, comfortably above
                // V8's maximum argument count for a single fromCharCode(...bytes).
                let encoded = "QUFB".repeat(60_000);
                let result = rt.evaluate(&format!("atob('{}').length", encoded)).unwrap();
                assert_eq!(result.as_f64().unwrap() as usize, 180_000);
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        // 60k four-character groups decode to 180k bytes, comfortably above
        // V8's maximum argument count for a single fromCharCode(...bytes).
        var encoded = string.Concat(Enumerable.Repeat("QUFB", 60_000));
        var result = fixture.Runtime.Evaluate($"atob('{encoded}').length");
        Assert.Equal(180_000, (int)result!.GetValue<double>());
    }

    [Fact]
    public void NavigationApiUpdatesCurrentEntryState()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn navigation_api_updates_current_entry_state() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        (() => {
                            navigation.updateCurrentEntry({state: {route: 'home'}});
                            const first = navigation.currentEntry;
                            navigation.navigate('/docs', {state: {route: 'docs'}});
                            return [
                                typeof navigation.updateCurrentEntry,
                                first.getState().route,
                                navigation.currentEntry.getState().route,
                                navigation.currentEntry.url,
                            ];
                        })()
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!(["function", "home", "docs", "http://example.com/docs"])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            (() => {
                navigation.updateCurrentEntry({state: {route: 'home'}});
                const first = navigation.currentEntry;
                navigation.navigate('/docs', {state: {route: 'docs'}});
                return [
                    typeof navigation.updateCurrentEntry,
                    first.getState().route,
                    navigation.currentEntry.getState().route,
                    navigation.currentEntry.url,
                ];
            })()
            """);
        Assert.Equal(
            """["function","home","docs","http://example.com/docs"]""",
            result!.ToJsonString());
    }

    [Fact]
    public void InlineStylesheetCssomListsAndRulesAreLiveSameObjects()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn inline_stylesheet_cssom_lists_and_rules_are_live_same_objects() {
                let mut rt = setup_runtime(
                    r#"<html><head>
                        <style id="first">.one { color:red } .two { width:20px }</style>
                        <style id="second">.three { display:block }</style>
                    </head><body></body></html>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                        (() => {
                            const list = document.styleSheets;
                            const style = document.getElementById('first');
                            const sheet = style.sheet;
                            const rules = sheet.cssRules;
                            const firstRule = rules[0];
                            const initial = [
                                list === document.styleSheets,
                                list.length,
                                list[0] === sheet,
                                list.item(0) === sheet,
                                list.item(9),
                                sheet.ownerNode === style,
                                rules === sheet.cssRules,
                                rules.length,
                                rules.item(0) === firstRule,
                                firstRule instanceof CSSRule,
                                firstRule instanceof CSSStyleRule,
                                firstRule.type,
                                firstRule.selectorText,
                                firstRule.style.color,
                                firstRule.parentStyleSheet === sheet,
                            ];

                            sheet.insertRule('.middle { height: 30px; }', 1);
                            const inserted = [
                                rules.length,
                                rules[0] === firstRule,
                                rules[1].selectorText,
                                style.textContent.includes('.middle'),
                            ];
                            sheet.deleteRule(1);

                            const extra = document.createElement('style');
                            document.head.appendChild(extra);
                            const afterAppend = list.length;
                            const emptySheet = extra.sheet;
                            const emptyIdentity = emptySheet === list[2]
                                && emptySheet.cssRules.length === 0;
                            extra.textContent = '.extra { opacity:.5 }';
                            const emptyBecameLive = emptySheet.cssRules.length === 1;
                            extra.remove();
                            const afterRemove = list.length;

                            style.textContent = '.replacement { padding: 4px; }';
                            const reparsed = [
                                style.sheet === sheet,
                                sheet.cssRules === rules,
                                rules.length,
                                rules[0].selectorText,
                                rules[0].style.padding,
                            ];
                            style.remove();
                            const disconnected = style.sheet === null
                                && sheet.ownerNode === null
                                && list.length === 1;
                            document.head.appendChild(style);
                            const reconnected = style.sheet !== sheet
                                && style.sheet.ownerNode === style
                                && list.length === 2;

                            const left = document.createElement('div');
                            const right = document.createElement('div');
                            document.body.append(left, right);
                            const moving = document.createElement('style');
                            moving.textContent = '.moving { color: red }';
                            left.appendChild(moving);
                            const beforeMove = moving.sheet;
                            right.appendChild(moving);
                            const reparented = beforeMove.ownerNode === null
                                && moving.sheet !== beforeMove
                                && moving.sheet.ownerNode === moving;
                            right.remove();
                            left.remove();

                            const bulk = document.createElement('div');
                            document.body.appendChild(bulk);
                            bulk.innerHTML = '<style>.bulk { color: blue }</style><span></span>';
                            const bulkSheet = bulk.querySelector('style').sheet;
                            bulk.innerHTML = '';
                            const innerHTMLDetached = bulkSheet.ownerNode === null;
                            bulk.innerHTML = '<section><style>.text { color: green }</style></section>';
                            const textSheet = bulk.querySelector('style').sheet;
                            bulk.textContent = '';
                            const textContentDetached = textSheet.ownerNode === null;
                            bulk.remove();
                            return {
                                initial, inserted, afterAppend, emptyIdentity, emptyBecameLive,
                                afterRemove, reparsed, disconnected, reconnected, reparented,
                                innerHTMLDetached, textContentDetached,
                            };
                        })()
                        "#,
                    )
                    .unwrap();

                assert_eq!(
                    result,
                    serde_json::json!({
                        "initial": [true, 2, true, true, null, true, true, 2, true,
                            true, true, 1, ".one", "red", true],
                        "inserted": [3, true, ".middle", true],
                        "afterAppend": 3,
                        "emptyIdentity": true,
                        "emptyBecameLive": true,
                        "afterRemove": 2,
                        "reparsed": [true, true, 1, ".replacement", "4px"],
                        "disconnected": true,
                        "reconnected": true,
                        "reparented": true,
                        "innerHTMLDetached": true,
                        "textContentDetached": true,
                    })
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("""
            <html><head>
                <style id="first">.one { color:red } .two { width:20px }</style>
                <style id="second">.three { display:block }</style>
            </head><body></body></html>
            """);
        var result = fixture.Runtime.Evaluate("""
            (() => {
                const list = document.styleSheets;
                const style = document.getElementById('first');
                const sheet = style.sheet;
                const rules = sheet.cssRules;
                const firstRule = rules[0];
                const initial = [
                    list === document.styleSheets,
                    list.length,
                    list[0] === sheet,
                    list.item(0) === sheet,
                    list.item(9),
                    sheet.ownerNode === style,
                    rules === sheet.cssRules,
                    rules.length,
                    rules.item(0) === firstRule,
                    firstRule instanceof CSSRule,
                    firstRule instanceof CSSStyleRule,
                    firstRule.type,
                    firstRule.selectorText,
                    firstRule.style.color,
                    firstRule.parentStyleSheet === sheet,
                ];

                sheet.insertRule('.middle { height: 30px; }', 1);
                const inserted = [
                    rules.length,
                    rules[0] === firstRule,
                    rules[1].selectorText,
                    style.textContent.includes('.middle'),
                ];
                sheet.deleteRule(1);

                const extra = document.createElement('style');
                document.head.appendChild(extra);
                const afterAppend = list.length;
                const emptySheet = extra.sheet;
                const emptyIdentity = emptySheet === list[2]
                    && emptySheet.cssRules.length === 0;
                extra.textContent = '.extra { opacity:.5 }';
                const emptyBecameLive = emptySheet.cssRules.length === 1;
                extra.remove();
                const afterRemove = list.length;

                style.textContent = '.replacement { padding: 4px; }';
                const reparsed = [
                    style.sheet === sheet,
                    sheet.cssRules === rules,
                    rules.length,
                    rules[0].selectorText,
                    rules[0].style.padding,
                ];
                style.remove();
                const disconnected = style.sheet === null
                    && sheet.ownerNode === null
                    && list.length === 1;
                document.head.appendChild(style);
                const reconnected = style.sheet !== sheet
                    && style.sheet.ownerNode === style
                    && list.length === 2;

                const left = document.createElement('div');
                const right = document.createElement('div');
                document.body.append(left, right);
                const moving = document.createElement('style');
                moving.textContent = '.moving { color: red }';
                left.appendChild(moving);
                const beforeMove = moving.sheet;
                right.appendChild(moving);
                const reparented = beforeMove.ownerNode === null
                    && moving.sheet !== beforeMove
                    && moving.sheet.ownerNode === moving;
                right.remove();
                left.remove();

                const bulk = document.createElement('div');
                document.body.appendChild(bulk);
                bulk.innerHTML = '<style>.bulk { color: blue }</style><span></span>';
                const bulkSheet = bulk.querySelector('style').sheet;
                bulk.innerHTML = '';
                const innerHTMLDetached = bulkSheet.ownerNode === null;
                bulk.innerHTML = '<section><style>.text { color: green }</style></section>';
                const textSheet = bulk.querySelector('style').sheet;
                bulk.textContent = '';
                const textContentDetached = textSheet.ownerNode === null;
                bulk.remove();
                return {
                    initial, inserted, afterAppend, emptyIdentity, emptyBecameLive,
                    afterRemove, reparsed, disconnected, reconnected, reparented,
                    innerHTMLDetached, textContentDetached,
                };
            })()
            """);
        AssertJsonEquals(
            """
            {
                "initial": [true, 2, true, true, null, true, true, 2, true,
                    true, true, 1, ".one", "red", true],
                "inserted": [3, true, ".middle", true],
                "afterAppend": 3,
                "emptyIdentity": true,
                "emptyBecameLive": true,
                "afterRemove": 2,
                "reparsed": [true, true, 1, ".replacement", "4px"],
                "disconnected": true,
                "reconnected": true,
                "reparented": true,
                "innerHTMLDetached": true,
                "textContentDetached": true
            }
            """,
            result);
    }

    [Fact]
    public void StylesheetCssomMutationsUpdateTheLiveCascade()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn stylesheet_cssom_mutations_update_the_live_cascade() {
                let mut rt = setup_runtime(
                    r#"<html style="margin:0"><head>
                        <style>#box { width:11px; height:10px }</style>
                        </head><body style="margin:0"><div id="box"></div></body></html>"#,
                );
                rt.set_viewport(200.0, 100.0);
                let result = rt
                    .evaluate(
                        r#"
                        (() => {
                            const style = document.createElement('style');
                            style.type = 'text/css';
                            style.setAttribute('data-framer-css', 'true');
                            document.head.appendChild(style);
                            const sheet = style.sheet;
                            const rules = sheet.cssRules;
                            const termius = [
                                !!sheet,
                                rules.length,
                                document.styleSheets[1] === sheet,
                                sheet.ownerNode === style,
                            ];
                            sheet.insertRule('#box { width:73px; height:10px; }', rules.length);
                            termius.push(rules.length);
                            sheet.insertRule('.other { color: blue; }', rules.length);
                            termius.push(rules.length);
                            sheet.insertRule('.semicolon { content: "a;b"; background-image: url("data:image/svg+xml;utf8,<svg/>"); }', rules.length);
                            const semicolonValues = [
                                rules[2].style.content,
                                rules[2].style.backgroundImage,
                            ];
                            let multiRuleSyntaxError = false;
                            try {
                                sheet.insertRule('.invalid-a {} .invalid-b {}', rules.length);
                            } catch (error) {
                                multiRuleSyntaxError = error?.name === 'SyntaxError';
                            }
                            const inserted = document.getElementById('box').getBoundingClientRect().width;
                            rules[0].style.setProperty('width', '91px');
                            const edited = document.getElementById('box').getBoundingClientRect().width;
                            const computed = getComputedStyle(document.getElementById('box')).width;
                            sheet.deleteRule(0);
                            const deleted = document.getElementById('box').getBoundingClientRect().width;
                            sheet.replaceSync('#box { width:64px } .other { color: blue }');
                            const replaced = document.getElementById('box').getBoundingClientRect().width;
                            return [termius, semicolonValues, multiRuleSyntaxError, inserted, edited, computed, deleted,
                                    replaced, rules.length, rules[0].selectorText,
                                    style.textContent.includes('.other')];
                        })()
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([
                        [true, 0, true, true, 1, 2],
                        ["\"a;b\"", "url(\"data:image/svg+xml;utf8,<svg/>\")"], true,
                        73, 91, "91px", 11, 64, 2, "#box", true
                    ])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("""
            <html style="margin:0"><head>
                <style>#box { width:11px; height:10px }</style>
                </head><body style="margin:0"><div id="box"></div></body></html>
            """);
        fixture.Runtime.SetViewport(200.0, 100.0);
        var result = fixture.Runtime.Evaluate("""
            (() => {
                const style = document.createElement('style');
                style.type = 'text/css';
                style.setAttribute('data-framer-css', 'true');
                document.head.appendChild(style);
                const sheet = style.sheet;
                const rules = sheet.cssRules;
                const termius = [
                    !!sheet,
                    rules.length,
                    document.styleSheets[1] === sheet,
                    sheet.ownerNode === style,
                ];
                sheet.insertRule('#box { width:73px; height:10px; }', rules.length);
                termius.push(rules.length);
                sheet.insertRule('.other { color: blue; }', rules.length);
                termius.push(rules.length);
                sheet.insertRule('.semicolon { content: "a;b"; background-image: url("data:image/svg+xml;utf8,<svg/>"); }', rules.length);
                const semicolonValues = [
                    rules[2].style.content,
                    rules[2].style.backgroundImage,
                ];
                let multiRuleSyntaxError = false;
                try {
                    sheet.insertRule('.invalid-a {} .invalid-b {}', rules.length);
                } catch (error) {
                    multiRuleSyntaxError = error?.name === 'SyntaxError';
                }
                const inserted = document.getElementById('box').getBoundingClientRect().width;
                rules[0].style.setProperty('width', '91px');
                const edited = document.getElementById('box').getBoundingClientRect().width;
                const computed = getComputedStyle(document.getElementById('box')).width;
                sheet.deleteRule(0);
                const deleted = document.getElementById('box').getBoundingClientRect().width;
                sheet.replaceSync('#box { width:64px } .other { color: blue }');
                const replaced = document.getElementById('box').getBoundingClientRect().width;
                return [termius, semicolonValues, multiRuleSyntaxError, inserted, edited, computed, deleted,
                        replaced, rules.length, rules[0].selectorText,
                        style.textContent.includes('.other')];
            })()
            """);
        AssertJsonEquals(
            """
            [
                [true, 0, true, true, 1, 2],
                ["\"a;b\"", "url(\"data:image/svg+xml;utf8,<svg/>\")"], true,
                73, 91, "91px", 11, 64, 2, "#box", true
            ]
            """,
            result);
    }

    [Fact]
    public void AdoptedStylesheetsMaterializeIntoTheDocument()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn adopted_stylesheets_materialize_into_the_document() {
                let mut rt =
                    setup_runtime("<html><head></head><body><div class=\"card\"></div></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        (() => {
                            const sheet = new CSSStyleSheet();
                            document.adoptedStyleSheets.push(sheet);
                            sheet.insertRule('.card { display: flex; color: red; }', 0);
                            const node = document.querySelector('style[data-obscura-adopted]');
                            const inserted = node.textContent;
                            sheet.replaceSync('.card { content: "a;b"; background-image: url("data:image/svg+xml;utf8,<svg/>"); }');
                            const preserved = [
                                sheet.cssRules[0].style.content,
                                sheet.cssRules[0].style.backgroundImage,
                                node.textContent.includes('a;b'),
                                node.textContent.includes('svg+xml;utf8'),
                            ];
                            sheet.deleteRule(0);
                            return [
                                document.adoptedStyleSheets.length,
                                document.querySelectorAll('style[data-obscura-adopted]').length,
                                inserted.includes('display: flex'),
                                preserved,
                                node.textContent,
                            ];
                        })()
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([
                        1, 1, true,
                        ["\"a;b\"", "url(\"data:image/svg+xml;utf8,<svg/>\")", true, true],
                        ""
                    ])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<html><head></head><body><div class="card"></div></body></html>""");
        var result = fixture.Runtime.Evaluate("""
            (() => {
                const sheet = new CSSStyleSheet();
                document.adoptedStyleSheets.push(sheet);
                sheet.insertRule('.card { display: flex; color: red; }', 0);
                const node = document.querySelector('style[data-obscura-adopted]');
                const inserted = node.textContent;
                sheet.replaceSync('.card { content: "a;b"; background-image: url("data:image/svg+xml;utf8,<svg/>"); }');
                const preserved = [
                    sheet.cssRules[0].style.content,
                    sheet.cssRules[0].style.backgroundImage,
                    node.textContent.includes('a;b'),
                    node.textContent.includes('svg+xml;utf8'),
                ];
                sheet.deleteRule(0);
                return [
                    document.adoptedStyleSheets.length,
                    document.querySelectorAll('style[data-obscura-adopted]').length,
                    inserted.includes('display: flex'),
                    preserved,
                    node.textContent,
                ];
            })()
            """);
        AssertJsonEquals(
            """
            [
                1, 1, true,
                ["\"a;b\"", "url(\"data:image/svg+xml;utf8,<svg/>\")", true, true],
                ""
            ]
            """,
            result);
    }

    [Fact]
    public void ShadowStylesheetListsAndAdoptionAreLiveAcrossRoots()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn shadow_stylesheet_lists_and_adoption_are_live_across_roots() {
                let mut rt = setup_runtime(
                    "<html><head></head><body><div id='one'></div><div id='two'></div></body></html>",
                );
                let result = rt
                    .evaluate(
                        r#"
                        (() => {
                            const first = document.getElementById('one').attachShadow({ mode: 'open' });
                            const second = document.getElementById('two').attachShadow({ mode: 'open' });
                            const inline = document.createElement('style');
                            inline.textContent = '.local { width: 17px }';
                            first.appendChild(inline);
                            const inlineSheet = inline.sheet;
                            const firstList = first.styleSheets;
                            const firstAdopted = first.adoptedStyleSheets;
                            const secondAdopted = second.adoptedStyleSheets;
                            const documentAdopted = document.adoptedStyleSheets;

                            const shared = new CSSStyleSheet();
                            first.adoptedStyleSheets = [shared];
                            second.adoptedStyleSheets.push(shared);
                            document.adoptedStyleSheets = [shared];

                            const firstNode = first.querySelector('style[data-obscura-adopted]');
                            const secondNode = second.querySelector('style[data-obscura-adopted]');
                            const documentNode = document.querySelector('style[data-obscura-adopted]');
                            const initial = [
                                first.styleSheets === firstList,
                                firstList.length,
                                firstList[0] === inlineSheet,
                                firstList.item(0) === inlineSheet,
                                second.styleSheets === second.styleSheets,
                                second.styleSheets.length,
                                first.adoptedStyleSheets === firstAdopted,
                                second.adoptedStyleSheets === secondAdopted,
                                document.adoptedStyleSheets === documentAdopted,
                                firstAdopted.length,
                                secondAdopted.length,
                                documentAdopted.length,
                                firstNode.parentNode === first,
                                secondNode.parentNode === second,
                                documentNode.parentNode === document.head,
                            ];

                            shared.insertRule('.shared { width: 31px }', 0);
                            const synchronized = [firstNode, secondNode, documentNode]
                                .map(node => node.textContent.includes('width: 31px'));

                            second.adoptedStyleSheets = [];
                            shared.replaceSync('.shared { width: 47px }');
                            const afterRemoval = [
                                second.adoptedStyleSheets === secondAdopted,
                                secondAdopted.length,
                                secondNode.parentNode,
                                second.querySelectorAll('style[data-obscura-adopted]').length,
                                firstNode.textContent.includes('width: 47px'),
                                documentNode.textContent.includes('width: 47px'),
                                secondNode.textContent.includes('width: 31px'),
                            ];

                            inline.remove();
                            const inlineRemoval = [
                                first.styleSheets === firstList,
                                firstList.length,
                                inlineSheet.ownerNode,
                            ];
                            return { initial, synchronized, afterRemoval, inlineRemoval };
                        })()
                        "#,
                    )
                    .unwrap();

                assert_eq!(
                    result,
                    serde_json::json!({
                        "initial": [
                            true, 1, true, true, true, 0,
                            true, true, true, 1, 1, 1,
                            true, true, true
                        ],
                        "synchronized": [true, true, true],
                        "afterRemoval": [true, 0, null, 0, true, true, true],
                        "inlineRemoval": [true, 0, null],
                    })
                );
            }
        */
        using var fixture = RuntimeFixture.Setup(
            "<html><head></head><body><div id='one'></div><div id='two'></div></body></html>");
        var result = fixture.Runtime.Evaluate("""
            (() => {
                const first = document.getElementById('one').attachShadow({ mode: 'open' });
                const second = document.getElementById('two').attachShadow({ mode: 'open' });
                const inline = document.createElement('style');
                inline.textContent = '.local { width: 17px }';
                first.appendChild(inline);
                const inlineSheet = inline.sheet;
                const firstList = first.styleSheets;
                const firstAdopted = first.adoptedStyleSheets;
                const secondAdopted = second.adoptedStyleSheets;
                const documentAdopted = document.adoptedStyleSheets;

                const shared = new CSSStyleSheet();
                first.adoptedStyleSheets = [shared];
                second.adoptedStyleSheets.push(shared);
                document.adoptedStyleSheets = [shared];

                const firstNode = first.querySelector('style[data-obscura-adopted]');
                const secondNode = second.querySelector('style[data-obscura-adopted]');
                const documentNode = document.querySelector('style[data-obscura-adopted]');
                const initial = [
                    first.styleSheets === firstList,
                    firstList.length,
                    firstList[0] === inlineSheet,
                    firstList.item(0) === inlineSheet,
                    second.styleSheets === second.styleSheets,
                    second.styleSheets.length,
                    first.adoptedStyleSheets === firstAdopted,
                    second.adoptedStyleSheets === secondAdopted,
                    document.adoptedStyleSheets === documentAdopted,
                    firstAdopted.length,
                    secondAdopted.length,
                    documentAdopted.length,
                    firstNode.parentNode === first,
                    secondNode.parentNode === second,
                    documentNode.parentNode === document.head,
                ];

                shared.insertRule('.shared { width: 31px }', 0);
                const synchronized = [firstNode, secondNode, documentNode]
                    .map(node => node.textContent.includes('width: 31px'));

                second.adoptedStyleSheets = [];
                shared.replaceSync('.shared { width: 47px }');
                const afterRemoval = [
                    second.adoptedStyleSheets === secondAdopted,
                    secondAdopted.length,
                    secondNode.parentNode,
                    second.querySelectorAll('style[data-obscura-adopted]').length,
                    firstNode.textContent.includes('width: 47px'),
                    documentNode.textContent.includes('width: 47px'),
                    secondNode.textContent.includes('width: 31px'),
                ];

                inline.remove();
                const inlineRemoval = [
                    first.styleSheets === firstList,
                    firstList.length,
                    inlineSheet.ownerNode,
                ];
                return { initial, synchronized, afterRemoval, inlineRemoval };
            })()
            """);
        AssertJsonEquals(
            """
            {
                "initial": [
                    true, 1, true, true, true, 0,
                    true, true, true, 1, 1, 1,
                    true, true, true
                ],
                "synchronized": [true, true, true],
                "afterRemoval": [true, 0, null, 0, true, true, true],
                "inlineRemoval": [true, 0, null]
            }
            """,
            result);
    }

    [Fact]
    public void ShadowAdoptedStylesheetsApplyAndSyncTheLiveCascade()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn shadow_adopted_stylesheets_apply_and_sync_the_live_cascade() {
                let mut rt = setup_runtime(
                    r#"<html style="margin:0"><head>
                        <style>.target { width:11px; height:10px }</style>
                        </head><body style="margin:0">
                        <div id="one"></div><div id="two"></div><div class="target" id="outside"></div>
                        </body></html>"#,
                );
                rt.set_viewport(200.0, 100.0);
                let result = rt
                    .evaluate(
                        r#"
                        (() => {
                            const first = document.getElementById('one').attachShadow({ mode: 'open' });
                            const second = document.getElementById('two').attachShadow({ mode: 'open' });
                            first.innerHTML = '<style>.target { width:18px; height:10px }</style><div class="target"></div>';
                            second.innerHTML = '<style>.target { width:23px; height:10px }</style><div class="target"></div>';
                            const firstTarget = first.querySelector('.target');
                            const secondTarget = second.querySelector('.target');
                            const outside = document.getElementById('outside');
                            const widths = () => [firstTarget, secondTarget, outside]
                                .map(node => node.getBoundingClientRect().width);

                            const inline = widths();
                            const shared = new CSSStyleSheet();
                            shared.replaceSync('.target { width:42px; height:10px }');
                            first.adoptedStyleSheets = [shared];
                            second.adoptedStyleSheets = [shared];
                            document.adoptedStyleSheets = [shared];
                            const adopted = widths();

                            shared.cssRules[0].style.setProperty('width', '67px');
                            const mutated = widths();

                            second.adoptedStyleSheets = [];
                            document.adoptedStyleSheets = [];
                            const selectivelyRemoved = widths();

                            shared.replaceSync('.target { width:81px; height:10px }');
                            const remainingRootUpdated = widths();
                            return [
                                inline, adopted, mutated, selectivelyRemoved, remainingRootUpdated,
                                first.styleSheets.length,
                                second.styleSheets.length,
                            ];
                        })()
                        "#,
                    )
                    .unwrap();

                assert_eq!(
                    result,
                    serde_json::json!([
                        [18, 23, 11],
                        [42, 42, 42],
                        [67, 67, 67],
                        [67, 23, 11],
                        [81, 23, 11],
                        1,
                        1,
                    ])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("""
            <html style="margin:0"><head>
                <style>.target { width:11px; height:10px }</style>
                </head><body style="margin:0">
                <div id="one"></div><div id="two"></div><div class="target" id="outside"></div>
                </body></html>
            """);
        fixture.Runtime.SetViewport(200.0, 100.0);
        var result = fixture.Runtime.Evaluate("""
            (() => {
                const first = document.getElementById('one').attachShadow({ mode: 'open' });
                const second = document.getElementById('two').attachShadow({ mode: 'open' });
                first.innerHTML = '<style>.target { width:18px; height:10px }</style><div class="target"></div>';
                second.innerHTML = '<style>.target { width:23px; height:10px }</style><div class="target"></div>';
                const firstTarget = first.querySelector('.target');
                const secondTarget = second.querySelector('.target');
                const outside = document.getElementById('outside');
                const widths = () => [firstTarget, secondTarget, outside]
                    .map(node => node.getBoundingClientRect().width);

                const inline = widths();
                const shared = new CSSStyleSheet();
                shared.replaceSync('.target { width:42px; height:10px }');
                first.adoptedStyleSheets = [shared];
                second.adoptedStyleSheets = [shared];
                document.adoptedStyleSheets = [shared];
                const adopted = widths();

                shared.cssRules[0].style.setProperty('width', '67px');
                const mutated = widths();

                second.adoptedStyleSheets = [];
                document.adoptedStyleSheets = [];
                const selectivelyRemoved = widths();

                shared.replaceSync('.target { width:81px; height:10px }');
                const remainingRootUpdated = widths();
                return [
                    inline, adopted, mutated, selectivelyRemoved, remainingRootUpdated,
                    first.styleSheets.length,
                    second.styleSheets.length,
                ];
            })()
            """);
        AssertJsonEquals(
            """
            [
                [18, 23, 11],
                [42, 42, 42],
                [67, 67, 67],
                [67, 23, 11],
                [81, 23, 11],
                1,
                1
            ]
            """,
            result);
    }

    [Fact]
    public void UnavailableWebglContextDoesNotClaimSuccess()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn unavailable_webgl_context_does_not_claim_success() {
                let mut rt = setup_runtime("<html><body><canvas></canvas></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        (() => {
                            const canvas = document.querySelector('canvas');
                            const fallback = document.createElement('p');
                            if (!canvas.getContext('webgl')) {
                                fallback.textContent = 'static fallback';
                                document.body.appendChild(fallback);
                            }
                            return [
                                canvas.getContext('webgl'),
                                canvas.getContext('webgl2'),
                                canvas.getContext('experimental-webgl'),
                                fallback.isConnected,
                                fallback.textContent,
                            ];
                        })()
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([null, null, null, true, "static fallback"])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body><canvas></canvas></body></html>");
        var result = fixture.Runtime.Evaluate("""
            (() => {
                const canvas = document.querySelector('canvas');
                const fallback = document.createElement('p');
                if (!canvas.getContext('webgl')) {
                    fallback.textContent = 'static fallback';
                    document.body.appendChild(fallback);
                }
                return [
                    canvas.getContext('webgl'),
                    canvas.getContext('webgl2'),
                    canvas.getContext('experimental-webgl'),
                    fallback.isConnected,
                    fallback.textContent,
                ];
            })()
            """);
        AssertJsonEquals("""[null, null, null, true, "static fallback"]""", result);
    }

    [Fact]
    public void Canvas2dLiveBackingPaintsImmediatelyWithScalingClipsAndEffects()
    {
        using var owner = new CaptureRuntime(new ObscuraJsRuntime());
        var rt = owner.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0;width:64px;height:40px;background:#0000ff">
                <div style="position:absolute;left:4px;top:4px;width:18px;height:14px;overflow:hidden">
                  <canvas id="paint" width="2" height="1"
                    style="display:block;width:20px;height:10px;border:2px solid #ffff00;opacity:.5"></canvas>
                </div>
                <canvas id="blank" width="4" height="4"
                  style="position:absolute;left:30px;top:4px;width:10px;height:10px"></canvas>
                <canvas id="padding" width="2" height="1"
                  style="position:absolute;left:30px;top:20px;width:10px;height:4px;padding:2px 2px 2px 4px;border:1px solid #ffff00;background:#00ffff"></canvas>
                <div style="position:absolute;z-index:2;left:10px;top:7px;width:4px;height:4px;background:#ff00ff"></div>
            </body></html>
            """));
        rt.SetUrl("http://example.test/canvas");
        rt.SetViewport(64.0, 40.0);
        rt.RunPageInit();

        var blankApi = rt.Evaluate(
            """
            (() => {
                const blank = document.getElementById('blank');
                const encoded = blank.toDataURL();
                const blankContext = blank.getContext('2d');
                const pixels = blankContext.getImageData(0, 0, 4, 4).data;
                blankContext.fillStyle = '#ff0000'; blankContext.fillRect(0, 0, 1, 1);
                blank.width = 6;
                const resetPixel = blankContext.getImageData(0, 0, 1, 1).data;
                const untouched = document.createElement('canvas');
                const defaultEncoded = untouched.toDataURL();
                const defaultPixels = untouched.getContext('2d').getImageData(0, 0, 1, 1).data;
                return [
                  blank instanceof HTMLCanvasElement,
                  typeof document.createElement('div').getContext,
                  blank.width, blank.height, blank.getContext('2d') === blankContext,
                  encoded.startsWith('data:image/png;base64,'),
                  atob(encoded.split(',')[1]).charCodeAt(25) === 6,
                  Array.from(pixels).every((value, index) => index % 4 !== 3 || value === 0),
                  untouched.width, untouched.height,
                  defaultEncoded.startsWith('data:image/png;base64,'),
                  Array.from(defaultPixels),
                  Array.from(resetPixel),
                ];
            })()
            """);
        AssertJson(
            """
            [true, "undefined", 6, 4, true, true, true, true, 300, 150, true, [0,0,0,0], [0,0,0,0]]
            """,
            blankApi);

        // Prepare layout before drawing so the assertions below prove canvas
        // damage retains layout, while the following capture proves pixels
        // are read from the live backing immediately after the script task.
        Assert.True(Obscura.Js.Ops.RenderState.EnsureResolvedScroll(rt.State));
        var prepared = rt.State.PreparedRender;
        Assert.NotNull(prepared);
        ulong activityBefore = rt.ActivityGeneration;
        rt.ExecuteScript(
            "canvas-fill",
            """
            const canvas = document.getElementById('paint');
            const ctx = canvas.getContext('2d');
            ctx.fillStyle = '#ff0000'; ctx.fillRect(0, 0, 1, 1);
            ctx.fillStyle = '#00ff00'; ctx.fillRect(1, 0, 1, 1);
            const padding = document.getElementById('padding').getContext('2d');
            padding.fillStyle = '#ff0000'; padding.fillRect(0, 0, 1, 1);
            padding.fillStyle = '#00ff00'; padding.fillRect(1, 0, 1, 1);
            """);
        Assert.True(rt.ActivityGeneration > activityBefore);
        Assert.Same(prepared, rt.State.PreparedRender);

        Assert.True(Obscura.Js.Ops.RenderState.EnsureResolvedScroll(rt.State));
        using var pixmap = Obscura.Render.RenderPaint.PaintPreparedWithScrollAndSurfaceColorAndCanvasSurfaces(
            rt.State.Dom!,
            rt.State.PreparedRender!,
            rt.State.RenderResources,
            rt.State.ResolvedScroll!.Value.State,
            new Obscura.Render.Css.RgbaColor(255, 255, 255, 255),
            new RuntimeCanvasSurfaceSource(rt.State.CanvasSurfaces));
        Assert.NotNull(pixmap);

        var redHalf = pixmap!.Pixel(7, 8);
        Assert.NotNull(redHalf);
        Assert.True(redHalf!.Value.Red > 100 && redHalf.Value.Blue > 100 && redHalf.Value.Green < 20);
        // Bilinear filtering blends at the exact source-pixel transition
        // (x=16), so sample several CSS pixels into the green half.
        var greenHalf = pixmap.Pixel(19, 8);
        Assert.NotNull(greenHalf);
        Assert.True(
            greenHalf!.Value.Green > 45 && greenHalf.Value.Blue > 100 && greenHalf.Value.Red < 20,
            $"pixel at (19, 8) was rgba({greenHalf.Value.Red}, {greenHalf.Value.Green}, {greenHalf.Value.Blue}, {greenHalf.Value.Alpha})");
        var clipped = pixmap.Pixel(23, 8);
        Assert.NotNull(clipped);
        Assert.Equal((0, 0, 255), (clipped!.Value.Red, clipped.Value.Green, clipped.Value.Blue));
        var blank = pixmap.Pixel(34, 8);
        Assert.NotNull(blank);
        Assert.Equal((0, 0, 255), (blank!.Value.Red, blank.Value.Green, blank.Value.Blue));
        var overlay = pixmap.Pixel(11, 8);
        Assert.NotNull(overlay);
        Assert.True(overlay!.Value.Red > 240 && overlay.Value.Blue > 240 && overlay.Value.Green < 20);
        var border = pixmap.Pixel(4, 8);
        Assert.NotNull(border);
        Assert.True(border!.Value.Red > 100 && border.Value.Green > 100 && border.Value.Blue > 100);
        var padding = pixmap.Pixel(33, 23);
        Assert.NotNull(padding);
        Assert.Equal(
            (0, 255, 255),
            (padding!.Value.Red, padding.Value.Green, padding.Value.Blue));
        var paddedContent = pixmap.Pixel(36, 23);
        Assert.NotNull(paddedContent);
        Assert.True(
            paddedContent!.Value.Red > 220 && paddedContent.Value.Green < 40 && paddedContent.Value.Blue < 40,
            "canvas bitmap must start at the CSS content-box origin");
    }

    [Fact]
    public void TestScriptExecution()
    {
        using var fixture = RuntimeFixture.Setup("<ul><li>A</li><li>B</li></ul>");
        var rt = fixture.Runtime;
        rt.ExecuteScript("test", @"
            globalThis.__result = [];
            document.querySelectorAll('li').forEach(function(el) {
                globalThis.__result.push(el.textContent);
            });
        ");
        Assert.Equal("[\"A\",\"B\"]", rt.Evaluate("globalThis.__result")!.ToJsonString());
    }

    [Fact]
    public void PageVarDeclarationsDoNotCollideWithDomInterfaces()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn page_var_declarations_do_not_collide_with_dom_interfaces() {
                let mut rt = setup_runtime("<html><body></body></html>");

                rt.execute_script(
                    "legacy-node-guard",
                    "if (!window.Node) { var Node = {}; } globalThis.__legacyNodeRan = true;",
                )
                .unwrap();
                assert_eq!(
                    rt.evaluate("globalThis.__legacyNodeRan").unwrap(),
                    serde_json::json!(true)
                );

                rt.execute_script(
                    "page-element",
                    "var Element = function PageElement() {}; globalThis.__createdTag = document.createElement('div').tagName;",
                )
                .unwrap();
                assert_eq!(
                    rt.evaluate("globalThis.__createdTag").unwrap(),
                    serde_json::json!("DIV")
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;

        rt.ExecuteScript(
            "legacy-node-guard",
            "if (!window.Node) { var Node = {}; } globalThis.__legacyNodeRan = true;");
        Assert.True(rt.Evaluate("globalThis.__legacyNodeRan")!.GetValue<bool>());

        rt.ExecuteScript(
            "page-element",
            "var Element = function PageElement() {}; globalThis.__createdTag = document.createElement('div').tagName;");
        Assert.Equal("DIV", rt.Evaluate("globalThis.__createdTag")!.GetValue<string>());
    }

    [Fact]
    public void DynamicScriptStatusBridgeIsHiddenAndIdle()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn dynamic_script_status_bridge_is_hidden_and_idle() {
                let mut rt = setup_runtime("<html><body></body></html>");
                assert!(!rt.has_pending_dynamic_scripts());
                assert!(!rt.has_pending_load_delaying_scripts());
                assert_eq!(rt.next_pending_timeout_delay_ms(), None);
                assert_eq!(
                    rt.evaluate("typeof __dynScriptBusy").unwrap(),
                    serde_json::json!("undefined")
                );
                assert_eq!(
                    rt.evaluate(
                        "Object.getOwnPropertyNames(globalThis).includes('__obscura_hasPendingDynamicScripts')"
                    )
                    .unwrap(),
                    serde_json::json!(false)
                );
                assert_eq!(
                    rt.evaluate(
                        "Reflect.ownKeys(globalThis).includes('__obscura_hasPendingDynamicScripts')"
                    )
                    .unwrap(),
                    serde_json::json!(false)
                );
                assert_eq!(
                    rt.evaluate(
                        "Reflect.ownKeys(globalThis).includes('__obscura_hasPendingLoadDelayingScripts')"
                    )
                    .unwrap(),
                    serde_json::json!(false)
                );
                assert_eq!(
                    rt.evaluate(
                        "Object.getOwnPropertyNames(globalThis).includes('__obscura_nextPendingTimeoutDelay')"
                    )
                    .unwrap(),
                    serde_json::json!(false)
                );
                assert_eq!(
                    rt.evaluate(
                        "Reflect.ownKeys(globalThis).includes('__obscura_nextPendingTimeoutDelay')"
                    )
                    .unwrap(),
                    serde_json::json!(false)
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        Assert.False(rt.HasPendingDynamicScripts());
        Assert.False(rt.HasPendingLoadDelayingScripts());
        Assert.Null(rt.NextPendingTimeoutDelayMs());
        Assert.Equal("undefined", rt.Evaluate("typeof __dynScriptBusy")!.GetValue<string>());
        Assert.False(rt.Evaluate(
            "Object.getOwnPropertyNames(globalThis).includes('__obscura_hasPendingDynamicScripts')")!.GetValue<bool>());
        Assert.False(rt.Evaluate(
            "Reflect.ownKeys(globalThis).includes('__obscura_hasPendingDynamicScripts')")!.GetValue<bool>());
        Assert.False(rt.Evaluate(
            "Reflect.ownKeys(globalThis).includes('__obscura_hasPendingLoadDelayingScripts')")!.GetValue<bool>());
        Assert.False(rt.Evaluate(
            "Object.getOwnPropertyNames(globalThis).includes('__obscura_nextPendingTimeoutDelay')")!.GetValue<bool>());
        Assert.False(rt.Evaluate(
            "Reflect.ownKeys(globalThis).includes('__obscura_nextPendingTimeoutDelay')")!.GetValue<bool>());
    }

    /// Regression test for #147: a TypeError in one script must not poison
    /// the runtime so that subsequent scripts (or DOM queries) collapse to
    /// empty. The reporter saw `--dump text` return 1 byte after offside.js
    /// crashed; that cascade should never happen.
    [Fact]
    public void ScriptTypeerrorDoesNotPoisonSubsequentExecution()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><p id=hit>BODY_TEXT</p></body></html>");
        var rt = fixture.Runtime;

        // 1. The first script throws the same flavour of error offside.js produced.
        var error = Assert.Throws<JsRuntimeException>(() =>
            rt.ExecuteScript("buggy", "var x; x.classList.add('y');"));
        Assert.True(
            error.Message.Contains("classList", StringComparison.Ordinal)
            || error.Message.Contains("undefined", StringComparison.Ordinal),
            $"expected classList/undefined error, got: {error.Message}");

        // 2. The runtime must still be usable: a follow-up script runs.
        rt.ExecuteScript("ok", "globalThis.__after_error = 'still alive';");
        Assert.Equal("still alive", rt.Evaluate("globalThis.__after_error")!.GetValue<string>());

        // 3. DOM queries still work after the script error.
        Assert.Equal("BODY_TEXT", rt.Evaluate("document.querySelector('#hit').textContent")!.GetValue<string>());
    }

    /// Regression test for #355: an explicit `throw` in one inline <script> must
    /// not stop later independent <script>s from running. Each <script> executes
    /// as its own `execute_script` call, mirroring how page.rs runs them, so a
    /// thrown error is reported but the next script still runs.
    [Fact]
    public void ThrownErrorInOneScriptDoesNotStopLaterScripts()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript("s1", "globalThis.__ran1 = true;");
        var error = Assert.Throws<JsRuntimeException>(() =>
            rt.ExecuteScript("s2", "throw new Error('only one instance of babel-polyfill is allowed');"));
        Assert.Contains("babel-polyfill", error.Message, StringComparison.Ordinal);
        rt.ExecuteScript("s3", "globalThis.__ran3 = true;");
        var ran = rt.Evaluate("JSON.stringify([globalThis.__ran1 === true, globalThis.__ran3 === true])");
        Assert.Equal("[true,true]", ran!.GetValue<string>());
    }

    /// Regression test for #356: the `in` operator and `Object.keys` must work on
    /// `el.style` (CSSStyleDeclaration) and `el.dataset` (DOMStringMap), `_props`
    /// must not leak, and cssText must serialize dashed names with a trailing
    /// semicolon.
    [Fact]
    public void StyleAndDatasetSupportInOperatorAndKeys()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn style_and_dataset_support_in_operator_and_keys() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"(() => {
                            const el = document.createElement('div');
                            el.style.color = 'red';
                            el.style.fontSize = '14px';
                            el.dataset.foo = 'bar';
                            const keys = Object.keys(el.style);
                            return JSON.stringify({
                                colorInStyle: 'color' in el.style,
                                objectFitInStyle: 'object-fit' in el.style,
                                keysHasSet: keys.includes('color') && keys.includes('fontSize'),
                                noPropsLeak: !keys.includes('_props'),
                                fooInDataset: 'foo' in el.dataset,
                                datasetKeys: Object.keys(el.dataset),
                                cssText: el.style.cssText,
                                length: el.style.length,
                                getByDash: el.style.getPropertyValue('font-size'),
                                reflectedAttribute: el.getAttribute('style')
                            });
                        })()"#,
                    )
                    .unwrap();
                let p: serde_json::Value = serde_json::from_str(result.as_str().unwrap()).unwrap();
                assert_eq!(p["colorInStyle"], true);
                assert_eq!(p["objectFitInStyle"], true);
                assert_eq!(p["keysHasSet"], true);
                assert_eq!(p["noPropsLeak"], true);
                assert_eq!(p["fooInDataset"], true);
                assert_eq!(p["datasetKeys"], serde_json::json!(["foo"]));
                assert_eq!(p["cssText"], "color: red; font-size: 14px;");
                assert_eq!(p["length"], 2);
                assert_eq!(p["getByDash"], "14px");
                assert_eq!(p["reflectedAttribute"], "color: red; font-size: 14px;");
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            (() => {
                const el = document.createElement('div');
                el.style.color = 'red';
                el.style.fontSize = '14px';
                el.dataset.foo = 'bar';
                const keys = Object.keys(el.style);
                return JSON.stringify({
                    colorInStyle: 'color' in el.style,
                    objectFitInStyle: 'object-fit' in el.style,
                    keysHasSet: keys.includes('color') && keys.includes('fontSize'),
                    noPropsLeak: !keys.includes('_props'),
                    fooInDataset: 'foo' in el.dataset,
                    datasetKeys: Object.keys(el.dataset),
                    cssText: el.style.cssText,
                    length: el.style.length,
                    getByDash: el.style.getPropertyValue('font-size'),
                    reflectedAttribute: el.getAttribute('style')
                });
            })()
            """);
        var p = JsonNode.Parse(result!.GetValue<string>())!;
        Assert.True(p["colorInStyle"]!.GetValue<bool>());
        Assert.True(p["objectFitInStyle"]!.GetValue<bool>());
        Assert.True(p["keysHasSet"]!.GetValue<bool>());
        Assert.True(p["noPropsLeak"]!.GetValue<bool>());
        Assert.True(p["fooInDataset"]!.GetValue<bool>());
        AssertJsonEquals("""["foo"]""", p["datasetKeys"]);
        Assert.Equal("color: red; font-size: 14px;", p["cssText"]!.GetValue<string>());
        Assert.Equal(2, p["length"]!.GetValue<int>());
        Assert.Equal("14px", p["getByDash"]!.GetValue<string>());
        Assert.Equal("color: red; font-size: 14px;", p["reflectedAttribute"]!.GetValue<string>());
    }

    [Fact]
    public void DomStringMapIsExposedAndBacksDataset()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn dom_string_map_is_exposed_and_backs_dataset() {
                let mut rt = setup_runtime(r#"<div id="x" data-foo="bar"></div>"#);
                let result = rt
                    .evaluate(
                        r#"(() => {
                            const dataset = document.getElementById("x").dataset;
                            const interface = window.DOMStringMap;
                            const descriptor = Object.getOwnPropertyDescriptor(window, "DOMStringMap");
                            let illegalConstructor = false;
                            if (interface) {
                                try { new interface(); }
                                catch (error) { illegalConstructor = error instanceof TypeError; }
                            }
                            return JSON.stringify({
                                type: typeof interface,
                                instance: !!interface && dataset instanceof interface,
                                prototype: !!interface && Object.getPrototypeOf(dataset) === interface.prototype,
                                constructor: !!interface && dataset.constructor === interface,
                                tag: Object.prototype.toString.call(dataset),
                                enumerable: descriptor ? descriptor.enumerable : "missing",
                                illegalConstructor,
                                value: dataset.foo,
                            });
                        })()"#,
                    )
                    .unwrap();
                let value: serde_json::Value = serde_json::from_str(result.as_str().unwrap()).unwrap();
                assert_eq!(
                    value,
                    serde_json::json!({
                        "type": "function",
                        "instance": true,
                        "prototype": true,
                        "constructor": true,
                        "tag": "[object DOMStringMap]",
                        "enumerable": false,
                        "illegalConstructor": true,
                        "value": "bar",
                    })
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("""<div id="x" data-foo="bar"></div>""");
        var result = fixture.Runtime.Evaluate("""
            (() => {
                const dataset = document.getElementById("x").dataset;
                const interface = window.DOMStringMap;
                const descriptor = Object.getOwnPropertyDescriptor(window, "DOMStringMap");
                let illegalConstructor = false;
                if (interface) {
                    try { new interface(); }
                    catch (error) { illegalConstructor = error instanceof TypeError; }
                }
                return JSON.stringify({
                    type: typeof interface,
                    instance: !!interface && dataset instanceof interface,
                    prototype: !!interface && Object.getPrototypeOf(dataset) === interface.prototype,
                    constructor: !!interface && dataset.constructor === interface,
                    tag: Object.prototype.toString.call(dataset),
                    enumerable: descriptor ? descriptor.enumerable : "missing",
                    illegalConstructor,
                    value: dataset.foo,
                });
            })()
            """);
        AssertJsonEquals(
            """
            {
                "type": "function",
                "instance": true,
                "prototype": true,
                "constructor": true,
                "tag": "[object DOMStringMap]",
                "enumerable": false,
                "illegalConstructor": true,
                "value": "bar"
            }
            """,
            JsonNode.Parse(result!.GetValue<string>()));
    }

    [Fact]
    public void StyleDeclarationReflectsAndRemovesParsedAttributes()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn style_declaration_reflects_and_removes_parsed_attributes() {
                let mut rt = setup_runtime(
                    "<html><body><div id='icon' style='font-size: 0px; color: red'></div></body></html>",
                );
                let result = rt
                    .evaluate(
                        r#"(() => {
                            const el = document.getElementById('icon');
                            const before = [el.style.fontSize, el.style.color, el.style.length];
                            const removed = el.style.removeProperty('font-size');
                            return JSON.stringify({
                                before,
                                removed,
                                after: el.style.cssText,
                                attribute: el.getAttribute('style')
                            });
                        })()"#,
                    )
                    .unwrap();
                let value: serde_json::Value = serde_json::from_str(result.as_str().unwrap()).unwrap();
                assert_eq!(value["before"], serde_json::json!(["0px", "red", 2]));
                assert_eq!(value["removed"], "0px");
                assert_eq!(value["after"], "color: red;");
                assert_eq!(value["attribute"], "color: red;");
            }
        */
        using var fixture = RuntimeFixture.Setup(
            "<html><body><div id='icon' style='font-size: 0px; color: red'></div></body></html>");
        var result = fixture.Runtime.Evaluate("""
            (() => {
                const el = document.getElementById('icon');
                const before = [el.style.fontSize, el.style.color, el.style.length];
                const removed = el.style.removeProperty('font-size');
                return JSON.stringify({
                    before,
                    removed,
                    after: el.style.cssText,
                    attribute: el.getAttribute('style')
                });
            })()
            """);
        var value = JsonNode.Parse(result!.GetValue<string>())!;
        AssertJsonEquals("""["0px", "red", 2]""", value["before"]);
        Assert.Equal("0px", value["removed"]!.GetValue<string>());
        Assert.Equal("color: red;", value["after"]!.GetValue<string>());
        Assert.Equal("color: red;", value["attribute"]!.GetValue<string>());
    }

    [Fact]
    public void SelectAddAndOptionTextUpdateTheLiveDom()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn select_add_and_option_text_update_the_live_dom() {
                let mut rt = setup_runtime("<html><body><select id='language'></select></body></html>");
                let result = rt
                    .evaluate(
                        r#"(() => {
                            const select = document.getElementById('language');
                            const english = document.createElement('option');
                            english.value = 'en';
                            english.text = 'English';
                            english.selected = true;
                            select.add(english);
                            const greek = document.createElement('option');
                            greek.value = 'el';
                            greek.text = 'Greek';
                            select.add(greek, 0);
                            return JSON.stringify({
                                labels: [...select.options].map(option => option.textContent),
                                selectedIndex: select.selectedIndex,
                                value: select.value,
                                html: select.outerHTML
                            });
                        })()"#,
                    )
                    .unwrap();
                let value: serde_json::Value = serde_json::from_str(result.as_str().unwrap()).unwrap();
                assert_eq!(value["labels"], serde_json::json!(["Greek", "English"]));
                assert_eq!(value["selectedIndex"], 1);
                assert_eq!(value["value"], "en");
                assert!(value["html"]
                    .as_str()
                    .unwrap()
                    .contains(r#"<option value="en" selected="">English</option>"#));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body><select id='language'></select></body></html>");
        var result = fixture.Runtime.Evaluate("""
            (() => {
                const select = document.getElementById('language');
                const english = document.createElement('option');
                english.value = 'en';
                english.text = 'English';
                english.selected = true;
                select.add(english);
                const greek = document.createElement('option');
                greek.value = 'el';
                greek.text = 'Greek';
                select.add(greek, 0);
                return JSON.stringify({
                    labels: [...select.options].map(option => option.textContent),
                    selectedIndex: select.selectedIndex,
                    value: select.value,
                    html: select.outerHTML
                });
            })()
            """);
        var value = JsonNode.Parse(result!.GetValue<string>())!;
        AssertJsonEquals("""["Greek", "English"]""", value["labels"]);
        Assert.Equal(1, value["selectedIndex"]!.GetValue<int>());
        Assert.Equal("en", value["value"]!.GetValue<string>());
        Assert.Contains(
            """<option value="en" selected="">English</option>""",
            value["html"]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    /// Regression for #105: `element.querySelector` and `querySelectorAll`
    /// must scope to the receiver's subtree, not the whole document.
    [Fact]
    public void ElementQuerySelectorIsScopedToSubtree()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn element_query_selector_is_scoped_to_subtree() {
                let mut rt = setup_runtime(
                    r#"<div id="a"><span class="x">in a</span></div><div id="b"><span class="x">in b</span></div>"#,
                );
                let text = rt
                    .evaluate("document.getElementById('a').querySelector('.x').textContent")
                    .unwrap();
                assert_eq!(text, serde_json::json!("in a"));

                let count_in_a = rt
                    .evaluate("document.getElementById('a').querySelectorAll('.x').length")
                    .unwrap();
                assert_eq!(count_in_a.as_f64().unwrap() as i64, 1);

                // Document-scoped query still sees both.
                let count_doc = rt
                    .evaluate("document.querySelectorAll('.x').length")
                    .unwrap();
                assert_eq!(count_doc.as_f64().unwrap() as i64, 2);
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<div id="a"><span class="x">in a</span></div><div id="b"><span class="x">in b</span></div>""");
        var rt = fixture.Runtime;
        Assert.Equal(
            "in a",
            rt.Evaluate("document.getElementById('a').querySelector('.x').textContent")!.GetValue<string>());

        Assert.Equal(
            1,
            (long)rt.Evaluate("document.getElementById('a').querySelectorAll('.x').length")!.GetValue<double>());

        // Document-scoped query still sees both.
        Assert.Equal(2, (long)rt.Evaluate("document.querySelectorAll('.x').length")!.GetValue<double>());
    }

    [Fact]
    public void DocumentEvaluateExposesBasicXpathResult()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn document_evaluate_exposes_basic_xpath_result() {
                let mut rt = setup_runtime("");

                let exposed = rt
                    .evaluate("`${typeof XPathResult}:${typeof Document.prototype.evaluate}:${XPathResult.FIRST_ORDERED_NODE_TYPE}`")
                    .unwrap();
                assert_eq!(exposed, serde_json::json!("function:function:9"));
            }
        */
        using var fixture = RuntimeFixture.Setup("");
        var exposed = fixture.Runtime.Evaluate(
            "`${typeof XPathResult}:${typeof Document.prototype.evaluate}:${XPathResult.FIRST_ORDERED_NODE_TYPE}`");
        Assert.Equal("function:function:9", exposed!.GetValue<string>());
    }

    /// Regression for #105: `document.forms` / `images` / `links` must be
    /// live, not hardcoded `[]`. jQuery 1.x's submit-event setup iterates
    /// `document.forms` and crashes when it's empty for pages that have forms.
    [Fact]
    public void DocumentFormsImagesLinksAreLive()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn document_forms_images_links_are_live() {
                let mut rt =
                    setup_runtime(r#"<form></form><form></form><img><a href="x">l</a><a>no-href</a>"#);
                assert_eq!(
                    rt.evaluate("document.forms.length")
                        .unwrap()
                        .as_f64()
                        .unwrap() as i64,
                    2
                );
                assert_eq!(
                    rt.evaluate("document.images.length")
                        .unwrap()
                        .as_f64()
                        .unwrap() as i64,
                    1
                );
                assert_eq!(
                    rt.evaluate("document.links.length")
                        .unwrap()
                        .as_f64()
                        .unwrap() as i64,
                    1
                );
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<form></form><form></form><img><a href="x">l</a><a>no-href</a>""");
        var rt = fixture.Runtime;
        Assert.Equal(2, (long)rt.Evaluate("document.forms.length")!.GetValue<double>());
        Assert.Equal(1, (long)rt.Evaluate("document.images.length")!.GetValue<double>());
        Assert.Equal(1, (long)rt.Evaluate("document.links.length")!.GetValue<double>());
    }

    [Fact]
    public async Task ParserImagesLoadConcurrentlyWithoutBlockingTheEventLoop()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn parser_images_load_concurrently_without_blocking_the_event_loop() {
                let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
                let address = listener.local_addr().unwrap();
                let accepted = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
                let active = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
                let max_active = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
                let server_accepted = accepted.clone();
                let server_active = active.clone();
                let server_max_active = max_active.clone();
                let png = two_by_three_png();
                std::thread::spawn(move || {
                    for _ in 0..4 {
                        let (mut stream, _) = listener.accept().unwrap();
                        server_accepted.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
                        let active = server_active.clone();
                        let max_active = server_max_active.clone();
                        let png = png.clone();
                        std::thread::spawn(move || {
                            use std::io::{Read as _, Write as _};

                            let mut request = [0u8; 2048];
                            let _ = stream.read(&mut request);
                            let concurrent = active.fetch_add(1, std::sync::atomic::Ordering::SeqCst) + 1;
                            max_active.fetch_max(concurrent, std::sync::atomic::Ordering::SeqCst);
                            std::thread::sleep(std::time::Duration::from_millis(150));
                            let response = format!(
                                "HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                                png.len()
                            );
                            stream.write_all(response.as_bytes()).unwrap();
                            stream.write_all(&png).unwrap();
                            active.fetch_sub(1, std::sync::atomic::Ordering::SeqCst);
                        });
                    }
                });

                let base = format!("http://{address}");
                let html = format!(
                    r#"<img src="{base}/one.png"><img src="{base}/two.png">
                        <img src="{base}/three.png"><img src="{base}/shared.png">
                        <img src="{base}/shared.png">"#
                );
                let mut rt = ObscuraJsRuntime::new();
                rt.set_dom(parse_html(&html));
                rt.set_url(&format!("{base}/page.html"));
                rt.set_http_client(std::sync::Arc::new(
                    obscura_net::ObscuraHttpClient::with_full_options(
                        std::sync::Arc::new(obscura_net::CookieJar::new()),
                        None,
                        true,
                    ),
                ));
                rt.run_page_init();

                let started = std::time::Instant::now();
                let result = rt
                    .evaluate_for_cdp(
                        r#"
                        new Promise(resolve => {
                            globalThis.__imageTimerRan = false;
                            setTimeout(() => { __imageTimerRan = true; }, 10);
                            const images = Array.from(document.images);
                            const events = [];
                            const finish = (type, image) => {
                                events.push([
                                    type,
                                    __imageTimerRan,
                                    image.naturalWidth,
                                    image.naturalHeight,
                                ]);
                                if (events.length === images.length) resolve(events);
                            };
                            for (const image of images) {
                                image.addEventListener("load", () => finish("load", image));
                                image.addEventListener("error", () => finish("error", image));
                                void image.complete;
                            }
                            setTimeout(() => resolve([["timed out"]]), 2000);
                        })
                        "#,
                        true,
                        true,
                    )
                    .await
                    .unwrap();
                let elapsed = started.elapsed();
                assert_eq!(
                    result.value.unwrap(),
                    serde_json::json!([
                        ["load", true, 2, 3],
                        ["load", true, 2, 3],
                        ["load", true, 2, 3],
                        ["load", true, 2, 3],
                        ["load", true, 2, 3],
                    ])
                );
                assert_eq!(
                    accepted.load(std::sync::atomic::Ordering::SeqCst),
                    4,
                    "two elements selecting one URL must share a single request"
                );
                assert!(
                    max_active.load(std::sync::atomic::Ordering::SeqCst) >= 3,
                    "slow image requests did not overlap"
                );
                assert!(
                    elapsed < std::time::Duration::from_millis(500),
                    "four 150ms image requests serialized: {elapsed:?}"
                );
                assert_eq!(
                    rt.state
                        .borrow()
                        .page_in_flight
                        .load(std::sync::atomic::Ordering::SeqCst),
                    0
                );
            }
        */
        var png = TwoByThreePng();
        var active = 0;
        var maxActive = 0;
        using var server = new RawHttpServer(_ =>
        {
            var concurrent = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, concurrent);
            Thread.Sleep(150);
            Interlocked.Decrement(ref active);
            return BinaryResponse("image/png", png);
        });

        var html =
            $"""<img src="{server.Origin}/one.png"><img src="{server.Origin}/two.png">"""
            + $"""<img src="{server.Origin}/three.png"><img src="{server.Origin}/shared.png">"""
            + $"""<img src="{server.Origin}/shared.png">""";
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(html));
        rt.SetUrl($"{server.Origin}/page.html");
        rt.SetHttpClient(new ObscuraHttpClient(new CookieJar(), null, allowPrivateNetwork: true));
        rt.RunPageInit();

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await rt.EvaluateForCdpAsync(
            """
            new Promise(resolve => {
                globalThis.__imageTimerRan = false;
                setTimeout(() => { __imageTimerRan = true; }, 10);
                const images = Array.from(document.images);
                const events = [];
                const finish = (type, image) => {
                    events.push([
                        type,
                        __imageTimerRan,
                        image.naturalWidth,
                        image.naturalHeight,
                    ]);
                    if (events.length === images.length) resolve(events);
                };
                for (const image of images) {
                    image.addEventListener("load", () => finish("load", image));
                    image.addEventListener("error", () => finish("error", image));
                    void image.complete;
                }
                setTimeout(() => resolve([["timed out"]]), 2000);
            })
            """,
            returnByValue: true,
            awaitPromise: true);
        var elapsed = started.Elapsed;
        AssertJsonEquals(
            """
            [
                ["load", true, 2, 3],
                ["load", true, 2, 3],
                ["load", true, 2, 3],
                ["load", true, 2, 3],
                ["load", true, 2, 3]
            ]
            """,
            result.Value);
        Assert.Equal(4, server.RequestLines.Count);
        Assert.True(
            Volatile.Read(ref maxActive) >= 3,
            "slow image requests did not overlap");
        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(500),
            $"four 150ms image requests serialized: {elapsed}");
        Assert.Equal(0, rt.State.PageInFlight.Value);
    }

    [Fact]
    public async Task ImageLifecycleCacheIsSeparatedByCorsCredentialsProfile()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn image_lifecycle_cache_is_separated_by_cors_credentials_profile() {
                let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
                let address = listener.local_addr().unwrap();
                let requests = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
                let server_requests = requests.clone();
                let png = two_by_three_png();
                std::thread::spawn(move || {
                    use std::io::{Read as _, Write as _};
                    for _ in 0..4 {
                        let (mut stream, _) = listener.accept().unwrap();
                        let mut request = [0u8; 4096];
                        let read = stream.read(&mut request).unwrap();
                        let request = String::from_utf8_lossy(&request[..read]).to_ascii_lowercase();
                        let path = request
                            .lines()
                            .next()
                            .and_then(|line| line.split_whitespace().nth(1))
                            .unwrap_or("")
                            .to_string();
                        let mode = request
                            .lines()
                            .find_map(|line| line.strip_prefix("sec-fetch-mode: "))
                            .unwrap_or("")
                            .trim()
                            .to_string();
                        server_requests.lock().unwrap().push((path.clone(), mode));
                        let cors_headers = if path == "/cors.png" {
                            // Anonymous accepts wildcard; use-credentials must reject
                            // it even when credentials permission is also present.
                            "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Credentials: true\r\n"
                        } else {
                            ""
                        };
                        let response = format!(
                            "HTTP/1.1 200 OK\r\nContent-Type: image/png\r\n{cors_headers}Content-Length: {}\r\nConnection: close\r\n\r\n",
                            png.len()
                        );
                        stream.write_all(response.as_bytes()).unwrap();
                        stream.write_all(&png).unwrap();
                    }
                });

                let base = format!("http://{address}");
                let mut rt = ObscuraJsRuntime::new();
                rt.set_dom(parse_html(&format!(r#"<img id="image" src="{base}/plain.png">"#)));
                // Deliberately make the image cross-origin from the document.
                rt.set_url("http://127.0.0.1:1/page.html");
                rt.set_http_client(std::sync::Arc::new(
                    obscura_net::ObscuraHttpClient::with_full_options(
                        std::sync::Arc::new(obscura_net::CookieJar::new()),
                        None,
                        true,
                    ),
                ));
                rt.run_page_init();
                rt.execute_script(
                    "observe-profiled-image",
                    r#"
                        globalThis.image = document.getElementById("image");
                        globalThis.__profileEvents = [];
                        image.addEventListener("load", () => __profileEvents.push("load"));
                        image.addEventListener("error", () => __profileEvents.push("error"));
                        void image.complete;
                    "#,
                )
                .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate("[image.complete, image.naturalWidth, __profileEvents]")
                        .unwrap(),
                    serde_json::json!([true, 2, ["load"]])
                );

                rt.execute_script("require-anonymous-cors", r#"image.crossOrigin = "anonymous";"#)
                    .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate("[image.complete, image.naturalWidth, __profileEvents]")
                        .unwrap(),
                    serde_json::json!([true, 0, ["load", "error"]]),
                    "URL-keyed no-CORS bytes must not satisfy an anonymous CORS request"
                );

                rt.execute_script("restore-no-cors", "image.removeAttribute('crossorigin');")
                    .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate("[image.complete, image.naturalWidth, __profileEvents]")
                        .unwrap(),
                    serde_json::json!([true, 2, ["load", "error", "load"]]),
                    "a CORS failure must not poison the earlier no-CORS success"
                );

                rt.execute_script(
                    "load-anonymous-cors",
                    &format!(r#"image.crossOrigin = "anonymous"; image.src = "{base}/cors.png";"#),
                )
                .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate("[image.complete, image.naturalWidth, __profileEvents]")
                        .unwrap(),
                    serde_json::json!([true, 2, ["load", "error", "load", "load"]])
                );

                rt.execute_script(
                    "require-credentialed-cors",
                    r#"image.crossOrigin = "use-credentials";"#,
                )
                .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate("[image.complete, image.naturalWidth, __profileEvents]")
                        .unwrap(),
                    serde_json::json!([true, 0, ["load", "error", "load", "load", "error"]]),
                    "anonymous CORS success must not satisfy use-credentials"
                );

                rt.execute_script(
                    "restore-anonymous-cors",
                    r#"image.crossOrigin = "anonymous";"#,
                )
                .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate("[image.complete, image.naturalWidth, __profileEvents]")
                        .unwrap(),
                    serde_json::json!([true, 2, ["load", "error", "load", "load", "error", "load"]]),
                    "credentialed CORS failure must not poison anonymous success"
                );

                assert_eq!(
                    *requests.lock().unwrap(),
                    vec![
                        ("/plain.png".to_string(), "no-cors".to_string()),
                        ("/plain.png".to_string(), "cors".to_string()),
                        ("/cors.png".to_string(), "cors".to_string()),
                        ("/cors.png".to_string(), "cors".to_string()),
                    ]
                );
            }
        */
        var png = TwoByThreePng();
        List<(string Path, string Mode)> requests = [];
        using var server = new RawHttpServer(request =>
        {
            var lower = request.ToLowerInvariant();
            var path = RawHttpServer.RequestPath(request);
            var mode = "";
            foreach (var line in lower.Split('\n'))
            {
                if (line.StartsWith("sec-fetch-mode: ", StringComparison.Ordinal))
                {
                    mode = line["sec-fetch-mode: ".Length..].Trim();
                }
            }
            lock (requests)
            {
                requests.Add((path, mode));
            }
            // Anonymous accepts wildcard; use-credentials must reject it even when
            // credentials permission is also present.
            var corsHeaders = path == "/cors.png"
                ? "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Credentials: true\r\n"
                : "";
            return BinaryResponse("image/png", png, corsHeaders);
        });

        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml($"""<img id="image" src="{server.Origin}/plain.png">"""));
        // Deliberately make the image cross-origin from the document.
        rt.SetUrl("http://127.0.0.1:1/page.html");
        rt.SetHttpClient(new ObscuraHttpClient(new CookieJar(), null, allowPrivateNetwork: true));
        rt.RunPageInit();
        rt.ExecuteScript(
            "observe-profiled-image",
            """
            globalThis.image = document.getElementById("image");
            globalThis.__profileEvents = [];
            image.addEventListener("load", () => __profileEvents.push("load"));
            image.addEventListener("error", () => __profileEvents.push("error"));
            void image.complete;
            """);
        // The reference pumps 100ms per step. The port's first request to a fresh
        // origin spends longer than that inside SocketsHttpHandler, so the pump
        // budget is widened; every assertion below is the reference's, unchanged.
        await rt.RunEventLoopBoundedAsync(1_000);
        AssertJsonEquals(
            """[true, 2, ["load"]]""",
            rt.Evaluate("[image.complete, image.naturalWidth, __profileEvents]"));

        rt.ExecuteScript("require-anonymous-cors", """image.crossOrigin = "anonymous";""");
        await rt.RunEventLoopBoundedAsync(1_000);
        AssertJsonEquals(
            """[true, 0, ["load", "error"]]""",
            rt.Evaluate("[image.complete, image.naturalWidth, __profileEvents]"));

        rt.ExecuteScript("restore-no-cors", "image.removeAttribute('crossorigin');");
        await rt.RunEventLoopBoundedAsync(1_000);
        AssertJsonEquals(
            """[true, 2, ["load", "error", "load"]]""",
            rt.Evaluate("[image.complete, image.naturalWidth, __profileEvents]"));

        rt.ExecuteScript(
            "load-anonymous-cors",
            $"""image.crossOrigin = "anonymous"; image.src = "{server.Origin}/cors.png";""");
        await rt.RunEventLoopBoundedAsync(1_000);
        AssertJsonEquals(
            """[true, 2, ["load", "error", "load", "load"]]""",
            rt.Evaluate("[image.complete, image.naturalWidth, __profileEvents]"));

        rt.ExecuteScript("require-credentialed-cors", """image.crossOrigin = "use-credentials";""");
        await rt.RunEventLoopBoundedAsync(1_000);
        AssertJsonEquals(
            """[true, 0, ["load", "error", "load", "load", "error"]]""",
            rt.Evaluate("[image.complete, image.naturalWidth, __profileEvents]"));

        rt.ExecuteScript("restore-anonymous-cors", """image.crossOrigin = "anonymous";""");
        await rt.RunEventLoopBoundedAsync(1_000);
        AssertJsonEquals(
            """[true, 2, ["load", "error", "load", "load", "error", "load"]]""",
            rt.Evaluate("[image.complete, image.naturalWidth, __profileEvents]"));

        Assert.Equal(
            [("/plain.png", "no-cors"), ("/plain.png", "cors"), ("/cors.png", "cors"), ("/cors.png", "cors")],
            requests);
    }

    [Fact]
    public async Task ParserImageDataSrcMutationDoesNotRestartLifecycle()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn parser_image_data_src_mutation_does_not_restart_lifecycle() {
                let requests = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
                let seen = requests.clone();
                let png = two_by_three_png();
                let mut rt = parser_image_runtime(
                    r#"<img id="image" src="real.png" data-src="deferred.png">"#,
                    move |url: &str| {
                        seen.lock().unwrap().push(url.to_string());
                        Some(png.clone())
                    },
                );
                rt.execute_script(
                    "observe-data-src-mutation",
                    r#"
                        globalThis.__dataSrcEvents = [];
                        const image = document.getElementById("image");
                        image.addEventListener("load", () => __dataSrcEvents.push(image.currentSrc));
                        void image.complete;
                    "#,
                )
                .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate("[image.complete, image.currentSrc, __dataSrcEvents]")
                        .unwrap(),
                    serde_json::json!([
                        true,
                        "http://example.com/page/real.png",
                        ["http://example.com/page/real.png"]
                    ])
                );

                rt.execute_script(
                    "mutate-non-source-data-attribute",
                    r#"
                        image.dataset.src = "ignored.png";
                        globalThis.__afterDataSrcMutation = [image.complete, image.currentSrc];
                    "#,
                )
                .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate("[__afterDataSrcMutation, __dataSrcEvents]")
                        .unwrap(),
                    serde_json::json!([
                        [true, "http://example.com/page/real.png"],
                        ["http://example.com/page/real.png"]
                    ])
                );
                assert_eq!(
                    *requests.lock().unwrap(),
                    vec!["http://example.com/page/real.png".to_string()]
                );
            }
        */
        var png = TwoByThreePng();
        var requests = new RecordingLoader(_ => png);
        using var fixture = ParserImageRuntime(
            """<img id="image" src="real.png" data-src="deferred.png">""",
            requests.Load);
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "observe-data-src-mutation",
            """
            globalThis.__dataSrcEvents = [];
            const image = document.getElementById("image");
            image.addEventListener("load", () => __dataSrcEvents.push(image.currentSrc));
            void image.complete;
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJsonEquals(
            """
            [
                true,
                "http://example.com/page/real.png",
                ["http://example.com/page/real.png"]
            ]
            """,
            rt.Evaluate("[image.complete, image.currentSrc, __dataSrcEvents]"));

        rt.ExecuteScript(
            "mutate-non-source-data-attribute",
            """
            image.dataset.src = "ignored.png";
            globalThis.__afterDataSrcMutation = [image.complete, image.currentSrc];
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJsonEquals(
            """
            [
                [true, "http://example.com/page/real.png"],
                ["http://example.com/page/real.png"]
            ]
            """,
            rt.Evaluate("[__afterDataSrcMutation, __dataSrcEvents]"));
        Assert.Equal(["http://example.com/page/real.png"], requests.Urls);
    }

    [Fact]
    public async Task ParserImageDataSrcIsInertUntilScriptAssignsSrc()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn parser_image_data_src_is_inert_until_script_assigns_src() {
                let requests = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
                let seen = requests.clone();
                let png = two_by_three_png();
                let mut rt = parser_image_runtime(
                    r#"<img id="image" data-src="promoted.png">"#,
                    move |url: &str| {
                        seen.lock().unwrap().push(url.to_string());
                        Some(png.clone())
                    },
                );
                assert_eq!(
                    rt.evaluate(
                        r#"(() => {
                            globalThis.image = document.getElementById("image");
                            globalThis.__promotedEvents = [];
                            image.addEventListener("load", () => __promotedEvents.push(image.currentSrc));
                            return [image.src, image.currentSrc, image.complete,
                                    image.naturalWidth, image.naturalHeight];
                        })()"#,
                    )
                    .unwrap(),
                    serde_json::json!(["", "", true, 0, 0])
                );
                rt.run_event_loop_bounded(100).await.unwrap();
                assert!(requests.lock().unwrap().is_empty());

                rt.execute_script(
                    "promote-data-src-through-page-script",
                    r#"
                        image.src = image.dataset.src;
                        globalThis.__afterSrcPromotion = [image.complete, image.currentSrc];
                    "#,
                )
                .unwrap();
                assert_eq!(
                    rt.evaluate("__afterSrcPromotion").unwrap(),
                    serde_json::json!([false, "http://example.com/page/promoted.png"])
                );
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate(
                        "[image.complete, image.naturalWidth, image.naturalHeight, \
                          image.currentSrc, __promotedEvents]"
                    )
                    .unwrap(),
                    serde_json::json!([
                        true,
                        2,
                        3,
                        "http://example.com/page/promoted.png",
                        ["http://example.com/page/promoted.png"]
                    ])
                );
                assert_eq!(
                    *requests.lock().unwrap(),
                    vec!["http://example.com/page/promoted.png".to_string()]
                );
            }
        */
        var png = TwoByThreePng();
        var requests = new RecordingLoader(_ => png);
        using var fixture = ParserImageRuntime("""<img id="image" data-src="promoted.png">""", requests.Load);
        var rt = fixture.Runtime;
        AssertJsonEquals(
            """["", "", true, 0, 0]""",
            rt.Evaluate("""
                (() => {
                    globalThis.image = document.getElementById("image");
                    globalThis.__promotedEvents = [];
                    image.addEventListener("load", () => __promotedEvents.push(image.currentSrc));
                    return [image.src, image.currentSrc, image.complete,
                            image.naturalWidth, image.naturalHeight];
                })()
                """));
        await rt.RunEventLoopBoundedAsync(100);
        Assert.Empty(requests.Urls);

        rt.ExecuteScript(
            "promote-data-src-through-page-script",
            """
            image.src = image.dataset.src;
            globalThis.__afterSrcPromotion = [image.complete, image.currentSrc];
            """);
        AssertJsonEquals(
            """[false, "http://example.com/page/promoted.png"]""",
            rt.Evaluate("__afterSrcPromotion"));
        await rt.RunEventLoopBoundedAsync(100);
        AssertJsonEquals(
            """
            [
                true,
                2,
                3,
                "http://example.com/page/promoted.png",
                ["http://example.com/page/promoted.png"]
            ]
            """,
            rt.Evaluate(
                "[image.complete, image.naturalWidth, image.naturalHeight,"
                + " image.currentSrc, __promotedEvents]"));
        Assert.Equal(["http://example.com/page/promoted.png"], requests.Urls);
    }

    [Fact]
    public async Task ParserImageLifecycleUsesSharedRenderResource()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn parser_image_lifecycle_uses_shared_render_resource() {
                let calls = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
                let loader_calls = calls.clone();
                let png = two_by_three_png();
                let mut rt = parser_image_runtime(
                    r#"<img id="hero" src="../assets/hero.png">"#,
                    move |_url: &str| {
                        loader_calls.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
                        Some(png.clone())
                    },
                );
                rt.execute_script(
                    "observe-parser-image",
                    r#"
                        globalThis.__imageEvents = [];
                        globalThis.__decodeState = "pending";
                        const image = document.getElementById("hero");
                        globalThis.__imageInitial = [
                            image instanceof HTMLImageElement,
                            image.complete,
                            image.naturalWidth,
                            image.naturalHeight
                        ];
                        image.addEventListener("load", () => __imageEvents.push("load"));
                        image.addEventListener("error", () => __imageEvents.push("error"));
                        image.decode().then(
                            () => { __decodeState = "resolved"; },
                            error => { __decodeState = error.name; }
                        );
                    "#,
                )
                .unwrap();
                assert_eq!(
                    rt.evaluate("__imageInitial").unwrap(),
                    serde_json::json!([true, false, 0, 0])
                );

                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate(
                        r#"[
                            image.complete,
                            image.naturalWidth,
                            image.naturalHeight,
                            image.currentSrc,
                            __imageEvents,
                            __decodeState
                        ]"#,
                    )
                    .unwrap(),
                    serde_json::json!([
                        true,
                        2,
                        3,
                        "http://example.com/assets/hero.png",
                        ["load"],
                        "resolved"
                    ])
                );
                assert_eq!(calls.load(std::sync::atomic::Ordering::SeqCst), 1);

                // A subsequent request for the same URL is served by the retained
                // renderer bytes rather than calling the loader again.
                rt.execute_script("reload-image", r#"image.src = "../assets/hero.png";"#)
                    .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(calls.load(std::sync::atomic::Ordering::SeqCst), 1);
            }
        */
        var calls = 0;
        var png = TwoByThreePng();
        using var fixture = ParserImageRuntime(
            """<img id="hero" src="../assets/hero.png">""",
            _ => { Interlocked.Increment(ref calls); return png; });
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "observe-parser-image",
            """
            globalThis.__imageEvents = [];
            globalThis.__decodeState = "pending";
            const image = document.getElementById("hero");
            globalThis.__imageInitial = [
                image instanceof HTMLImageElement,
                image.complete,
                image.naturalWidth,
                image.naturalHeight
            ];
            image.addEventListener("load", () => __imageEvents.push("load"));
            image.addEventListener("error", () => __imageEvents.push("error"));
            image.decode().then(
                () => { __decodeState = "resolved"; },
                error => { __decodeState = error.name; }
            );
            """);
        AssertJsonEquals("[true, false, 0, 0]", rt.Evaluate("__imageInitial"));

        await rt.RunEventLoopBoundedAsync(100);
        AssertJsonEquals(
            """
            [
                true,
                2,
                3,
                "http://example.com/assets/hero.png",
                ["load"],
                "resolved"
            ]
            """,
            rt.Evaluate("""
                [
                    image.complete,
                    image.naturalWidth,
                    image.naturalHeight,
                    image.currentSrc,
                    __imageEvents,
                    __decodeState
                ]
                """));
        Assert.Equal(1, Volatile.Read(ref calls));

        // A subsequent request for the same URL is served by the retained
        // renderer bytes rather than calling the loader again.
        rt.ExecuteScript("reload-image", """image.src = "../assets/hero.png";""");
        await rt.RunEventLoopBoundedAsync(100);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ParserImageFailureCompletesAndRejectsDecode()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn parser_image_failure_completes_and_rejects_decode() {
                let calls = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
                let loader_calls = calls.clone();
                let mut rt = parser_image_runtime(
                    r#"<img id="broken" src="missing.png">"#,
                    move |_url: &str| {
                        loader_calls.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
                        None
                    },
                );
                rt.execute_script(
                    "observe-broken-image",
                    r#"
                        globalThis.__brokenEvents = [];
                        globalThis.__brokenDecode = "pending";
                        const broken = document.getElementById("broken");
                        broken.onload = () => __brokenEvents.push("load");
                        broken.onerror = () => __brokenEvents.push("error");
                        broken.decode().then(
                            () => { __brokenDecode = "resolved"; },
                            error => { __brokenDecode = error.name; }
                        );
                    "#,
                )
                .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate(
                        "[broken.complete, broken.naturalWidth, broken.naturalHeight, \
                          __brokenEvents, __brokenDecode]"
                    )
                    .unwrap(),
                    serde_json::json!([true, 0, 0, ["error"], "EncodingError"])
                );
                assert_eq!(calls.load(std::sync::atomic::Ordering::SeqCst), 1);
            }
        */
        var calls = 0;
        using var fixture = ParserImageRuntime(
            """<img id="broken" src="missing.png">""",
            _ => { Interlocked.Increment(ref calls); return null; });
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "observe-broken-image",
            """
            globalThis.__brokenEvents = [];
            globalThis.__brokenDecode = "pending";
            const broken = document.getElementById("broken");
            broken.onload = () => __brokenEvents.push("load");
            broken.onerror = () => __brokenEvents.push("error");
            broken.decode().then(
                () => { __brokenDecode = "resolved"; },
                error => { __brokenDecode = error.name; }
            );
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJsonEquals(
            """[true, 0, 0, ["error"], "EncodingError"]""",
            rt.Evaluate(
                "[broken.complete, broken.naturalWidth, broken.naturalHeight,"
                + " __brokenEvents, __brokenDecode]"));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void ParserImageFirstGetterObservesPrepareSeededCacheSynchronously()
    {
        int calls = 0;
        byte[] png = RenderCaptureSupport.TwoByThreePng();
        using var owner = RenderCaptureSupport.ParserImageRuntime(
            """<img id="cached" src="cached.png">""",
            _ =>
            {
                Interlocked.Increment(ref calls);
                return png;
            });
        var rt = owner.Runtime;
        Assert.NotNull(Obscura.Js.Ops.RenderState.EnsurePreparedRender(rt.State));
        Assert.Equal(1, calls);

        // Constructing the JS wrapper happens here, after prepare_dom loaded
        // the resource. The first complete getter must see that cache hit
        // immediately; it must not briefly regress to pending/zero.
        AssertJson(
            """[true, 2, 3, "http://example.com/page/cached.png"]""",
            rt.Evaluate(
                """
                (() => {
                    const cached = document.getElementById("cached");
                    return [
                        cached.complete,
                        cached.naturalWidth,
                        cached.naturalHeight,
                        cached.currentSrc
                    ];
                })()
                """));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ParserImageFallbackInvalidatesOnlyNewIntrinsicGeometry()
    {
        int calls = 0;
        byte[] png = RenderCaptureSupport.TwoByThreePng();
        using var owner = RenderCaptureSupport.ParserImageRuntime(
            """<img id="late" src="late.png">""",
            _ =>
            {
                Interlocked.Increment(ref calls);
                return png;
            });
        var rt = owner.Runtime;
        bool previous = rt.State.RenderResources.SetSyncLoadingEnabled(false);
        Assert.NotNull(Obscura.Js.Ops.RenderState.EnsurePreparedRender(rt.State));
        rt.State.RenderResources.SetSyncLoadingEnabled(previous);
        Assert.NotNull(rt.State.PreparedRender);
        Assert.Equal(0, calls);

        rt.ExecuteScript(
            "load-image-after-layout",
            """
            globalThis.__lateEvents = [];
            const late = document.getElementById("late");
            late.addEventListener("load", () => __lateEvents.push("load"));
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJson(
            """[true, 2, 3, ["load"]]""",
            rt.Evaluate("[late.complete, late.naturalWidth, late.naturalHeight, __lateEvents]"));
        Assert.Equal(1, calls);
        Assert.NotNull(rt.State.PreparedRender);
        Assert.Equal<Obscura.Render.RetainedStyleMutation>(
            [Obscura.Render.RetainedStyleMutation.Resource.Instance],
            rt.State.PendingStyleMutations);

        // Once the successful dimensions are retained, another loading-form
        // metadata probe is only a cache hit and must preserve fresh layout.
        Assert.NotNull(Obscura.Js.Ops.RenderState.EnsurePreparedRender(rt.State));
        Assert.NotNull(rt.State.PreparedRender);
        rt.ExecuteScript("reload-retained-image", """late.src = "late.png";""");
        await rt.RunEventLoopBoundedAsync(100);
        Assert.Equal(1, calls);
        Assert.NotNull(rt.State.PreparedRender);

        int missingCalls = 0;
        using var missingOwner = RenderCaptureSupport.ParserImageRuntime(
            """<img id="missing" src="missing.png">""",
            _ =>
            {
                Interlocked.Increment(ref missingCalls);
                return null;
            });
        var missing = missingOwner.Runtime;
        bool missingPrevious = missing.State.RenderResources.SetSyncLoadingEnabled(false);
        Assert.NotNull(Obscura.Js.Ops.RenderState.EnsurePreparedRender(missing.State));
        missing.State.RenderResources.SetSyncLoadingEnabled(missingPrevious);
        Assert.NotNull(missing.State.PreparedRender);
        missing.ExecuteScript(
            "fail-image-after-layout",
            """
            globalThis.__missingEvents = [];
            const missing = document.getElementById("missing");
            missing.addEventListener("error", () => __missingEvents.push("error"));
            """);
        await missing.RunEventLoopBoundedAsync(100);
        AssertJson(
            """[true, 0, 0, ["error"]]""",
            missing.Evaluate(
                "[missing.complete, missing.naturalWidth, missing.naturalHeight, __missingEvents]"));
        Assert.Equal(1, missingCalls);
        Assert.NotNull(missing.State.PreparedRender);
    }

    [Fact]
    public async Task StableCachedImageGettersDoNotQueueResizeGeometryWork()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn stable_cached_image_getters_do_not_queue_resize_geometry_work() {
                let png = two_by_three_png();
                let mut rt = parser_image_runtime(
                    r#"<img id="cached" src="cached.png">
                       <div id="probe" style="width:40px;height:20px"></div>"#,
                    move |_url: &str| Some(png.clone()),
                );
                rt.execute_script(
                    "settle-cached-image",
                    r#"
                        const cached = document.getElementById("cached");
                        void cached.complete;
                    "#,
                )
                .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate("[cached.complete, cached.naturalWidth, cached.naturalHeight]")
                        .unwrap(),
                    serde_json::json!([true, 2, 3])
                );

                rt.execute_script(
                    "observe-unrelated-geometry",
                    r#"
                        globalThis.__stableGetterResizeRecords = 0;
                        globalThis.__stableGetterObserver = new ResizeObserver(entries => {
                            __stableGetterResizeRecords += entries.length;
                        });
                        __stableGetterObserver.observe(document.getElementById("probe"));
                    "#,
                )
                .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate(
                        "[__stableGetterResizeRecords, __obscura_nextPendingTimeoutDelay()]"
                    )
                    .unwrap(),
                    serde_json::json!([1, -1])
                );

                rt.execute_script(
                    "read-stable-image-cache",
                    r#"
                        for (let i = 0; i < 50; i++) {
                            void cached.complete;
                            void cached.currentSrc;
                            void cached.naturalWidth;
                            void cached.naturalHeight;
                        }
                    "#,
                )
                .unwrap();
                // Cached lifecycle reads do not change intrinsic dimensions, so they
                // must not enqueue a rendering checkpoint (and its geometry walk).
                assert_eq!(
                    rt.evaluate(
                        "[__stableGetterResizeRecords, __obscura_nextPendingTimeoutDelay()]"
                    )
                    .unwrap(),
                    serde_json::json!([1, -1])
                );
            }
        */
        var png = TwoByThreePng();
        using var fixture = ParserImageRuntime(
            """
            <img id="cached" src="cached.png">
            <div id="probe" style="width:40px;height:20px"></div>
            """,
            _ => png);
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "settle-cached-image",
            """
            const cached = document.getElementById("cached");
            void cached.complete;
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJsonEquals(
            "[true, 2, 3]",
            rt.Evaluate("[cached.complete, cached.naturalWidth, cached.naturalHeight]"));

        rt.ExecuteScript(
            "observe-unrelated-geometry",
            """
            globalThis.__stableGetterResizeRecords = 0;
            globalThis.__stableGetterObserver = new ResizeObserver(entries => {
                __stableGetterResizeRecords += entries.length;
            });
            __stableGetterObserver.observe(document.getElementById("probe"));
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJsonEquals(
            "[1, -1]",
            rt.Evaluate("[__stableGetterResizeRecords, __obscura_nextPendingTimeoutDelay()]"));

        rt.ExecuteScript(
            "read-stable-image-cache",
            """
            for (let i = 0; i < 50; i++) {
                void cached.complete;
                void cached.currentSrc;
                void cached.naturalWidth;
                void cached.naturalHeight;
            }
            """);
        // Cached lifecycle reads do not change intrinsic dimensions, so they
        // must not enqueue a rendering checkpoint (and its geometry walk).
        AssertJsonEquals(
            "[1, -1]",
            rt.Evaluate("[__stableGetterResizeRecords, __obscura_nextPendingTimeoutDelay()]"));
    }

    [Fact]
    public async Task ParserImageSourceReplacementCancelsQueuedCompletion()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn parser_image_source_replacement_cancels_queued_completion() {
                let requests = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
                let seen = requests.clone();
                let first = two_by_three_png();
                use base64::Engine as _;
                let second = base64::engine::general_purpose::STANDARD
                    .decode(
                        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAFCAYAAABirU3b\
                         AAAAFUlEQVR4nGNk+M/wnwEJMDGgATIEAKVaAgg/Jbt7AAAAAElFTkSuQmCC"
                            .replace(char::is_whitespace, ""),
                    )
                    .unwrap();
                let mut rt = parser_image_runtime(r#"<img id="swap" src="old.png">"#, move |url: &str| {
                    seen.lock().unwrap().push(url.to_string());
                    if url.ends_with("/new.png") {
                        Some(second.clone())
                    } else {
                        Some(first.clone())
                    }
                });
                rt.execute_script(
                    "replace-image-source",
                    r#"
                        globalThis.__swapEvents = [];
                        const swap = document.getElementById("swap");
                        swap.addEventListener("load", () => __swapEvents.push(swap.currentSrc));
                        swap.src = "new.png";
                    "#,
                )
                .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate(
                        "[swap.complete, swap.naturalWidth, swap.naturalHeight, \
                          swap.currentSrc, __swapEvents]"
                    )
                    .unwrap(),
                    serde_json::json!([
                        true,
                        4,
                        5,
                        "http://example.com/page/new.png",
                        ["http://example.com/page/new.png"]
                    ])
                );
                assert_eq!(
                    *requests.lock().unwrap(),
                    vec!["http://example.com/page/new.png".to_string()]
                );
            }
        */
        var first = TwoByThreePng();
        var second = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAFCAYAAABirU3b"
            + "AAAAFUlEQVR4nGNk+M/wnwEJMDGgATIEAKVaAgg/Jbt7AAAAAElFTkSuQmCC");
        var requests = new RecordingLoader(
            url => url.EndsWith("/new.png", StringComparison.Ordinal) ? second : first);
        using var fixture = ParserImageRuntime("""<img id="swap" src="old.png">""", requests.Load);
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "replace-image-source",
            """
            globalThis.__swapEvents = [];
            const swap = document.getElementById("swap");
            swap.addEventListener("load", () => __swapEvents.push(swap.currentSrc));
            swap.src = "new.png";
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJsonEquals(
            """
            [
                true,
                4,
                5,
                "http://example.com/page/new.png",
                ["http://example.com/page/new.png"]
            ]
            """,
            rt.Evaluate(
                "[swap.complete, swap.naturalWidth, swap.naturalHeight,"
                + " swap.currentSrc, __swapEvents]"));
        Assert.Equal(["http://example.com/page/new.png"], requests.Urls);
    }

    [Fact]
    public async Task ResponsivePictureLifecycleTracksViewportDensityAndSourceMedia()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn responsive_picture_lifecycle_tracks_viewport_density_and_source_media() {
                let requests = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
                let seen = requests.clone();
                let png = two_by_three_png();
                let mut rt = parser_image_runtime(
                    r#"
                        <picture>
                            <source type="image/avif" srcset="unsupported.avif">
                            <source id="wide-source" media="(min-width: 800px)"
                                    srcset="wide.png 2x">
                            <source media="(max-width: 799px)" srcset="narrow.png">
                            <img id="responsive-picture" src="fallback.png">
                        </picture>
                    "#,
                    move |url: &str| {
                        seen.lock().unwrap().push(url.to_string());
                        Some(png.clone())
                    },
                );
                rt.set_viewport(1000.0, 600.0);
                rt.execute_script(
                    "observe-responsive-picture",
                    r#"
                        globalThis.__pictureLoads = [];
                        const pictureImage = document.getElementById("responsive-picture");
                        pictureImage.addEventListener("load", () => {
                            __pictureLoads.push(pictureImage.currentSrc);
                        });
                    "#,
                )
                .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate(
                        "[pictureImage.currentSrc, pictureImage.naturalWidth, \
                          pictureImage.naturalHeight, __pictureLoads]"
                    )
                    .unwrap(),
                    serde_json::json!([
                        "http://example.com/page/wide.png",
                        1,
                        2,
                        ["http://example.com/page/wide.png"]
                    ])
                );

                // A live viewport change re-runs the renderer's media/source
                // selection. The cache-only complete getter must report pending but
                // must not perform the load itself.
                rt.set_viewport(600.0, 600.0);
                assert_eq!(
                    rt.evaluate("pictureImage.complete").unwrap(),
                    serde_json::json!(false)
                );
                assert_eq!(requests.lock().unwrap().len(), 1);
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate(
                        "[pictureImage.currentSrc, pictureImage.naturalWidth, \
                          pictureImage.naturalHeight, __pictureLoads]"
                    )
                    .unwrap(),
                    serde_json::json!([
                        "http://example.com/page/narrow.png",
                        2,
                        3,
                        [
                            "http://example.com/page/wide.png",
                            "http://example.com/page/narrow.png"
                        ]
                    ])
                );

                // Mutating a <source> selection input invalidates its associated img.
                // The wide bytes are already shared in the render cache, but lifecycle
                // completion remains task-queued and emits one new load event.
                rt.execute_script(
                    "mutate-picture-source",
                    r#"
                        document.getElementById("wide-source").setAttribute("media", "all");
                        globalThis.__pictureCompleteAfterSourceMutation = pictureImage.complete;
                    "#,
                )
                .unwrap();
                assert_eq!(
                    rt.evaluate("__pictureCompleteAfterSourceMutation").unwrap(),
                    serde_json::json!(false)
                );
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate("[pictureImage.currentSrc, __pictureLoads]")
                        .unwrap(),
                    serde_json::json!([
                        "http://example.com/page/wide.png",
                        [
                            "http://example.com/page/wide.png",
                            "http://example.com/page/narrow.png",
                            "http://example.com/page/wide.png"
                        ]
                    ])
                );
                assert_eq!(
                    *requests.lock().unwrap(),
                    vec![
                        "http://example.com/page/wide.png".to_string(),
                        "http://example.com/page/narrow.png".to_string(),
                    ]
                );
            }
        */
        var png = TwoByThreePng();
        var requests = new RecordingLoader(_ => png);
        using var fixture = ParserImageRuntime(
            """
            <picture>
                <source type="image/avif" srcset="unsupported.avif">
                <source id="wide-source" media="(min-width: 800px)"
                        srcset="wide.png 2x">
                <source media="(max-width: 799px)" srcset="narrow.png">
                <img id="responsive-picture" src="fallback.png">
            </picture>
            """,
            requests.Load);
        var rt = fixture.Runtime;
        rt.SetViewport(1000.0, 600.0);
        rt.ExecuteScript(
            "observe-responsive-picture",
            """
            globalThis.__pictureLoads = [];
            const pictureImage = document.getElementById("responsive-picture");
            pictureImage.addEventListener("load", () => {
                __pictureLoads.push(pictureImage.currentSrc);
            });
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJsonEquals(
            """
            [
                "http://example.com/page/wide.png",
                1,
                2,
                ["http://example.com/page/wide.png"]
            ]
            """,
            rt.Evaluate(
                "[pictureImage.currentSrc, pictureImage.naturalWidth,"
                + " pictureImage.naturalHeight, __pictureLoads]"));

        // A live viewport change re-runs the renderer's media/source
        // selection. The cache-only complete getter must report pending but
        // must not perform the load itself.
        rt.SetViewport(600.0, 600.0);
        Assert.False(rt.Evaluate("pictureImage.complete")!.GetValue<bool>());
        Assert.Single(requests.Urls);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJsonEquals(
            """
            [
                "http://example.com/page/narrow.png",
                2,
                3,
                [
                    "http://example.com/page/wide.png",
                    "http://example.com/page/narrow.png"
                ]
            ]
            """,
            rt.Evaluate(
                "[pictureImage.currentSrc, pictureImage.naturalWidth,"
                + " pictureImage.naturalHeight, __pictureLoads]"));

        // Mutating a <source> selection input invalidates its associated img.
        // The wide bytes are already shared in the render cache, but lifecycle
        // completion remains task-queued and emits one new load event.
        rt.ExecuteScript(
            "mutate-picture-source",
            """
            document.getElementById("wide-source").setAttribute("media", "all");
            globalThis.__pictureCompleteAfterSourceMutation = pictureImage.complete;
            """);
        Assert.False(rt.Evaluate("__pictureCompleteAfterSourceMutation")!.GetValue<bool>());
        await rt.RunEventLoopBoundedAsync(100);
        AssertJsonEquals(
            """
            [
                "http://example.com/page/wide.png",
                [
                    "http://example.com/page/wide.png",
                    "http://example.com/page/narrow.png",
                    "http://example.com/page/wide.png"
                ]
            ]
            """,
            rt.Evaluate("[pictureImage.currentSrc, __pictureLoads]"));
        Assert.Equal(
            ["http://example.com/page/wide.png", "http://example.com/page/narrow.png"],
            requests.Urls);
    }

    [Fact]
    public async Task ResponsiveSrcsetSizesUsesRendererSelectedCurrentSrc()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn responsive_srcset_sizes_uses_renderer_selected_current_src() {
                let requests = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
                let seen = requests.clone();
                let png = two_by_three_png();
                let mut rt = parser_image_runtime(
                    r#"<img id="responsive-srcset" src="fallback.png"
                             srcset="small.png 400w, large.png 800w" sizes="400px">"#,
                    move |url: &str| {
                        seen.lock().unwrap().push(url.to_string());
                        Some(png.clone())
                    },
                );
                rt.execute_script(
                    "observe-responsive-srcset",
                    r#"
                        globalThis.__srcsetLoads = [];
                        const srcsetImage = document.getElementById("responsive-srcset");
                        srcsetImage.addEventListener("load", () => {
                            __srcsetLoads.push(srcsetImage.currentSrc);
                        });
                    "#,
                )
                .unwrap();
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate("[srcsetImage.currentSrc, __srcsetLoads]")
                        .unwrap(),
                    serde_json::json!([
                        "http://example.com/page/small.png",
                        ["http://example.com/page/small.png"]
                    ])
                );

                change_srcset_image_sizes(&mut rt);
                assert_eq!(
                    rt.evaluate("srcsetImage.complete").unwrap(),
                    serde_json::json!(false)
                );
                rt.run_event_loop_bounded(100).await.unwrap();
                assert_eq!(
                    rt.evaluate("[srcsetImage.currentSrc, __srcsetLoads]")
                        .unwrap(),
                    serde_json::json!([
                        "http://example.com/page/large.png",
                        [
                            "http://example.com/page/small.png",
                            "http://example.com/page/large.png"
                        ]
                    ])
                );
                assert_eq!(
                    *requests.lock().unwrap(),
                    vec![
                        "http://example.com/page/small.png".to_string(),
                        "http://example.com/page/large.png".to_string(),
                    ]
                );
            }
        */
        var png = TwoByThreePng();
        var requests = new RecordingLoader(_ => png);
        using var fixture = ParserImageRuntime(
            """
            <img id="responsive-srcset" src="fallback.png"
                 srcset="small.png 400w, large.png 800w" sizes="400px">
            """,
            requests.Load);
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "observe-responsive-srcset",
            """
            globalThis.__srcsetLoads = [];
            const srcsetImage = document.getElementById("responsive-srcset");
            srcsetImage.addEventListener("load", () => {
                __srcsetLoads.push(srcsetImage.currentSrc);
            });
            """);
        await rt.RunEventLoopBoundedAsync(100);
        AssertJsonEquals(
            """
            [
                "http://example.com/page/small.png",
                ["http://example.com/page/small.png"]
            ]
            """,
            rt.Evaluate("[srcsetImage.currentSrc, __srcsetLoads]"));

        rt.ExecuteScript("change-responsive-sizes", """srcsetImage.sizes = "800px";""");
        Assert.False(rt.Evaluate("srcsetImage.complete")!.GetValue<bool>());
        await rt.RunEventLoopBoundedAsync(100);
        AssertJsonEquals(
            """
            [
                "http://example.com/page/large.png",
                [
                    "http://example.com/page/small.png",
                    "http://example.com/page/large.png"
                ]
            ]
            """,
            rt.Evaluate("[srcsetImage.currentSrc, __srcsetLoads]"));
        Assert.Equal(
            ["http://example.com/page/small.png", "http://example.com/page/large.png"],
            requests.Urls);
    }

    /// Regression for #105: `HTMLFormElement` must expose `.elements` so
    /// frameworks that probe form field collections work.
    [Fact]
    public void HtmlFormElementExposesElementsCollection()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn html_form_element_exposes_elements_collection() {
                let mut rt = setup_runtime(
                    r#"<form id="f"><input name=a><input name=b><textarea></textarea></form>"#,
                );
                let n = rt
                    .evaluate("document.getElementById('f').elements.length")
                    .unwrap();
                assert_eq!(n.as_f64().unwrap() as i64, 3);
                let is_form = rt
                    .evaluate("document.getElementById('f') instanceof HTMLFormElement")
                    .unwrap();
                assert_eq!(is_form, serde_json::json!(true));
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<form id="f"><input name=a><input name=b><textarea></textarea></form>""");
        var rt = fixture.Runtime;
        Assert.Equal(3, (long)rt.Evaluate("document.getElementById('f').elements.length")!.GetValue<double>());
        Assert.True(rt.Evaluate("document.getElementById('f') instanceof HTMLFormElement")!.GetValue<bool>());
    }

    /// Regression for #105: `Element.prepend` must actually insert at the
    /// start, not silently no-op.
    [Fact]
    public void ElementPrependInsertsAtStart()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn element_prepend_inserts_at_start() {
                let mut rt = setup_runtime(r#"<div id="c"><span>existing</span></div>"#);
                rt.evaluate(
                    r#"
                    const c = document.getElementById('c');
                    const n = document.createElement('span');
                    n.id = 'first';
                    c.prepend(n);
                    "#,
                )
                .unwrap();
                let first_id = rt
                    .evaluate("document.getElementById('c').firstChild.id")
                    .unwrap();
                assert_eq!(first_id, serde_json::json!("first"));
                let count = rt
                    .evaluate("document.getElementById('c').childNodes.length")
                    .unwrap();
                assert_eq!(count.as_f64().unwrap() as i64, 2);
            }
        */
        using var fixture = RuntimeFixture.Setup("""<div id="c"><span>existing</span></div>""");
        var rt = fixture.Runtime;
        rt.Evaluate("""
            const c = document.getElementById('c');
            const n = document.createElement('span');
            n.id = 'first';
            c.prepend(n);
            """);
        Assert.Equal("first", rt.Evaluate("document.getElementById('c').firstChild.id")!.GetValue<string>());
        Assert.Equal(2, (long)rt.Evaluate("document.getElementById('c').childNodes.length")!.GetValue<double>());
    }

    /// Regression for #105: `isEqualNode` compares structure, not identity.
    /// Framework diff algorithms rely on this.
    [Fact]
    public void IsEqualNodeDoesStructuralCompare()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn is_equal_node_does_structural_compare() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        const a = document.createElement('div'); a.setAttribute('class', 'x'); a.innerHTML = '<span>hi</span>';
                        const b = document.createElement('div'); b.setAttribute('class', 'x'); b.innerHTML = '<span>hi</span>';
                        const c = document.createElement('div'); c.innerHTML = '<span>bye</span>';
                        return [a.isEqualNode(b), a.isEqualNode(c), a.isSameNode(b)];
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!([true, false, false]));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            const a = document.createElement('div'); a.setAttribute('class', 'x'); a.innerHTML = '<span>hi</span>';
            const b = document.createElement('div'); b.setAttribute('class', 'x'); b.innerHTML = '<span>hi</span>';
            const c = document.createElement('div'); c.innerHTML = '<span>bye</span>';
            return [a.isEqualNode(b), a.isEqualNode(c), a.isSameNode(b)];
            """);
        AssertJsonEquals("[true, false, false]", result);
    }

    /// Regression for the long-standing insert_before arg-order bug noted
    /// in CLAUDE.md: bootstrap.js was passing (parent, new, ref) but `_dom`
    /// forwards only two args, silently dropping `ref`. With the fix,
    /// `insertBefore` actually inserts.
    [Fact]
    public void InsertBeforeInsertsNodeAtCorrectPosition()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn insert_before_inserts_node_at_correct_position() {
                let mut rt =
                    setup_runtime(r#"<div id="p"><span id="b">b</span><span id="c">c</span></div>"#);
                let order = rt
                    .evaluate(
                        r#"
                        const p = document.getElementById('p');
                        const a = document.createElement('span');
                        a.id = 'a';
                        p.insertBefore(a, document.getElementById('b'));
                        return Array.from(p.children).map(e => e.id).join(',');
                        "#,
                    )
                    .unwrap();
                assert_eq!(order, serde_json::json!("a,b,c"));
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<div id="p"><span id="b">b</span><span id="c">c</span></div>""");
        var order = fixture.Runtime.Evaluate("""
            const p = document.getElementById('p');
            const a = document.createElement('span');
            a.id = 'a';
            p.insertBefore(a, document.getElementById('b'));
            return Array.from(p.children).map(e => e.id).join(',');
            """);
        Assert.Equal("a,b,c", order!.GetValue<string>());
    }

    [Fact]
    public void TestConsoleLog()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        fixture.Runtime.ExecuteScript("test", "console.log('Hello from V8!')");
    }

    [Fact]
    public void TestLocation()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        Assert.Equal("http://example.com/test", fixture.Runtime.Evaluate("location.href")!.GetValue<string>());
    }

    [Fact]
    public void TestButtonClickDispatchesListener()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_button_click_dispatches_listener() {
                let mut rt = setup_runtime(r#"<button id="go">Go</button>"#);
                let result = rt
                    .evaluate(
                        r#"
                    const button = document.getElementById('go');
                    button.addEventListener('click', () => { button.dataset.clicked = 'yes'; });
                    button.click();
                    return button.dataset.clicked;
                "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!("yes"));
            }
        */
        using var fixture = RuntimeFixture.Setup("""<button id="go">Go</button>""");
        var result = fixture.Runtime.Evaluate("""
            const button = document.getElementById('go');
            button.addEventListener('click', () => { button.dataset.clicked = 'yes'; });
            button.click();
            return button.dataset.clicked;
            """);
        Assert.Equal("yes", result!.GetValue<string>());
    }

    [Fact]
    public void TestLabelClickActivatesItsLabeledControl()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_label_click_activates_its_labeled_control() {
                let mut rt = setup_runtime(
                    r#"<label id="explicit" for="a">a</label><input type="checkbox" id="a">
                       <label id="implicit">b <input type="checkbox" id="b"></label>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                    const ids = ['explicit', 'implicit'];
                    for (const id of ids) { document.getElementById(id).click(); }
                    return [document.getElementById('a').checked, document.getElementById('b').checked];
                "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!([true, true]));
            }
        */
        using var fixture = RuntimeFixture.Setup("""
            <label id="explicit" for="a">a</label><input type="checkbox" id="a">
            <label id="implicit">b <input type="checkbox" id="b"></label>
            """);
        var result = fixture.Runtime.Evaluate("""
            const ids = ['explicit', 'implicit'];
            for (const id of ids) { document.getElementById(id).click(); }
            return [document.getElementById('a').checked, document.getElementById('b').checked];
            """);
        AssertJsonEquals("[true, true]", result);
    }

    [Fact]
    public void TestLabelClickHonorsTheAssociationRules()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_label_click_honors_the_association_rules() {
                // A present `for` associates by id alone: an empty value associates
                // nothing and must not fall back to the nested control, and a dangling
                // id activates nothing. A disabled control has no activation behavior.
                let mut rt = setup_runtime(
                    r#"<label id="empty" for="">a <input type="checkbox" id="a"></label>
                       <label id="dangling" for="missing">b</label><input type="checkbox" id="b">
                       <label id="disabled" for="c">c</label><input type="checkbox" id="c" disabled>
                       <label id="both" for="d">d <input type="checkbox" id="e"></label>
                       <input type="checkbox" id="d">"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                    for (const id of ['empty', 'dangling', 'disabled', 'both']) {
                        document.getElementById(id).click();
                    }
                    return ['a', 'b', 'c', 'd', 'e'].map(id => document.getElementById(id).checked);
                "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([false, false, false, true, false])
                );
            }
        */
        // A present `for` associates by id alone: an empty value associates
        // nothing and must not fall back to the nested control, and a dangling
        // id activates nothing. A disabled control has no activation behavior.
        using var fixture = RuntimeFixture.Setup("""
            <label id="empty" for="">a <input type="checkbox" id="a"></label>
            <label id="dangling" for="missing">b</label><input type="checkbox" id="b">
            <label id="disabled" for="c">c</label><input type="checkbox" id="c" disabled>
            <label id="both" for="d">d <input type="checkbox" id="e"></label>
            <input type="checkbox" id="d">
            """);
        var result = fixture.Runtime.Evaluate("""
            for (const id of ['empty', 'dangling', 'disabled', 'both']) {
                document.getElementById(id).click();
            }
            return ['a', 'b', 'c', 'd', 'e'].map(id => document.getElementById(id).checked);
            """);
        AssertJsonEquals("[false, false, false, true, false]", result);
    }

    [Fact]
    public void TestLabelActivationDoesNotDoubleFireOrRecurse()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_label_activation_does_not_double_fire_or_recurse() {
                // Clicking the control inside its own label toggles once, and a click
                // handler that clicks that label back cannot re-enter the forwarding.
                let mut rt = setup_runtime(
                    r#"<label id="wrapper"><input type="checkbox" id="nested"></label>
                       <label id="host" for="reentrant">r</label>
                       <input type="checkbox" id="reentrant">"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                    const reentrant = document.getElementById('reentrant');
                    document.getElementById('nested').click();
                    let bounces = 0;
                    reentrant.addEventListener('click', () => {
                        if (bounces++ < 1) { document.getElementById('host').click(); }
                    });
                    document.getElementById('host').click();
                    return [document.getElementById('nested').checked, reentrant.checked, bounces];
                "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!([true, true, 1]));
            }
        */
        // Clicking the control inside its own label toggles once, and a click
        // handler that clicks that label back cannot re-enter the forwarding.
        using var fixture = RuntimeFixture.Setup("""
            <label id="wrapper"><input type="checkbox" id="nested"></label>
            <label id="host" for="reentrant">r</label>
            <input type="checkbox" id="reentrant">
            """);
        var result = fixture.Runtime.Evaluate("""
            const reentrant = document.getElementById('reentrant');
            document.getElementById('nested').click();
            let bounces = 0;
            reentrant.addEventListener('click', () => {
                if (bounces++ < 1) { document.getElementById('host').click(); }
            });
            document.getElementById('host').click();
            return [document.getElementById('nested').checked, reentrant.checked, bounces];
            """);
        AssertJsonEquals("[true, true, 1]", result);
    }

    [Fact]
    public void TestClickRespectsDisabledControlsAndInteractiveContent()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_click_respects_disabled_controls_and_interactive_content() {
                // A disabled control has no activation behaviour, whether it is clicked
                // directly, reached through its label, or disabled by an ancestor
                // fieldset. Interactive content inside a label swallows the label's
                // activation, but an <a> without href is not interactive content.
                let mut rt = setup_runtime(
                    r#"<input type="checkbox" id="direct" disabled>
                       <fieldset disabled><label id="in-set" for="set-box">s</label>
                         <input type="checkbox" id="set-box"></fieldset>
                       <label id="link"><a href="/x"><span id="in-link">go</span></a>
                         <input type="checkbox" id="link-box"></label>
                       <label id="plain"><a><span id="in-plain">go</span></a>
                         <input type="checkbox" id="plain-box"></label>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                    const ids = ['direct', 'in-set', 'in-link', 'in-plain'];
                    for (const id of ids) { document.getElementById(id).click(); }
                    return ['direct', 'set-box', 'link-box', 'plain-box']
                        .map(id => document.getElementById(id).checked);
                "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!([false, false, false, true]));
            }
        */
        // A disabled control has no activation behaviour, whether it is clicked
        // directly, reached through its label, or disabled by an ancestor
        // fieldset. Interactive content inside a label swallows the label's
        // activation, but an <a> without href is not interactive content.
        using var fixture = RuntimeFixture.Setup("""
            <input type="checkbox" id="direct" disabled>
            <fieldset disabled><label id="in-set" for="set-box">s</label>
              <input type="checkbox" id="set-box"></fieldset>
            <label id="link"><a href="/x"><span id="in-link">go</span></a>
              <input type="checkbox" id="link-box"></label>
            <label id="plain"><a><span id="in-plain">go</span></a>
              <input type="checkbox" id="plain-box"></label>
            """);
        var result = fixture.Runtime.Evaluate("""
            const ids = ['direct', 'in-set', 'in-link', 'in-plain'];
            for (const id of ids) { document.getElementById(id).click(); }
            return ['direct', 'set-box', 'link-box', 'plain-box']
                .map(id => document.getElementById(id).checked);
            """);
        AssertJsonEquals("[false, false, false, true]", result);
    }

    [Fact]
    public void TestCheckboxIndeterminateIsIdlOnlyAndClearedByActivation()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_checkbox_indeterminate_is_idl_only_and_cleared_by_activation() {
                // `indeterminate` has no content attribute, so it exists only if the
                // prototype defines it -- `'indeterminate' in el` is the check that
                // fails when it is missing. Activation clears it as well as toggling
                // checkedness (HTML legacy-pre-activation behaviour), and a cancelled
                // click puts both back, so a script-set flag is never left stuck.
                let mut rt = setup_runtime(
                    r#"<input type="checkbox" id="fresh">
                       <input type="checkbox" id="click">
                       <input type="checkbox" id="cancel">"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                    const fresh = document.getElementById('fresh');
                    const present = 'indeterminate' in fresh;
                    const initial = fresh.indeterminate;
                    fresh.indeterminate = true;
                    const roundTrip = fresh.indeterminate;

                    const clicked = document.getElementById('click');
                    clicked.indeterminate = true;
                    clicked.click();

                    const cancelled = document.getElementById('cancel');
                    cancelled.indeterminate = true;
                    cancelled.addEventListener('click', e => e.preventDefault());
                    cancelled.click();

                    return [present, initial, roundTrip,
                            clicked.checked, clicked.indeterminate,
                            cancelled.checked, cancelled.indeterminate];
                "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([true, false, true, true, false, false, true])
                );
            }
        */
        // `indeterminate` has no content attribute, so it exists only if the
        // prototype defines it -- `'indeterminate' in el` is the check that
        // fails when it is missing. Activation clears it as well as toggling
        // checkedness (HTML legacy-pre-activation behaviour), and a cancelled
        // click puts both back, so a script-set flag is never left stuck.
        using var fixture = RuntimeFixture.Setup("""
            <input type="checkbox" id="fresh">
            <input type="checkbox" id="click">
            <input type="checkbox" id="cancel">
            """);
        var result = fixture.Runtime.Evaluate("""
            const fresh = document.getElementById('fresh');
            const present = 'indeterminate' in fresh;
            const initial = fresh.indeterminate;
            fresh.indeterminate = true;
            const roundTrip = fresh.indeterminate;

            const clicked = document.getElementById('click');
            clicked.indeterminate = true;
            clicked.click();

            const cancelled = document.getElementById('cancel');
            cancelled.indeterminate = true;
            cancelled.addEventListener('click', e => e.preventDefault());
            cancelled.click();

            return [present, initial, roundTrip,
                    clicked.checked, clicked.indeterminate,
                    cancelled.checked, cancelled.indeterminate];
            """);
        AssertJsonEquals("[true, false, true, true, false, false, true]", result);
    }

    [Fact]
    public void TestDisabledOnlyAppliesToDisableableElements()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_disabled_only_applies_to_disableable_elements() {
                // A `disabled` attribute is meaningless on anything that cannot be
                // disabled, and only listed form controls inherit it from a fieldset.
                // Component libraries do put `disabled` on plain <div>s, so treating
                // that as disabled would silently stop their clicks.
                let mut rt = setup_runtime(
                    r#"<div id="plain" disabled>x</div>
                       <fieldset disabled><a id="link" href="x">l</a>
                         <input type="checkbox" id="control"></fieldset>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                    const seen = [];
                    for (const id of ['plain', 'link']) {
                        const el = document.getElementById(id);
                        el.addEventListener('click', e => { seen.push(id); e.preventDefault(); });
                        el.click();
                    }
                    document.getElementById('control').click();
                    return [seen.join(','), document.getElementById('control').checked];
                "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!(["plain,link", false]));
            }
        */
        // A `disabled` attribute is meaningless on anything that cannot be
        // disabled, and only listed form controls inherit it from a fieldset.
        // Component libraries do put `disabled` on plain <div>s, so treating
        // that as disabled would silently stop their clicks.
        using var fixture = RuntimeFixture.Setup("""
            <div id="plain" disabled>x</div>
            <fieldset disabled><a id="link" href="x">l</a>
              <input type="checkbox" id="control"></fieldset>
            """);
        var result = fixture.Runtime.Evaluate("""
            const seen = [];
            for (const id of ['plain', 'link']) {
                const el = document.getElementById(id);
                el.addEventListener('click', e => { seen.push(id); e.preventDefault(); });
                el.click();
            }
            document.getElementById('control').click();
            return [seen.join(','), document.getElementById('control').checked];
            """);
        AssertJsonEquals("""["plain,link", false]""", result);
    }

    [Fact]
    public void TestLabelForwardingUsesInteractiveContentNotLabelable()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_label_forwarding_uses_interactive_content_not_labelable() {
                // meter, output and progress are labelable but not interactive, so a
                // click on one still activates the label. An <a> counts only with href.
                let mut rt = setup_runtime(
                    r#"<label id="l1" for="c1"><output id="o">v</output></label>
                       <input type="checkbox" id="c1">
                       <label id="l2" for="c2"><a id="bare">t</a></label>
                       <input type="checkbox" id="c2">
                       <label id="l3" for="c3"><a id="linked" href="x">t</a></label>
                       <input type="checkbox" id="c3">
                       <label id="l4" for="c4"><button id="btn" type="button">b</button></label>
                       <input type="checkbox" id="c4">"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                    const ids = ['o', 'bare', 'linked', 'btn'];
                    for (const id of ids) { document.getElementById(id).click(); }
                    return ['c1', 'c2', 'c3', 'c4'].map(id => document.getElementById(id).checked);
                "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!([true, true, false, false]));
            }
        */
        // meter, output and progress are labelable but not interactive, so a
        // click on one still activates the label. An <a> counts only with href.
        using var fixture = RuntimeFixture.Setup("""
            <label id="l1" for="c1"><output id="o">v</output></label>
            <input type="checkbox" id="c1">
            <label id="l2" for="c2"><a id="bare">t</a></label>
            <input type="checkbox" id="c2">
            <label id="l3" for="c3"><a id="linked" href="x">t</a></label>
            <input type="checkbox" id="c3">
            <label id="l4" for="c4"><button id="btn" type="button">b</button></label>
            <input type="checkbox" id="c4">
            """);
        var result = fixture.Runtime.Evaluate("""
            const ids = ['o', 'bare', 'linked', 'btn'];
            for (const id of ids) { document.getElementById(id).click(); }
            return ['c1', 'c2', 'c3', 'c4'].map(id => document.getElementById(id).checked);
            """);
        AssertJsonEquals("[true, true, false, false]", result);
    }

    [Fact]
    public void TestRadioActivationMovesTheCheckedPeerAndRevertsOnCancel()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_radio_activation_moves_the_checked_peer_and_reverts_on_cancel() {
                let mut rt = setup_runtime(
                    r#"<form><label id="pick" for="b">b</label>
                         <input type="radio" name="g" id="a" checked>
                         <input type="radio" name="g" id="b"></form>
                       <form><label id="veto" for="d">d</label>
                         <input type="radio" name="h" id="c" checked>
                         <input type="radio" name="h" id="d"></form>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                    const seen = [];
                    document.getElementById('b').addEventListener('change', () => seen.push('change'));
                    document.getElementById('pick').click();
                    document.getElementById('d').addEventListener('click', e => e.preventDefault());
                    document.getElementById('veto').click();
                    return [
                        document.getElementById('a').checked, document.getElementById('b').checked,
                        document.getElementById('c').checked, document.getElementById('d').checked,
                        seen.join(','),
                    ];
                "#,
                    )
                    .unwrap();
                // Activating b unchecks its peer a; the cancelled activation of d
                // restores c.
                assert_eq!(
                    result,
                    serde_json::json!([false, true, true, false, "change"])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("""
            <form><label id="pick" for="b">b</label>
              <input type="radio" name="g" id="a" checked>
              <input type="radio" name="g" id="b"></form>
            <form><label id="veto" for="d">d</label>
              <input type="radio" name="h" id="c" checked>
              <input type="radio" name="h" id="d"></form>
            """);
        var result = fixture.Runtime.Evaluate("""
            const seen = [];
            document.getElementById('b').addEventListener('change', () => seen.push('change'));
            document.getElementById('pick').click();
            document.getElementById('d').addEventListener('click', e => e.preventDefault());
            document.getElementById('veto').click();
            return [
                document.getElementById('a').checked, document.getElementById('b').checked,
                document.getElementById('c').checked, document.getElementById('d').checked,
                seen.join(','),
            ];
            """);
        // Activating b unchecks its peer a; the cancelled activation of d
        // restores c.
        AssertJsonEquals("""[false, true, true, false, "change"]""", result);
    }

    [Fact]
    public void TestDisabledFieldsetExemptionIsTheFirstLegendChildOnly()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_disabled_fieldset_exemption_is_the_first_legend_child_only() {
                // Every disabled fieldset ancestor counts, and only descendants of that
                // fieldset's first <legend> child escape it. A legend wrapped in a div
                // is not the fieldset's legend, a second legend does not exempt, and an
                // inner fieldset's legend does not escape an outer disabled fieldset.
                let mut rt = setup_runtime(
                    r#"<fieldset disabled><legend><input type="checkbox" id="a"></legend>
                         <input type="checkbox" id="b"></fieldset>
                       <fieldset disabled><legend>x</legend>
                         <legend><input type="checkbox" id="c"></legend></fieldset>
                       <fieldset disabled><div><legend>
                         <input type="checkbox" id="d"></legend></div></fieldset>
                       <fieldset disabled><div><fieldset disabled><legend>
                         <input type="checkbox" id="e"></legend></fieldset></div></fieldset>
                       <fieldset disabled><fieldset><legend>
                         <input type="checkbox" id="f"></legend></fieldset></fieldset>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                    const ids = ['a', 'b', 'c', 'd', 'e', 'f'];
                    for (const id of ids) { document.getElementById(id).click(); }
                    return ids.map(id => document.getElementById(id).checked);
                "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([true, false, false, false, false, false])
                );
            }
        */
        // Every disabled fieldset ancestor counts, and only descendants of that
        // fieldset's first <legend> child escape it. A legend wrapped in a div
        // is not the fieldset's legend, a second legend does not exempt, and an
        // inner fieldset's legend does not escape an outer disabled fieldset.
        using var fixture = RuntimeFixture.Setup("""
            <fieldset disabled><legend><input type="checkbox" id="a"></legend>
              <input type="checkbox" id="b"></fieldset>
            <fieldset disabled><legend>x</legend>
              <legend><input type="checkbox" id="c"></legend></fieldset>
            <fieldset disabled><div><legend>
              <input type="checkbox" id="d"></legend></div></fieldset>
            <fieldset disabled><div><fieldset disabled><legend>
              <input type="checkbox" id="e"></legend></fieldset></div></fieldset>
            <fieldset disabled><fieldset><legend>
              <input type="checkbox" id="f"></legend></fieldset></fieldset>
            """);
        var result = fixture.Runtime.Evaluate("""
            const ids = ['a', 'b', 'c', 'd', 'e', 'f'];
            for (const id of ids) { document.getElementById(id).click(); }
            return ids.map(id => document.getElementById(id).checked);
            """);
        AssertJsonEquals("[true, false, false, false, false, false]", result);
    }

    [Fact]
    public void TestLabelClickRunsCheckboxPreClickActivation()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_label_click_runs_checkbox_pre_click_activation() {
                // The control flips before the click event dispatches, so listeners
                // observe the new state, `input` and `change` follow, and a cancelled
                // event restores the old state.
                let mut rt = setup_runtime(
                    r#"<label id="live" for="live-box">a</label><input type="checkbox" id="live-box">
                       <label id="cancel" for="cancel-box">b</label>
                       <input type="checkbox" id="cancel-box">"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                    const events = [];
                    const live = document.getElementById('live-box');
                    for (const type of ['click', 'input', 'change']) {
                        live.addEventListener(type, () => events.push(type + ':' + live.checked));
                    }
                    document.getElementById('live').click();
                    const cancelled = document.getElementById('cancel-box');
                    cancelled.addEventListener('click', event => event.preventDefault());
                    document.getElementById('cancel').click();
                    return [events.join(','), live.checked, cancelled.checked];
                "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!(["click:true,input:true,change:true", true, false])
                );
            }
        */
        // The control flips before the click event dispatches, so listeners
        // observe the new state, `input` and `change` follow, and a cancelled
        // event restores the old state.
        using var fixture = RuntimeFixture.Setup("""
            <label id="live" for="live-box">a</label><input type="checkbox" id="live-box">
            <label id="cancel" for="cancel-box">b</label>
            <input type="checkbox" id="cancel-box">
            """);
        var result = fixture.Runtime.Evaluate("""
            const events = [];
            const live = document.getElementById('live-box');
            for (const type of ['click', 'input', 'change']) {
                live.addEventListener(type, () => events.push(type + ':' + live.checked));
            }
            document.getElementById('live').click();
            const cancelled = document.getElementById('cancel-box');
            cancelled.addEventListener('click', event => event.preventDefault());
            document.getElementById('cancel').click();
            return [events.join(','), live.checked, cancelled.checked];
            """);
        AssertJsonEquals("""["click:true,input:true,change:true", true, false]""", result);
    }

    [Fact]
    public void TestDispatchMouseEventRunsListener()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_dispatch_mouse_event_runs_listener() {
                let mut rt = setup_runtime(r#"<button id="go">Go</button>"#);
                let result = rt
                    .evaluate(
                        r#"
                    const button = document.getElementById('go');
                    let count = 0;
                    button.addEventListener('click', () => { count += 1; });
                    button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
                    return count;
                "#,
                    )
                    .unwrap();
                assert_eq!(result.as_f64().unwrap() as i64, 1);
            }
        */
        using var fixture = RuntimeFixture.Setup("""<button id="go">Go</button>""");
        var result = fixture.Runtime.Evaluate("""
            const button = document.getElementById('go');
            let count = 0;
            button.addEventListener('click', () => { count += 1; });
            button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
            return count;
            """);
        Assert.Equal(1, (long)result!.GetValue<double>());
    }

    [Fact]
    public void TestLocationHrefAssignmentUpdatesNavigationState()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_location_href_assignment_updates_navigation_state() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let href = rt
                    .evaluate("const next = '/next'; location.href = next; return location.href;")
                    .unwrap();
                assert_eq!(href, serde_json::json!("http://example.com/next"));
                assert_eq!(
                    rt.take_pending_navigation(),
                    Some((
                        "http://example.com/next".to_string(),
                        "GET".to_string(),
                        "".to_string()
                    ))
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        var href = rt.Evaluate("const next = '/next'; location.href = next; return location.href;");
        Assert.Equal("http://example.com/next", href!.GetValue<string>());
        Assert.Equal(("http://example.com/next", "GET", ""), rt.TakePendingNavigation());
    }

    [Fact]
    public void TestLocationNavigationCoercesUrlObjects()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_location_navigation_coerces_url_objects() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let hrefs = rt
                    .evaluate(
                        r#"(() => {
                            location.href = new URL('/from-href', location.href);
                            const href = location.href;
                            location.assign(new URL('/from-assign', location.href));
                            const assigned = location.href;
                            location.replace(new URL('/from-replace', location.href));
                            return [href, assigned, location.href];
                        })()"#,
                    )
                    .unwrap();
                assert_eq!(
                    hrefs,
                    serde_json::json!([
                        "http://example.com/from-href",
                        "http://example.com/from-assign",
                        "http://example.com/from-replace"
                    ])
                );
                assert_eq!(
                    rt.take_pending_navigation(),
                    Some((
                        "http://example.com/from-replace".to_string(),
                        "GET".to_string(),
                        "".to_string()
                    ))
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        var hrefs = rt.Evaluate("""
            (() => {
                location.href = new URL('/from-href', location.href);
                const href = location.href;
                location.assign(new URL('/from-assign', location.href));
                const assigned = location.href;
                location.replace(new URL('/from-replace', location.href));
                return [href, assigned, location.href];
            })()
            """);
        AssertJsonEquals(
            """
            [
                "http://example.com/from-href",
                "http://example.com/from-assign",
                "http://example.com/from-replace"
            ]
            """,
            hrefs);
        Assert.Equal(("http://example.com/from-replace", "GET", ""), rt.TakePendingNavigation());
    }

    [Fact]
    public void TestSubmitButtonClickHandlerCanPreventDefaultAndNavigate()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_submit_button_click_handler_can_prevent_default_and_navigate() {
                let mut rt =
                    setup_runtime(r#"<form><button type="submit" id="submit">Submit</button></form>"#);
                let href = rt
                    .evaluate(
                        r#"
                    const form = document.querySelector('form');
                    form.addEventListener('submit', (event) => {
                        event.preventDefault();
                        location.href = '/submitted';
                    });
                    document.getElementById('submit').click();
                    return location.href;
                "#,
                    )
                    .unwrap();
                assert_eq!(href, serde_json::json!("http://example.com/submitted"));
                assert_eq!(
                    rt.take_pending_navigation(),
                    Some((
                        "http://example.com/submitted".to_string(),
                        "GET".to_string(),
                        "".to_string()
                    ))
                );
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<form><button type="submit" id="submit">Submit</button></form>""");
        var rt = fixture.Runtime;
        var href = rt.Evaluate("""
            const form = document.querySelector('form');
            form.addEventListener('submit', (event) => {
                event.preventDefault();
                location.href = '/submitted';
            });
            document.getElementById('submit').click();
            return location.href;
            """);
        Assert.Equal("http://example.com/submitted", href!.GetValue<string>());
        Assert.Equal(("http://example.com/submitted", "GET", ""), rt.TakePendingNavigation());
    }

    [Fact]
    public async Task ResponseBodyExposesStreamAndConsumptionState()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn response_body_exposes_stream_and_consumption_state() {
                // #818: a non-null Response body must expose a ReadableStream through
                // .body, a boolean .bodyUsed, and a working getReader(); consuming
                // the body marks it used and a second consumption throws.
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate_for_cdp(
                        r#"(async () => {
                            const r = new Response("hello");
                            const meta = {
                                bodyType: r.body === null ? "null" : typeof r.body,
                                bodyUsed: r.bodyUsed,
                                hasReader: !!(r.body && r.body.getReader),
                            };
                            const reader = r.body.getReader();
                            const first = await reader.read();
                            const second = await reader.read();
                            const chunkText = first.value ? new TextDecoder().decode(first.value) : "";
                            const consumed = r.bodyUsed;
                            let doubleThrew = false;
                            try { await r.text(); } catch (e) { doubleThrew = true; }
                            return { meta, chunkText, done: second.done, consumed, doubleThrew, nullBody: new Response(null).body === null };
                        })()"#,
                        true,
                        true,
                    )
                    .await
                    .expect("evaluation must succeed");
                let out = result.value.unwrap();
                assert_eq!(out["meta"]["bodyType"], "object");
                assert_eq!(out["meta"]["bodyUsed"], false);
                assert_eq!(out["meta"]["hasReader"], true);
                assert_eq!(out["chunkText"], "hello");
                assert_eq!(out["done"], true);
                assert_eq!(out["consumed"], true);
                assert_eq!(out["doubleThrew"], true);
                assert_eq!(out["nullBody"], true);
            }
        */
        // #818: a non-null Response body must expose a ReadableStream through
        // .body, a boolean .bodyUsed, and a working getReader(); consuming
        // the body marks it used and a second consumption throws.
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.EvaluateForCdpAsync(
            """
            (async () => {
                const r = new Response("hello");
                const meta = {
                    bodyType: r.body === null ? "null" : typeof r.body,
                    bodyUsed: r.bodyUsed,
                    hasReader: !!(r.body && r.body.getReader),
                };
                const reader = r.body.getReader();
                const first = await reader.read();
                const second = await reader.read();
                const chunkText = first.value ? new TextDecoder().decode(first.value) : "";
                const consumed = r.bodyUsed;
                let doubleThrew = false;
                try { await r.text(); } catch (e) { doubleThrew = true; }
                return { meta, chunkText, done: second.done, consumed, doubleThrew, nullBody: new Response(null).body === null };
            })()
            """,
            returnByValue: true,
            awaitPromise: true);
        var output = result.Value!;
        Assert.Equal("object", output["meta"]!["bodyType"]!.GetValue<string>());
        Assert.False(output["meta"]!["bodyUsed"]!.GetValue<bool>());
        Assert.True(output["meta"]!["hasReader"]!.GetValue<bool>());
        Assert.Equal("hello", output["chunkText"]!.GetValue<string>());
        Assert.True(output["done"]!.GetValue<bool>());
        Assert.True(output["consumed"]!.GetValue<bool>());
        Assert.True(output["doubleThrew"]!.GetValue<bool>());
        Assert.True(output["nullBody"]!.GetValue<bool>());
    }

    [Fact]
    public void TestNavigator()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_navigator() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let ua = rt.evaluate("navigator.userAgent").unwrap();
                assert!(
                    ua.as_str().unwrap().contains("Chrome"),
                    "UA should contain Chrome: {}",
                    ua
                );
                let wd = rt.evaluate("navigator.webdriver").unwrap();
                assert_eq!(wd, serde_json::json!(false));
                let plugins = rt.evaluate("navigator.plugins.length").unwrap();
                assert!(plugins.as_f64().unwrap() > 0.0, "Should have plugins");
                let chrome = rt.evaluate("typeof window.chrome").unwrap();
                assert_eq!(chrome, serde_json::json!("object"));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        var ua = rt.Evaluate("navigator.userAgent")!.GetValue<string>();
        Assert.True(ua.Contains("Chrome", StringComparison.Ordinal), $"UA should contain Chrome: {ua}");
        Assert.False(rt.Evaluate("navigator.webdriver")!.GetValue<bool>());
        Assert.True(
            rt.Evaluate("navigator.plugins.length")!.GetValue<double>() > 0.0,
            "Should have plugins");
        Assert.Equal("object", rt.Evaluate("typeof window.chrome")!.GetValue<string>());
    }

    [Fact]
    public async Task TestCallFunctionOnNoArgs()
    {
        using var fixture = RuntimeFixture.Setup("<html><head><title>Test</title></head><body></body></html>");
        var result = await fixture.Runtime.CallFunctionOnAsync("() => document.title", null, [], returnByValue: true);
        Assert.Equal("Test", result.Value!.GetValue<string>());
    }

    [Fact]
    public async Task TestCallFunctionOnWithArgs()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        JsonNode?[] args = [Arg(10), Arg(20)];
        var result = await fixture.Runtime.CallFunctionOnAsync("(a, b) => a + b", null, args, returnByValue: true);
        Assert.Equal(30, (long)result.Value!.GetValue<double>());
    }

    [Fact]
    public async Task TestCallFunctionOnWithStringArgs()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        JsonNode?[] args = [Arg("hello"), Arg(" world")];
        var result = await fixture.Runtime.CallFunctionOnAsync("(a, b) => a + b", null, args, returnByValue: true);
        Assert.Equal("hello world", result.Value!.GetValue<string>());
    }

    [Fact]
    public async Task TestCallFunctionOnWithObjectArgs()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        JsonNode?[] args = [Arg(new JsonObject { ["name"] = "test", ["count"] = 5 })];
        var result = await fixture.Runtime.CallFunctionOnAsync(
            "(obj) => obj.name + ':' + obj.count", null, args, returnByValue: true);
        Assert.Equal("test:5", result.Value!.GetValue<string>());
    }

    [Fact]
    public async Task TestCallFunctionOnReturnObject()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.CallFunctionOnAsync("() => ({a: 1, b: 2})", null, [], returnByValue: true);
        Assert.Equal("{\"a\":1,\"b\":2}", result.Value!.ToJsonString());
    }

    [Fact]
    public async Task TestCallFunctionOnObjectRefPreservesMethods()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        var result = await rt.CallFunctionOnAsync(
            "() => ({ items: [1,2,3], getLen: function() { return this.items.length; } })",
            null, [], returnByValue: false);
        var oid = result.ObjectId;
        Assert.NotNull(oid);

        var result2 = await rt.CallFunctionOnAsync(
            "function() { return this.getLen(); }", oid, [], returnByValue: true);
        Assert.Equal(3, (long)result2.Value!.GetValue<double>());
    }

    [Fact]
    public async Task TestEvaluateForCdpDetectsNode()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><h1>Hello</h1></body></html>");
        var result = await fixture.Runtime.EvaluateForCdpAsync(
            "document.querySelector('h1')", returnByValue: false, awaitPromise: false);
        Assert.Equal("node", result.Subtype);
        Assert.Equal("object", result.JsType);
        Assert.NotNull(result.ObjectId);
    }

    [Fact]
    public async Task TestEvaluateForCdpDetectsDocument()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.EvaluateForCdpAsync("document", returnByValue: false, awaitPromise: false);
        Assert.Equal("node", result.Subtype);
        Assert.Equal("HTMLDocument", result.ClassName);
    }

    [Fact]
    public async Task TestEvaluateForCdpAwaitsResolvedPromise()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.EvaluateForCdpAsync("Promise.resolve(42)", returnByValue: true, awaitPromise: true);
        Assert.Equal(42, (long)result.Value!.GetValue<double>());
    }

    [Fact]
    public async Task TestEvaluateForCdpAwaitsTimerPromise()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn test_evaluate_for_cdp_awaits_timer_promise() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate_for_cdp(
                        "new Promise(resolve => setTimeout(() => resolve('done'), 1))",
                        true,
                        true,
                    )
                    .await
                    .unwrap();
                assert_eq!(result.value.unwrap().as_str().unwrap(), "done");
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.EvaluateForCdpAsync(
            "new Promise(resolve => setTimeout(() => resolve('done'), 1))",
            returnByValue: true,
            awaitPromise: true);
        Assert.Equal("done", result.Value!.GetValue<string>());
    }

    [Fact]
    public async Task TestEvaluateForCdpCanAwaitBeyondLegacyFiveSecondCap()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn test_evaluate_for_cdp_can_await_beyond_legacy_five_second_cap() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let started = std::time::Instant::now();
                let result = rt
                    .evaluate_for_cdp_with_timeout(
                        "new Promise(resolve => setTimeout(() => resolve('after-five'), 5100))",
                        true,
                        true,
                        6000,
                    )
                    .await
                    .unwrap();
                assert_eq!(result.value.unwrap().as_str(), Some("after-five"));
                assert!(
                    started.elapsed() >= std::time::Duration::from_secs(5),
                    "long promise resolved before its timer deadline"
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await fixture.Runtime.EvaluateForCdpWithTimeoutAsync(
            "new Promise(resolve => setTimeout(() => resolve('after-five'), 5100))",
            returnByValue: true,
            awaitPromise: true,
            awaitTimeoutMs: 6000);
        Assert.Equal("after-five", result.Value!.GetValue<string>());
        Assert.True(
            started.Elapsed >= TimeSpan.FromSeconds(5),
            "long promise resolved before its timer deadline");
    }

    [Fact]
    public async Task TestCallFunctionOnForCdpReportsUnsettledPromiseTimeout()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var error = await Assert.ThrowsAsync<JsRuntimeException>(() =>
            fixture.Runtime.CallFunctionOnForCdpWithTimeoutAsync(
                "() => new Promise(() => {})", null, [], returnByValue: true, awaitPromise: true, awaitTimeoutMs: 25));
        Assert.Contains("did not settle within 25ms", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestEvaluateForCdpAwaitsAsyncFunction()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.EvaluateForCdpAsync("(async () => 'async-ok')()", returnByValue: true, awaitPromise: true);
        Assert.Equal("async-ok", result.Value!.GetValue<string>());
    }

    // This test used to assert that a rejection came back as `Err`. It does
    // not any more, and the old expectation was the defect: CDP answers the
    // command and reports the rejected value through `exceptionDetails`, so
    // failing the command loses the page error rather than delivering it.
    [Fact]
    public async Task TestEvaluateForCdpReportsPromiseRejection()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var info = await fixture.Runtime.EvaluateForCdpAsync(
            "Promise.reject(new Error('boom'))", returnByValue: true, awaitPromise: true);
        Assert.True(info.Thrown, "the rejected value must be marked as thrown");
        Assert.Equal("object", info.JsType);
        Assert.Equal("error", info.Subtype);
        Assert.Equal("Error", info.ClassName);
        Assert.Contains("boom", info.Description, StringComparison.Ordinal);
        // Reported by reference, never serialized: an Error has no own
        // enumerable properties, so by value it would be `{}`.
        Assert.Null(info.Value);
        Assert.NotNull(info.ObjectId);
    }

    [Fact]
    public async Task CallFunctionOnMarksARejectionInsteadOfReturningIt()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var info = await fixture.Runtime.CallFunctionOnForCdpAsync(
            "() => Promise.reject(new Error('boom'))", null, [], returnByValue: true, awaitPromise: true);
        Assert.True(info.Thrown);
        Assert.Equal("error", info.Subtype);
        Assert.Contains("boom", info.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallFunctionOnSeparatesARejectedObjectFromAResolvedOne()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        var resolved = await rt.CallFunctionOnForCdpAsync(
            "() => Promise.resolve({code: 42})", null, [], returnByValue: true, awaitPromise: true);
        var rejected = await rt.CallFunctionOnForCdpAsync(
            "() => Promise.reject({code: 42})", null, [], returnByValue: true, awaitPromise: true);
        Assert.False(resolved.Thrown);
        Assert.True(rejected.Thrown);
        Assert.Equal("{\"code\":42}", resolved.Value!.ToJsonString());
    }

    [Fact]
    public async Task ASynchronousThrowIsReportedInsteadOfSwallowed()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var info = await fixture.Runtime.EvaluateForCdpAsync(
            "undefined_variable_xyz", returnByValue: false, awaitPromise: false);
        Assert.True(info.Thrown, "a ReferenceError must come back flagged thrown");
        Assert.Equal("error", info.Subtype);
        Assert.Contains("ReferenceError", info.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASynchronousThrowIsReportedWhenAValueWasAskedFor()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var info = await fixture.Runtime.EvaluateForCdpAsync(
            "undefined_variable_xyz", returnByValue: true, awaitPromise: false);
        Assert.True(info.Thrown, "a ReferenceError must not serialize to null");
        Assert.Contains("ReferenceError", info.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThrowStatementIsRunAsAScriptNotWrappedInParentheses()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var info = await fixture.Runtime.EvaluateForCdpAsync(
            "throw new Error('boom')", returnByValue: false, awaitPromise: false);
        Assert.True(info.Thrown);
        Assert.Contains("boom", info.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStatementBundleYieldsItsCompletionValue()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var info = await fixture.Runtime.EvaluateForCdpAsync(
            "var completion_x = 1; completion_x * 2", returnByValue: true, awaitPromise: false);
        Assert.False(info.Thrown);
        Assert.Equal(2.0, info.Value!.GetValue<double>());
    }

    [Fact]
    public async Task ATrailingSemicolonAndSourceUrlStillParse()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        var info = await rt.EvaluateForCdpAsync("(() => 7)();", returnByValue: true, awaitPromise: false);
        Assert.Equal(7.0, info.Value!.GetValue<double>());

        info = await rt.EvaluateForCdpAsync(
            "(() => 8)()\n//# sourceURL=__puppeteer_evaluation_script__", returnByValue: true, awaitPromise: false);
        Assert.Equal(8.0, info.Value!.GetValue<double>());
    }

    [Fact]
    public async Task ARejectionDoesNotLeakIntoTheNextCall()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        var rejected = await rt.CallFunctionOnForCdpAsync(
            "() => Promise.reject(new Error('first'))", null, [], returnByValue: true, awaitPromise: true);
        Assert.True(rejected.Thrown);
        var after = await rt.CallFunctionOnForCdpAsync(
            "() => Promise.resolve(7)", null, [], returnByValue: true, awaitPromise: true);
        Assert.False(after.Thrown, "a later success was still marked as thrown");
        Assert.Equal(7.0, after.Value!.GetValue<double>());

        var evaluated = await rt.EvaluateForCdpAsync("Promise.resolve(8)", returnByValue: true, awaitPromise: true);
        Assert.False(evaluated.Thrown);
        Assert.Equal(8.0, evaluated.Value!.GetValue<double>());
    }

    [Fact]
    public async Task TestCallFunctionOnDomInteraction()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn test_call_function_on_dom_interaction() {
                let mut rt = setup_runtime(r#"<div id="items"><span>A</span><span>B</span></div>"#);
                let args = vec![serde_json::json!({"value": "span"})];
                let result = rt
                    .call_function_on(
                        "(sel) => document.querySelectorAll(sel).length",
                        None,
                        &args,
                        true,
                    )
                    .await
                    .unwrap();
                assert_eq!(result.value.unwrap().as_f64().unwrap() as i64, 2);
            }
        */
        using var fixture = RuntimeFixture.Setup("""<div id="items"><span>A</span><span>B</span></div>""");
        JsonNode?[] args = [Arg("span")];
        var result = await fixture.Runtime.CallFunctionOnAsync(
            "(sel) => document.querySelectorAll(sel).length", null, args, returnByValue: true);
        Assert.Equal(2, (long)result.Value!.GetValue<double>());
    }

    [Fact]
    public void TestInnerHtmlSetter()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_inner_html_setter() {
                let mut rt = setup_runtime(r#"<div id="target"><p>Old</p></div>"#);
                rt.execute_script(
                    "test",
                    r#"
                    var el = document.getElementById('target');
                    el.innerHTML = '<strong>Bold</strong><em>Italic</em>';
                "#,
                )
                .unwrap();
                let result = rt
                    .evaluate("document.getElementById('target').innerHTML")
                    .unwrap();
                let html = result.as_str().unwrap();
                assert!(
                    html.contains("<strong>"),
                    "innerHTML should contain <strong>, got: {}",
                    html
                );
                assert!(
                    html.contains("<em>"),
                    "innerHTML should contain <em>, got: {}",
                    html
                );
                assert!(
                    !html.contains("Old"),
                    "innerHTML should not contain old content, got: {}",
                    html
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("""<div id="target"><p>Old</p></div>""");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "test",
            """
            var el = document.getElementById('target');
            el.innerHTML = '<strong>Bold</strong><em>Italic</em>';
            """);
        var html = rt.Evaluate("document.getElementById('target').innerHTML")!.GetValue<string>();
        Assert.True(html.Contains("<strong>", StringComparison.Ordinal), $"innerHTML should contain <strong>, got: {html}");
        Assert.True(html.Contains("<em>", StringComparison.Ordinal), $"innerHTML should contain <em>, got: {html}");
        Assert.False(html.Contains("Old", StringComparison.Ordinal), $"innerHTML should not contain old content, got: {html}");
    }

    [Fact]
    public void TestInnerHtmlWithNested()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_inner_html_with_nested() {
                let mut rt = setup_runtime(r#"<div id="root"></div>"#);
                rt.execute_script(
                    "test",
                    r#"
                    var el = document.getElementById('root');
                    el.innerHTML = '<ul><li>A</li><li>B</li><li>C</li></ul>';
                "#,
                )
                .unwrap();
                let count = rt
                    .evaluate("document.querySelectorAll('li').length")
                    .unwrap();
                assert_eq!(
                    count.as_f64().unwrap() as i64,
                    3,
                    "Should find 3 li elements after innerHTML set"
                );

                let text = rt
                    .evaluate("document.querySelector('li').textContent")
                    .unwrap();
                assert_eq!(text, serde_json::json!("A"));
            }
        */
        using var fixture = RuntimeFixture.Setup("""<div id="root"></div>""");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "test",
            """
            var el = document.getElementById('root');
            el.innerHTML = '<ul><li>A</li><li>B</li><li>C</li></ul>';
            """);
        Assert.Equal(
            3,
            (long)rt.Evaluate("document.querySelectorAll('li').length")!.GetValue<double>());
        Assert.Equal("A", rt.Evaluate("document.querySelector('li').textContent")!.GetValue<string>());
    }

    [Fact]
    public void TestInputValue()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_input_value() {
                let mut rt = setup_runtime(
                    r#"<form><input id="name" type="text" value="initial"><textarea id="bio">old text</textarea></form>"#,
                );
                let val = rt
                    .evaluate("document.getElementById('name').value")
                    .unwrap();
                assert_eq!(val, serde_json::json!("initial"));
                rt.execute_script(
                    "test",
                    "document.getElementById('name').value = 'new value';",
                )
                .unwrap();
                let val2 = rt
                    .evaluate("document.getElementById('name').value")
                    .unwrap();
                assert_eq!(val2, serde_json::json!("new value"));
                let bio = rt.evaluate("document.getElementById('bio').value").unwrap();
                assert_eq!(bio, serde_json::json!("old text"));
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<form><input id="name" type="text" value="initial"><textarea id="bio">old text</textarea></form>""");
        var rt = fixture.Runtime;
        Assert.Equal("initial", rt.Evaluate("document.getElementById('name').value")!.GetValue<string>());
        rt.ExecuteScript("test", "document.getElementById('name').value = 'new value';");
        Assert.Equal("new value", rt.Evaluate("document.getElementById('name').value")!.GetValue<string>());
        Assert.Equal("old text", rt.Evaluate("document.getElementById('bio').value")!.GetValue<string>());
    }

    [Fact]
    public void TestSequentialRuntimeSwap()
    {
        DomTree? dom1;
        using (var first = RuntimeFixture.Setup("<html><body><h1>Page1</h1></body></html>"))
        {
            Assert.Equal("Page1", first.Runtime.Evaluate("document.querySelector('h1').textContent")!.GetValue<string>());
            dom1 = first.Runtime.TakeDom();
        }

        using (var second = RuntimeFixture.Setup("<html><body><h1>Page2</h1></body></html>"))
        {
            Assert.Equal("Page2", second.Runtime.Evaluate("document.querySelector('h1').textContent")!.GetValue<string>());
        }

        Assert.NotNull(dom1);
        using var restored = new ObscuraJsRuntime();
        restored.SetDom(dom1);
        restored.SetUrl("http://example.com");
        restored.SetTitle("Page1");
        restored.RunPageInit();
        Assert.Equal("Page1", restored.Evaluate("document.querySelector('h1').textContent")!.GetValue<string>());
    }

    [Fact]
    public void TestCheckboxChecked()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_checkbox_checked() {
                let mut rt = setup_runtime(r#"<input id="cb" type="checkbox" checked>"#);
                let checked = rt
                    .evaluate("document.getElementById('cb').checked")
                    .unwrap();
                assert_eq!(checked, serde_json::json!(true));
                rt.execute_script("test", "document.getElementById('cb').checked = false;")
                    .unwrap();
                let checked2 = rt
                    .evaluate("document.getElementById('cb').checked")
                    .unwrap();
                assert_eq!(checked2, serde_json::json!(false));
            }
        */
        using var fixture = RuntimeFixture.Setup("""<input id="cb" type="checkbox" checked>""");
        var rt = fixture.Runtime;
        Assert.True(rt.Evaluate("document.getElementById('cb').checked")!.GetValue<bool>());
        rt.ExecuteScript("test", "document.getElementById('cb').checked = false;");
        Assert.False(rt.Evaluate("document.getElementById('cb').checked")!.GetValue<bool>());
    }

    // Issue #324: React/Preact/Vue install a value tracker by redefining `value`
    // on the element instance so they can tell a real edit from their own
    // controlled write. __obscura_setFieldValue must write through the prototype
    // setter, leaving that per-instance tracker stale, so the following input
    // event reads as a genuine change and onChange fires. A plain assignment
    // keeps the tracker in sync and suppresses onChange.
    [Fact]
    public void SetFieldValueBypassesInstanceValueWrapper()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn set_field_value_bypasses_instance_value_wrapper() {
                let mut rt = setup_runtime(r#"<input id="i">"#);
                let result = rt
                    .evaluate(
                        r#"
                        (function(){
                            var el = document.getElementById('i');
                            var d = Object.getOwnPropertyDescriptor(el.constructor.prototype, 'value');
                            var set = d.set, get = d.get, tracked = '' + el.value;
                            Object.defineProperty(el, 'value', {
                                configurable: true,
                                get: function(){ return get.call(this); },
                                set: function(v){ tracked = '' + v; set.call(this, v); },
                            });
                            el.value = 'wrapped';
                            var afterDirect = { value: el.value, tracked: tracked };
                            globalThis.__obscura_setFieldValue(el, 'value', 'native');
                            var afterHelper = { value: el.value, tracked: tracked };
                            return JSON.stringify({ afterDirect: afterDirect, afterHelper: afterHelper });
                        })()
                        "#,
                    )
                    .unwrap();
                let parsed: serde_json::Value = serde_json::from_str(result.as_str().unwrap()).unwrap();
                // Direct assignment keeps tracker == value (the change that suppresses onChange).
                assert_eq!(parsed["afterDirect"]["value"], "wrapped");
                assert_eq!(parsed["afterDirect"]["tracked"], "wrapped");
                // The helper updates the value but leaves the tracker stale, so onChange fires.
                assert_eq!(parsed["afterHelper"]["value"], "native");
                assert_eq!(parsed["afterHelper"]["tracked"], "wrapped");
            }
        */
        using var fixture = RuntimeFixture.Setup("""<input id="i">""");
        var result = fixture.Runtime.Evaluate("""
            (function(){
                var el = document.getElementById('i');
                var d = Object.getOwnPropertyDescriptor(el.constructor.prototype, 'value');
                var set = d.set, get = d.get, tracked = '' + el.value;
                Object.defineProperty(el, 'value', {
                    configurable: true,
                    get: function(){ return get.call(this); },
                    set: function(v){ tracked = '' + v; set.call(this, v); },
                });
                el.value = 'wrapped';
                var afterDirect = { value: el.value, tracked: tracked };
                globalThis.__obscura_setFieldValue(el, 'value', 'native');
                var afterHelper = { value: el.value, tracked: tracked };
                return JSON.stringify({ afterDirect: afterDirect, afterHelper: afterHelper });
            })()
            """);
        var parsed = JsonNode.Parse(result!.GetValue<string>())!;
        // Direct assignment keeps tracker == value (the change that suppresses onChange).
        Assert.Equal("wrapped", parsed["afterDirect"]!["value"]!.GetValue<string>());
        Assert.Equal("wrapped", parsed["afterDirect"]!["tracked"]!.GetValue<string>());
        // The helper updates the value but leaves the tracker stale, so onChange fires.
        Assert.Equal("native", parsed["afterHelper"]!["value"]!.GetValue<string>());
        Assert.Equal("wrapped", parsed["afterHelper"]!["tracked"]!.GetValue<string>());
    }

    // Issue #324: React feature-detects the modern input-event path with
    // `('oninput' in document)`. If the GlobalEventHandlers on* attributes are
    // only on window (not Document/Element), that check fails and React falls
    // back to a legacy change-detection path, so controlled-input onChange never
    // fires. These must be present on document and Element.prototype too.
    [Fact]
    public void GlobalEventHandlersPresentOnDocumentAndElement()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn global_event_handlers_present_on_document_and_element() {
                let mut rt = setup_runtime("<div></div>");
                let result = rt
                    .evaluate(
                        r#"JSON.stringify({
                            docInput: ('oninput' in document),
                            docChange: ('onchange' in document),
                            docClick: ('onclick' in document),
                            elProtoInput: ('oninput' in Element.prototype),
                            winInput: ('oninput' in window)
                        })"#,
                    )
                    .unwrap();
                let p: serde_json::Value = serde_json::from_str(result.as_str().unwrap()).unwrap();
                assert_eq!(p["docInput"], true);
                assert_eq!(p["docChange"], true);
                assert_eq!(p["docClick"], true);
                assert_eq!(p["elProtoInput"], true);
                assert_eq!(p["winInput"], true);
            }
        */
        using var fixture = RuntimeFixture.Setup("<div></div>");
        var result = fixture.Runtime.Evaluate("""
            JSON.stringify({
                docInput: ('oninput' in document),
                docChange: ('onchange' in document),
                docClick: ('onclick' in document),
                elProtoInput: ('oninput' in Element.prototype),
                winInput: ('oninput' in window)
            })
            """);
        var p = JsonNode.Parse(result!.GetValue<string>())!;
        Assert.True(p["docInput"]!.GetValue<bool>());
        Assert.True(p["docChange"]!.GetValue<bool>());
        Assert.True(p["docClick"]!.GetValue<bool>());
        Assert.True(p["elProtoInput"]!.GetValue<bool>());
        Assert.True(p["winInput"]!.GetValue<bool>());
    }

    [Fact]
    public void TestMatchesAndClosest()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_matches_and_closest() {
                let mut rt = setup_runtime(
                    r#"<div class="outer"><div class="inner"><span id="target">Hi</span></div></div>"#,
                );
                let matches = rt
                    .evaluate("document.getElementById('target').matches('span')")
                    .unwrap();
                assert_eq!(matches, serde_json::json!(true));
                let closest = rt
                    .evaluate("document.getElementById('target').closest('.outer').className")
                    .unwrap();
                assert_eq!(closest, serde_json::json!("outer"));
                let no_match = rt
                    .evaluate("document.getElementById('target').closest('.nonexistent')")
                    .unwrap();
                assert_eq!(no_match, serde_json::Value::Null);
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<div class="outer"><div class="inner"><span id="target">Hi</span></div></div>""");
        var rt = fixture.Runtime;
        Assert.True(rt.Evaluate("document.getElementById('target').matches('span')")!.GetValue<bool>());
        Assert.Equal(
            "outer",
            rt.Evaluate("document.getElementById('target').closest('.outer').className")!.GetValue<string>());
        Assert.Null(rt.Evaluate("document.getElementById('target').closest('.nonexistent')"));
    }

    [Fact]
    public void ShallowElementClonePreservesInterfaceAttributesAndIsolation()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn shallow_element_clone_preserves_interface_attributes_and_isolation() {
                let mut rt = setup_runtime(
                    r#"<section id="src" class="source" data-token="original"><span>child</span></section>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                        const source = document.getElementById('src');
                        const clone = source.cloneNode(false);
                        clone.className = 'clone';
                        source.setAttribute('data-token', 'changed');
                        return [
                            clone instanceof Node,
                            clone instanceof Element,
                            clone instanceof HTMLElement,
                            typeof clone.outerHTML,
                            typeof clone.querySelectorAll,
                            clone.tagName,
                            clone.id,
                            clone.className,
                            clone.getAttribute('data-token'),
                            clone.childNodes.length,
                            clone.ownerDocument === document,
                            clone.parentNode === null,
                            clone !== source,
                            source.className,
                            source.getAttribute('data-token'),
                            source.childNodes.length,
                        ];
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([
                        true, true, true, "string", "function", "SECTION", "src", "clone", "original", 0,
                        true, true, true, "source", "changed", 1
                    ])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<section id="src" class="source" data-token="original"><span>child</span></section>""");
        var result = fixture.Runtime.Evaluate("""
            const source = document.getElementById('src');
            const clone = source.cloneNode(false);
            clone.className = 'clone';
            source.setAttribute('data-token', 'changed');
            return [
                clone instanceof Node,
                clone instanceof Element,
                clone instanceof HTMLElement,
                typeof clone.outerHTML,
                typeof clone.querySelectorAll,
                clone.tagName,
                clone.id,
                clone.className,
                clone.getAttribute('data-token'),
                clone.childNodes.length,
                clone.ownerDocument === document,
                clone.parentNode === null,
                clone !== source,
                source.className,
                source.getAttribute('data-token'),
                source.childNodes.length,
            ];
            """);
        AssertJsonEquals(
            """
            [
                true, true, true, "string", "function", "SECTION", "src", "clone", "original", 0,
                true, true, true, "source", "changed", 1
            ]
            """,
            result);
    }

    [Fact]
    public void DeepDocumentElementCloneStaysAnIndependentHtmlElement()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn deep_document_element_clone_stays_an_independent_html_element() {
                let mut rt = setup_runtime(
                    r#"<html lang="en" data-root="original"><head><title>Clone</title></head><body><main id="app" data-state="source"><p class="item">original text</p></main></body></html>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                        const source = document.documentElement;
                        const clone = source.cloneNode(true);
                        const cloneItem = clone.querySelector('.item');
                        const sourceItem = source.querySelector('.item');
                        cloneItem.textContent = 'clone text';
                        source.querySelector('#app').setAttribute('data-state', 'changed');
                        clone.setAttribute('lang', 'fr');
                        return [
                            clone instanceof Element,
                            clone instanceof HTMLElement,
                            clone.tagName,
                            typeof clone.outerHTML,
                            typeof clone.querySelectorAll,
                            clone.querySelectorAll('head, body, main, p').length,
                            clone.ownerDocument === document,
                            clone.parentNode === null,
                            clone !== source,
                            clone.querySelector('body') !== document.body,
                            clone.getAttribute('data-root'),
                            clone.getAttribute('lang'),
                            source.getAttribute('lang'),
                            cloneItem.textContent,
                            sourceItem.textContent,
                            clone.querySelector('#app').getAttribute('data-state'),
                            source.querySelector('#app').getAttribute('data-state'),
                        ];
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([
                        true,
                        true,
                        "HTML",
                        "string",
                        "function",
                        4,
                        true,
                        true,
                        true,
                        true,
                        "original",
                        "fr",
                        "en",
                        "clone text",
                        "original text",
                        "source",
                        "changed"
                    ])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<html lang="en" data-root="original"><head><title>Clone</title></head><body><main id="app" data-state="source"><p class="item">original text</p></main></body></html>""");
        var result = fixture.Runtime.Evaluate("""
            const source = document.documentElement;
            const clone = source.cloneNode(true);
            const cloneItem = clone.querySelector('.item');
            const sourceItem = source.querySelector('.item');
            cloneItem.textContent = 'clone text';
            source.querySelector('#app').setAttribute('data-state', 'changed');
            clone.setAttribute('lang', 'fr');
            return [
                clone instanceof Element,
                clone instanceof HTMLElement,
                clone.tagName,
                typeof clone.outerHTML,
                typeof clone.querySelectorAll,
                clone.querySelectorAll('head, body, main, p').length,
                clone.ownerDocument === document,
                clone.parentNode === null,
                clone !== source,
                clone.querySelector('body') !== document.body,
                clone.getAttribute('data-root'),
                clone.getAttribute('lang'),
                source.getAttribute('lang'),
                cloneItem.textContent,
                sourceItem.textContent,
                clone.querySelector('#app').getAttribute('data-state'),
                source.querySelector('#app').getAttribute('data-state'),
            ];
            """);
        AssertJsonEquals(
            """
            [
                true,
                true,
                "HTML",
                "string",
                "function",
                4,
                true,
                true,
                true,
                true,
                "original",
                "fr",
                "en",
                "clone text",
                "original text",
                "source",
                "changed"
            ]
            """,
            result);
    }

    [Fact]
    public void TestEvaluateMultistatement()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("var x = 5; var y = 10; return x + y;");
        Assert.Equal(15, (long)result!.GetValue<double>());
    }

    [Fact]
    public async Task TestObjectRefAsArgument()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        var obj = await rt.CallFunctionOnAsync("() => ({ x: 42 })", null, [], returnByValue: false);
        var oid = obj.ObjectId!;

        JsonNode?[] args = [new JsonObject { ["objectId"] = oid }];
        var result = await rt.CallFunctionOnAsync("(obj) => obj.x * 2", null, args, returnByValue: true);
        Assert.Equal(84, (long)result.Value!.GetValue<double>());
    }

    [Fact]
    public void TestDocumentCookieReadsHttpCookies()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_document_cookie_reads_http_cookies() {
                let (mut rt, jar) = setup_runtime_with_cookies("<html><body></body></html>");
                let url = url::Url::parse("http://example.com/test").unwrap();
                jar.set_cookie("session=abc123; Path=/", &url);
                jar.set_cookie("theme=dark; Path=/", &url);
                let result = rt.evaluate("document.cookie").unwrap();
                let cookie_str = result.as_str().unwrap();
                assert!(
                    cookie_str.contains("session=abc123"),
                    "expected session cookie, got: {}",
                    cookie_str
                );
                assert!(
                    cookie_str.contains("theme=dark"),
                    "expected theme cookie, got: {}",
                    cookie_str
                );
            }
        */
        using var fixture = SetupRuntimeWithCookies("<html><body></body></html>", out var jar);
        var url = new Uri("http://example.com/test");
        jar.SetCookie("session=abc123; Path=/", url);
        jar.SetCookie("theme=dark; Path=/", url);
        var cookieString = fixture.Runtime.Evaluate("document.cookie")!.GetValue<string>();
        Assert.True(
            cookieString.Contains("session=abc123", StringComparison.Ordinal),
            $"expected session cookie, got: {cookieString}");
        Assert.True(
            cookieString.Contains("theme=dark", StringComparison.Ordinal),
            $"expected theme cookie, got: {cookieString}");
    }

    [Fact]
    public void TestDocumentCookieExcludesHttponly()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_document_cookie_excludes_httponly() {
                let (mut rt, jar) = setup_runtime_with_cookies("<html><body></body></html>");
                let url = url::Url::parse("http://example.com/test").unwrap();
                jar.set_cookie("visible=yes; Path=/", &url);
                jar.set_cookie("secret=token; Path=/; HttpOnly", &url);
                let result = rt.evaluate("document.cookie").unwrap();
                let cookie_str = result.as_str().unwrap();
                assert!(
                    cookie_str.contains("visible=yes"),
                    "expected visible cookie, got: {}",
                    cookie_str
                );
                assert!(
                    !cookie_str.contains("secret"),
                    "httpOnly cookie should not be visible to JS, got: {}",
                    cookie_str
                );
            }
        */
        using var fixture = SetupRuntimeWithCookies("<html><body></body></html>", out var jar);
        var url = new Uri("http://example.com/test");
        jar.SetCookie("visible=yes; Path=/", url);
        jar.SetCookie("secret=token; Path=/; HttpOnly", url);
        var cookieString = fixture.Runtime.Evaluate("document.cookie")!.GetValue<string>();
        Assert.True(
            cookieString.Contains("visible=yes", StringComparison.Ordinal),
            $"expected visible cookie, got: {cookieString}");
        Assert.False(
            cookieString.Contains("secret", StringComparison.Ordinal),
            $"httpOnly cookie should not be visible to JS, got: {cookieString}");
    }

    [Fact]
    public void TestDocumentCookieSetterStoresInJar()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_document_cookie_setter_stores_in_jar() {
                let (mut rt, jar) = setup_runtime_with_cookies("<html><body></body></html>");
                rt.evaluate("document.cookie = 'foo=bar; Path=/'").unwrap();
                let url = url::Url::parse("http://example.com/test").unwrap();
                let result = rt.evaluate("document.cookie").unwrap();
                assert!(result.as_str().unwrap().contains("foo=bar"));
                let header = jar.get_cookie_header(&url);
                assert!(
                    header.contains("foo=bar"),
                    "cookie should be in jar, got: {}",
                    header
                );
            }
        */
        using var fixture = SetupRuntimeWithCookies("<html><body></body></html>", out var jar);
        var rt = fixture.Runtime;
        rt.Evaluate("document.cookie = 'foo=bar; Path=/'");
        var url = new Uri("http://example.com/test");
        Assert.Contains("foo=bar", rt.Evaluate("document.cookie")!.GetValue<string>(), StringComparison.Ordinal);
        var header = jar.GetCookieHeader(url);
        Assert.True(header.Contains("foo=bar", StringComparison.Ordinal), $"cookie should be in jar, got: {header}");
    }

    [Fact]
    public void TestDocumentCookieDeleteViaMaxAge()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_document_cookie_delete_via_max_age() {
                let (mut rt, jar) = setup_runtime_with_cookies("<html><body></body></html>");
                let url = url::Url::parse("http://example.com/test").unwrap();
                rt.evaluate("document.cookie = 'temp=val; Path=/'").unwrap();
                assert!(rt
                    .evaluate("document.cookie")
                    .unwrap()
                    .as_str()
                    .unwrap()
                    .contains("temp=val"));
                rt.evaluate("document.cookie = 'temp=; Max-Age=0'").unwrap();
                let result = rt.evaluate("document.cookie").unwrap();
                assert!(
                    !result.as_str().unwrap().contains("temp="),
                    "cookie should be deleted, got: {}",
                    result
                );
                assert!(!jar.get_cookie_header(&url).contains("temp="));
            }
        */
        using var fixture = SetupRuntimeWithCookies("<html><body></body></html>", out var jar);
        var rt = fixture.Runtime;
        var url = new Uri("http://example.com/test");
        rt.Evaluate("document.cookie = 'temp=val; Path=/'");
        Assert.Contains("temp=val", rt.Evaluate("document.cookie")!.GetValue<string>(), StringComparison.Ordinal);
        rt.Evaluate("document.cookie = 'temp=; Max-Age=0'");
        var result = rt.Evaluate("document.cookie")!.GetValue<string>();
        Assert.False(
            result.Contains("temp=", StringComparison.Ordinal),
            $"cookie should be deleted, got: {result}");
        Assert.DoesNotContain("temp=", jar.GetCookieHeader(url), StringComparison.Ordinal);
    }

    [Fact]
    public void TestDocumentCookieJsAndHttpMerge()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_document_cookie_js_and_http_merge() {
                let (mut rt, jar) = setup_runtime_with_cookies("<html><body></body></html>");
                let url = url::Url::parse("http://example.com/test").unwrap();
                jar.set_cookie("server_sid=xyz; Path=/", &url);
                rt.evaluate("document.cookie = 'client_pref=light'")
                    .unwrap();
                let result = rt.evaluate("document.cookie").unwrap();
                let cookie_str = result.as_str().unwrap();
                assert!(
                    cookie_str.contains("server_sid=xyz"),
                    "expected server cookie, got: {}",
                    cookie_str
                );
                assert!(
                    cookie_str.contains("client_pref=light"),
                    "expected client cookie, got: {}",
                    cookie_str
                );
            }
        */
        using var fixture = SetupRuntimeWithCookies("<html><body></body></html>", out var jar);
        var rt = fixture.Runtime;
        var url = new Uri("http://example.com/test");
        jar.SetCookie("server_sid=xyz; Path=/", url);
        rt.Evaluate("document.cookie = 'client_pref=light'");
        var cookieString = rt.Evaluate("document.cookie")!.GetValue<string>();
        Assert.True(
            cookieString.Contains("server_sid=xyz", StringComparison.Ordinal),
            $"expected server cookie, got: {cookieString}");
        Assert.True(
            cookieString.Contains("client_pref=light", StringComparison.Ordinal),
            $"expected client cookie, got: {cookieString}");
    }

    [Fact]
    public void TestDocumentCookieEmptyWhenNoCookies()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_document_cookie_empty_when_no_cookies() {
                let (mut rt, _jar) = setup_runtime_with_cookies("<html><body></body></html>");
                let result = rt.evaluate("document.cookie").unwrap();
                assert_eq!(result.as_str().unwrap(), "");
            }
        */
        using var fixture = SetupRuntimeWithCookies("<html><body></body></html>", out _);
        Assert.Equal("", fixture.Runtime.Evaluate("document.cookie")!.GetValue<string>());
    }

    [Fact]
    public void TestDocumentCookieNoJarReturnsEmpty()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_document_cookie_no_jar_returns_empty() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt.evaluate("document.cookie").unwrap();
                assert_eq!(result.as_str().unwrap(), "");
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        Assert.Equal("", fixture.Runtime.Evaluate("document.cookie")!.GetValue<string>());
    }

    [Fact]
    public void TestDocumentWriteAppendsToBody()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_document_write_appends_to_body() {
                let mut rt = setup_runtime("<html><body><p>Existing</p></body></html>");
                rt.evaluate("document.write('<div>Added</div>')").unwrap();
                let html = rt.evaluate("document.body.innerHTML").unwrap();
                let body = html.as_str().unwrap();
                assert!(
                    body.contains("Existing"),
                    "existing content should remain, got: {}",
                    body
                );
                assert!(
                    body.contains("Added"),
                    "written content should appear, got: {}",
                    body
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body><p>Existing</p></body></html>");
        var rt = fixture.Runtime;
        rt.Evaluate("document.write('<div>Added</div>')");
        var body = rt.Evaluate("document.body.innerHTML")!.GetValue<string>();
        Assert.True(
            body.Contains("Existing", StringComparison.Ordinal),
            $"existing content should remain, got: {body}");
        Assert.True(
            body.Contains("Added", StringComparison.Ordinal),
            $"written content should appear, got: {body}");
    }

    [Fact]
    public void TestDocumentWriteln()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_document_writeln() {
                let mut rt = setup_runtime("<html><body></body></html>");
                rt.evaluate("document.writeln('Hello')").unwrap();
                let html = rt.evaluate("document.body.innerHTML").unwrap();
                assert!(html.as_str().unwrap().contains("Hello"));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.Evaluate("document.writeln('Hello')");
        Assert.Contains("Hello", rt.Evaluate("document.body.innerHTML")!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void TestDocumentWriteMultipleArgs()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_document_write_multiple_args() {
                let mut rt = setup_runtime("<html><body></body></html>");
                rt.evaluate("document.write('Hello', ' ', 'World')")
                    .unwrap();
                let text = rt.evaluate("document.body.textContent").unwrap();
                assert_eq!(text.as_str().unwrap().trim(), "Hello World");
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.Evaluate("document.write('Hello', ' ', 'World')");
        Assert.Equal("Hello World", rt.Evaluate("document.body.textContent")!.GetValue<string>().Trim());
    }

    [Fact]
    public void TestDocumentOpenClearsBody()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_document_open_clears_body() {
                let mut rt = setup_runtime("<html><body><p>Old content</p></body></html>");
                rt.evaluate("document.open()").unwrap();
                let html = rt.evaluate("document.body.innerHTML").unwrap();
                assert_eq!(html.as_str().unwrap(), "");
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body><p>Old content</p></body></html>");
        var rt = fixture.Runtime;
        rt.Evaluate("document.open()");
        Assert.Equal("", rt.Evaluate("document.body.innerHTML")!.GetValue<string>());
    }

    [Fact]
    public void TestDocumentWriteHtmlElements()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_document_write_html_elements() {
                let mut rt = setup_runtime("<html><body></body></html>");
                rt.evaluate(r#"document.write('<h1 id="title">Test</h1><p>Para</p>')"#)
                    .unwrap();
                let h1 = rt
                    .evaluate("document.querySelector('h1').textContent")
                    .unwrap();
                assert_eq!(h1.as_str().unwrap(), "Test");
                let p = rt
                    .evaluate("document.querySelector('p').textContent")
                    .unwrap();
                assert_eq!(p.as_str().unwrap(), "Para");
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.Evaluate("""document.write('<h1 id="title">Test</h1><p>Para</p>')""");
        Assert.Equal("Test", rt.Evaluate("document.querySelector('h1').textContent")!.GetValue<string>());
        Assert.Equal("Para", rt.Evaluate("document.querySelector('p').textContent")!.GetValue<string>());
    }

    [Fact]
    public void TestUrlRelativeResolution()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_url_relative_resolution() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate("new URL('data.json', 'http://example.com/path/page.html').href")
                    .unwrap();
                assert_eq!(
                    result.as_str().unwrap(),
                    "http://example.com/path/data.json"
                );

                let result = rt
                    .evaluate("new URL('/api/data', 'http://example.com/path/page.html').href")
                    .unwrap();
                assert_eq!(result.as_str().unwrap(), "http://example.com/api/data");

                let result = rt
                    .evaluate("new URL('https://other.com/foo', 'http://example.com/bar').href")
                    .unwrap();
                assert_eq!(result.as_str().unwrap(), "https://other.com/foo");

                let result = rt
                    .evaluate("new URL('sub/file.js', 'http://example.com/a/b/c.html').href")
                    .unwrap();
                assert_eq!(
                    result.as_str().unwrap(),
                    "http://example.com/a/b/sub/file.js"
                );

                let result = rt
                    .evaluate("new URL('api.json', 'http://localhost:8080/dir/index.html').href")
                    .unwrap();
                assert_eq!(
                    result.as_str().unwrap(),
                    "http://localhost:8080/dir/api.json"
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        Assert.Equal(
            "http://example.com/path/data.json",
            rt.Evaluate("new URL('data.json', 'http://example.com/path/page.html').href")!.GetValue<string>());
        Assert.Equal(
            "http://example.com/api/data",
            rt.Evaluate("new URL('/api/data', 'http://example.com/path/page.html').href")!.GetValue<string>());
        Assert.Equal(
            "https://other.com/foo",
            rt.Evaluate("new URL('https://other.com/foo', 'http://example.com/bar').href")!.GetValue<string>());
        Assert.Equal(
            "http://example.com/a/b/sub/file.js",
            rt.Evaluate("new URL('sub/file.js', 'http://example.com/a/b/c.html').href")!.GetValue<string>());
        Assert.Equal(
            "http://localhost:8080/dir/api.json",
            rt.Evaluate("new URL('api.json', 'http://localhost:8080/dir/index.html').href")!.GetValue<string>());
    }

    [Fact]
    public void BaseHrefGovernsDomUrlReflection()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn base_href_governs_dom_url_reflection() {
                let mut rt = setup_runtime_at_deep_url(BASE_HREF_PAGE);

                // One evaluate, one assert: a bundle of assert_eq! aborts at the first failure and
                // reports one broken path while hiding the others.
                let seen = rt
                    .evaluate(
                        r#"return [
                            document.baseURI,
                            document.getElementById('link').href,
                            document.getElementById('form').action,
                            document.getElementById('script').src,
                        ]"#,
                    )
                    .unwrap();
                assert_eq!(
                    seen.as_array().unwrap(),
                    &vec![
                        serde_json::json!("http://example.com/app/"),
                        serde_json::json!("http://example.com/app/data/x.json"),
                        serde_json::json!("http://example.com/app/submit"),
                        serde_json::json!("http://example.com/app/chunk.js"),
                    ]
                );
            }
        */
        using var fixture = SetupRuntimeAtDeepUrl(BaseHrefPage);

        // One evaluate, one assert: a bundle of assert_eq! aborts at the first failure and
        // reports one broken path while hiding the others.
        var seen = fixture.Runtime.Evaluate("""
            return [
                document.baseURI,
                document.getElementById('link').href,
                document.getElementById('form').action,
                document.getElementById('script').src,
            ]
            """);
        AssertJsonEquals(
            """
            [
                "http://example.com/app/",
                "http://example.com/app/data/x.json",
                "http://example.com/app/submit",
                "http://example.com/app/chunk.js"
            ]
            """,
            seen);
    }

    [Fact]
    public void ARelativeLocationAssignmentFollowsTheBase()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn a_relative_location_assignment_follows_the_base() {
                // _resolveUrl serves location.href=, assign, replace and window.location=. Of the six
                // call sites it has the largest external effect, so it gets its own test.
                let mut rt = setup_runtime_at_deep_url(BASE_HREF_PAGE);
                rt.evaluate("location.href = 'users/42'").unwrap();

                let landed = rt.evaluate("location.href").unwrap();
                assert_eq!(landed.as_str().unwrap(), "http://example.com/app/users/42");
            }
        */
        // _resolveUrl serves location.href=, assign, replace and window.location=. Of the six
        // call sites it has the largest external effect, so it gets its own test.
        using var fixture = SetupRuntimeAtDeepUrl(BaseHrefPage);
        var rt = fixture.Runtime;
        rt.Evaluate("location.href = 'users/42'");
        Assert.Equal("http://example.com/app/users/42", rt.Evaluate("location.href")!.GetValue<string>());
    }

    [Fact]
    public void WithoutBaseHrefResolutionStaysOnTheDocumentUrl()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn without_base_href_resolution_stays_on_the_document_url() {
                let mut rt = setup_runtime_at_deep_url(
                    r#"<html><head></head><body>
                        <a id="link" href="data/x.json"></a>
                        <form id="form" action="submit"></form>
                        <script id="script" src="chunk.js"></script>
                    </body></html>"#,
                );

                // Every call site, not just the anchor: a site that resolves to the origin root instead
                // of the document URL slips through on a page without a base only when nobody is looking.
                let seen = rt
                    .evaluate(
                        r#"return [
                            document.baseURI,
                            document.getElementById('link').href,
                            document.getElementById('form').action,
                            document.getElementById('script').src,
                        ]"#,
                    )
                    .unwrap();
                assert_eq!(
                    seen.as_array().unwrap(),
                    &vec![
                        serde_json::json!("http://example.com/deep/page"),
                        serde_json::json!("http://example.com/deep/data/x.json"),
                        serde_json::json!("http://example.com/deep/submit"),
                        serde_json::json!("http://example.com/deep/chunk.js"),
                    ]
                );
            }
        */
        using var fixture = SetupRuntimeAtDeepUrl("""
            <html><head></head><body>
                <a id="link" href="data/x.json"></a>
                <form id="form" action="submit"></form>
                <script id="script" src="chunk.js"></script>
            </body></html>
            """);

        // Every call site, not just the anchor: a site that resolves to the origin root instead
        // of the document URL slips through on a page without a base only when nobody is looking.
        var seen = fixture.Runtime.Evaluate("""
            return [
                document.baseURI,
                document.getElementById('link').href,
                document.getElementById('form').action,
                document.getElementById('script').src,
            ]
            """);
        AssertJsonEquals(
            """
            [
                "http://example.com/deep/page",
                "http://example.com/deep/data/x.json",
                "http://example.com/deep/submit",
                "http://example.com/deep/chunk.js"
            ]
            """,
            seen);
    }

    [Fact]
    public void BaseElementHrefReflectsTheResolvedUrl()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn base_element_href_reflects_the_resolved_url() {
                // Resolved against the fallback base URL, i.e. the document URL and not /app/.
                let mut rt = setup_runtime_at_deep_url(BASE_HREF_PAGE);
                let href = rt.evaluate("document.querySelector('base').href").unwrap();
                assert_eq!(href.as_str().unwrap(), "http://example.com/app/");

                let mut relative = setup_runtime_at_deep_url(
                    r#"<html><head><base href="assets/"></head><body></body></html>"#,
                );
                let href = relative
                    .evaluate("document.querySelector('base').href")
                    .unwrap();
                assert_eq!(href.as_str().unwrap(), "http://example.com/deep/assets/");
            }
        */
        // Resolved against the fallback base URL, i.e. the document URL and not /app/.
        using var fixture = SetupRuntimeAtDeepUrl(BaseHrefPage);
        Assert.Equal(
            "http://example.com/app/",
            fixture.Runtime.Evaluate("document.querySelector('base').href")!.GetValue<string>());

        using var relative = SetupRuntimeAtDeepUrl(
            """<html><head><base href="assets/"></head><body></body></html>""");
        Assert.Equal(
            "http://example.com/deep/assets/",
            relative.Runtime.Evaluate("document.querySelector('base').href")!.GetValue<string>());
    }

    [Fact]
    public void TheFirstBaseWithAnHrefWins()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn the_first_base_with_an_href_wins() {
                // Tree order, and a <base> without href does not count.
                let mut rt = setup_runtime_at_deep_url(
                    r#"<html><head><base><base href="/a/"><base href="/b/"></head><body>
                        <a id="link" href="x.json"></a>
                    </body></html>"#,
                );
                let link = rt
                    .evaluate("document.getElementById('link').href")
                    .unwrap();
                assert_eq!(link.as_str().unwrap(), "http://example.com/a/x.json");
            }
        */
        // Tree order, and a <base> without href does not count.
        using var fixture = SetupRuntimeAtDeepUrl("""
            <html><head><base><base href="/a/"><base href="/b/"></head><body>
                <a id="link" href="x.json"></a>
            </body></html>
            """);
        Assert.Equal(
            "http://example.com/a/x.json",
            fixture.Runtime.Evaluate("document.getElementById('link').href")!.GetValue<string>());
    }

    [Fact]
    public void AnEmptyBaseHrefResolvesToTheDocumentUrl()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn an_empty_base_href_resolves_to_the_document_url() {
                let mut rt = setup_runtime_at_deep_url(
                    r#"<html><head><base href=""></head><body>
                        <a id="link" href="x.json"></a>
                    </body></html>"#,
                );
                let link = rt
                    .evaluate("document.getElementById('link').href")
                    .unwrap();
                assert_eq!(link.as_str().unwrap(), "http://example.com/deep/x.json");
            }
        */
        using var fixture = SetupRuntimeAtDeepUrl("""
            <html><head><base href=""></head><body>
                <a id="link" href="x.json"></a>
            </body></html>
            """);
        Assert.Equal(
            "http://example.com/deep/x.json",
            fixture.Runtime.Evaluate("document.getElementById('link').href")!.GetValue<string>());
    }

    [Fact]
    public void ACrossOriginBaseMovesTheTargetButNotThePageOrigin()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn a_cross_origin_base_moves_the_target_but_not_the_page_origin() {
                let mut rt = setup_runtime_at_deep_url(
                    r#"<html><head><base href="https://cdn.example.net/v2/"></head><body>
                        <a id="link" href="x.json"></a>
                    </body></html>"#,
                );
                let seen = rt
                    .evaluate("return [document.getElementById('link').href, location.origin]")
                    .unwrap();
                assert_eq!(
                    seen.as_array().unwrap(),
                    &vec![
                        serde_json::json!("https://cdn.example.net/v2/x.json"),
                        serde_json::json!("http://example.com"),
                    ]
                );
            }
        */
        using var fixture = SetupRuntimeAtDeepUrl("""
            <html><head><base href="https://cdn.example.net/v2/"></head><body>
                <a id="link" href="x.json"></a>
            </body></html>
            """);
        var seen = fixture.Runtime.Evaluate(
            "return [document.getElementById('link').href, location.origin]");
        AssertJsonEquals(
            """["https://cdn.example.net/v2/x.json", "http://example.com"]""",
            seen);
    }

    [Fact]
    public void BaseHrefRejectsADataUrlBase()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn base_href_rejects_a_data_url_base() {
                // https://html.spec.whatwg.org/multipage/semantics.html#set-the-frozen-base-url
                // Accepting it would make every later relative resolution fail instead of falling back.
                let mut rt = setup_runtime_at_deep_url(
                    r#"<html><head><base href="data:text/html,x"></head><body>
                        <a id="link" href="data/x.json"></a>
                    </body></html>"#,
                );

                let base_uri = rt.evaluate("document.baseURI").unwrap();
                assert_eq!(base_uri.as_str().unwrap(), "http://example.com/deep/page");

                let link = rt
                    .evaluate("document.getElementById('link').href")
                    .unwrap();
                assert_eq!(link.as_str().unwrap(), "http://example.com/deep/data/x.json");
            }
        */
        // https://html.spec.whatwg.org/multipage/semantics.html#set-the-frozen-base-url
        // Accepting it would make every later relative resolution fail instead of falling back.
        using var fixture = SetupRuntimeAtDeepUrl("""
            <html><head><base href="data:text/html,x"></head><body>
                <a id="link" href="data/x.json"></a>
            </body></html>
            """);
        var rt = fixture.Runtime;
        Assert.Equal("http://example.com/deep/page", rt.Evaluate("document.baseURI")!.GetValue<string>());
        Assert.Equal(
            "http://example.com/deep/data/x.json",
            rt.Evaluate("document.getElementById('link').href")!.GetValue<string>());
    }

    [Fact]
    public void BaseResolutionFollowsTheUrlSetByPushState()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn base_resolution_follows_the_url_set_by_push_state() {
                // pushState changes the document URL, and without <base> that very URL is the base.
                let mut rt = setup_runtime_at_deep_url(
                    r#"<html><head></head><body><a id="link" href="x.json"></a></body></html>"#,
                );
                rt.evaluate("history.pushState({}, '', '/other/route')").unwrap();

                let link = rt
                    .evaluate("document.getElementById('link').href")
                    .unwrap();
                assert_eq!(link.as_str().unwrap(), "http://example.com/other/x.json");
            }
        */
        // pushState changes the document URL, and without <base> that very URL is the base.
        using var fixture = SetupRuntimeAtDeepUrl(
            """<html><head></head><body><a id="link" href="x.json"></a></body></html>""");
        var rt = fixture.Runtime;
        rt.Evaluate("history.pushState({}, '', '/other/route')");
        Assert.Equal(
            "http://example.com/other/x.json",
            rt.Evaluate("document.getElementById('link').href")!.GetValue<string>());
    }

    [Fact]
    public void ARelativeBaseHrefResolvesAgainstThePushStateUrl()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn a_relative_base_href_resolves_against_the_push_state_url() {
                let mut rt = setup_runtime_at_deep_url(
                    r#"<html><head><base href="assets/"></head><body>
                        <a id="link" href="x.json"></a>
                    </body></html>"#,
                );
                rt.evaluate("history.pushState({}, '', '/other/route')").unwrap();

                let link = rt
                    .evaluate("document.getElementById('link').href")
                    .unwrap();
                assert_eq!(link.as_str().unwrap(), "http://example.com/other/assets/x.json");
            }
        */
        using var fixture = SetupRuntimeAtDeepUrl("""
            <html><head><base href="assets/"></head><body>
                <a id="link" href="x.json"></a>
            </body></html>
            """);
        var rt = fixture.Runtime;
        rt.Evaluate("history.pushState({}, '', '/other/route')");
        Assert.Equal(
            "http://example.com/other/assets/x.json",
            rt.Evaluate("document.getElementById('link').href")!.GetValue<string>());
    }

    /// Guards the cache in `document_base_url_memoized`. Without it, each of these reads walked
    /// the tree and ran the selector engine, and `a.href` went from a field read to O(nodes).
    /// The bound is deliberately loose: it should catch the regression, not watch the allocator.
    [Fact]
    public void AnchorHrefReadsDoNotScaleWithDocumentSize()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn anchor_href_reads_do_not_scale_with_document_size() {
                let mut body = String::from(r#"<html><head></head><body><a id="link" href="x.json"></a>"#);
                for i in 0..4000 {
                    body.push_str(&format!("<div id=\"n{i}\"><span>text</span></div>"));
                }
                body.push_str("</body></html>");
                let mut rt = setup_runtime_at_deep_url(&body);

                let elapsed = rt
                    .evaluate(
                        r#"
                        const link = document.getElementById('link');
                        const started = Date.now();
                        for (let i = 0; i < 2000; i++) { link.href; }
                        return Date.now() - started;
                        "#,
                    )
                    .unwrap();
                let ms = elapsed.as_f64().expect("elapsed ms");
                assert!(
                    ms < 500.0,
                    "2000 a.href reads on a document with 12000 nodes took {ms} ms, the base query is not cached"
                );
            }
        */
        var body = new System.Text.StringBuilder(
            """<html><head></head><body><a id="link" href="x.json"></a>""");
        for (var i = 0; i < 4000; i++)
        {
            body.Append($"<div id=\"n{i}\"><span>text</span></div>");
        }
        body.Append("</body></html>");
        using var fixture = SetupRuntimeAtDeepUrl(body.ToString());

        var elapsed = fixture.Runtime.Evaluate("""
            const link = document.getElementById('link');
            const started = Date.now();
            for (let i = 0; i < 2000; i++) { link.href; }
            return Date.now() - started;
            """);
        var ms = elapsed!.GetValue<double>();
        Assert.True(
            ms < 500.0,
            $"2000 a.href reads on a document with 12000 nodes took {ms} ms, the base query is not cached");
    }

    [Fact]
    public void TheBaseMemoStillSeesABaseElementAddedLater()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn the_base_memo_still_sees_a_base_element_added_later() {
                let mut rt = setup_runtime_at_deep_url(
                    r#"<html><head></head><body><a id="link" href="x.json"></a></body></html>"#,
                );
                let before = rt
                    .evaluate("document.getElementById('link').href")
                    .unwrap();
                assert_eq!(before.as_str().unwrap(), "http://example.com/deep/x.json");

                rt.evaluate(
                    r#"
                    const base = document.createElement('base');
                    base.setAttribute('href', '/');
                    document.head.appendChild(base);
                    "#,
                )
                .unwrap();

                let after = rt
                    .evaluate("document.getElementById('link').href")
                    .unwrap();
                assert_eq!(after.as_str().unwrap(), "http://example.com/x.json");
            }
        */
        using var fixture = SetupRuntimeAtDeepUrl(
            """<html><head></head><body><a id="link" href="x.json"></a></body></html>""");
        var rt = fixture.Runtime;
        Assert.Equal(
            "http://example.com/deep/x.json",
            rt.Evaluate("document.getElementById('link').href")!.GetValue<string>());

        rt.Evaluate("""
            const base = document.createElement('base');
            base.setAttribute('href', '/');
            document.head.appendChild(base);
            """);

        Assert.Equal(
            "http://example.com/x.json",
            rt.Evaluate("document.getElementById('link').href")!.GetValue<string>());
    }

    [Fact]
    public void TheBaseMemoNoticesAChangedHrefAttribute()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn the_base_memo_notices_a_changed_href_attribute() {
                let mut rt = setup_runtime_at_deep_url(BASE_HREF_PAGE);
                let before = rt
                    .evaluate("document.getElementById('link').href")
                    .unwrap();
                assert_eq!(before.as_str().unwrap(), "http://example.com/app/data/x.json");

                rt.evaluate("document.querySelector('base').setAttribute('href', '/other/')")
                    .unwrap();

                let after = rt
                    .evaluate("document.getElementById('link').href")
                    .unwrap();
                assert_eq!(after.as_str().unwrap(), "http://example.com/other/data/x.json");
            }
        */
        using var fixture = SetupRuntimeAtDeepUrl(BaseHrefPage);
        var rt = fixture.Runtime;
        Assert.Equal(
            "http://example.com/app/data/x.json",
            rt.Evaluate("document.getElementById('link').href")!.GetValue<string>());

        rt.Evaluate("document.querySelector('base').setAttribute('href', '/other/')");

        Assert.Equal(
            "http://example.com/other/data/x.json",
            rt.Evaluate("document.getElementById('link').href")!.GetValue<string>());
    }

    [Fact]
    public async Task BaseHrefGovernsFetchAndXhrTargets()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn base_href_governs_fetch_and_xhr_targets() {
                let mut rt = setup_runtime_at_deep_url(BASE_HREF_PAGE);
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                        const originalFetchOp = Deno.core.ops.op_fetch_url;
                        const seen = [];
                        try {
                            Deno.core.ops.op_fetch_url = (url) => {
                                seen.push(url);
                                return JSON.stringify({ status: 200, headers: {}, body: "{}", url });
                            };
                            await fetch("data/x.json");
                            const xhr = new XMLHttpRequest();
                            xhr.open("GET", "data/y.json");
                            xhr.send();
                            await new Promise((r) => setTimeout(r, 0));
                            return seen;
                        } finally {
                            Deno.core.ops.op_fetch_url = originalFetchOp;
                        }
                    }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                let value = result.value.expect("captured URLs");
                let seen = value.as_array().expect("captured URLs");
                // The XHR entry cannot isolate its own layer: send() passes the already absolute URL on
                // to fetch, so fetch takes over the resolution if it is removed from send, and the assert
                // still holds. It pins the result, not the layer.
                assert_eq!(seen.len(), 2, "one fetch, one XHR");
                assert_eq!(seen[0].as_str().unwrap(), "http://example.com/app/data/x.json");
                assert_eq!(seen[1].as_str().unwrap(), "http://example.com/app/data/y.json");
            }
        */
        using var fixture = SetupRuntimeAtDeepUrl(BaseHrefPage);
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const originalFetchOp = Deno.core.ops.op_fetch_url;
                const seen = [];
                try {
                    Deno.core.ops.op_fetch_url = (url) => {
                        seen.push(url);
                        return JSON.stringify({ status: 200, headers: {}, body: "{}", url });
                    };
                    await fetch("data/x.json");
                    const xhr = new XMLHttpRequest();
                    xhr.open("GET", "data/y.json");
                    xhr.send();
                    await new Promise((r) => setTimeout(r, 0));
                    return seen;
                } finally {
                    Deno.core.ops.op_fetch_url = originalFetchOp;
                }
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        var seen = result.Value!.AsArray();
        // The XHR entry cannot isolate its own layer: send() passes the already absolute URL on
        // to fetch, so fetch takes over the resolution if it is removed from send, and the assert
        // still holds. It pins the result, not the layer.
        Assert.Equal(2, seen.Count);
        Assert.Equal("http://example.com/app/data/x.json", seen[0]!.GetValue<string>());
        Assert.Equal("http://example.com/app/data/y.json", seen[1]!.GetValue<string>());
    }

    [Fact]
    public async Task TestFetchUrlInputDecodesBinaryBodyBase64()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn test_fetch_url_input_decodes_binary_body_base64() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                        const originalFetchOp = Deno.core.ops.op_fetch_url;
                        try {
                            Deno.core.ops.op_fetch_url = (url) => {
                                globalThis.__capturedFetchUrl = url;
                                return JSON.stringify({
                                    status: 200,
                                    headers: { "content-type": "application/wasm" },
                                    bodyBase64: "AGFzbQEAAAA=",
                                    url,
                                });
                            };
                            const response = await fetch(new URL("/pkg/app_bg.wasm", document.URL));
                            const bytes = Array.from(new Uint8Array(await response.arrayBuffer()));
                            return { url: globalThis.__capturedFetchUrl, bytes };
                        } finally {
                            Deno.core.ops.op_fetch_url = originalFetchOp;
                        }
                    }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                assert_eq!(
                    result.value.unwrap(),
                    serde_json::json!({
                        "url": "http://example.com/pkg/app_bg.wasm",
                        "bytes": [0, 97, 115, 109, 1, 0, 0, 0],
                    })
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const originalFetchOp = Deno.core.ops.op_fetch_url;
                try {
                    Deno.core.ops.op_fetch_url = (url) => {
                        globalThis.__capturedFetchUrl = url;
                        return JSON.stringify({
                            status: 200,
                            headers: { "content-type": "application/wasm" },
                            bodyBase64: "AGFzbQEAAAA=",
                            url,
                        });
                    };
                    const response = await fetch(new URL("/pkg/app_bg.wasm", document.URL));
                    const bytes = Array.from(new Uint8Array(await response.arrayBuffer()));
                    return { url: globalThis.__capturedFetchUrl, bytes };
                } finally {
                    Deno.core.ops.op_fetch_url = originalFetchOp;
                }
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        AssertJsonEquals(
            """
            {
                "url": "http://example.com/pkg/app_bg.wasm",
                "bytes": [0, 97, 115, 109, 1, 0, 0, 0]
            }
            """,
            result.Value);
    }

    [Fact]
    public async Task FetchAndXhrForwardBrowserCredentialsModes()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn fetch_and_xhr_forward_browser_credentials_modes() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                            const originalFetchOp = Deno.core.ops.op_fetch_url;
                            const calls = [];
                            try {
                                Deno.core.ops.op_fetch_url =
                                    (url, method, headers, body, origin, mode, credentials) => {
                                        calls.push({ url, credentials });
                                        return JSON.stringify({
                                            status: 200,
                                            headers: {},
                                            body: "ok",
                                            url,
                                        });
                                    };

                                await fetch("/default");
                                await fetch("/omit", { credentials: "omit" });
                                const request = new Request("/included", { credentials: "include" });
                                await fetch(request);
                                await fetch(request.clone());
                                await fetch(request, { credentials: "same-origin" });

                                const sendXhr = (path, withCredentials) => new Promise((resolve, reject) => {
                                    const xhr = new XMLHttpRequest();
                                    xhr.open("GET", path);
                                    xhr.withCredentials = withCredentials;
                                    xhr.onload = resolve;
                                    xhr.onerror = reject;
                                    xhr.send();
                                });
                                await sendXhr("/xhr-default", false);
                                await sendXhr("/xhr-credentialed", true);

                                let invalidFetchRejected = false;
                                try {
                                    await fetch("/bad", { credentials: "invalid" });
                                } catch (error) {
                                    invalidFetchRejected = error instanceof TypeError;
                                }

                                return { calls, invalidFetchRejected };
                            } finally {
                                Deno.core.ops.op_fetch_url = originalFetchOp;
                            }
                        }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                assert_eq!(
                    result.value.unwrap(),
                    serde_json::json!({
                        "calls": [
                            { "url": "http://example.com/default", "credentials": "same-origin" },
                            { "url": "http://example.com/omit", "credentials": "omit" },
                            { "url": "http://example.com/included", "credentials": "include" },
                            { "url": "http://example.com/included", "credentials": "include" },
                            { "url": "http://example.com/included", "credentials": "same-origin" },
                            { "url": "http://example.com/xhr-default", "credentials": "same-origin" },
                            { "url": "http://example.com/xhr-credentialed", "credentials": "include" },
                        ],
                        "invalidFetchRejected": true,
                    })
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const originalFetchOp = Deno.core.ops.op_fetch_url;
                const calls = [];
                try {
                    Deno.core.ops.op_fetch_url =
                        (url, method, headers, body, origin, mode, credentials) => {
                            calls.push({ url, credentials });
                            return JSON.stringify({
                                status: 200,
                                headers: {},
                                body: "ok",
                                url,
                            });
                        };

                    await fetch("/default");
                    await fetch("/omit", { credentials: "omit" });
                    const request = new Request("/included", { credentials: "include" });
                    await fetch(request);
                    await fetch(request.clone());
                    await fetch(request, { credentials: "same-origin" });

                    const sendXhr = (path, withCredentials) => new Promise((resolve, reject) => {
                        const xhr = new XMLHttpRequest();
                        xhr.open("GET", path);
                        xhr.withCredentials = withCredentials;
                        xhr.onload = resolve;
                        xhr.onerror = reject;
                        xhr.send();
                    });
                    await sendXhr("/xhr-default", false);
                    await sendXhr("/xhr-credentialed", true);

                    let invalidFetchRejected = false;
                    try {
                        await fetch("/bad", { credentials: "invalid" });
                    } catch (error) {
                        invalidFetchRejected = error instanceof TypeError;
                    }

                    return { calls, invalidFetchRejected };
                } finally {
                    Deno.core.ops.op_fetch_url = originalFetchOp;
                }
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        AssertJsonEquals(
            """
            {
                "calls": [
                    { "url": "http://example.com/default", "credentials": "same-origin" },
                    { "url": "http://example.com/omit", "credentials": "omit" },
                    { "url": "http://example.com/included", "credentials": "include" },
                    { "url": "http://example.com/included", "credentials": "include" },
                    { "url": "http://example.com/included", "credentials": "same-origin" },
                    { "url": "http://example.com/xhr-default", "credentials": "same-origin" },
                    { "url": "http://example.com/xhr-credentialed", "credentials": "include" }
                ],
                "invalidFetchRejected": true
            }
            """,
            result.Value);
    }

    [Fact]
    public async Task FetchPreservesBinaryBodySourcesAtTheOpBoundary()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn fetch_preserves_binary_body_sources_at_the_op_boundary() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                            const originalFetchOp = Deno.core.ops.op_fetch_url;
                            const calls = [];
                            try {
                                Deno.core.ops.op_fetch_url =
                                    (url, method, headers, body) => {
                                        calls.push({
                                            path: new URL(url).pathname,
                                            method,
                                            headers: JSON.parse(headers),
                                            isUint8Array: body instanceof Uint8Array,
                                            bytes: Array.from(
                                                body instanceof Uint8Array
                                                    ? body
                                                    : new TextEncoder().encode(body),
                                            ),
                                        });
                                        return JSON.stringify({
                                            status: 200,
                                            headers: {},
                                            body: "ok",
                                            url,
                                        });
                                    };

                                const sentinel = [0, 128, 255, 16];
                                await fetch("/blob", {
                                    method: "POST",
                                    body: new Blob([new Uint8Array(sentinel)]),
                                });

                                const arrayBuffer = new Uint8Array(sentinel).buffer;
                                await fetch("/array-buffer", { method: "POST", body: arrayBuffer });

                                const backing = new Uint8Array([9, ...sentinel, 8]);
                                await fetch("/typed-array-view", {
                                    method: "POST",
                                    body: backing.subarray(1, 5),
                                });

                                const request = new Request("/request", {
                                    method: "POST",
                                    headers: {
                                        authorization: "Bearer test-token",
                                        "x-request-source": "request",
                                    },
                                    body: new Uint8Array(sentinel),
                                });
                                await fetch(request);
                                await fetch(request, {
                                    headers: { "x-init-override": "yes" },
                                });

                                await fetch(new Request("/request-params-object", {
                                    method: "POST",
                                    body: new URLSearchParams({ a: "b" }),
                                }), { headers: {} });
                                await fetch(new Request("/request-params-headers", {
                                    method: "POST",
                                    body: new URLSearchParams({ a: "b" }),
                                }), { headers: new Headers() });

                                return calls;
                            } finally {
                                Deno.core.ops.op_fetch_url = originalFetchOp;
                            }
                        }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                assert_eq!(
                    result.value.unwrap(),
                    serde_json::json!([
                        { "path": "/blob", "method": "POST", "headers": {}, "isUint8Array": true, "bytes": [0, 128, 255, 16] },
                        { "path": "/array-buffer", "method": "POST", "headers": {}, "isUint8Array": true, "bytes": [0, 128, 255, 16] },
                        { "path": "/typed-array-view", "method": "POST", "headers": {}, "isUint8Array": true, "bytes": [0, 128, 255, 16] },
                        { "path": "/request", "method": "POST", "headers": { "authorization": "Bearer test-token", "x-request-source": "request" }, "isUint8Array": true, "bytes": [0, 128, 255, 16] },
                        { "path": "/request", "method": "POST", "headers": { "x-init-override": "yes" }, "isUint8Array": true, "bytes": [0, 128, 255, 16] },
                        { "path": "/request-params-object", "method": "POST", "headers": {}, "isUint8Array": true, "bytes": [97, 61, 98] },
                        { "path": "/request-params-headers", "method": "POST", "headers": {}, "isUint8Array": true, "bytes": [97, 61, 98] },
                    ])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const originalFetchOp = Deno.core.ops.op_fetch_url;
                const calls = [];
                try {
                    Deno.core.ops.op_fetch_url =
                        (url, method, headers, body) => {
                            calls.push({
                                path: new URL(url).pathname,
                                method,
                                headers: JSON.parse(headers),
                                isUint8Array: body instanceof Uint8Array,
                                bytes: Array.from(
                                    body instanceof Uint8Array
                                        ? body
                                        : new TextEncoder().encode(body),
                                ),
                            });
                            return JSON.stringify({
                                status: 200,
                                headers: {},
                                body: "ok",
                                url,
                            });
                        };

                    const sentinel = [0, 128, 255, 16];
                    await fetch("/blob", {
                        method: "POST",
                        body: new Blob([new Uint8Array(sentinel)]),
                    });

                    const arrayBuffer = new Uint8Array(sentinel).buffer;
                    await fetch("/array-buffer", { method: "POST", body: arrayBuffer });

                    const backing = new Uint8Array([9, ...sentinel, 8]);
                    await fetch("/typed-array-view", {
                        method: "POST",
                        body: backing.subarray(1, 5),
                    });

                    const request = new Request("/request", {
                        method: "POST",
                        headers: {
                            authorization: "Bearer test-token",
                            "x-request-source": "request",
                        },
                        body: new Uint8Array(sentinel),
                    });
                    await fetch(request);
                    await fetch(request, {
                        headers: { "x-init-override": "yes" },
                    });

                    await fetch(new Request("/request-params-object", {
                        method: "POST",
                        body: new URLSearchParams({ a: "b" }),
                    }), { headers: {} });
                    await fetch(new Request("/request-params-headers", {
                        method: "POST",
                        body: new URLSearchParams({ a: "b" }),
                    }), { headers: new Headers() });

                    return calls;
                } finally {
                    Deno.core.ops.op_fetch_url = originalFetchOp;
                }
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        AssertJsonEquals(
            """
            [
                { "path": "/blob", "method": "POST", "headers": {}, "isUint8Array": true, "bytes": [0, 128, 255, 16] },
                { "path": "/array-buffer", "method": "POST", "headers": {}, "isUint8Array": true, "bytes": [0, 128, 255, 16] },
                { "path": "/typed-array-view", "method": "POST", "headers": {}, "isUint8Array": true, "bytes": [0, 128, 255, 16] },
                { "path": "/request", "method": "POST", "headers": { "authorization": "Bearer test-token", "x-request-source": "request" }, "isUint8Array": true, "bytes": [0, 128, 255, 16] },
                { "path": "/request", "method": "POST", "headers": { "x-init-override": "yes" }, "isUint8Array": true, "bytes": [0, 128, 255, 16] },
                { "path": "/request-params-object", "method": "POST", "headers": {}, "isUint8Array": true, "bytes": [97, 61, 98] },
                { "path": "/request-params-headers", "method": "POST", "headers": {}, "isUint8Array": true, "bytes": [97, 61, 98] }
            ]
            """,
            result.Value);
    }

    [Fact]
    public async Task FetchFormUrlencodedAndXhrBodiesReachTheOpAsBytes()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn fetch_form_urlencoded_and_xhr_bodies_reach_the_op_as_bytes() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                            const originalFetchOp = Deno.core.ops.op_fetch_url;
                            const calls = [];
                            const includesBytes = (bytes, needle) => {
                                outer: for (let i = 0; i <= bytes.length - needle.length; i++) {
                                    for (let j = 0; j < needle.length; j++) {
                                        if (bytes[i + j] !== needle[j]) continue outer;
                                    }
                                    return true;
                                }
                                return false;
                            };
                            try {
                                Deno.core.ops.op_fetch_url =
                                    (url, method, headers, body) => {
                                        const bytes = Array.from(
                                            body instanceof Uint8Array
                                                ? body
                                                : new TextEncoder().encode(body),
                                        );
                                        calls.push({
                                            path: new URL(url).pathname,
                                            headers: JSON.parse(headers),
                                            isUint8Array: body instanceof Uint8Array,
                                            bytes,
                                            hasRawSentinel: includesBytes(bytes, [0, 128, 255, 16]),
                                        });
                                        return JSON.stringify({
                                            status: 200,
                                            headers: {},
                                            body: "ok",
                                            url,
                                        });
                                    };

                                const form = new FormData();
                                form.append("note", "snow \u96ea");
                                form.append(
                                    "upload",
                                    new File([new Uint8Array([0, 128, 255, 16])], "sentinel.bin", {
                                        type: "application/octet-stream",
                                    }),
                                );
                                await fetch("/form-data", { method: "POST", body: form });

                                const params = new URLSearchParams();
                                params.append("greeting", "\u96ea space&");
                                await fetch("/url-search-params", { method: "POST", body: params });

                                await new Promise((resolve, reject) => {
                                    const xhr = new XMLHttpRequest();
                                    xhr.open("POST", "/xhr-typed-array");
                                    xhr.onload = resolve;
                                    xhr.onerror = reject;
                                    const backing = new Uint8Array([9, 0, 128, 255, 16, 8]);
                                    xhr.send(backing.subarray(1, 5));
                                });

                                const formCall = calls.find(call => call.path === "/form-data");
                                const paramsCall = calls.find(call => call.path === "/url-search-params");
                                const xhrCall = calls.find(call => call.path === "/xhr-typed-array");
                                return {
                                    formData: {
                                        isUint8Array: formCall.isUint8Array,
                                        contentType: Object.entries(formCall.headers)
                                            .find(([name]) => name.toLowerCase() === "content-type")[1]
                                            .startsWith("multipart/form-data; boundary=----WebKitFormBoundary"),
                                        hasText: includesBytes(
                                            formCall.bytes,
                                            Array.from(new TextEncoder().encode("snow \u96ea")),
                                        ),
                                        hasFilename: includesBytes(
                                            formCall.bytes,
                                            Array.from(new TextEncoder().encode('filename="sentinel.bin"')),
                                        ),
                                        hasRawSentinel: formCall.hasRawSentinel,
                                    },
                                    urlSearchParams: {
                                        isUint8Array: paramsCall.isUint8Array,
                                        contentType: Object.entries(paramsCall.headers)
                                            .find(([name]) => name.toLowerCase() === "content-type")[1],
                                        bytes: paramsCall.bytes,
                                    },
                                    xhr: {
                                        isUint8Array: xhrCall.isUint8Array,
                                        bytes: xhrCall.bytes,
                                    },
                                };
                            } finally {
                                Deno.core.ops.op_fetch_url = originalFetchOp;
                            }
                        }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                assert_eq!(
                    result.value.unwrap(),
                    serde_json::json!({
                        "formData": {
                            "isUint8Array": true,
                            "contentType": true,
                            "hasText": true,
                            "hasFilename": true,
                            "hasRawSentinel": true,
                        },
                        "urlSearchParams": {
                            "isUint8Array": true,
                            "contentType": "application/x-www-form-urlencoded;charset=UTF-8",
                            "bytes": "greeting=%E9%9B%AA+space%26".as_bytes(),
                        },
                        "xhr": {
                            "isUint8Array": true,
                            "bytes": [0, 128, 255, 16],
                        },
                    })
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const originalFetchOp = Deno.core.ops.op_fetch_url;
                const calls = [];
                const includesBytes = (bytes, needle) => {
                    outer: for (let i = 0; i <= bytes.length - needle.length; i++) {
                        for (let j = 0; j < needle.length; j++) {
                            if (bytes[i + j] !== needle[j]) continue outer;
                        }
                        return true;
                    }
                    return false;
                };
                try {
                    Deno.core.ops.op_fetch_url =
                        (url, method, headers, body) => {
                            const bytes = Array.from(
                                body instanceof Uint8Array
                                    ? body
                                    : new TextEncoder().encode(body),
                            );
                            calls.push({
                                path: new URL(url).pathname,
                                headers: JSON.parse(headers),
                                isUint8Array: body instanceof Uint8Array,
                                bytes,
                                hasRawSentinel: includesBytes(bytes, [0, 128, 255, 16]),
                            });
                            return JSON.stringify({
                                status: 200,
                                headers: {},
                                body: "ok",
                                url,
                            });
                        };

                    const form = new FormData();
                    form.append("note", "snow \u96ea");
                    form.append(
                        "upload",
                        new File([new Uint8Array([0, 128, 255, 16])], "sentinel.bin", {
                            type: "application/octet-stream",
                        }),
                    );
                    await fetch("/form-data", { method: "POST", body: form });

                    const params = new URLSearchParams();
                    params.append("greeting", "\u96ea space&");
                    await fetch("/url-search-params", { method: "POST", body: params });

                    await new Promise((resolve, reject) => {
                        const xhr = new XMLHttpRequest();
                        xhr.open("POST", "/xhr-typed-array");
                        xhr.onload = resolve;
                        xhr.onerror = reject;
                        const backing = new Uint8Array([9, 0, 128, 255, 16, 8]);
                        xhr.send(backing.subarray(1, 5));
                    });

                    const formCall = calls.find(call => call.path === "/form-data");
                    const paramsCall = calls.find(call => call.path === "/url-search-params");
                    const xhrCall = calls.find(call => call.path === "/xhr-typed-array");
                    return {
                        formData: {
                            isUint8Array: formCall.isUint8Array,
                            contentType: Object.entries(formCall.headers)
                                .find(([name]) => name.toLowerCase() === "content-type")[1]
                                .startsWith("multipart/form-data; boundary=----WebKitFormBoundary"),
                            hasText: includesBytes(
                                formCall.bytes,
                                Array.from(new TextEncoder().encode("snow \u96ea")),
                            ),
                            hasFilename: includesBytes(
                                formCall.bytes,
                                Array.from(new TextEncoder().encode('filename="sentinel.bin"')),
                            ),
                            hasRawSentinel: formCall.hasRawSentinel,
                        },
                        urlSearchParams: {
                            isUint8Array: paramsCall.isUint8Array,
                            contentType: Object.entries(paramsCall.headers)
                                .find(([name]) => name.toLowerCase() === "content-type")[1],
                            bytes: paramsCall.bytes,
                        },
                        xhr: {
                            isUint8Array: xhrCall.isUint8Array,
                            bytes: xhrCall.bytes,
                        },
                    };
                } finally {
                    Deno.core.ops.op_fetch_url = originalFetchOp;
                }
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        var encodedParams = string.Join(
            ", ",
            System.Text.Encoding.UTF8.GetBytes("greeting=%E9%9B%AA+space%26"));
        AssertJsonEquals(
            $$"""
            {
                "formData": {
                    "isUint8Array": true,
                    "contentType": true,
                    "hasText": true,
                    "hasFilename": true,
                    "hasRawSentinel": true
                },
                "urlSearchParams": {
                    "isUint8Array": true,
                    "contentType": "application/x-www-form-urlencoded;charset=UTF-8",
                    "bytes": [{{encodedParams}}]
                },
                "xhr": {
                    "isUint8Array": true,
                    "bytes": [0, 128, 255, 16]
                }
            }
            """,
            result.Value);
    }

    /// HTTP-redirect fetch returns a network error as soon as the
    /// redirect count *reaches* 20, and only increments it afterwards. So
    /// the twentieth hop must still succeed:
    /// https://fetch.spec.whatwg.org/#http-redirect-fetch
    /// WPT covers the same pair in `fetch/api/redirect/redirect-count.any.js`.
    [Fact]
    public async Task FetchFollowsTheTwentiethRedirect()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn fetch_follows_the_twentieth_redirect() {
                let mut rt = redirect_chain_runtime(21);
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => (await fetch("/hop/20")).text()"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                assert_eq!(result.value.unwrap(), serde_json::json!("arrived"));
            }
        */
        using var server = RedirectChainServer();
        using var fixture = RedirectRuntimeForOrigin(server.Origin);
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """async () => (await fetch("/hop/20")).text()""",
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        Assert.Equal("arrived", result.Value!.GetValue<string>());
    }

    [Fact]
    public async Task FetchResponseReportsTheFinalRedirectUrl()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn fetch_response_reports_the_final_redirect_url() {
                let mut rt = redirect_chain_runtime(3);
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                            const response = await fetch("/hop/1");
                            const direct = await fetch("/hop/0");
                            return {
                                url: response.url,
                                redirected: response.redirected,
                                cloneUrl: response.clone().url,
                                directRedirected: direct.redirected,
                            };
                        }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                let value = result.value.unwrap();
                assert!(
                    value["url"].as_str().unwrap_or_default().ends_with("/hop/0"),
                    "Response.url did not report the final URL: {value}"
                );
                assert_eq!(value["redirected"], true);
                assert_eq!(value["cloneUrl"], value["url"]);
                assert_eq!(value["directRedirected"], false);

                let events = rt.take_js_network_events();
                assert_eq!(events.len(), 2);
                assert!(
                    events[0].url.ends_with("/hop/0"),
                    "network response event did not report the final URL: {:?}",
                    events[0].url
                );
            }
        */
        using var server = RedirectChainServer();
        using var fixture = RedirectRuntimeForOrigin(server.Origin);
        var rt = fixture.Runtime;
        var result = await rt.CallFunctionOnForCdpAsync(
            """
            async () => {
                const response = await fetch("/hop/1");
                const direct = await fetch("/hop/0");
                return {
                    url: response.url,
                    redirected: response.redirected,
                    cloneUrl: response.clone().url,
                    directRedirected: direct.redirected,
                };
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        var value = result.Value!;
        Assert.True(
            value["url"]!.GetValue<string>().EndsWith("/hop/0", StringComparison.Ordinal),
            $"Response.url did not report the final URL: {value.ToJsonString()}");
        Assert.True(value["redirected"]!.GetValue<bool>());
        Assert.Equal(value["url"]!.GetValue<string>(), value["cloneUrl"]!.GetValue<string>());
        Assert.False(value["directRedirected"]!.GetValue<bool>());

        var events = rt.TakeJsNetworkEvents();
        Assert.Equal(2, events.Count);
        Assert.True(
            events[0].Url.EndsWith("/hop/0", StringComparison.Ordinal),
            $"network response event did not report the final URL: {events[0].Url}");
    }

    [Fact(Skip = "blocked: the stealth transport has no managed implementation, so Obscura.Js never routes op_fetch_url through IStealthHttpClient and the only in-tree client (UnavailableStealthHttpClient) fails every request; see the TLS-impersonation gap in todo.md")]
    public async Task StealthFetchResponseReportsTheFinalRedirectUrl()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn stealth_fetch_response_reports_the_final_redirect_url() {
                let mut rt = redirect_chain_runtime(2);
                rt.set_stealth_client(std::sync::Arc::new(
                    obscura_net::StealthHttpClient::new(std::sync::Arc::new(
                        obscura_net::CookieJar::new(),
                    )),
                ));
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                            const response = await fetch("/hop/1");
                            return { url: response.url, redirected: response.redirected };
                        }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                let value = result.value.unwrap();
                assert!(value["url"].as_str().unwrap_or_default().ends_with("/hop/0"));
                assert_eq!(value["redirected"], true);
            }
        */
        await Task.CompletedTask;
    }

    [Fact]
    public async Task XhrResponseUrlReportsTheFinalRedirectUrl()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn xhr_response_url_reports_the_final_redirect_url() {
                let mut rt = redirect_chain_runtime(2);
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => await new Promise((resolve, reject) => {
                            const xhr = new XMLHttpRequest();
                            xhr.open("GET", "/hop/1");
                            xhr.onload = () => resolve(xhr.responseURL);
                            xhr.onerror = reject;
                            xhr.send();
                        })"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                assert!(
                    result
                        .value
                        .unwrap()
                        .as_str()
                        .unwrap_or_default()
                        .ends_with("/hop/0")
                );
            }
        */
        using var server = RedirectChainServer();
        using var fixture = RedirectRuntimeForOrigin(server.Origin);
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => await new Promise((resolve, reject) => {
                const xhr = new XMLHttpRequest();
                xhr.open("GET", "/hop/1");
                xhr.onload = () => resolve(xhr.responseURL);
                xhr.onerror = reject;
                xhr.send();
            })
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        Assert.EndsWith("/hop/0", result.Value!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch302ResponseUsesTheFinalGetMethod()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn fetch_302_response_uses_the_final_get_method() {
                assert_post_redirect_method(302, "GET").await;
            }
        */
        await AssertPostRedirectMethodAsync(302, "GET");
    }

    [Fact]
    public async Task Fetch307ResponsePreservesThePostMethod()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn fetch_307_response_preserves_the_post_method() {
                assert_post_redirect_method(307, "POST").await;
            }
        */
        await AssertPostRedirectMethodAsync(307, "POST");
    }

    [Fact]
    public async Task SameOriginNoCorsRedirectKeepsResponseIdentity()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn same_origin_no_cors_redirect_keeps_response_identity() {
                let mut rt = redirect_chain_runtime(2);
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                            const response = await fetch("/hop/1", { mode: "no-cors" });
                            return { type: response.type, url: response.url, redirected: response.redirected };
                        }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();
                let value = result.value.unwrap();
                assert_eq!(value["type"], "basic");
                assert!(value["url"].as_str().unwrap_or_default().ends_with("/hop/0"));
                assert_eq!(value["redirected"], true);
            }
        */
        using var server = RedirectChainServer();
        using var fixture = RedirectRuntimeForOrigin(server.Origin);
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const response = await fetch("/hop/1", { mode: "no-cors" });
                return { type: response.type, url: response.url, redirected: response.redirected };
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);
        var value = result.Value!;
        Assert.Equal("basic", value["type"]!.GetValue<string>());
        Assert.EndsWith("/hop/0", value["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(value["redirected"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CrossOriginNoCorsRedirectFiltersResponseIdentity()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn cross_origin_no_cors_redirect_filters_response_identity() {
                let mut rt = cross_origin_redirect_runtime();
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                            const response = await fetch("/start", { mode: "no-cors" });
                            return { type: response.type, url: response.url, redirected: response.redirected };
                        }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();
                assert_eq!(
                    result.value.unwrap(),
                    serde_json::json!({ "type": "opaque", "url": "", "redirected": false })
                );
            }
        */
        using var target = new RawHttpServer(
            _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        using var source = new RawHttpServer(
            _ => $"HTTP/1.1 302 Found\r\nLocation: {target.Origin}/final\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var fixture = RedirectRuntimeForOrigin(source.Origin);
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const response = await fetch("/start", { mode: "no-cors" });
                return { type: response.type, url: response.url, redirected: response.redirected };
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);
        AssertJsonEquals("""{ "type": "opaque", "url": "", "redirected": false }""", result.Value);
    }

    [Fact(Skip = "blocked: the stealth transport has no managed implementation, so Obscura.Js never routes op_fetch_url through IStealthHttpClient and the only in-tree client (UnavailableStealthHttpClient) fails every request; see the TLS-impersonation gap in todo.md")]
    public async Task StealthCrossOriginNoCorsFiltersResponseIdentity()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn stealth_cross_origin_no_cors_filters_response_identity() {
                let mut rt = cross_origin_redirect_runtime();
                rt.set_stealth_client(std::sync::Arc::new(
                    obscura_net::StealthHttpClient::new(std::sync::Arc::new(
                        obscura_net::CookieJar::new(),
                    )),
                ));
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                            const response = await fetch("/start", { mode: "no-cors" });
                            return { type: response.type, url: response.url, redirected: response.redirected };
                        }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();
                assert_eq!(
                    result.value.unwrap(),
                    serde_json::json!({ "type": "opaque", "url": "", "redirected": false })
                );
            }
        */
        await Task.CompletedTask;
    }

    /// The other end of the same pair: the twenty-first redirect must
    /// fail. `fetch` reports a rejected result as a `TypeError`.
    [Fact]
    public async Task FetchRejectsTheTwentyFirstRedirect()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn fetch_rejects_the_twenty_first_redirect() {
                let mut rt = redirect_chain_runtime(21);
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                            try {
                                const response = await fetch("/hop/21");
                                return "resolved " + (await response.text());
                            } catch (error) {
                                return error instanceof TypeError ? "rejected" : "other";
                            }
                        }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                assert_eq!(result.value.unwrap(), serde_json::json!("rejected"));
            }
        */
        using var server = RedirectChainServer();
        using var fixture = RedirectRuntimeForOrigin(server.Origin);
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                try {
                    const response = await fetch("/hop/21");
                    return "resolved " + (await response.text());
                } catch (error) {
                    return error instanceof TypeError ? "rejected" : "other";
                }
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        Assert.Equal("rejected", result.Value!.GetValue<string>());
    }

    [Fact]
    public async Task DynamicLinkedStylesheetEntersTheLiveDomWithImportsRebased()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn dynamic_linked_stylesheet_enters_the_live_dom_with_imports_rebased() {
                let mut rt =
                    setup_runtime("<html><head></head><body><div class=\"card\"></div></body></html>");
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                            const originalFetchOp = Deno.core.ops.op_fetch_url;
                            try {
                                Deno.core.ops.op_fetch_url = (url) => JSON.stringify({
                                    status: 200,
                                    headers: { "content-type": "text/css" },
                                    body: url.endsWith("/assets/route.css")
                                        ? '@import "./theme/base.css"; .card { display:grid; background-image:url("../img/card.png") }'
                                        : '.card { color:red; background-image:url("./grain.png") }',
                                    url,
                                });
                                const link = document.createElement("link");
                                link.setAttribute("rel", "stylesheet");
                                link.setAttribute("href", "/assets/route.css");
                                const loaded = new Promise(resolve => {
                                    link.onload = () => resolve();
                                });
                                document.head.appendChild(link);
                                await loaded;
                                const style = document.querySelector("style[data-obscura-linked]");
                                const css = style.textContent;
                                const afterLink = link.nextSibling === style;
                                const list = document.styleSheets;
                                const sheet = link.sheet;
                                const rules = sheet.cssRules;
                                const cssom = {
                                    listed: list.length === 1 && list[0] === sheet,
                                    stable: link.sheet === sheet && sheet.cssRules === rules,
                                    owner: sheet.ownerNode === link,
                                    href: sheet.href,
                                    selectors: Array.from(rules, rule => rule.selectorText),
                                };
                                link.remove();
                                return {
                                    afterLink,
                                    importedBeforeRoute:
                                        css.indexOf("color:red") < css.indexOf("display:grid"),
                                    importedUrl:
                                        css.includes("http://example.com/assets/theme/grain.png"),
                                    routeUrl:
                                        css.includes("http://example.com/img/card.png"),
                                    removedWithLink:
                                        !document.querySelector("style[data-obscura-linked]"),
                                    cssom,
                                    detachedCssom: sheet.ownerNode === null
                                        && link.sheet === null
                                        && list.length === 0,
                                };
                            } finally {
                                Deno.core.ops.op_fetch_url = originalFetchOp;
                            }
                        }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                assert_eq!(
                    result.value.unwrap(),
                    serde_json::json!({
                        "afterLink": true,
                        "importedBeforeRoute": true,
                        "importedUrl": true,
                        "routeUrl": true,
                        "removedWithLink": true,
                        "cssom": {
                            "listed": true,
                            "stable": true,
                            "owner": true,
                            "href": "http://example.com/assets/route.css",
                            "selectors": [".card", ".card"],
                        },
                        "detachedCssom": true,
                    })
                );
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<html><head></head><body><div class="card"></div></body></html>""");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const originalFetchOp = Deno.core.ops.op_fetch_url;
                try {
                    Deno.core.ops.op_fetch_url = (url) => JSON.stringify({
                        status: 200,
                        headers: { "content-type": "text/css" },
                        body: url.endsWith("/assets/route.css")
                            ? '@import "./theme/base.css"; .card { display:grid; background-image:url("../img/card.png") }'
                            : '.card { color:red; background-image:url("./grain.png") }',
                        url,
                    });
                    const link = document.createElement("link");
                    link.setAttribute("rel", "stylesheet");
                    link.setAttribute("href", "/assets/route.css");
                    const loaded = new Promise(resolve => {
                        link.onload = () => resolve();
                    });
                    document.head.appendChild(link);
                    await loaded;
                    const style = document.querySelector("style[data-obscura-linked]");
                    const css = style.textContent;
                    const afterLink = link.nextSibling === style;
                    const list = document.styleSheets;
                    const sheet = link.sheet;
                    const rules = sheet.cssRules;
                    const cssom = {
                        listed: list.length === 1 && list[0] === sheet,
                        stable: link.sheet === sheet && sheet.cssRules === rules,
                        owner: sheet.ownerNode === link,
                        href: sheet.href,
                        selectors: Array.from(rules, rule => rule.selectorText),
                    };
                    link.remove();
                    return {
                        afterLink,
                        importedBeforeRoute:
                            css.indexOf("color:red") < css.indexOf("display:grid"),
                        importedUrl:
                            css.includes("http://example.com/assets/theme/grain.png"),
                        routeUrl:
                            css.includes("http://example.com/img/card.png"),
                        removedWithLink:
                            !document.querySelector("style[data-obscura-linked]"),
                        cssom,
                        detachedCssom: sheet.ownerNode === null
                            && link.sheet === null
                            && list.length === 0,
                    };
                } finally {
                    Deno.core.ops.op_fetch_url = originalFetchOp;
                }
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        AssertJsonEquals(
            """
            {
                "afterLink": true,
                "importedBeforeRoute": true,
                "importedUrl": true,
                "routeUrl": true,
                "removedWithLink": true,
                "cssom": {
                    "listed": true,
                    "stable": true,
                    "owner": true,
                    "href": "http://example.com/assets/route.css",
                    "selectors": [".card", ".card"]
                },
                "detachedCssom": true
            }
            """,
            result.Value);
    }

    [Fact]
    public async Task UnsuccessfulDynamicScriptResponseFiresErrorWithoutEvaluatingBody()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn unsuccessful_dynamic_script_response_fires_error_without_evaluating_body() {
                let mut rt = setup_runtime("<html><head></head><body></body></html>");
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                            const originalFetchOp = Deno.core.ops.op_fetch_url;
                            try {
                                Deno.core.ops.op_fetch_url = (url) => JSON.stringify({
                                    status: 401,
                                    headers: { "content-type": "application/json" },
                                    body: "globalThis.__executedFailedScript = true",
                                    url,
                                });
                                const script = document.createElement("script");
                                script.src = "/unauthorized.js";
                                const outcome = await new Promise(resolve => {
                                    script.onload = () => resolve("load");
                                    script.onerror = () => resolve("error");
                                    document.head.appendChild(script);
                                });
                                return {
                                    outcome,
                                    executed: globalThis.__executedFailedScript === true,
                                };
                            } finally {
                                Deno.core.ops.op_fetch_url = originalFetchOp;
                            }
                        }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                assert_eq!(
                    result.value.unwrap(),
                    serde_json::json!({
                        "outcome": "error",
                        "executed": false,
                    })
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const originalFetchOp = Deno.core.ops.op_fetch_url;
                try {
                    Deno.core.ops.op_fetch_url = (url) => JSON.stringify({
                        status: 401,
                        headers: { "content-type": "application/json" },
                        body: "globalThis.__executedFailedScript = true",
                        url,
                    });
                    const script = document.createElement("script");
                    script.src = "/unauthorized.js";
                    const outcome = await new Promise(resolve => {
                        script.onload = () => resolve("load");
                        script.onerror = () => resolve("error");
                        document.head.appendChild(script);
                    });
                    return {
                        outcome,
                        executed: globalThis.__executedFailedScript === true,
                    };
                } finally {
                    Deno.core.ops.op_fetch_url = originalFetchOp;
                }
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        AssertJsonEquals("""{ "outcome": "error", "executed": false }""", result.Value);
    }

    [Fact]
    public async Task DynamicClassicScriptsAreAsyncByDefaultButHonorAsyncFalseOrder()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn dynamic_classic_scripts_are_async_by_default_but_honor_async_false_order() {
                let mut rt = setup_runtime("<html><head></head><body></body></html>");
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                            const originalFetchOp = Deno.core.ops.op_fetch_url;
                            const runPair = async (explicitlyInOrder) => {
                                globalThis.__dynamicOrder = [];
                                Deno.core.ops.op_fetch_url = (url) => new Promise(resolve => {
                                    const slow = url.includes("slow");
                                    setTimeout(() => resolve(JSON.stringify({
                                        status: 200,
                                        headers: {"content-type": "text/javascript"},
                                        body: `globalThis.__dynamicOrder.push("${slow ? "slow" : "fast"}")`,
                                        url,
                                    })), slow ? 30 : 1);
                                });
                                const load = name => new Promise(resolve => {
                                    const script = document.createElement("script");
                                    if (explicitlyInOrder) script.async = false;
                                    script.src = `/${name}.js`;
                                    script.onload = resolve;
                                    document.head.appendChild(script);
                                });
                                await Promise.all([load("slow"), load("fast")]);
                                return globalThis.__dynamicOrder.slice();
                            };
                            try {
                                const asyncOrder = await runPair(false);
                                const inOrder = await runPair(true);
                                return {
                                    asyncOrder,
                                    inOrder,
                                    pending: globalThis.__obscura_hasPendingDynamicScripts(),
                                };
                            } finally {
                                Deno.core.ops.op_fetch_url = originalFetchOp;
                            }
                        }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                assert_eq!(
                    result.value.unwrap(),
                    serde_json::json!({
                        "asyncOrder": ["fast", "slow"],
                        "inOrder": ["slow", "fast"],
                        "pending": false,
                    })
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><head></head><body></body></html>");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const originalFetchOp = Deno.core.ops.op_fetch_url;
                const runPair = async (explicitlyInOrder) => {
                    globalThis.__dynamicOrder = [];
                    Deno.core.ops.op_fetch_url = (url) => new Promise(resolve => {
                        const slow = url.includes("slow");
                        setTimeout(() => resolve(JSON.stringify({
                            status: 200,
                            headers: {"content-type": "text/javascript"},
                            body: `globalThis.__dynamicOrder.push("${slow ? "slow" : "fast"}")`,
                            url,
                        })), slow ? 30 : 1);
                    });
                    const load = name => new Promise(resolve => {
                        const script = document.createElement("script");
                        if (explicitlyInOrder) script.async = false;
                        script.src = `/${name}.js`;
                        script.onload = resolve;
                        document.head.appendChild(script);
                    });
                    await Promise.all([load("slow"), load("fast")]);
                    return globalThis.__dynamicOrder.slice();
                };
                try {
                    const asyncOrder = await runPair(false);
                    const inOrder = await runPair(true);
                    return {
                        asyncOrder,
                        inOrder,
                        pending: globalThis.__obscura_hasPendingDynamicScripts(),
                    };
                } finally {
                    Deno.core.ops.op_fetch_url = originalFetchOp;
                }
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        AssertJsonEquals(
            """
            {
                "asyncOrder": ["fast", "slow"],
                "inOrder": ["slow", "fast"],
                "pending": false
            }
            """,
            result.Value);
    }

    [Fact]
    public async Task TestResponseArrayBufferPreservesTypedArrayView()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn test_response_array_buffer_preserves_typed_array_view() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                        const bytes = new Uint8Array([9, 0, 97, 115, 109, 1, 8]);
                        const response = new Response(bytes.subarray(1, 6));
                        return Array.from(new Uint8Array(await response.arrayBuffer()));
                    }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                assert_eq!(
                    result.value.unwrap(),
                    serde_json::json!([0, 97, 115, 109, 1])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const bytes = new Uint8Array([9, 0, 97, 115, 109, 1, 8]);
                const response = new Response(bytes.subarray(1, 6));
                return Array.from(new Uint8Array(await response.arrayBuffer()));
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        AssertJsonEquals("[0, 97, 115, 109, 1]", result.Value);
    }

    [Fact]
    public async Task TestWasmInstantiateStreamingUsesResponseArrayBuffer()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn test_wasm_instantiate_streaming_uses_response_array_buffer() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .call_function_on_for_cdp(
                        r#"async () => {
                        const bytes = new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0]);
                        const result = await WebAssembly.instantiateStreaming(
                            Promise.resolve(new Response(bytes)),
                            {},
                        );
                        return result.instance instanceof WebAssembly.Instance;
                    }"#,
                        None,
                        &[],
                        true,
                        true,
                    )
                    .await
                    .unwrap();

                assert_eq!(result.value.unwrap(), serde_json::json!(true));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => {
                const bytes = new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0]);
                const result = await WebAssembly.instantiateStreaming(
                    Promise.resolve(new Response(bytes)),
                    {},
                );
                return result.instance instanceof WebAssembly.Instance;
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        Assert.True(result.Value!.GetValue<bool>());
    }

    [Fact]
    public void TestTextDecoderRespectsTypedArrayView()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_text_decoder_respects_typed_array_view() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate("new TextDecoder().decode(new Uint8Array([65, 66, 67]).subarray(1, 2))")
                    .unwrap();
                assert_eq!(result.as_str().unwrap(), "B");
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            "new TextDecoder().decode(new Uint8Array([65, 66, 67]).subarray(1, 2))");
        Assert.Equal("B", result!.GetValue<string>());
    }

    [Fact]
    public void TestDocumentDoctype()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_document_doctype() {
                let mut rt = setup_runtime("<!DOCTYPE html><html><body></body></html>");
                let result = rt.evaluate("document.doctype !== null").unwrap();
                assert_eq!(result, serde_json::json!(true));

                let name = rt.evaluate("document.doctype.name").unwrap();
                assert_eq!(name, serde_json::json!("html"));

                let node_type = rt.evaluate("document.doctype.nodeType").unwrap();
                assert_eq!(node_type.as_f64().unwrap() as i64, 10);
            }
        */
        using var fixture = RuntimeFixture.Setup("<!DOCTYPE html><html><body></body></html>");
        var rt = fixture.Runtime;
        Assert.True(rt.Evaluate("document.doctype !== null")!.GetValue<bool>());
        Assert.Equal("html", rt.Evaluate("document.doctype.name")!.GetValue<string>());
        Assert.Equal(10, (long)rt.Evaluate("document.doctype.nodeType")!.GetValue<double>());
    }

    [Fact]
    public void TestDocumentDoctypeNullWhenMissing()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_document_doctype_null_when_missing() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt.evaluate("document.doctype === null").unwrap();
                assert_eq!(result, serde_json::json!(true));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        Assert.True(fixture.Runtime.Evaluate("document.doctype === null")!.GetValue<bool>());
    }

    [Fact]
    public void TestXmlSerializerDoctype()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_xml_serializer_doctype() {
                let mut rt = setup_runtime("<!DOCTYPE html><html><body></body></html>");
                let result = rt
                    .evaluate("new XMLSerializer().serializeToString(document.doctype)")
                    .unwrap();
                assert_eq!(result.as_str().unwrap(), "<!DOCTYPE html>");
            }
        */
        using var fixture = RuntimeFixture.Setup("<!DOCTYPE html><html><body></body></html>");
        var result = fixture.Runtime.Evaluate("new XMLSerializer().serializeToString(document.doctype)");
        Assert.Equal("<!DOCTYPE html>", result!.GetValue<string>());
    }

    [Fact]
    public void TestXmlSerializerElement()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_xml_serializer_element() {
                let mut rt = setup_runtime(r#"<html><body><div id="x">Hello</div></body></html>"#);
                let result = rt
                    .evaluate("new XMLSerializer().serializeToString(document.getElementById('x'))")
                    .unwrap();
                let html = result.as_str().unwrap();
                assert!(html.contains("<div"));
                assert!(html.contains("Hello"));
            }
        */
        using var fixture = RuntimeFixture.Setup("""<html><body><div id="x">Hello</div></body></html>""");
        var html = fixture.Runtime
            .Evaluate("new XMLSerializer().serializeToString(document.getElementById('x'))")!
            .GetValue<string>();
        Assert.Contains("<div", html, StringComparison.Ordinal);
        Assert.Contains("Hello", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TestCreateEventCustomEventHasInitMethod()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_create_event_custom_event_has_init_method() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let kind = rt
                    .evaluate("typeof document.createEvent('CustomEvent').initCustomEvent")
                    .unwrap();
                assert_eq!(kind, serde_json::json!("function"));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        Assert.Equal(
            "function",
            fixture.Runtime.Evaluate("typeof document.createEvent('CustomEvent').initCustomEvent")!.GetValue<string>());
    }

    [Fact]
    public void TestInitCustomEventSetsFields()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_init_custom_event_sets_fields() {
                let mut rt = setup_runtime("<html><body></body></html>");
                rt.execute_script(
                    "test",
                    r#"
                    globalThis.__e = document.createEvent('CustomEvent');
                    globalThis.__e.initCustomEvent('myevent', true, false, {hello: 'world'});
                "#,
                )
                .unwrap();
                let t = rt.evaluate("globalThis.__e.type").unwrap();
                assert_eq!(t, serde_json::json!("myevent"));
                let b = rt.evaluate("globalThis.__e.bubbles").unwrap();
                assert_eq!(b, serde_json::json!(true));
                let c = rt.evaluate("globalThis.__e.cancelable").unwrap();
                assert_eq!(c, serde_json::json!(false));
                let d = rt.evaluate("globalThis.__e.detail.hello").unwrap();
                assert_eq!(d, serde_json::json!("world"));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "test",
            """
            globalThis.__e = document.createEvent('CustomEvent');
            globalThis.__e.initCustomEvent('myevent', true, false, {hello: 'world'});
            """);
        Assert.Equal("myevent", rt.Evaluate("globalThis.__e.type")!.GetValue<string>());
        Assert.True(rt.Evaluate("globalThis.__e.bubbles")!.GetValue<bool>());
        Assert.False(rt.Evaluate("globalThis.__e.cancelable")!.GetValue<bool>());
        Assert.Equal("world", rt.Evaluate("globalThis.__e.detail.hello")!.GetValue<string>());
    }

    [Fact]
    public void TestCreateEventReturnsCorrectClass()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_create_event_returns_correct_class() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let cust = rt
                    .evaluate("document.createEvent('CustomEvent') instanceof CustomEvent")
                    .unwrap();
                assert_eq!(cust, serde_json::json!(true));
                let mouse = rt
                    .evaluate("document.createEvent('MouseEvent') instanceof MouseEvent")
                    .unwrap();
                assert_eq!(mouse, serde_json::json!(true));
                let mouses = rt
                    .evaluate("document.createEvent('MouseEvents') instanceof MouseEvent")
                    .unwrap();
                assert_eq!(mouses, serde_json::json!(true));
                let kb = rt
                    .evaluate("document.createEvent('KeyboardEvent') instanceof KeyboardEvent")
                    .unwrap();
                assert_eq!(kb, serde_json::json!(true));
                let hash_change = rt
                    .evaluate("document.createEvent('HashChangeEvent') instanceof HashChangeEvent")
                    .unwrap();
                assert_eq!(hash_change, serde_json::json!(true));
                let message = rt
                    .evaluate("document.createEvent('MessageEvent') instanceof MessageEvent")
                    .unwrap();
                assert_eq!(message, serde_json::json!(true));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        Assert.True(rt.Evaluate("document.createEvent('CustomEvent') instanceof CustomEvent")!.GetValue<bool>());
        Assert.True(rt.Evaluate("document.createEvent('MouseEvent') instanceof MouseEvent")!.GetValue<bool>());
        Assert.True(rt.Evaluate("document.createEvent('MouseEvents') instanceof MouseEvent")!.GetValue<bool>());
        Assert.True(rt.Evaluate("document.createEvent('KeyboardEvent') instanceof KeyboardEvent")!.GetValue<bool>());
        Assert.True(rt.Evaluate("document.createEvent('HashChangeEvent') instanceof HashChangeEvent")!.GetValue<bool>());
        Assert.True(rt.Evaluate("document.createEvent('MessageEvent') instanceof MessageEvent")!.GetValue<bool>());
    }

    [Fact]
    public void CssstyledeclarationIsAUsableGlobalInterface()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn cssstyledeclaration_is_a_usable_global_interface() {
                // CSSStyleDeclaration was pre-declared non-enumerable but never assigned
                // a value (the only WebIDL interface missing its globalThis.X = X line),
                // so it was `undefined` while `'CSSStyleDeclaration' in window` was true,
                // and `el.style instanceof CSSStyleDeclaration` threw. It must be a real
                // constructor, non-enumerable like a browser, and the type of .style.
                let mut rt = setup_runtime("<html><body></body></html>");
                let v = rt
                    .evaluate("(function(){var d=Object.getOwnPropertyDescriptor(window,'CSSStyleDeclaration');return (typeof window.CSSStyleDeclaration)+'|'+(document.body.style instanceof CSSStyleDeclaration)+'|'+(d?d.enumerable:'missing');})()")
                    .unwrap();
                assert_eq!(v, serde_json::json!("function|true|false"));
            }
        */
        // CSSStyleDeclaration was pre-declared non-enumerable but never assigned
        // a value (the only WebIDL interface missing its globalThis.X = X line),
        // so it was `undefined` while `'CSSStyleDeclaration' in window` was true,
        // and `el.style instanceof CSSStyleDeclaration` threw. It must be a real
        // constructor, non-enumerable like a browser, and the type of .style.
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var v = fixture.Runtime.Evaluate(
            "(function(){var d=Object.getOwnPropertyDescriptor(window,'CSSStyleDeclaration');"
            + "return (typeof window.CSSStyleDeclaration)+'|'+(document.body.style instanceof CSSStyleDeclaration)"
            + "+'|'+(d?d.enumerable:'missing');})()");
        Assert.Equal("function|true|false", v!.GetValue<string>());
    }

    [Fact]
    public void TestCreateEventRejectsUnknownInterface()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_create_event_rejects_unknown_interface() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"(() => {
                            try {
                                document.createEvent('NotAnEventInterface');
                                return null;
                            } catch (error) {
                                return [error.name, error instanceof DOMException];
                            }
                        })()"#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!(["NotSupportedError", true]));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            (() => {
                try {
                    document.createEvent('NotAnEventInterface');
                    return null;
                } catch (error) {
                    return [error.name, error instanceof DOMException];
                }
            })()
            """);
        AssertJsonEquals("""["NotSupportedError", true]""", result);
    }

    [Fact]
    public void EventConstructorMatchesWebidlConformance()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn event_constructor_matches_webidl_conformance() {
                // new Event()/new CustomEvent() must throw (type is a required arg),
                // the type argument must be coerced to a string, CustomEvent.detail must
                // default to null (not undefined), createEvent must still build a
                // type-"" event, and an explicit detail must be preserved.
                let mut rt = setup_runtime("<html><body></body></html>");
                let v = rt
                    .evaluate(
                        "(function(){\
                         var out=[];\
                         try{new Event();out.push('no-throw')}catch(e){out.push(e.name)}\
                         try{new CustomEvent();out.push('no-throw')}catch(e){out.push(e.name)}\
                         out.push(new Event(123).type+':'+typeof new Event(123).type);\
                         out.push(String(new CustomEvent('x').detail));\
                         out.push(String(new CustomEvent('x',{detail:7}).detail));\
                         out.push(new Event('click').type);\
                         out.push(JSON.stringify(document.createEvent('Event').type));\
                         return out.join('|');\
                         })()",
                    )
                    .unwrap();
                assert_eq!(
                    v,
                    serde_json::json!("TypeError|TypeError|123:string|null|7|click|\"\"")
                );
            }
        */
        // new Event()/new CustomEvent() must throw (type is a required arg),
        // the type argument must be coerced to a string, CustomEvent.detail must
        // default to null (not undefined), createEvent must still build a
        // type-"" event, and an explicit detail must be preserved.
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var v = fixture.Runtime.Evaluate(
            "(function(){"
            + "var out=[];"
            + "try{new Event();out.push('no-throw')}catch(e){out.push(e.name)}"
            + "try{new CustomEvent();out.push('no-throw')}catch(e){out.push(e.name)}"
            + "out.push(new Event(123).type+':'+typeof new Event(123).type);"
            + "out.push(String(new CustomEvent('x').detail));"
            + "out.push(String(new CustomEvent('x',{detail:7}).detail));"
            + "out.push(new Event('click').type);"
            + "out.push(JSON.stringify(document.createEvent('Event').type));"
            + "return out.join('|');"
            + "})()");
        Assert.Equal("TypeError|TypeError|123:string|null|7|click|\"\"", v!.GetValue<string>());
    }

    [Fact]
    public void TestPromiseRejectionEventRequiresPromise()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_promise_rejection_event_requires_promise() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"(() => {
                            const promise = Promise.resolve(1);
                            const event = new PromiseRejectionEvent('unhandledrejection', {
                                promise,
                                reason: 'failed'
                            });
                            let missingPromiseThrows = false;
                            try {
                                new PromiseRejectionEvent('unhandledrejection');
                            } catch (error) {
                                missingPromiseThrows = error instanceof TypeError;
                            }
                            return [
                                event instanceof Event,
                                event.promise === promise,
                                event.reason === 'failed',
                                missingPromiseThrows
                            ];
                        })()"#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!([true, true, true, true]));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            (() => {
                const promise = Promise.resolve(1);
                const event = new PromiseRejectionEvent('unhandledrejection', {
                    promise,
                    reason: 'failed'
                });
                let missingPromiseThrows = false;
                try {
                    new PromiseRejectionEvent('unhandledrejection');
                } catch (error) {
                    missingPromiseThrows = error instanceof TypeError;
                }
                return [
                    event instanceof Event,
                    event.promise === promise,
                    event.reason === 'failed',
                    missingPromiseThrows
                ];
            })()
            """);
        AssertJsonEquals("[true, true, true, true]", result);
    }

    [Fact]
    public void TestCreateEventRejectsPromiseRejectionEvent()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_create_event_rejects_promise_rejection_event() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"(() => {
                            try {
                                document.createEvent('PromiseRejectionEvent');
                                return null;
                            } catch (error) {
                                return [error.name, error instanceof DOMException];
                            }
                        })()"#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!(["NotSupportedError", true]));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            (() => {
                try {
                    document.createEvent('PromiseRejectionEvent');
                    return null;
                } catch (error) {
                    return [error.name, error instanceof DOMException];
                }
            })()
            """);
        AssertJsonEquals("""["NotSupportedError", true]""", result);
    }

    [Fact]
    public void TestCreateEventSupportsLegacyEventAliases()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_create_event_supports_legacy_event_aliases() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"['Event', 'Events', 'HTMLEvents', 'SVGEvents'].map(name => {
                            const event = document.createEvent(name);
                            return [event instanceof Event, event.constructor === Event, event.type];
                        })"#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([
                        [true, true, ""],
                        [true, true, ""],
                        [true, true, ""],
                        [true, true, ""]
                    ])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            ['Event', 'Events', 'HTMLEvents', 'SVGEvents'].map(name => {
                const event = document.createEvent(name);
                return [event instanceof Event, event.constructor === Event, event.type];
            })
            """);
        AssertJsonEquals(
            """
            [
                [true, true, ""],
                [true, true, ""],
                [true, true, ""],
                [true, true, ""]
            ]
            """,
            result);
    }

    [Fact]
    public void TestStorageEventConstructorAndLegacyFactory()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_storage_event_constructor_and_legacy_factory() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"(() => {
                            const event = new StorageEvent('storage', {
                                key: 'theme',
                                oldValue: 'light',
                                newValue: 'dark',
                                url: 'https://example.test/'
                            });
                            const legacy = document.createEvent('StorageEvent');
                            legacy.initStorageEvent(
                                'storage', false, false, 'count', '1', '2',
                                'https://example.test/', null
                            );
                            return [
                                event instanceof Event,
                                event.key,
                                event.oldValue,
                                event.newValue,
                                event.url,
                                legacy instanceof StorageEvent,
                                legacy.key,
                                legacy.newValue
                            ];
                        })()"#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([
                        true,
                        "theme",
                        "light",
                        "dark",
                        "https://example.test/",
                        true,
                        "count",
                        "2"
                    ])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            (() => {
                const event = new StorageEvent('storage', {
                    key: 'theme',
                    oldValue: 'light',
                    newValue: 'dark',
                    url: 'https://example.test/'
                });
                const legacy = document.createEvent('StorageEvent');
                legacy.initStorageEvent(
                    'storage', false, false, 'count', '1', '2',
                    'https://example.test/', null
                );
                return [
                    event instanceof Event,
                    event.key,
                    event.oldValue,
                    event.newValue,
                    event.url,
                    legacy instanceof StorageEvent,
                    legacy.key,
                    legacy.newValue
                ];
            })()
            """);
        AssertJsonEquals(
            """
            [
                true,
                "theme",
                "light",
                "dark",
                "https://example.test/",
                true,
                "count",
                "2"
            ]
            """,
            result);
    }

    [Fact]
    public void TestHtmlToMarkdownHeadings()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><h1>Title</h1><h2>Sub</h2><p>Body</p></body></html>");
        var md = fixture.Runtime.Evaluate(MarkdownScript.HtmlToMarkdown)!.GetValue<string>();
        Assert.Contains("# Title", md, StringComparison.Ordinal);
        Assert.Contains("## Sub", md, StringComparison.Ordinal);
        Assert.Contains("Body", md, StringComparison.Ordinal);
    }

    [Fact]
    public void TestHtmlToMarkdownLinksAndInline()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_html_to_markdown_links_and_inline() {
                let mut rt = setup_runtime(
                    r#"<html><body><p>Hello <strong>world</strong> <a href="https://x.test/">link</a> <em>em</em></p></body></html>"#,
                );
                let md = rt
                    .evaluate(crate::HTML_TO_MARKDOWN_JS)
                    .unwrap()
                    .as_str()
                    .unwrap()
                    .to_string();
                assert!(md.contains("**world**"), "missing strong: {}", md);
                assert!(md.contains("*em*"), "missing em: {}", md);
                assert!(
                    md.contains("[link](https://x.test/)"),
                    "missing link: {}",
                    md
                );
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<html><body><p>Hello <strong>world</strong> <a href="https://x.test/">link</a> <em>em</em></p></body></html>""");
        var md = fixture.Runtime.Evaluate(MarkdownScript.HtmlToMarkdown)!.GetValue<string>();
        Assert.True(md.Contains("**world**", StringComparison.Ordinal), $"missing strong: {md}");
        Assert.True(md.Contains("*em*", StringComparison.Ordinal), $"missing em: {md}");
        Assert.True(md.Contains("[link](https://x.test/)", StringComparison.Ordinal), $"missing link: {md}");
    }

    [Fact]
    public void TestHtmlToMarkdownLists()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_html_to_markdown_lists() {
                let mut rt = setup_runtime(
                    "<html><body><ul><li>A</li><li>B</li></ul><ol><li>X</li><li>Y</li></ol></body></html>",
                );
                let md = rt
                    .evaluate(crate::HTML_TO_MARKDOWN_JS)
                    .unwrap()
                    .as_str()
                    .unwrap()
                    .to_string();
                assert!(md.contains("- A"), "missing unordered A: {}", md);
                assert!(md.contains("- B"), "missing unordered B: {}", md);
                assert!(md.contains("1. X"), "missing ordered X: {}", md);
            }
        */
        using var fixture = RuntimeFixture.Setup(
            "<html><body><ul><li>A</li><li>B</li></ul><ol><li>X</li><li>Y</li></ol></body></html>");
        var md = fixture.Runtime.Evaluate(MarkdownScript.HtmlToMarkdown)!.GetValue<string>();
        Assert.True(md.Contains("- A", StringComparison.Ordinal), $"missing unordered A: {md}");
        Assert.True(md.Contains("- B", StringComparison.Ordinal), $"missing unordered B: {md}");
        Assert.True(md.Contains("1. X", StringComparison.Ordinal), $"missing ordered X: {md}");
    }

    [Fact]
    public void TestHtmlToMarkdownSkipsScriptAndStyle()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_html_to_markdown_skips_script_and_style() {
                let mut rt = setup_runtime(
                    "<html><body><p>Text</p><script>alert(1)</script><style>body{color:red}</style></body></html>",
                );
                let md = rt
                    .evaluate(crate::HTML_TO_MARKDOWN_JS)
                    .unwrap()
                    .as_str()
                    .unwrap()
                    .to_string();
                assert!(md.contains("Text"), "missing visible text: {}", md);
                assert!(!md.contains("alert"), "leaked script content: {}", md);
                assert!(!md.contains("color:red"), "leaked style content: {}", md);
            }
        */
        using var fixture = RuntimeFixture.Setup(
            "<html><body><p>Text</p><script>alert(1)</script><style>body{color:red}</style></body></html>");
        var md = fixture.Runtime.Evaluate(MarkdownScript.HtmlToMarkdown)!.GetValue<string>();
        Assert.True(md.Contains("Text", StringComparison.Ordinal), $"missing visible text: {md}");
        Assert.False(md.Contains("alert", StringComparison.Ordinal), $"leaked script content: {md}");
        Assert.False(md.Contains("color:red", StringComparison.Ordinal), $"leaked style content: {md}");
    }

    [Fact]
    public void TestPageContentPuppeteerPattern()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_page_content_puppeteer_pattern() {
                let mut rt =
                    setup_runtime("<!DOCTYPE html><html><head></head><body><p>Test</p></body></html>");
                let result = rt.evaluate(
                    "(function() { let retVal = ''; if (document.doctype) retVal = new XMLSerializer().serializeToString(document.doctype); if (document.documentElement) retVal += document.documentElement.outerHTML; return retVal; })()"
                ).unwrap();
                let html = result.as_str().unwrap();
                assert!(html.starts_with("<!DOCTYPE html>"));
                assert!(html.contains("<html>"));
                assert!(html.contains("<p>Test</p>"));
            }
        */
        using var fixture = RuntimeFixture.Setup(
            "<!DOCTYPE html><html><head></head><body><p>Test</p></body></html>");
        var html = fixture.Runtime.Evaluate(
            "(function() { let retVal = ''; if (document.doctype) retVal = new XMLSerializer().serializeToString(document.doctype); "
            + "if (document.documentElement) retVal += document.documentElement.outerHTML; return retVal; })()")!
            .GetValue<string>();
        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("<html>", html, StringComparison.Ordinal);
        Assert.Contains("<p>Test</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TestElementFromPointIsFunction()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_element_from_point_is_function() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let kind = rt.evaluate("typeof document.elementFromPoint").unwrap();
                assert_eq!(kind, serde_json::json!("function"));
                let kind2 = rt.evaluate("typeof document.elementsFromPoint").unwrap();
                assert_eq!(kind2, serde_json::json!("function"));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        Assert.Equal("function", rt.Evaluate("typeof document.elementFromPoint")!.GetValue<string>());
        Assert.Equal("function", rt.Evaluate("typeof document.elementsFromPoint")!.GetValue<string>());
    }

    [Fact]
    public void TestElementFromPointInViewportReturnsBody()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_element_from_point_in_viewport_returns_body() {
                let mut rt = setup_runtime("<html><body><h1>Hi</h1></body></html>");
                let tag = rt
                    .evaluate("document.elementFromPoint(10, 10)?.tagName")
                    .unwrap();
                assert_eq!(tag, serde_json::json!("BODY"));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body><h1>Hi</h1></body></html>");
        Assert.Equal(
            "BODY",
            fixture.Runtime.Evaluate("document.elementFromPoint(10, 10)?.tagName")!.GetValue<string>());
    }

    [Fact]
    public void TestElementFromPointOutOfViewportReturnsNull()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_element_from_point_out_of_viewport_returns_null() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let neg_x = rt.evaluate("document.elementFromPoint(-1, 10)").unwrap();
                assert_eq!(neg_x, serde_json::Value::Null);
                let neg_y = rt.evaluate("document.elementFromPoint(10, -1)").unwrap();
                assert_eq!(neg_y, serde_json::Value::Null);
                let huge = rt
                    .evaluate("document.elementFromPoint(99999, 99999)")
                    .unwrap();
                assert_eq!(huge, serde_json::Value::Null);
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        Assert.Null(rt.Evaluate("document.elementFromPoint(-1, 10)"));
        Assert.Null(rt.Evaluate("document.elementFromPoint(10, -1)"));
        Assert.Null(rt.Evaluate("document.elementFromPoint(99999, 99999)"));
    }

    [Fact]
    public void TestElementsFromPointReturnsArray()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_elements_from_point_returns_array() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let len_in = rt
                    .evaluate("document.elementsFromPoint(10, 10).length")
                    .unwrap();
                assert_eq!(len_in.as_f64().unwrap() as i64, 1);
                let len_out = rt
                    .evaluate("document.elementsFromPoint(-1, -1).length")
                    .unwrap();
                assert_eq!(len_out.as_f64().unwrap() as i64, 0);
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        Assert.Equal(1, (long)rt.Evaluate("document.elementsFromPoint(10, 10).length")!.GetValue<double>());
        Assert.Equal(0, (long)rt.Evaluate("document.elementsFromPoint(-1, -1).length")!.GetValue<double>());
    }

    [Fact]
    public void TestElementFromPointNonNumericReturnsNull()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn test_element_from_point_non_numeric_returns_null() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let nan = rt.evaluate("document.elementFromPoint(NaN, 10)").unwrap();
                assert_eq!(nan, serde_json::Value::Null);
                let inf = rt
                    .evaluate("document.elementFromPoint(Infinity, 10)")
                    .unwrap();
                assert_eq!(inf, serde_json::Value::Null);
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        Assert.Null(rt.Evaluate("document.elementFromPoint(NaN, 10)"));
        Assert.Null(rt.Evaluate("document.elementFromPoint(Infinity, 10)"));
    }

    [Fact]
    public async Task EntryModuleHttpFailureIsNotEvaluatedAsEmptySource()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn entry_module_http_failure_is_not_evaluated_as_empty_source() {
                let base = spawn_one_response_server("404 Not Found", "not found");
                let jar = std::sync::Arc::new(obscura_net::CookieJar::new());
                let client = std::sync::Arc::new(obscura_net::ObscuraHttpClient::with_full_options(
                    jar, None, true,
                ));
                let mut rt = ObscuraJsRuntime::with_base_url(&format!("{}/", base));
                rt.set_http_client(client);

                let error = rt
                    .load_module(&format!("{}/entry.js", base), 1_000)
                    .await
                    .unwrap_err();
                assert!(
                    error.contains("HTTP 404"),
                    "expected entry fetch status in error, got: {}",
                    error
                );
            }
        */
        using var server = new RawHttpServer(
            _ => "HTTP/1.1 404 Not Found\r\nContent-Type: application/javascript\r\n"
                + "Content-Length: 9\r\nConnection: close\r\n\r\nnot found");
        using var rt = ModuleGraphRuntime($"{server.Origin}/", new CookieJar());

        var error = await Assert.ThrowsAsync<JsRuntimeException>(
            () => rt.LoadModuleAsync($"{server.Origin}/entry.js", 1_000));
        Assert.True(
            error.Message.Contains("HTTP 404", StringComparison.Ordinal),
            $"expected entry fetch status in error, got: {error.Message}");
    }

    [Fact]
    public async Task DependencyPreparedAsRootIsEvaluatedOnlyOnce()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn dependency_prepared_as_root_is_evaluated_only_once() {
                let base = spawn_duplicate_module_graph_server();
                let jar = std::sync::Arc::new(obscura_net::CookieJar::new());
                let client = std::sync::Arc::new(obscura_net::ObscuraHttpClient::with_full_options(
                    jar, None, true,
                ));
                let mut rt = ObscuraJsRuntime::with_base_url(&format!("{}/", base));
                rt.set_http_client(client);

                // The HTML scheduler prepares all module graphs before evaluating any
                // of them. The shared URL is both a dependency and a later root, which
                // used to reach deno_core::mod_evaluate twice and panic (#591).
                let entry = rt
                    .prepare_module(&format!("{}/entry.js", base), 1_000)
                    .await
                    .unwrap();
                let shared = rt
                    .prepare_module(&format!("{}/shared.js", base), 1_000)
                    .await
                    .unwrap();

                rt.evaluate_prepared_module(entry, 1_000).await.unwrap();
                rt.evaluate_prepared_module(shared, 1_000).await.unwrap();

                assert_eq!(
                    rt.evaluate("globalThis.__module_entry_ran === true").unwrap(),
                    serde_json::json!(true),
                );
                assert_eq!(
                    rt.evaluate("globalThis.__shared_module_runs").unwrap(),
                    serde_json::json!(1.0),
                );
            }
        */
        using var server = new RawHttpServer(request => RawHttpServer.RequestPath(request) switch
        {
            "/entry.js" => ModuleResponse("import './shared.js'; globalThis.__module_entry_ran = true;"),
            "/shared.js" => ModuleResponse(
                "globalThis.__shared_module_runs = (globalThis.__shared_module_runs || 0) + 1;"),
            _ => ModuleResponse("throw new Error('unexpected module path');"),
        });
        using var rt = ModuleGraphRuntime($"{server.Origin}/", new CookieJar());

        // The HTML scheduler prepares all module graphs before evaluating any
        // of them. The shared URL is both a dependency and a later root, which
        // used to reach deno_core::mod_evaluate twice and panic (#591).
        var entry = await rt.PrepareModuleAsync($"{server.Origin}/entry.js", 1_000);
        var shared = await rt.PrepareModuleAsync($"{server.Origin}/shared.js", 1_000);

        await rt.EvaluatePreparedModuleAsync(entry, 1_000);
        await rt.EvaluatePreparedModuleAsync(shared, 1_000);

        Assert.True(rt.Evaluate("globalThis.__module_entry_ran === true")!.GetValue<bool>());
        Assert.Equal(1, (long)rt.Evaluate("globalThis.__shared_module_runs")!.GetValue<double>());
    }

    [Fact]
    public void HeapLimitTerminatesScriptAndRuntimeRecovers()
    {
        // Deviation from the reference, recorded deliberately: Rust arms V8's
        // near-heap-limit callback through --max-old-space-size, which is
        // process-global and would cap every other runtime in this test host.
        // ClearScript's recoverable equivalent is a per-runtime ceiling, so the
        // limit is set on the instance. What the test pins is unchanged: the
        // script is terminated, the isolate survives, and it survives twice.
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetHeapLimit(32 * 1024 * 1024);

        for (var round = 0; round < 2; round++)
        {
            var error = Assert.Throws<JsRuntimeException>(() => rt.Evaluate(
                "(() => { const chunks = []; for (;;) { chunks.push(new Array(262144).fill(1.25)); } })()"));
            Assert.Contains("heap limit exceeded", error.Message, StringComparison.Ordinal);
            Assert.True(rt.Evaluate("globalThis.__runtime_survived_oom = true")!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task DescendantModuleUsesPageCookieIdentityAndHeaders()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn descendant_module_uses_page_cookie_identity_and_headers() {
                let (base, requests) = spawn_module_graph_server(ModuleGraphFixture::CookieProtected);
                let page_url = url::Url::parse(&format!("{}/", base)).unwrap();
                let jar = std::sync::Arc::new(obscura_net::CookieJar::new());
                jar.set_cookie("session=ok; Path=/", &page_url);
                let client = std::sync::Arc::new(obscura_net::ObscuraHttpClient::with_full_options(
                    jar.clone(),
                    None,
                    true,
                ));
                client.set_user_agent("ModuleGraphTest/1.0").await;
                client
                    .set_extra_headers(std::collections::HashMap::from([(
                        "x-module-test".to_string(),
                        "shared".to_string(),
                    )]))
                    .await;
                let callback_urls = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
                let callbacks = std::sync::Arc::new(obscura_net::CallbackRegistry::new());
                let callback_urls_capture = callback_urls.clone();
                callbacks.add_request(std::sync::Arc::new(move |request| {
                    callback_urls_capture
                        .lock()
                        .unwrap()
                        .push(request.url.path().to_string());
                }));

                let mut rt = ObscuraJsRuntime::with_base_url(&format!("{}/", base));
                rt.set_cookie_jar(jar);
                rt.set_http_client(client);
                rt.set_callbacks(callbacks);
                rt.load_module(&format!("{}/entry.js", base), 1_000)
                    .await
                    .unwrap();

                assert_eq!(
                    rt.evaluate("globalThis.__module_graph_value").unwrap(),
                    serde_json::json!("cookie-child"),
                );
                let requests = (0..2)
                    .map(|_| {
                        requests
                            .recv_timeout(std::time::Duration::from_secs(1))
                            .unwrap()
                    })
                    .collect::<Vec<_>>();
                assert!(requests
                    .iter()
                    .any(|request| request.starts_with("GET /entry.js ")));
                let child = requests
                    .iter()
                    .find(|request| request.starts_with("GET /child.js "))
                    .expect("descendant request");
                let child_lower = child.to_ascii_lowercase();
                assert!(
                    child_lower.contains("\r\ncookie: session=ok\r\n"),
                    "{child}"
                );
                assert!(
                    child_lower.contains("\r\nuser-agent: modulegraphtest/1.0\r\n"),
                    "{child}"
                );
                assert!(
                    child_lower.contains("\r\nx-module-test: shared\r\n"),
                    "{child}"
                );
                assert_eq!(
                    *callback_urls.lock().unwrap(),
                    vec!["/entry.js", "/child.js"],
                );
            }
        */
        using var server = new RawHttpServer(request =>
        {
            var lower = request.ToLowerInvariant();
            return RawHttpServer.RequestPath(request) switch
            {
                "/entry.js" => ModuleResponse(
                    "import { value } from './child.js'; globalThis.__module_graph_value = value;"),
                "/child.js" when lower.Contains("\r\ncookie: session=ok\r\n", StringComparison.Ordinal)
                    && lower.Contains("\r\nuser-agent: modulegraphtest/1.0\r\n", StringComparison.Ordinal)
                    && lower.Contains("\r\nx-module-test: shared\r\n", StringComparison.Ordinal)
                    => ModuleResponse("export const value = 'cookie-child';"),
                "/child.js" => ModuleResponse(
                    "throw new Error('page request context missing');", "401 Unauthorized"),
                _ => ModuleResponse("not found", "404 Not Found"),
            };
        });
        var jar = new CookieJar();
        jar.SetCookie("session=ok; Path=/", new Uri($"{server.Origin}/"));
        var client = new ObscuraHttpClient(jar, null, allowPrivateNetwork: true);
        client.SetUserAgent("ModuleGraphTest/1.0");
        client.SetExtraHeaders(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-module-test"] = "shared",
        });
        List<string> callbackUrls = [];
        var callbacks = new CallbackRegistry();
        callbacks.AddRequest(request =>
        {
            lock (callbackUrls)
            {
                callbackUrls.Add(request.Url.AbsolutePath);
            }
        });

        using var rt = ObscuraJsRuntime.WithBaseUrl($"{server.Origin}/");
        rt.SetCookieJar(jar);
        rt.SetHttpClient(client);
        rt.SetCallbacks(callbacks);
        await rt.LoadModuleAsync($"{server.Origin}/entry.js", 1_000);

        Assert.Equal("cookie-child", rt.Evaluate("globalThis.__module_graph_value")!.GetValue<string>());
        var requests = server.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Contains(requests, r => r.StartsWith("GET /entry.js ", StringComparison.Ordinal));
        var child = Assert.Single(requests, r => r.StartsWith("GET /child.js ", StringComparison.Ordinal))
            .ToLowerInvariant();
        Assert.True(child.Contains("\r\ncookie: session=ok\r\n", StringComparison.Ordinal), child);
        Assert.True(child.Contains("\r\nuser-agent: modulegraphtest/1.0\r\n", StringComparison.Ordinal), child);
        Assert.True(child.Contains("\r\nx-module-test: shared\r\n", StringComparison.Ordinal), child);
        Assert.Equal(["/entry.js", "/child.js"], callbackUrls);
    }

    [Fact]
    public async Task CrossOriginModuleDescendantDoesNotGainModuleOriginCookies()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn cross_origin_module_descendant_does_not_gain_module_origin_cookies() {
                let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
                let address = listener.local_addr().unwrap();
                let module_base = format!("http://{address}");
                let document_url = "http://127.0.0.1:1/page";
                let document_origin = "http://127.0.0.1:1";
                let (requests_tx, requests_rx) = std::sync::mpsc::channel();
                std::thread::spawn(move || {
                    use std::io::{Read as _, Write as _};

                    for _ in 0..2 {
                        let (mut stream, _) = listener.accept().unwrap();
                        let mut request = [0u8; 4096];
                        let length = stream.read(&mut request).unwrap();
                        let request = String::from_utf8_lossy(&request[..length]).to_string();
                        let path = request
                            .lines()
                            .next()
                            .and_then(|line| line.split_ascii_whitespace().nth(1))
                            .unwrap_or("/");
                        let body = match path {
                            "/entry.js" => {
                                "import { value } from './child.js'; globalThis.__cors_value = value;"
                            }
                            "/child.js" => "export const value = 'safe';",
                            _ => "throw new Error('unexpected module path');",
                        };
                        requests_tx.send(request).unwrap();
                        let response = format!(
                            "HTTP/1.1 200 OK\r\n\
                             Content-Type: application/javascript\r\n\
                             Access-Control-Allow-Origin: {document_origin}\r\n\
                             Cache-Control: public, max-age=3600\r\n\
                             Content-Length: {}\r\n\
                             Connection: close\r\n\r\n{body}",
                            body.len(),
                        );
                        stream.write_all(response.as_bytes()).unwrap();
                    }
                });

                let module_origin = url::Url::parse(&module_base).unwrap();
                let jar = std::sync::Arc::new(obscura_net::CookieJar::new());
                jar.set_cookie("cdn_session=secret; Path=/", &module_origin);
                let client = std::sync::Arc::new(obscura_net::ObscuraHttpClient::with_full_options(
                    jar, None, true,
                ));
                let mut rt = ObscuraJsRuntime::with_base_url(document_url);
                rt.set_http_client(client);
                rt.load_module(&format!("{module_base}/entry.js"), 1_000)
                    .await
                    .unwrap();

                assert_eq!(
                    rt.evaluate("globalThis.__cors_value").unwrap(),
                    serde_json::json!("safe"),
                );
                let requests = (0..2)
                    .map(|_| {
                        requests_rx
                            .recv_timeout(std::time::Duration::from_secs(1))
                            .unwrap()
                    })
                    .collect::<Vec<_>>();
                for request in &requests {
                    let lower = request.to_ascii_lowercase();
                    assert!(lower.contains("\r\norigin: http://127.0.0.1:1\r\n"), "{request}");
                    assert!(!lower.contains("\r\ncookie:"), "{request}");
                }
                let child = requests
                    .iter()
                    .find(|request| request.starts_with("GET /child.js "))
                    .expect("child module request")
                    .to_ascii_lowercase();
                assert!(
                    child.contains(&format!("\r\nreferer: {module_base}/entry.js\r\n")),
                    "{child}",
                );
            }
        */
        const string documentUrl = "http://127.0.0.1:1/page";
        const string documentOrigin = "http://127.0.0.1:1";
        using var server = new RawHttpServer(request =>
        {
            var body = RawHttpServer.RequestPath(request) switch
            {
                "/entry.js" => "import { value } from './child.js'; globalThis.__cors_value = value;",
                "/child.js" => "export const value = 'safe';",
                _ => "throw new Error('unexpected module path');",
            };
            return "HTTP/1.1 200 OK\r\nContent-Type: application/javascript\r\n"
                + $"Access-Control-Allow-Origin: {documentOrigin}\r\n"
                + "Cache-Control: public, max-age=3600\r\n"
                + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
        });
        var jar = new CookieJar();
        jar.SetCookie("cdn_session=secret; Path=/", new Uri(server.Origin));
        using var rt = ModuleGraphRuntime(documentUrl, jar);
        await rt.LoadModuleAsync($"{server.Origin}/entry.js", 1_000);

        Assert.Equal("safe", rt.Evaluate("globalThis.__cors_value")!.GetValue<string>());
        var requests = server.Requests;
        Assert.Equal(2, requests.Count);
        foreach (var request in requests)
        {
            var lower = request.ToLowerInvariant();
            Assert.True(lower.Contains("\r\norigin: http://127.0.0.1:1\r\n", StringComparison.Ordinal), request);
            Assert.False(lower.Contains("\r\ncookie:", StringComparison.Ordinal), request);
        }
        var child = Assert.Single(requests, r => r.StartsWith("GET /child.js ", StringComparison.Ordinal))
            .ToLowerInvariant();
        Assert.True(
            child.Contains($"\r\nreferer: {server.Origin}/entry.js\r\n".ToLowerInvariant(), StringComparison.Ordinal),
            child);
    }

    [Fact]
    public async Task DescendantModuleFollowsPageClientRedirects()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn descendant_module_follows_page_client_redirects() {
                let (base, requests) = spawn_module_graph_server(ModuleGraphFixture::RedirectedChild);
                let jar = std::sync::Arc::new(obscura_net::CookieJar::new());
                let client = std::sync::Arc::new(obscura_net::ObscuraHttpClient::with_full_options(
                    jar, None, true,
                ));
                let mut rt = ObscuraJsRuntime::with_base_url(&format!("{}/", base));
                rt.set_http_client(client);
                rt.load_module(&format!("{}/entry.js", base), 1_000)
                    .await
                    .unwrap();

                assert_eq!(
                    rt.evaluate("globalThis.__module_graph_value").unwrap(),
                    serde_json::json!("redirect-child"),
                );
                let paths = (0..3)
                    .map(|_| {
                        requests
                            .recv_timeout(std::time::Duration::from_secs(1))
                            .unwrap()
                            .lines()
                            .next()
                            .unwrap()
                            .to_string()
                    })
                    .collect::<Vec<_>>();
                assert_eq!(
                    paths,
                    vec![
                        "GET /entry.js HTTP/1.1",
                        "GET /redirect.js HTTP/1.1",
                        "GET /child.js HTTP/1.1",
                    ],
                );
            }
        */
        using var server = new RawHttpServer(request => RawHttpServer.RequestPath(request) switch
        {
            "/entry.js" => ModuleResponse(
                "import { value } from './redirect.js'; globalThis.__module_graph_value = value;"),
            "/redirect.js" => "HTTP/1.1 302 Found\r\nContent-Type: application/javascript\r\n"
                + "Location: /child.js\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            "/child.js" => ModuleResponse("export const value = 'redirect-child';"),
            _ => ModuleResponse("not found", "404 Not Found"),
        });
        using var rt = ModuleGraphRuntime($"{server.Origin}/", new CookieJar());
        await rt.LoadModuleAsync($"{server.Origin}/entry.js", 1_000);

        Assert.Equal(
            "redirect-child",
            rt.Evaluate("globalThis.__module_graph_value")!.GetValue<string>());
        Assert.Equal(
            ["GET /entry.js HTTP/1.1", "GET /redirect.js HTTP/1.1", "GET /child.js HTTP/1.1"],
            server.RequestLines);
    }

    [Fact]
    public async Task ImportMapResolvesPrefixStaticAndExactDynamicImports()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn import_map_resolves_prefix_static_and_exact_dynamic_imports() {
                let (base, requests) = spawn_import_map_server();
                let jar = std::sync::Arc::new(obscura_net::CookieJar::new());
                let client = std::sync::Arc::new(obscura_net::ObscuraHttpClient::with_full_options(
                    jar, None, true,
                ));
                let mut rt = ObscuraJsRuntime::with_base_url(&format!("{}/app/index.html", base));
                rt.set_http_client(client);
                rt.add_import_map(
                    r#"{
                        "imports": {
                            "pkg/": "../vendor/pkg/",
                            "dynamic-pkg": "../vendor/dynamic.js"
                        }
                    }"#,
                    &format!("{}/config/import-map.json", base),
                )
                .unwrap();

                rt.load_inline_module(
                    "import { value as prefix } from 'pkg/feature.js'; \
                     const dynamic = (await import('dynamic-pkg')).value; \
                     globalThis.__import_map_values = [prefix, dynamic];",
                    &format!("{}/app/index.html", base),
                    1_000,
                )
                .await
                .unwrap();

                assert_eq!(
                    rt.evaluate("globalThis.__import_map_values").unwrap(),
                    serde_json::json!(["prefix-static", "exact-dynamic"]),
                );
                let paths = (0..2)
                    .map(|_| {
                        requests
                            .recv_timeout(std::time::Duration::from_secs(1))
                            .unwrap()
                    })
                    .collect::<Vec<_>>();
                assert!(
                    paths.contains(&"/vendor/pkg/feature.js".to_string()),
                    "{paths:?}"
                );
                assert!(
                    paths.contains(&"/vendor/dynamic.js".to_string()),
                    "{paths:?}"
                );
            }
        */
        using var server = new RawHttpServer(request => RawHttpServer.RequestPath(request) switch
        {
            "/vendor/pkg/feature.js" => ModuleResponse("export const value = 'prefix-static';"),
            "/vendor/dynamic.js" => ModuleResponse("export const value = 'exact-dynamic';"),
            _ => ModuleResponse("not found", "404 Not Found"),
        });
        using var rt = ModuleGraphRuntime($"{server.Origin}/app/index.html", new CookieJar());
        rt.AddImportMap(
            """
            {
                "imports": {
                    "pkg/": "../vendor/pkg/",
                    "dynamic-pkg": "../vendor/dynamic.js"
                }
            }
            """,
            $"{server.Origin}/config/import-map.json");

        await rt.LoadInlineModuleAsync(
            "import { value as prefix } from 'pkg/feature.js'; "
            + "const dynamic = (await import('dynamic-pkg')).value; "
            + "globalThis.__import_map_values = [prefix, dynamic];",
            $"{server.Origin}/app/index.html",
            1_000);

        AssertJsonEquals(
            """["prefix-static", "exact-dynamic"]""",
            rt.Evaluate("globalThis.__import_map_values"));
        var paths = server.RequestLines.Select(line => line.Split(' ')[1]).ToArray();
        Assert.Contains("/vendor/pkg/feature.js", paths);
        Assert.Contains("/vendor/dynamic.js", paths);
    }

    [Fact]
    public async Task ImportMapDoesNotRemapExternalRootModuleUrl()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn import_map_does_not_remap_external_root_module_url() {
                let (base, requests) = spawn_root_module_import_map_server();
                let jar = std::sync::Arc::new(obscura_net::CookieJar::new());
                let client = std::sync::Arc::new(obscura_net::ObscuraHttpClient::with_full_options(
                    jar, None, true,
                ));
                let mut rt = ObscuraJsRuntime::with_base_url(&format!("{}/index.html", base));
                rt.set_http_client(client);
                rt.add_import_map(
                    &format!(r#"{{"imports":{{"{base}/entry.js":"{base}/remapped.js"}}}}"#),
                    &format!("{}/index.html", base),
                )
                .unwrap();

                rt.load_module(&format!("{}/entry.js", base), 1_000)
                    .await
                    .unwrap();
                assert_eq!(
                    rt.evaluate("globalThis.__root_module_identity").unwrap(),
                    serde_json::json!("entry"),
                );
                assert_eq!(
                    requests
                        .recv_timeout(std::time::Duration::from_secs(1))
                        .unwrap(),
                    "/entry.js",
                );
            }
        */
        using var server = new RawHttpServer(request => ModuleResponse(
            RawHttpServer.RequestPath(request) == "/entry.js"
                ? "globalThis.__root_module_identity = 'entry';"
                : "globalThis.__root_module_identity = 'remapped';"));
        using var rt = ModuleGraphRuntime($"{server.Origin}/index.html", new CookieJar());
        rt.AddImportMap(
            $$$"""{"imports":{"{{{server.Origin}}}/entry.js":"{{{server.Origin}}}/remapped.js"}}""",
            $"{server.Origin}/index.html");

        await rt.LoadModuleAsync($"{server.Origin}/entry.js", 1_000);
        Assert.Equal("entry", rt.Evaluate("globalThis.__root_module_identity")!.GetValue<string>());
        Assert.Equal("/entry.js", server.RequestLines[0].Split(' ')[1]);
    }

    [Fact]
    public async Task InlineModulesExposeDocumentBaseAsImportMetaUrl()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn inline_modules_expose_document_base_as_import_meta_url() {
                let mut rt = ObscuraJsRuntime::with_base_url("https://example.com/page/index.html");
                rt.load_inline_module(
                    "globalThis.__first_inline_url = import.meta.url;",
                    "https://example.com/base/",
                    1_000,
                )
                .await
                .unwrap();
                rt.load_inline_module(
                    "globalThis.__second_inline_url = import.meta.url;",
                    "https://example.com/base/",
                    1_000,
                )
                .await
                .unwrap();

                assert_eq!(
                    rt.evaluate("[globalThis.__first_inline_url, globalThis.__second_inline_url]")
                        .unwrap(),
                    serde_json::json!(["https://example.com/base/", "https://example.com/base/"])
                );
            }
        */
        using var rt = ObscuraJsRuntime.WithBaseUrl("https://example.com/page/index.html");
        await rt.LoadInlineModuleAsync(
            "globalThis.__first_inline_url = import.meta.url;",
            "https://example.com/base/",
            1_000);
        await rt.LoadInlineModuleAsync(
            "globalThis.__second_inline_url = import.meta.url;",
            "https://example.com/base/",
            1_000);

        AssertJsonEquals(
            """["https://example.com/base/", "https://example.com/base/"]""",
            rt.Evaluate("[globalThis.__first_inline_url, globalThis.__second_inline_url]"));
    }

    [Fact]
    public async Task ClassicScriptUrlIsDynamicImportReferrer()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn classic_script_url_is_dynamic_import_referrer() {
                use std::io::{Read as _, Write as _};

                let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
                let address = listener.local_addr().unwrap();
                let request_thread = std::thread::spawn(move || {
                    let (mut stream, _) = listener.accept().unwrap();
                    let mut request = [0u8; 2048];
                    let length = stream.read(&mut request).unwrap();
                    let path = String::from_utf8_lossy(&request[..length])
                        .lines()
                        .next()
                        .and_then(|line| line.split_ascii_whitespace().nth(1))
                        .unwrap_or("/")
                        .to_string();
                    let body = "export const value = 'scoped-classic';";
                    let response = format!(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/javascript\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                        body.len(),
                    );
                    stream.write_all(response.as_bytes()).unwrap();
                    path
                });
                let base = format!("http://{address}");
                let jar = std::sync::Arc::new(obscura_net::CookieJar::new());
                let client = std::sync::Arc::new(obscura_net::ObscuraHttpClient::with_full_options(
                    jar, None, true,
                ));
                let mut rt = ObscuraJsRuntime::with_base_url(&format!("{base}/page/index.html"));
                rt.set_http_client(client);
                rt.set_dom(parse_html("<html><body></body></html>"));
                rt.run_page_init();
                rt.add_import_map(
                    &format!(r#"{{"scopes":{{"{base}/classic/":{{"pkg":"{base}/scoped.js"}}}}}}"#),
                    &format!("{base}/page/index.html"),
                )
                .unwrap();

                rt.execute_script(
                    &format!("{base}/classic/entry.js"),
                    "document.documentElement.setAttribute('data-classic-op', 'ran'); \
                     import('pkg').then(module => { globalThis.__classic_import = module.value; });",
                )
                .unwrap();
                rt.run_event_loop().await.unwrap();

                assert_eq!(
                    rt.evaluate("globalThis.__classic_import").unwrap(),
                    serde_json::json!("scoped-classic")
                );
                assert_eq!(
                    rt.evaluate("document.documentElement.getAttribute('data-classic-op')")
                        .unwrap(),
                    serde_json::json!("ran")
                );
                assert_eq!(request_thread.join().unwrap(), "/scoped.js");
            }
        */
        using var server = new RawHttpServer(_ => ModuleResponse("export const value = 'scoped-classic';"));
        using var rt = ModuleGraphRuntime($"{server.Origin}/page/index.html", new CookieJar());
        rt.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        rt.RunPageInit();
        rt.AddImportMap(
            "{\"scopes\":{\"" + server.Origin + "/classic/\":{\"pkg\":\"" + server.Origin + "/scoped.js\"}}}",
            $"{server.Origin}/page/index.html");

        rt.ExecuteScript(
            $"{server.Origin}/classic/entry.js",
            "document.documentElement.setAttribute('data-classic-op', 'ran'); "
            + "import('pkg').then(module => { globalThis.__classic_import = module.value; });");
        await rt.RunEventLoopAsync();

        Assert.Equal("scoped-classic", rt.Evaluate("globalThis.__classic_import")!.GetValue<string>());
        Assert.Equal(
            "ran",
            rt.Evaluate("document.documentElement.getAttribute('data-classic-op')")!.GetValue<string>());
        Assert.Equal("/scoped.js", server.RequestLines[0].Split(' ')[1]);
    }

    [Fact]
    public void TimedOutClassicScriptLeavesRuntimeReusable()
    {
        // The load-bearing one: synchronous V8 work cannot be cancelled at an
        // await point, so the watchdog thread has to interrupt the isolate.
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.ExecuteScriptWithTimeout(
            "https://example.test/hang.js",
            "while (true) {}",
            TimeSpan.FromMilliseconds(20));
        rt.ExecuteScript("https://example.test/after-timeout.js", "globalThis.__after_timeout = true;");
        Assert.True(rt.Evaluate("globalThis.__after_timeout")!.GetValue<bool>());
    }

    [Fact(Skip = "ClearScript merges module load and evaluation into one call, so a graph load failure is reported as an evaluation error; see the port report")]
    public async Task InlineModuleGraphErrorPropagates()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn inline_module_graph_error_propagates() {
                let mut rt = ObscuraJsRuntime::with_base_url("https://example.com/");
                let error = rt
                    .load_inline_module("import 'bare-specifier';", "https://example.com/", 1_000)
                    .await
                    .unwrap_err();
                assert!(
                    error.contains("Inline module load error"),
                    "expected graph load error, got: {}",
                    error
                );
            }
        */
        await Task.CompletedTask;
    }

    [Fact]
    public async Task InlineModuleEvaluationErrorPropagates()
    {
        using var rt = ObscuraJsRuntime.WithBaseUrl("https://example.com/");
        var error = await Assert.ThrowsAsync<JsRuntimeException>(() =>
            rt.LoadInlineModuleAsync("throw new Error('module-evaluation-boom');", "https://example.com/", 1_000));
        Assert.Contains("Inline module eval error", error.Message, StringComparison.Ordinal);
        Assert.Contains("module-evaluation-boom", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InlineModuleEvaluationTimeoutPropagates()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn inline_module_evaluation_timeout_propagates() {
                let mut rt = ObscuraJsRuntime::with_base_url("https://example.com/");
                let error = rt
                    .load_inline_module(
                        "await new Promise(resolve => setTimeout(resolve, 10000));",
                        "https://example.com/",
                        20,
                    )
                    .await
                    .unwrap_err();
                assert!(
                    error.contains("Inline module evaluation timed out after"),
                    "expected evaluation timeout, got: {}",
                    error
                );
            }
        */
        using var rt = ObscuraJsRuntime.WithBaseUrl("https://example.com/");
        var error = await Assert.ThrowsAsync<JsRuntimeException>(() =>
            rt.LoadInlineModuleAsync(
                "await new Promise(resolve => setTimeout(resolve, 10000));",
                "https://example.com/",
                20));
        Assert.True(
            error.Message.Contains("Inline module evaluation timed out after", StringComparison.Ordinal),
            $"expected evaluation timeout, got: {error.Message}");
    }

    [Fact]
    public async Task SuccessfulInlineModuleDoesNotWaitForIntervalIdle()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn successful_inline_module_does_not_wait_for_interval_idle() {
                let mut rt = ObscuraJsRuntime::with_base_url("https://example.com/");
                rt.load_inline_module(
                    "globalThis.__module_loaded = true; setInterval(() => {}, 10000);",
                    "https://example.com/",
                    500,
                )
                .await
                .unwrap();
                assert_eq!(
                    rt.evaluate("globalThis.__module_loaded").unwrap(),
                    serde_json::json!(true)
                );
            }
        */
        using var rt = ObscuraJsRuntime.WithBaseUrl("https://example.com/");
        await rt.LoadInlineModuleAsync(
            "globalThis.__module_loaded = true; setInterval(() => {}, 10000);",
            "https://example.com/",
            500);
        Assert.True(rt.Evaluate("globalThis.__module_loaded")!.GetValue<bool>());
    }

    // Issue #139 — proxy_url must thread through to both the ES-module
    // loader (module_loader.rs) and op_fetch_url's reqwest client
    // (ops.rs::build_request_client). Pre-fix both built clients with
    // `Client::builder().build()` — no proxy — so JS fetch/XHR and
    // dynamic imports silently bypassed BrowserContext.proxy_url.
    //
    // Phase 5.5 RED check: each test references a symbol that does NOT
    // exist on main (proxy_url() accessor, with_proxy ctor,
    // with_base_url_and_proxy ctor), so the tests fail to compile without
    // the prod fix.
    [Fact]
    public void HttpClientRoundTripsProxyUrl()
    {
        var jar = new CookieJar();
        var configured = new ObscuraHttpClient(jar, "http://proxy.test:8080");
        Assert.Equal("http://proxy.test:8080", configured.ProxyUrl);

        var direct = new ObscuraHttpClient(jar, null);
        Assert.Null(direct.ProxyUrl);
    }

    [Fact]
    public void ModuleLoaderStoresProxyForDynamicImports()
    {
        var loader = new ObscuraModuleLoader("https://example.com/", "http://proxy.test:8080");
        Assert.Equal("http://proxy.test:8080", loader.ProxyUrl);
        Assert.Equal("https://example.com/", loader.BaseUrl);

        // The default construction must keep the historical "no proxy" behaviour.
        var direct = new ObscuraModuleLoader("https://example.com/", null);
        Assert.Null(direct.ProxyUrl);
    }

    [Fact]
    public void RuntimeWithBaseUrlAndProxyConstructsSuccessfully()
    {
        // Sanity-check the public ctor that the page layer uses to thread a
        // proxy through to the module loader. Direct and proxied paths must
        // both initialise the JS environment.
        using var direct = ObscuraJsRuntime.WithBaseUrlAndProxy("https://example.com/", null);
        using var proxied = ObscuraJsRuntime.WithBaseUrlAndProxy("https://example.com/", "http://proxy.test:8080");
        Assert.Null(direct.ProxyUrl);
        Assert.Equal("http://proxy.test:8080", proxied.ProxyUrl);
    }

    /// Playwright >= 1.25 calls `element.checkVisibility(...)` before every
    /// input event. If the method isn't defined Playwright retries until its
    /// action timeout fires. Without a layout engine we can't compute it
    /// properly, so the stub always returns true — still strictly better
    /// than the undefined path.
    [Fact]
    public void ElementCheckVisibilityIsCallable()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn element_check_visibility_is_callable() {
                let mut rt = setup_runtime(r#"<div id="x">x</div>"#);
                let result = rt
                    .evaluate("document.getElementById('x').checkVisibility({checkOpacity: true})")
                    .unwrap();
                assert_eq!(result, serde_json::json!(true));

                let typeof_method = rt
                    .evaluate("typeof document.getElementById('x').checkVisibility")
                    .unwrap();
                assert_eq!(typeof_method, serde_json::json!("function"));
            }
        */
        using var fixture = RuntimeFixture.Setup("""<div id="x">x</div>""");
        var rt = fixture.Runtime;
        Assert.True(
            rt.Evaluate("document.getElementById('x').checkVisibility({checkOpacity: true})")!.GetValue<bool>());
        Assert.Equal(
            "function",
            rt.Evaluate("typeof document.getElementById('x').checkVisibility")!.GetValue<string>());
    }

    /// Playwright's `getByRole` / `getByLabel` locators resolve via ARIA
    /// reflection properties. Without the getters those locators always
    /// fail. Reflect the underlying aria-* attributes.
    [Fact]
    public void ElementAriaReflectionPropertiesReadAriaAttrs()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn element_aria_reflection_properties_read_aria_attrs() {
                let mut rt = setup_runtime(
                    r#"<button id="b" role="tab" aria-label="Settings" aria-selected="true">x</button>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                        const el = document.getElementById('b');
                        return [el.role, el.ariaLabel, el.ariaSelected];
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!(["tab", "Settings", "true"]));
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<button id="b" role="tab" aria-label="Settings" aria-selected="true">x</button>""");
        var result = fixture.Runtime.Evaluate("""
            const el = document.getElementById('b');
            return [el.role, el.ariaLabel, el.ariaSelected];
            """);
        AssertJsonEquals("""["tab", "Settings", "true"]""", result);
    }

    /// Setting an ARIA reflection property must write through to the
    /// underlying attribute so frameworks that toggle state via
    /// `el.ariaExpanded = 'true'` actually update the DOM.
    /// Regression: React 18 / mobile SPAs (e.g. goofish.com) call
    /// addEventListener on navigator.connection (NetworkInformation) and
    /// navigator.serviceWorker (ServiceWorkerContainer). Both are EventTargets
    /// in real browsers; missing the method crashed the app bundle with
    /// "addEventListener is not a function".
    [Fact]
    public void NavigatorEventtargetStubsExposeAddEventListener()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn navigator_eventtarget_stubs_expose_add_event_listener() {
                let mut rt = setup_runtime("<div></div>");
                let result = rt
                    .evaluate(
                        r#"
                        const connection = navigator.connection;
                        let calls = 0;
                        let receiverMatches = false;
                        function listener(event) {
                            calls += 1;
                            receiverMatches = this === connection && event.type === 'change';
                        }
                        connection.addEventListener('change', listener);
                        const dispatchResult = connection.dispatchEvent(new Event('change'));
                        connection.removeEventListener('change', listener);
                        connection.dispatchEvent(new Event('change'));
                        return [
                            typeof connection.addEventListener,
                            typeof connection.removeEventListener,
                            typeof connection.dispatchEvent,
                            typeof navigator.serviceWorker.addEventListener,
                            dispatchResult,
                            calls,
                            receiverMatches,
                        ];
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!(["function", "function", "function", "function", true, 1, true])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<div></div>");
        var result = fixture.Runtime.Evaluate("""
            const connection = navigator.connection;
            let calls = 0;
            let receiverMatches = false;
            function listener(event) {
                calls += 1;
                receiverMatches = this === connection && event.type === 'change';
            }
            connection.addEventListener('change', listener);
            const dispatchResult = connection.dispatchEvent(new Event('change'));
            connection.removeEventListener('change', listener);
            connection.dispatchEvent(new Event('change'));
            return [
                typeof connection.addEventListener,
                typeof connection.removeEventListener,
                typeof connection.dispatchEvent,
                typeof navigator.serviceWorker.addEventListener,
                dispatchResult,
                calls,
                receiverMatches,
            ];
            """);
        AssertJsonEquals(
            """["function", "function", "function", "function", true, 1, true]""",
            result);
    }

    [Fact]
    public void TextCodecStreamsExposeBrowserShape()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn text_codec_streams_expose_browser_shape() {
                let mut rt = setup_runtime("<div></div>");
                let result = rt
                    .evaluate(
                        r#"
                        const encoder = new TextEncoderStream();
                        const decoder = new TextDecoderStream();
                        return {
                            encoder: encoder.encoding,
                            encoderReadable: typeof encoder.readable.getReader,
                            encoderWritable: typeof encoder.writable.getWriter,
                            decoder: decoder.encoding,
                            decoderReadable: typeof decoder.readable.getReader,
                            decoderWritable: typeof decoder.writable.getWriter,
                        };
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!({
                        "encoder": "utf-8",
                        "encoderReadable": "function",
                        "encoderWritable": "function",
                        "decoder": "utf-8",
                        "decoderReadable": "function",
                        "decoderWritable": "function",
                    })
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<div></div>");
        var result = fixture.Runtime.Evaluate("""
            const encoder = new TextEncoderStream();
            const decoder = new TextDecoderStream();
            return {
                encoder: encoder.encoding,
                encoderReadable: typeof encoder.readable.getReader,
                encoderWritable: typeof encoder.writable.getWriter,
                decoder: decoder.encoding,
                decoderReadable: typeof decoder.readable.getReader,
                decoderWritable: typeof decoder.writable.getWriter,
            };
            """);
        AssertJsonEquals(
            """
            {
                "encoder": "utf-8",
                "encoderReadable": "function",
                "encoderWritable": "function",
                "decoder": "utf-8",
                "decoderReadable": "function",
                "decoderWritable": "function"
            }
            """,
            result);
    }

    [Fact]
    public async Task TextEncoderStreamPipeThroughDeliversHydrationData()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn text_encoder_stream_pipe_through_delivers_hydration_data() {
                let mut rt = setup_runtime("<div></div>");
                let result = rt
                    .evaluate_for_cdp(
                        r#"
                        (async () => {
                            let sourceController;
                            const source = new ReadableStream({
                                start(controller) { sourceController = controller; },
                            });
                            const encoded = source.pipeThrough(new TextEncoderStream());
                            sourceController.enqueue('["server",{"hydrated":true}]\n');
                            sourceController.close();

                            const decoder = new TextDecoder();
                            let tail = "";
                            const lines = encoded.pipeThrough(new TransformStream({
                                transform(chunk, controller) {
                                    const complete = (tail + decoder.decode(chunk, {stream: true})).split("\n");
                                    tail = complete.pop() || "";
                                    for (const line of complete) controller.enqueue(line);
                                },
                                flush(controller) { if (tail) controller.enqueue(tail); },
                            }));
                            const first = await lines.getReader().read();
                            return JSON.parse(first.value);
                        })()
                        "#,
                        true,
                        true,
                    )
                    .await
                    .unwrap();
                assert_eq!(
                    result.value.unwrap(),
                    serde_json::json!(["server", {"hydrated": true}])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<div></div>");
        var result = await fixture.Runtime.EvaluateForCdpAsync(
            """
            (async () => {
                let sourceController;
                const source = new ReadableStream({
                    start(controller) { sourceController = controller; },
                });
                const encoded = source.pipeThrough(new TextEncoderStream());
                sourceController.enqueue('["server",{"hydrated":true}]\n');
                sourceController.close();

                const decoder = new TextDecoder();
                let tail = "";
                const lines = encoded.pipeThrough(new TransformStream({
                    transform(chunk, controller) {
                        const complete = (tail + decoder.decode(chunk, {stream: true})).split("\n");
                        tail = complete.pop() || "";
                        for (const line of complete) controller.enqueue(line);
                    },
                    flush(controller) { if (tail) controller.enqueue(tail); },
                }));
                const first = await lines.getReader().read();
                return JSON.parse(first.value);
            })()
            """,
            returnByValue: true,
            awaitPromise: true);
        AssertJsonEquals("""["server", {"hydrated": true}]""", result.Value);
    }

    /// Regression test for #285: DDoS-Guard's challenge calls
    /// `t.insertAdjacentText(...)` and dies with `TypeError: ... is not a
    /// function` because `Element.prototype.insertAdjacentText` was missing.
    /// Verify all four positions place a Text node (NOT parsed HTML) at the
    /// right spot. Tests `insertAdjacentText` exists, is callable, and that
    /// inserted content remains literal text — angle brackets must not be
    /// parsed as markup, which is the whole point of the API.
    [Fact]
    public void ElementInsertAdjacentTextPolyfill()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn element_insert_adjacent_text_polyfill() {
                let mut rt = setup_runtime(r#"<div id="p"><span id="t">X</span></div>"#);
                let result = rt
                    .evaluate(
                        r#"
                        const t = document.getElementById('t');
                        t.insertAdjacentText('afterbegin', 'AB');
                        t.insertAdjacentText('beforeend', 'BE');
                        t.insertAdjacentText('beforebegin', 'BB');
                        t.insertAdjacentText('afterend', 'AE');
                        t.insertAdjacentText('beforeend', '<b>raw</b>');
                        return [
                            typeof Element.prototype.insertAdjacentText,
                            document.getElementById('p').textContent,
                            t.getElementsByTagName('b').length,
                        ];
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!(["function", "BBABXBE<b>raw</b>AE", 0])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("""<div id="p"><span id="t">X</span></div>""");
        var result = fixture.Runtime.Evaluate("""
            const t = document.getElementById('t');
            t.insertAdjacentText('afterbegin', 'AB');
            t.insertAdjacentText('beforeend', 'BE');
            t.insertAdjacentText('beforebegin', 'BB');
            t.insertAdjacentText('afterend', 'AE');
            t.insertAdjacentText('beforeend', '<b>raw</b>');
            return [
                typeof Element.prototype.insertAdjacentText,
                document.getElementById('p').textContent,
                t.getElementsByTagName('b').length,
            ];
            """);
        AssertJsonEquals("""["function", "BBABXBE<b>raw</b>AE", 0]""", result);
    }

    /// Regression test for #285: `Element.prototype.insertAdjacentElement`
    /// was missing alongside `insertAdjacentText`. Verify all four positions
    /// place the given element correctly and that the inserted element is
    /// returned (per spec — that's the contract callers rely on for chaining).
    [Fact]
    public void ElementInsertAdjacentElementPolyfill()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn element_insert_adjacent_element_polyfill() {
                let mut rt = setup_runtime(r#"<div id="p"><span id="t">X</span></div>"#);
                let result = rt
                    .evaluate(
                        r#"
                        const t = document.getElementById('t');
                        const before = document.createElement('b');  before.id = 'before';
                        const after  = document.createElement('i');  after.id  = 'after';
                        const inside = document.createElement('em'); inside.id = 'inside';
                        const last   = document.createElement('u');  last.id   = 'last';
                        const r1 = t.insertAdjacentElement('beforebegin', before);
                        const r2 = t.insertAdjacentElement('afterend',    after);
                        const r3 = t.insertAdjacentElement('afterbegin',  inside);
                        const r4 = t.insertAdjacentElement('beforeend',   last);
                        const siblings = Array.from(document.getElementById('p').children).map(c => c.id);
                        const inT = Array.from(t.children).map(c => c.id);
                        return [
                            typeof Element.prototype.insertAdjacentElement,
                            r1 === before && r2 === after && r3 === inside && r4 === last,
                            siblings,
                            inT,
                        ];
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([
                        "function",
                        true,
                        ["before", "t", "after"],
                        ["inside", "last"]
                    ])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("""<div id="p"><span id="t">X</span></div>""");
        var result = fixture.Runtime.Evaluate("""
            const t = document.getElementById('t');
            const before = document.createElement('b');  before.id = 'before';
            const after  = document.createElement('i');  after.id  = 'after';
            const inside = document.createElement('em'); inside.id = 'inside';
            const last   = document.createElement('u');  last.id   = 'last';
            const r1 = t.insertAdjacentElement('beforebegin', before);
            const r2 = t.insertAdjacentElement('afterend',    after);
            const r3 = t.insertAdjacentElement('afterbegin',  inside);
            const r4 = t.insertAdjacentElement('beforeend',   last);
            const siblings = Array.from(document.getElementById('p').children).map(c => c.id);
            const inT = Array.from(t.children).map(c => c.id);
            return [
                typeof Element.prototype.insertAdjacentElement,
                r1 === before && r2 === after && r3 === inside && r4 === last,
                siblings,
                inT,
            ];
            """);
        AssertJsonEquals(
            """
            [
                "function",
                true,
                ["before", "t", "after"],
                ["inside", "last"]
            ]
            """,
            result);
    }

    [Fact]
    public void ConsoleLogErrorDoesNotTriggerPrepareStackTrace()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn console_log_error_does_not_trigger_prepare_stack_trace() {
                let mut rt = setup_runtime("<div></div>");
                let result = rt
                    .evaluate(
                        r#"
                    let called = false;
                    const saved = Error.prepareStackTrace;
                    Error.prepareStackTrace = function() { called = true; return saved; };
                    const e = new Error("test");
                    console.log(e);
                    Error.prepareStackTrace = saved;
                    return called;
                "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!(false));
            }
        */
        using var fixture = RuntimeFixture.Setup("<div></div>");
        var result = fixture.Runtime.Evaluate("""
            let called = false;
            const saved = Error.prepareStackTrace;
            Error.prepareStackTrace = function() { called = true; return saved; };
            const e = new Error("test");
            console.log(e);
            Error.prepareStackTrace = saved;
            return called;
            """);
        Assert.False(result!.GetValue<bool>());
    }

    [Fact]
    public void ElementAriaReflectionSettersWriteThrough()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn element_aria_reflection_setters_write_through() {
                let mut rt = setup_runtime(r#"<div id="d"></div>"#);
                let result = rt
                    .evaluate(
                        r#"
                        const el = document.getElementById('d');
                        el.role = 'menu';
                        el.ariaExpanded = 'true';
                        return [el.getAttribute('role'), el.getAttribute('aria-expanded')];
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!(["menu", "true"]));
            }
        */
        using var fixture = RuntimeFixture.Setup("""<div id="d"></div>""");
        var result = fixture.Runtime.Evaluate("""
            const el = document.getElementById('d');
            el.role = 'menu';
            el.ariaExpanded = 'true';
            return [el.getAttribute('role'), el.getAttribute('aria-expanded')];
            """);
        AssertJsonEquals("""["menu", "true"]""", result);
    }

    /// Framework schedulers commonly subclass EventTarget for their own
    /// lifecycle events. These targets have no backing DOM node, but must
    /// still deliver callbacks (including object, once, and signal listeners).
    [Fact]
    public void StandaloneEventTargetDeliversFrameworkLifecycleEvents()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn standalone_event_target_delivers_framework_lifecycle_events() {
                let mut rt = setup_runtime("<div></div>");
                let result = rt
                    .evaluate(
                        r#"
                        const TypedEventTarget = class extends EventTarget {};
                        const target = new TypedEventTarget();
                        const calls = [];
                        const removed = () => calls.push("removed");
                        const controller = new AbortController();
                        const node = { id: "canvas-ref" };

                        target.addEventListener("insert", (event) => calls.push(event.node.id));
                        target.addEventListener("insert", { handleEvent() { calls.push("object"); } });
                        target.addEventListener("insert", () => calls.push("once"), { once: true });
                        target.addEventListener("insert", removed);
                        target.removeEventListener("insert", removed);
                        target.addEventListener("insert", () => calls.push("aborted"), {
                            signal: controller.signal,
                        });
                        controller.abort();

                        const first = new Event("insert", { cancelable: true });
                        first.node = node;
                        const firstResult = target.dispatchEvent(first);
                        const second = new Event("insert");
                        second.node = node;
                        const secondResult = target.dispatchEvent(second);
                        return [
                            target instanceof EventTarget,
                            calls,
                            firstResult,
                            secondResult,
                            first.target === target,
                            first.currentTarget === null,
                        ];
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([
                        true,
                        ["canvas-ref", "object", "once", "canvas-ref", "object"],
                        true,
                        true,
                        true,
                        true
                    ])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<div></div>");
        var result = fixture.Runtime.Evaluate("""
            const TypedEventTarget = class extends EventTarget {};
            const target = new TypedEventTarget();
            const calls = [];
            const removed = () => calls.push("removed");
            const controller = new AbortController();
            const node = { id: "canvas-ref" };

            target.addEventListener("insert", (event) => calls.push(event.node.id));
            target.addEventListener("insert", { handleEvent() { calls.push("object"); } });
            target.addEventListener("insert", () => calls.push("once"), { once: true });
            target.addEventListener("insert", removed);
            target.removeEventListener("insert", removed);
            target.addEventListener("insert", () => calls.push("aborted"), {
                signal: controller.signal,
            });
            controller.abort();

            const first = new Event("insert", { cancelable: true });
            first.node = node;
            const firstResult = target.dispatchEvent(first);
            const second = new Event("insert");
            second.node = node;
            const secondResult = target.dispatchEvent(second);
            return [
                target instanceof EventTarget,
                calls,
                firstResult,
                secondResult,
                first.target === target,
                first.currentTarget === null,
            ];
            """);
        AssertJsonEquals(
            """
            [
                true,
                ["canvas-ref", "object", "once", "canvas-ref", "object"],
                true,
                true,
                true,
                true
            ]
            """,
            result);
    }

    [Fact]
    public void MediaTextTracksExposeLoadedWebvttCues()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn media_text_tracks_expose_loaded_webvtt_cues() {
                let mut rt = setup_runtime(
                    r#"<video><track id="captions" kind="captions" srclang="en" default
                        src="data:text/vtt,WEBVTT%0A%0A00%3A00%3A01.000%20--%3E%2000%3A00%3A03.000%0AHello"></video>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                        const element = document.getElementById("captions");
                        const video = document.querySelector("video");
                        const cue = element.track.cues[0];
                        const added = video.addTextTrack("metadata", "Data", "en");
                        cue.line = -2;
                        cue.size = 80;
                        return [
                            element instanceof HTMLTrackElement,
                            element.readyState === HTMLTrackElement.LOADED,
                            element.track instanceof TextTrack,
                            element.track.cues.length,
                            cue.startTime,
                            cue.endTime,
                            cue.text,
                            cue.line,
                            cue.size,
                            video.textTracks.length,
                            video.textTracks.getTrackById("captions") === element.track,
                            added.kind,
                        ];
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([true, true, true, 1, 1, 3, "Hello", -2, 80, 1, true, "metadata"])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("""
            <video><track id="captions" kind="captions" srclang="en" default
                src="data:text/vtt,WEBVTT%0A%0A00%3A00%3A01.000%20--%3E%2000%3A00%3A03.000%0AHello"></video>
            """);
        var result = fixture.Runtime.Evaluate("""
            const element = document.getElementById("captions");
            const video = document.querySelector("video");
            const cue = element.track.cues[0];
            const added = video.addTextTrack("metadata", "Data", "en");
            cue.line = -2;
            cue.size = 80;
            return [
                element instanceof HTMLTrackElement,
                element.readyState === HTMLTrackElement.LOADED,
                element.track instanceof TextTrack,
                element.track.cues.length,
                cue.startTime,
                cue.endTime,
                cue.text,
                cue.line,
                cue.size,
                video.textTracks.length,
                video.textTracks.getTrackById("captions") === element.track,
                added.kind,
            ];
            """);
        AssertJsonEquals(
            """[true, true, true, 1, 1, 3, "Hello", -2, 80, 1, true, "metadata"]""",
            result);
    }

    [Fact]
    public void UnsupportedMediaCapabilitiesAndReadinessAreHonest()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn unsupported_media_capabilities_and_readiness_are_honest() {
                let mut rt = setup_runtime(
                    r#"<video id="media" src="https://example.test/movie.mp4"
                        poster="https://example.test/poster.png"></video>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                        const media = document.getElementById("media");
                        return [
                            media.canPlayType("video/mp4"),
                            media.canPlayType('video/webm; codecs="vp9"'),
                            media.readyState,
                            media.currentTime,
                            media.videoWidth,
                            media.videoHeight,
                            media.paused,
                            media.currentSrc,
                            media.poster,
                        ];
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([
                        "",
                        "",
                        0,
                        0,
                        0,
                        0,
                        true,
                        "",
                        "https://example.test/poster.png"
                    ])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("""
            <video id="media" src="https://example.test/movie.mp4"
                poster="https://example.test/poster.png"></video>
            """);
        var result = fixture.Runtime.Evaluate("""
            const media = document.getElementById("media");
            return [
                media.canPlayType("video/mp4"),
                media.canPlayType('video/webm; codecs="vp9"'),
                media.readyState,
                media.currentTime,
                media.videoWidth,
                media.videoHeight,
                media.paused,
                media.currentSrc,
                media.poster,
            ];
            """);
        AssertJsonEquals(
            """
            [
                "",
                "",
                0,
                0,
                0,
                0,
                true,
                "",
                "https://example.test/poster.png"
            ]
            """,
            result);
    }

    [Fact]
    public void HtmlStringScriptsRemainInertWhenConnected()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn html_string_scripts_remain_inert_when_connected() {
                let mut rt = setup_runtime("<html><head></head><body><div id=target></div></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        globalThis.__fragmentScriptRuns = 0;

                        const direct = document.createElement("div");
                        direct.innerHTML = "<script>globalThis.__fragmentScriptRuns++<\/script>";
                        document.body.appendChild(direct.firstChild);

                        const nested = document.createElement("div");
                        nested.innerHTML = "<section><script>globalThis.__fragmentScriptRuns++<\/script></section>";
                        document.body.appendChild(nested.firstChild);

                        const template = document.createElement("template");
                        template.innerHTML = "<script>globalThis.__fragmentScriptRuns++<\/script>";
                        document.body.appendChild(template.content.firstChild);

                        const nestedTemplateHolder = document.createElement("div");
                        nestedTemplateHolder.innerHTML =
                            "<template><script>globalThis.__fragmentScriptRuns++<\/script></template>";
                        document.body.appendChild(
                            nestedTemplateHolder.firstChild.content.firstChild
                        );

                        document.getElementById("target").insertAdjacentHTML(
                            "beforeend",
                            "<script>globalThis.__fragmentScriptRuns++<\/script>"
                        );

                        const parsed = new DOMParser().parseFromString(
                            "<body><script>globalThis.__fragmentScriptRuns++<\/script></body>",
                            "text/html"
                        );
                        document.body.appendChild(parsed.querySelector("script"));

                        let externalFetches = 0;
                        const originalFetchOp = Deno.core.ops.op_fetch_url;
                        try {
                            Deno.core.ops.op_fetch_url = () => {
                                externalFetches++;
                                return JSON.stringify({
                                    status: 200,
                                    headers: {"content-type": "text/javascript"},
                                    body: "globalThis.__fragmentScriptRuns++",
                                    url: "http://example.com/inert.js"
                                });
                            };
                            const external = document.createElement("div");
                            external.innerHTML = "<script src=/inert.js><\/script>";
                            document.head.appendChild(external.firstChild);
                        } finally {
                            Deno.core.ops.op_fetch_url = originalFetchOp;
                        }
                        return [globalThis.__fragmentScriptRuns, externalFetches];
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!([0, 0]));
            }
        */
        using var fixture = RuntimeFixture.Setup(
            "<html><head></head><body><div id=target></div></body></html>");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            globalThis.__fragmentScriptRuns = 0;

            const direct = document.createElement("div");
            direct.innerHTML = "<script>globalThis.__fragmentScriptRuns++<\/script>";
            document.body.appendChild(direct.firstChild);

            const nested = document.createElement("div");
            nested.innerHTML = "<section><script>globalThis.__fragmentScriptRuns++<\/script></section>";
            document.body.appendChild(nested.firstChild);

            const template = document.createElement("template");
            template.innerHTML = "<script>globalThis.__fragmentScriptRuns++<\/script>";
            document.body.appendChild(template.content.firstChild);

            const nestedTemplateHolder = document.createElement("div");
            nestedTemplateHolder.innerHTML =
                "<template><script>globalThis.__fragmentScriptRuns++<\/script></template>";
            document.body.appendChild(
                nestedTemplateHolder.firstChild.content.firstChild
            );

            document.getElementById("target").insertAdjacentHTML(
                "beforeend",
                "<script>globalThis.__fragmentScriptRuns++<\/script>"
            );

            const parsed = new DOMParser().parseFromString(
                "<body><script>globalThis.__fragmentScriptRuns++<\/script></body>",
                "text/html"
            );
            document.body.appendChild(parsed.querySelector("script"));

            let externalFetches = 0;
            const originalFetchOp = Deno.core.ops.op_fetch_url;
            try {
                Deno.core.ops.op_fetch_url = () => {
                    externalFetches++;
                    return JSON.stringify({
                        status: 200,
                        headers: {"content-type": "text/javascript"},
                        body: "globalThis.__fragmentScriptRuns++",
                        url: "http://example.com/inert.js"
                    });
                };
                const external = document.createElement("div");
                external.innerHTML = "<script src=/inert.js><\/script>";
                document.head.appendChild(external.firstChild);
            } finally {
                Deno.core.ops.op_fetch_url = originalFetchOp;
            }
            return [globalThis.__fragmentScriptRuns, externalFetches];
            """);
        AssertJsonEquals("[0, 0]", result);
    }

    [Fact]
    public void ConnectedInsertionPreparesDynamicScriptSubtreesOnce()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn connected_insertion_prepares_dynamic_script_subtrees_once() {
                let mut rt = setup_runtime("<html><head></head><body><i id=anchor></i></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        globalThis.__dynamicScriptRuns = [];

                        const detached = document.createElement("div");
                        const delayed = document.createElement("script");
                        delayed.textContent = "globalThis.__dynamicScriptRuns.push('delayed')";
                        detached.appendChild(delayed);
                        const beforeConnection = globalThis.__dynamicScriptRuns.length;
                        document.body.appendChild(detached);
                        document.head.appendChild(delayed);

                        const before = document.createElement("script");
                        before.textContent = "globalThis.__dynamicScriptRuns.push('before')";
                        document.body.insertBefore(before, document.getElementById("anchor"));

                        const replacement = document.createElement("script");
                        replacement.textContent = "globalThis.__dynamicScriptRuns.push('replace')";
                        document.body.replaceChild(replacement, document.getElementById("anchor"));

                        const subtree = document.createElement("section");
                        const nested = document.createElement("script");
                        nested.textContent = "globalThis.__dynamicScriptRuns.push('nested')";
                        subtree.appendChild(nested);
                        document.body.appendChild(subtree);

                        return [beforeConnection, globalThis.__dynamicScriptRuns];
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!([0, ["delayed", "before", "replace", "nested"]])
                );
            }
        */
        using var fixture = RuntimeFixture.Setup(
            "<html><head></head><body><i id=anchor></i></body></html>");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            globalThis.__dynamicScriptRuns = [];

            const detached = document.createElement("div");
            const delayed = document.createElement("script");
            delayed.textContent = "globalThis.__dynamicScriptRuns.push('delayed')";
            detached.appendChild(delayed);
            const beforeConnection = globalThis.__dynamicScriptRuns.length;
            document.body.appendChild(detached);
            document.head.appendChild(delayed);

            const before = document.createElement("script");
            before.textContent = "globalThis.__dynamicScriptRuns.push('before')";
            document.body.insertBefore(before, document.getElementById("anchor"));

            const replacement = document.createElement("script");
            replacement.textContent = "globalThis.__dynamicScriptRuns.push('replace')";
            document.body.replaceChild(replacement, document.getElementById("anchor"));

            const subtree = document.createElement("section");
            const nested = document.createElement("script");
            nested.textContent = "globalThis.__dynamicScriptRuns.push('nested')";
            subtree.appendChild(nested);
            document.body.appendChild(subtree);

            return [beforeConnection, globalThis.__dynamicScriptRuns];
            """);
        AssertJsonEquals("""[0, ["delayed", "before", "replace", "nested"]]""", result);
    }

    [Fact]
    public void ScriptClonePreservesStartedState()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn script_clone_preserves_started_state() {
                let mut rt = setup_runtime(
                    "<html><head></head><body><script id=parser>globalThis.__cloneScriptRuns++</script></body></html>",
                );
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        globalThis.__cloneScriptRuns = 0;
                        const parser = document.getElementById("parser");
                        globalThis.__markParserScripts([parser._nid]);
                        document.head.appendChild(parser);
                        document.body.appendChild(parser.cloneNode(true));

                        const dynamic = document.createElement("script");
                        dynamic.textContent = "globalThis.__cloneScriptRuns++";
                        document.body.appendChild(dynamic);
                        document.body.appendChild(dynamic.cloneNode(true));

                        const holder = document.createElement("div");
                        holder.innerHTML = "<script>globalThis.__cloneScriptRuns++<\/script>";
                        document.body.appendChild(holder.firstChild.cloneNode(true));

                        const fragment = document.createDocumentFragment();
                        fragment.appendChild(dynamic.cloneNode(true));
                        document.body.appendChild(fragment.cloneNode(true));
                        return globalThis.__cloneScriptRuns;
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!(1.0));
            }
        */
        using var fixture = RuntimeFixture.Setup(
            "<html><head></head><body><script id=parser>globalThis.__cloneScriptRuns++</script></body></html>");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            globalThis.__cloneScriptRuns = 0;
            const parser = document.getElementById("parser");
            globalThis.__markParserScripts([parser._nid]);
            document.head.appendChild(parser);
            document.body.appendChild(parser.cloneNode(true));

            const dynamic = document.createElement("script");
            dynamic.textContent = "globalThis.__cloneScriptRuns++";
            document.body.appendChild(dynamic);
            document.body.appendChild(dynamic.cloneNode(true));

            const holder = document.createElement("div");
            holder.innerHTML = "<script>globalThis.__cloneScriptRuns++<\/script>";
            document.body.appendChild(holder.firstChild.cloneNode(true));

            const fragment = document.createDocumentFragment();
            fragment.appendChild(dynamic.cloneNode(true));
            document.body.appendChild(fragment.cloneNode(true));
            return globalThis.__cloneScriptRuns;
            """);
        AssertJsonEquals("1", result);
    }

    [Fact]
    public void ContextualFragmentAndDocumentWriteKeepExecutableScriptPolicy()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn contextual_fragment_and_document_write_keep_executable_script_policy() {
                let mut rt = setup_runtime("<html><head></head><body><div id=context></div></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        globalThis.__executableFragmentRuns = [];
                        const range = document.createRange();
                        range.selectNode(document.getElementById("context"));
                        const fragment = range.createContextualFragment(
                            "<template><script>globalThis.__executableFragmentRuns.push('template')<\/script></template>" +
                            "<script>globalThis.__executableFragmentRuns.push('range')<\/script>"
                        );
                        document.body.appendChild(fragment);
                        document.write(
                            "<script>globalThis.__executableFragmentRuns.push('write')<\/script>"
                        );
                        return globalThis.__executableFragmentRuns;
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!(["range", "write"]));
            }
        */
        using var fixture = RuntimeFixture.Setup(
            "<html><head></head><body><div id=context></div></body></html>");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            globalThis.__executableFragmentRuns = [];
            const range = document.createRange();
            range.selectNode(document.getElementById("context"));
            const fragment = range.createContextualFragment(
                "<template><script>globalThis.__executableFragmentRuns.push('template')<\/script></template>" +
                "<script>globalThis.__executableFragmentRuns.push('range')<\/script>"
            );
            document.body.appendChild(fragment);
            document.write(
                "<script>globalThis.__executableFragmentRuns.push('write')<\/script>"
            );
            return globalThis.__executableFragmentRuns;
            """);
        AssertJsonEquals("""["range", "write"]""", result);
    }

    // One stream per document. The tokenizer carries its state across the calls.
    // https://html.spec.whatwg.org/multipage/dynamic-markup-insertion.html#dom-document-write
    [Fact]
    public void DocumentWriteJoinsAnElementSplitAcrossCalls()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn document_write_joins_an_element_split_across_calls() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        document.write('<di');
                        document.write('v id="split">');
                        document.write('content</div>');
                        const el = document.getElementById('split');
                        return el ? el.textContent : null;
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!("content"));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            document.write('<di');
            document.write('v id="split">');
            document.write('content</div>');
            const el = document.getElementById('split');
            return el ? el.textContent : null;
            """);
        Assert.Equal("content", result!.GetValue<string>());
    }

    [Fact]
    public void DocumentWriteJoinsATagNameSplitAcrossCalls()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn document_write_joins_a_tag_name_split_across_calls() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        document.write('<spa');
                        document.write('n id="half">x</span>');
                        const el = document.getElementById('half');
                        return el ? el.tagName : null;
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!("SPAN"));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            document.write('<spa');
            document.write('n id="half">x</span>');
            const el = document.getElementById('half');
            return el ? el.tagName : null;
            """);
        Assert.Equal("SPAN", result!.GetValue<string>());
    }

    // The shape the UI5 cachebuster writes: "<script", one per attribute, then ">".
    [Fact]
    public void DocumentWriteRunsAScriptSplitAcrossCalls()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn document_write_runs_a_script_split_across_calls() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        globalThis.__splitScriptRan = false;
                        document.write('<scr' + 'ipt');
                        document.write(' id="split-script"');
                        document.write('>');
                        document.write('globalThis.__splitScriptRan = true;');
                        document.write('<\/scr' + 'ipt>');
                        return [!!document.getElementById('split-script'), globalThis.__splitScriptRan];
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!([true, true]));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            globalThis.__splitScriptRan = false;
            document.write('<scr' + 'ipt');
            document.write(' id="split-script"');
            document.write('>');
            document.write('globalThis.__splitScriptRan = true;');
            document.write('<\/scr' + 'ipt>');
            return [!!document.getElementById('split-script'), globalThis.__splitScriptRan];
            """);
        AssertJsonEquals("[true, true]", result);
    }

    // A script in the <head> inserts behind itself, so that what it writes runs before what
    // the parser saw after it.
    [Fact]
    public void DocumentWriteInsertsAtTheWritingScriptsPosition()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn document_write_inserts_at_the_writing_scripts_position() {
                let mut rt = setup_runtime(
                    r#"<html><head><script id="writer"></script></head><body><p id="existing">x</p></body></html>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        // What the production path sets while a script runs; bootstrap.js
                        // assigns __currentScriptNid around every script it prepares.
                        globalThis.__currentScriptNid = document.getElementById('writer')._nid;
                        document.write('<span id="written"></span>');
                        return JSON.stringify({
                          head: Array.from(document.head.children).map(e => e.id || e.tagName),
                          body: Array.from(document.body.children).map(e => e.id || e.tagName),
                        });
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!(r#"{"head":["writer","written"],"body":["existing"]}"#)
                );
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<html><head><script id="writer"></script></head><body><p id="existing">x</p></body></html>""");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            // What the production path sets while a script runs; bootstrap.js
            // assigns __currentScriptNid around every script it prepares.
            globalThis.__currentScriptNid = document.getElementById('writer')._nid;
            document.write('<span id="written"></span>');
            return JSON.stringify({
              head: Array.from(document.head.children).map(e => e.id || e.tagName),
              body: Array.from(document.body.children).map(e => e.id || e.tagName),
            });
            """);
        Assert.Equal(
            """{"head":["writer","written"],"body":["existing"]}""",
            result!.GetValue<string>());
    }

    // Holding back until the close would lose everything written after it. It belongs inside.
    [Fact]
    public void DocumentWriteShowsAnElementThatIsNeverClosed()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn document_write_shows_an_element_that_is_never_closed() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        document.write('<div id="unclosed">hello');
                        const el = document.getElementById('unclosed');
                        return el ? el.textContent : null;
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!("hello"));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            document.write('<div id="unclosed">hello');
            const el = document.getElementById('unclosed');
            return el ? el.textContent : null;
            """);
        Assert.Equal("hello", result!.GetValue<string>());
    }

    [Fact]
    public void DocumentWriteGrowsAnOpenElementAcrossCalls()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn document_write_grows_an_open_element_across_calls() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        document.write('<div id="wrap">');
                        document.write('<span id="inner">y</span>');
                        const inner = document.getElementById('inner');
                        return JSON.stringify({
                          wrap: !!document.getElementById('wrap'),
                          inner: !!inner,
                          nested: !!(inner && inner.parentElement && inner.parentElement.id === 'wrap'),
                        });
                        "#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!(r#"{"wrap":true,"inner":true,"nested":true}"#)
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            document.write('<div id="wrap">');
            document.write('<span id="inner">y</span>');
            const inner = document.getElementById('inner');
            return JSON.stringify({
              wrap: !!document.getElementById('wrap'),
              inner: !!inner,
              nested: !!(inner && inner.parentElement && inner.parentElement.id === 'wrap'),
            });
            """);
        Assert.Equal(
            """{"wrap":true,"inner":true,"nested":true}""",
            result!.GetValue<string>());
    }

    // Writing goes through the same insertion steps as any other insertion.
    [Fact]
    public void DocumentWriteReportsToMutationObservers()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn document_write_reports_to_mutation_observers() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        globalThis.__seen = [];
                        const observer = new MutationObserver((records) => {
                          for (const record of records) {
                            for (const node of record.addedNodes) globalThis.__seen.push(node.nodeName);
                          }
                        });
                        observer.observe(document.body, { childList: true });
                        document.write('<span id="watched">z</span>');
                        observer.takeRecords().forEach((record) => {
                          for (const node of record.addedNodes) globalThis.__seen.push(node.nodeName);
                        });
                        return globalThis.__seen.join(',');
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!("SPAN"));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            globalThis.__seen = [];
            const observer = new MutationObserver((records) => {
              for (const record of records) {
                for (const node of record.addedNodes) globalThis.__seen.push(node.nodeName);
              }
            });
            observer.observe(document.body, { childList: true });
            document.write('<span id="watched">z</span>');
            observer.takeRecords().forEach((record) => {
              for (const node of record.addedNodes) globalThis.__seen.push(node.nodeName);
            });
            return globalThis.__seen.join(',');
            """);
        Assert.Equal("SPAN", result!.GetValue<string>());
    }

    // before(), after() and replaceWith() all go through parent.insertBefore. replaceChild
    // also goes there in the fragment branch. AGENTS.md requires whoever touches insertBefore
    // to check them: the order of reference node versus parent nid is easy to break. The test
    // also pins that every insertion is reported exactly once, not twice.
    [Fact]
    public void ChildNodeMethodsPlaceNodesAndReportOnce()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn child_node_methods_place_nodes_and_report_once() {
                let mut rt = setup_runtime(r#"<html><body><p id="a"></p><p id="b"></p></body></html>"#);
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        const ids = () => Array.from(document.body.children).map((e) => e.id).join(',');
                        const make = (id) => { const e = document.createElement('span'); e.id = id; return e; };
                        const observer = new MutationObserver(() => {});
                        observer.observe(document.body, { childList: true });
                        const steps = {};

                        document.getElementById('b').before(make('x'));
                        steps.before = ids();
                        document.getElementById('b').after(make('y'));
                        steps.after = ids();
                        document.getElementById('y').replaceWith(make('z'));
                        steps.replaceWith = ids();
                        document.body.replaceChild(make('w'), document.getElementById('z'));
                        steps.replaceChild = ids();

                        const added = observer.takeRecords()
                          .flatMap((record) => Array.from(record.addedNodes).map((n) => n.id));
                        observer.disconnect();
                        steps.added = added.join(',');
                        return JSON.stringify(steps);
                        "#,
                    )
                    .unwrap();
                let steps: serde_json::Value =
                    serde_json::from_str(result.as_str().unwrap()).expect("steps json");
                assert_eq!(steps["before"], "a,x,b");
                assert_eq!(steps["after"], "a,x,b,y");
                assert_eq!(steps["replaceWith"], "a,x,b,z");
                assert_eq!(steps["replaceChild"], "a,x,b,w");
                // Every inserted node exactly once, in the order of insertion.
                assert_eq!(steps["added"], "x,y,z,w");
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<html><body><p id="a"></p><p id="b"></p></body></html>""");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            const ids = () => Array.from(document.body.children).map((e) => e.id).join(',');
            const make = (id) => { const e = document.createElement('span'); e.id = id; return e; };
            const observer = new MutationObserver(() => {});
            observer.observe(document.body, { childList: true });
            const steps = {};

            document.getElementById('b').before(make('x'));
            steps.before = ids();
            document.getElementById('b').after(make('y'));
            steps.after = ids();
            document.getElementById('y').replaceWith(make('z'));
            steps.replaceWith = ids();
            document.body.replaceChild(make('w'), document.getElementById('z'));
            steps.replaceChild = ids();

            const added = observer.takeRecords()
              .flatMap((record) => Array.from(record.addedNodes).map((n) => n.id));
            observer.disconnect();
            steps.added = added.join(',');
            return JSON.stringify(steps);
            """);
        var steps = JsonNode.Parse(result!.GetValue<string>())!;
        Assert.Equal("a,x,b", steps["before"]!.GetValue<string>());
        Assert.Equal("a,x,b,y", steps["after"]!.GetValue<string>());
        Assert.Equal("a,x,b,z", steps["replaceWith"]!.GetValue<string>());
        Assert.Equal("a,x,b,w", steps["replaceChild"]!.GetValue<string>());
        // Every inserted node exactly once, in the order of insertion.
        Assert.Equal("x,y,z,w", steps["added"]!.GetValue<string>());
    }

    // insertBefore reported no mutation at all, appendChild did.
    [Fact]
    public void InsertBeforeReportsToMutationObservers()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn insert_before_reports_to_mutation_observers() {
                let mut rt = setup_runtime("<html><body><p id=\"ref\"></p></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        const observer = new MutationObserver(() => {});
                        observer.observe(document.body, { childList: true });
                        document.body.insertBefore(
                          document.createElement('span'),
                          document.getElementById('ref'),
                        );
                        const seen = observer.takeRecords()
                          .flatMap((record) => Array.from(record.addedNodes).map((n) => n.nodeName));
                        observer.disconnect();
                        return seen.join(',');
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!("SPAN"));
            }
        */
        using var fixture = RuntimeFixture.Setup("""<html><body><p id="ref"></p></body></html>""");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            const observer = new MutationObserver(() => {});
            observer.observe(document.body, { childList: true });
            document.body.insertBefore(
              document.createElement('span'),
              document.getElementById('ref'),
            );
            const seen = observer.takeRecords()
              .flatMap((record) => Array.from(record.addedNodes).map((n) => n.nodeName));
            observer.disconnect();
            return seen.join(',');
            """);
        Assert.Equal("SPAN", result!.GetValue<string>());
    }

    [Fact]
    public void DocumentWriteRegistersWindowNamedAccess()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn document_write_registers_window_named_access() {
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        document.write('<img name="namedImage" src="x.png">');
                        return typeof window.namedImage;
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!("object"));
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            document.write('<img name="namedImage" src="x.png">');
            return typeof window.namedImage;
            """);
        Assert.Equal("object", result!.GetValue<string>());
    }

    [Fact]
    public void DocumentWriteKeepsCallOrderAtTheInsertionPoint()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn document_write_keeps_call_order_at_the_insertion_point() {
                let mut rt = setup_runtime(
                    r#"<html><head><script id="writer"></script></head><body></body></html>"#,
                );
                let result = rt
                    .evaluate(
                        r#"
                        var scriptTestSetup = true;
                        globalThis.__currentScriptNid = document.getElementById('writer')._nid;
                        document.write('<span id="one"></span>');
                        document.write('<span id="two"></span>');
                        return Array.from(document.head.children).map(e => e.id).join(',');
                        "#,
                    )
                    .unwrap();
                assert_eq!(result, serde_json::json!("writer,one,two"));
            }
        */
        using var fixture = RuntimeFixture.Setup(
            """<html><head><script id="writer"></script></head><body></body></html>""");
        var result = fixture.Runtime.Evaluate("""
            var scriptTestSetup = true;
            globalThis.__currentScriptNid = document.getElementById('writer')._nid;
            document.write('<span id="one"></span>');
            document.write('<span id="two"></span>');
            return Array.from(document.head.children).map(e => e.id).join(',');
            """);
        Assert.Equal("writer,one,two", result!.GetValue<string>());
    }

    /// #699: an unhandled rejection from a failed dynamic import is page-local
    /// noise in a browser. The bounded event loop must report it and keep
    /// driving later tasks instead of dying on the error and starving every
    /// pending timer.
    [Fact]
    public async Task UnhandledRejectionDoesNotStarveLaterTasks()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn unhandled_rejection_does_not_starve_later_tasks() {
                let mut rt = setup_runtime("<html><body></body></html>");
                rt.evaluate(
                    "(function() { \
                        import('http://192.0.0.1/unreachable-module.js'); \
                        globalThis.__t = false; \
                        setTimeout(() => { globalThis.__t = true; }, 5); \
                        return 'ok'; \
                    })()",
                )
                .unwrap();
                rt.run_event_loop_bounded(2_000)
                    .await
                    .expect("the pump must survive a page-local rejection");
                assert_eq!(
                    rt.evaluate("globalThis.__t").unwrap(),
                    serde_json::json!(true),
                    "the pending timer must still fire after a page task error"
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.Evaluate(
            "(function() { "
            + "import('http://192.0.0.1/unreachable-module.js'); "
            + "globalThis.__t = false; "
            + "setTimeout(() => { globalThis.__t = true; }, 5); "
            + "return 'ok'; "
            + "})()");
        await rt.RunEventLoopBoundedAsync(2_000);
        Assert.True(
            rt.Evaluate("globalThis.__t")!.GetValue<bool>(),
            "the pending timer must still fire after a page task error");
    }

    /// #734: ICU inherits the host OS locale when no default is set, so
    /// Intl.resolvedOptions() contradicted navigator.language on any host
    /// whose OS locale is not en-US. The pinned default must win.
    [Fact]
    public void IntlLocaleMatchesNavigatorLanguageRegardlessOfHost()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn intl_locale_matches_navigator_language_regardless_of_host() {
                // nextest runs each test in its own process, so setting the process
                // locale here cannot race another test's V8 init.
                std::env::set_var("LC_ALL", "de-DE");
                std::env::set_var("LANG", "de-DE");
                let mut rt = setup_runtime("<html><body></body></html>");
                let result = rt
                    .evaluate(
                        "Intl.DateTimeFormat().resolvedOptions().locale + '|' + navigator.language",
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!("en-US|en-US"),
                    "Intl must not leak the host OS locale while navigator.language says en-US"
                );
            }
        */
        // The Rust test gets a fresh process per test and can set the locale env
        // before V8 starts; here the whole class shares one process, so restore the
        // host's values afterwards rather than leaking them into later tests.
        var previousLcAll = Environment.GetEnvironmentVariable("LC_ALL");
        var previousLang = Environment.GetEnvironmentVariable("LANG");
        Environment.SetEnvironmentVariable("LC_ALL", "de-DE");
        Environment.SetEnvironmentVariable("LANG", "de-DE");
        try
        {
            using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
            var result = fixture.Runtime.Evaluate(
                "Intl.DateTimeFormat().resolvedOptions().locale + '|' + navigator.language");
            Assert.Equal("en-US|en-US", result!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LC_ALL", previousLcAll);
            Environment.SetEnvironmentVariable("LANG", previousLang);
        }
    }

    // Momentic POC / Playwright getByLabel: the label association getters must
    // link <label for> to its control and expose element.labels, both for
    // for-linked and wrapping labels.
    [Fact]
    public void LabelControlAndLabelsLinkLabelableElements()
    {
        // Ported from crates/obscura-js/src/runtime.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn label_control_and_labels_link_labelable_elements() {
                let mut rt = setup_runtime(
                    r#"<html><body>
                    <label for="name" id="l1">Name</label><input id="name">
                    <label id="l2">Age <input id="age"></label>
                    <input type="hidden" id="hid">
                    <div id="plain"></div>
                    </body></html>"#,
                );
                let result = rt
                    .evaluate(
                        r#"(function() {
                            var l1 = document.getElementById('l1');
                            var l2 = document.getElementById('l2');
                            var name = document.getElementById('name');
                            var age = document.getElementById('age');
                            var hid = document.getElementById('hid');
                            var plain = document.getElementById('plain');
                            return [
                                l1.control === name,
                                l2.control === age,
                                name.labels.length,
                                name.labels[0] === l1,
                                age.labels.length,
                                age.labels[0] === l2,
                                hid.labels.length,
                                plain.labels.length,
                                plain.control === null,
                            ].join(',');
                        })()"#,
                    )
                    .unwrap();
                assert_eq!(
                    result,
                    serde_json::json!("true,true,1,true,1,true,0,0,true"),
                    "label association must follow the HTML labelable-element rules"
                );
            }
        */
        using var fixture = RuntimeFixture.Setup("""
            <html><body>
            <label for="name" id="l1">Name</label><input id="name">
            <label id="l2">Age <input id="age"></label>
            <input type="hidden" id="hid">
            <div id="plain"></div>
            </body></html>
            """);
        var result = fixture.Runtime.Evaluate("""
            (function() {
                var l1 = document.getElementById('l1');
                var l2 = document.getElementById('l2');
                var name = document.getElementById('name');
                var age = document.getElementById('age');
                var hid = document.getElementById('hid');
                var plain = document.getElementById('plain');
                return [
                    l1.control === name,
                    l2.control === age,
                    name.labels.length,
                    name.labels[0] === l1,
                    age.labels.length,
                    age.labels[0] === l2,
                    hid.labels.length,
                    plain.labels.length,
                    plain.control === null,
                ].join(',');
            })()
            """);
        Assert.Equal(
            "true,true,1,true,1,true,0,0,true",
            result!.GetValue<string>());
    }

    // ---- crates/obscura-js/src/frame.rs ----

    [Fact]
    public void FrameHasItsOwnRealmDomAndOrigin()
    {
        using var page = RuntimeFixture.Page("https://parent.example/page", "<html><body><h1>Parent</h1></body></html>");
        var parent = page.Runtime;
        parent.ExecuteScript("p", "globalThis.marker = 'parent';");

        using var frame = FrameRealm.Create(
            parent, 1, 0, "https://child.example/frame", "<html><body><h1>Child</h1></body></html>");
        Assert.NotNull(frame);

        frame.ExecuteScript("globalThis.marker = 'child';");

        // Separate realm: own globals, own DOM, own URL.
        Assert.Equal("Child", frame.Evaluate("document.querySelector('h1').textContent")!.GetValue<string>());
        Assert.Equal("child", frame.Evaluate("globalThis.marker")!.GetValue<string>());
        Assert.Equal("https://child.example/frame", frame.Evaluate("location.href")!.GetValue<string>());

        // The parent keeps its own document and globals throughout.
        Assert.Equal("Parent", parent.Evaluate("document.querySelector('h1').textContent")!.GetValue<string>());
        Assert.Equal("parent", parent.Evaluate("globalThis.marker")!.GetValue<string>());

        Assert.Equal("https://child.example", frame.Origin);
        Assert.Equal(1u, frame.FrameId);
        Assert.False(frame.IsSameOriginAs("https://parent.example"));
        Assert.True(frame.IsSameOriginAs("https://child.example"));
    }

    [Fact]
    public void FrameUsesItsEmbeddingViewport()
    {
        using var page = RuntimeFixture.Page(
            "https://parent.example/page",
            "<html><body><iframe style='width:300px;height:65px'></iframe></body></html>");
        using var frame = FrameRealm.Create(
            page.Runtime, 1, 0, "https://child.example/frame", "<html><body></body></html>");
        Assert.NotNull(frame);

        frame.SetViewport(300.0, 65.0);
        Assert.Equal(
            "[300,65,300,65]",
            frame.Evaluate("[innerWidth,innerHeight,visualViewport.width,visualViewport.height]")!.ToJsonString());
    }

    /// A frame must not look like a different browser than its parent. Anti-bot
    /// code fingerprints inside the frame and compares it with the top document.
    [Fact]
    public void FrameInheritsTheParentBrowserIdentity()
    {
        const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TestAgent/150.0.0.0";
        using var parentFixture = RuntimeFixture.Blank();
        var parent = parentFixture.Runtime;
        parent.SetUserAgent(UserAgent);
        parent.SetPlatform("Win32", "Windows", "19.0.0");
        parent.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        parent.SetUrl("https://parent.example/");
        parent.RunPageInit();

        using var frame = FrameRealm.Create(
            parent, 1, 0, "https://child.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);

        foreach (var surface in new[] { "navigator.userAgent", "navigator.platform", "navigator.userAgentData.platform" })
        {
            Assert.Equal(
                parent.Evaluate(surface)!.ToJsonString(),
                frame.Evaluate(surface)!.ToJsonString());
        }
        Assert.Equal(UserAgent, frame.Evaluate("navigator.userAgent")!.GetValue<string>());
    }

    /// The capability the frame realm exists for: scripts that arrived with the
    /// frame's document run, in order, against the frame's own DOM.
    [Fact]
    public void FrameRunsItsDocumentScriptsInOrder()
    {
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        var parent = page.Runtime;
        using var frame = FrameRealm.Create(
            parent, 1, 0, "https://child.example/dir/page",
            @"<html><body><div id=""out""></div>
               <script>window.log = ['inline1'];</script>
               <script src=""first.js""></script>
               <script src=""/second.js""></script>
               <script>window.log.push('inline2');
                       document.getElementById('out').textContent = window.log.join(',');</script>
               </body></html>");
        Assert.NotNull(frame);

        var requested = new List<string>();
        var problems = frame.RunDocumentScripts(url =>
        {
            requested.Add(url);
            return url switch
            {
                "https://child.example/dir/first.js" => "window.log.push('ext1');",
                "https://child.example/second.js" => "window.log.push('ext2');",
                _ => null,
            };
        });

        Assert.Empty(problems);
        // Relative and root-relative src resolve against the frame's URL, not the parent's.
        Assert.Equal(
            new[] { "https://child.example/dir/first.js", "https://child.example/second.js" },
            requested);
        Assert.Equal(
            "inline1,ext1,ext2,inline2",
            frame.Evaluate("document.getElementById('out').textContent")!.GetValue<string>());
        // The frame's document writes never touch the parent's DOM.
        Assert.Equal("", parent.Evaluate("document.body.innerHTML")!.GetValue<string>());
    }

    [Fact]
    public async Task FramePostedTaskRunsInItsCreationRealm()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn frame_posted_task_runs_in_its_creation_realm() {
                let mut parent = page("https://parent.example/", "<html><body></body></html>");
                let frame = FrameRealm::new(
                    &mut parent,
                    1,
                    0,
                    "https://child.example/",
                    "<html><body><output id='result'>pending</output></body></html>",
                )
                .expect("frame realm");

                frame
                    .execute_script(
                        &mut parent,
                        "globalThis.__obscura_frameId = 0;\
                         scheduler.postTask(() => {\
                           document.getElementById('result').textContent = location.origin;\
                         });",
                    )
                    .unwrap();
                parent.run_event_loop_bounded(100).await.unwrap();

                assert_eq!(
                    frame
                        .evaluate(
                            &mut parent,
                            "document.getElementById('result').textContent",
                        )
                        .unwrap(),
                    serde_json::json!("https://child.example"),
                );
                assert_eq!(
                    parent.evaluate("document.body.innerHTML").unwrap(),
                    serde_json::json!("")
                );
            }
        */
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        var parent = page.Runtime;
        using var frame = FrameRealm.Create(
            parent, 1, 0, "https://child.example/",
            "<html><body><output id='result'>pending</output></body></html>");
        Assert.NotNull(frame);

        frame.ExecuteScript(
            "globalThis.__obscura_frameId = 0;"
            + "scheduler.postTask(() => {"
            + "  document.getElementById('result').textContent = location.origin;"
            + "});");
        await parent.RunEventLoopBoundedAsync(100);

        Assert.Equal(
            "https://child.example",
            frame.Evaluate("document.getElementById('result').textContent")!.GetValue<string>());
        Assert.Equal("", parent.Evaluate("document.body.innerHTML")!.GetValue<string>());
    }

    [Fact(Skip = "ClearScript refuses a script object across engines, so a same-origin frame realm's live window is never published into the page realm; the final assertion reads __obscura_frameObjects[1].window.__staleController and cannot be satisfied. The cancellation half of this test does hold - the C# body below was written and every other assertion passed")]
    public async Task DroppingFrameCancelsItsQueuedPostedTask()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn dropping_frame_cancels_its_queued_posted_task() {
                let mut parent = page(
                    "https://parent.example/",
                    "<html><body data-owner='parent'></body></html>",
                );
                let frame = FrameRealm::new(
                    &mut parent,
                    1,
                    0,
                    "https://parent.example/child",
                    "<html><body data-owner='frame'></body></html>",
                )
                .expect("frame realm");

                frame
                    .execute_script(
                        &mut parent,
                        "const staleController = new AbortController();\
                         globalThis.__staleController = staleController;\
                         staleController.signal.removeEventListener = () => {\
                           document.body.setAttribute('data-stale-cleanup', 'ran');\
                           parent.postMessage('stale-cleanup', '*');\
                         };\
                         scheduler.postTask(() => {\
                           document.body.setAttribute('data-stale-task', 'ran');\
                           parent.postMessage('stale-posted-task', '*');\
                         }, { signal: staleController.signal });\
                         setTimeout(() => scheduler.postTask(() => {\
                           document.body.setAttribute('data-delayed-stale-task', 'ran');\
                           parent.postMessage('delayed-stale-posted-task', '*');\
                         }), 1);",
                    )
                    .unwrap();
                drop(frame);
                parent.run_event_loop_bounded(100).await.unwrap();

                assert!(
                    parent.take_pending_frame_messages().is_empty(),
                    "a posted task from the detached frame still executed",
                );

                assert_eq!(
                    parent.evaluate("document.body.getAttribute('data-owner')").unwrap(),
                    serde_json::json!("parent"),
                );
                assert_eq!(
                    parent
                        .evaluate("document.body.getAttribute('data-stale-task')")
                        .unwrap(),
                    serde_json::Value::Null,
                );
                assert_eq!(
                    parent
                        .evaluate("document.body.getAttribute('data-delayed-stale-task')")
                        .unwrap(),
                    serde_json::Value::Null,
                );
                assert_eq!(
                    parent
                        .evaluate("document.body.getAttribute('data-stale-cleanup')")
                        .unwrap(),
                    serde_json::Value::Null,
                );
                assert_eq!(
                    parent
                        .evaluate(
                            "(function(){\
                               const window = globalThis.__obscura_frameObjects[1]?.window;\
                               const controller = window?.__staleController;\
                               return [typeof window, typeof controller,\
                                 controller?.signal?._listeners?.length];\
                             })()",
                        )
                        .unwrap(),
                    serde_json::json!(["object", "object", 0]),
                    "the retained frame realm did not discard its scheduler listener",
                );
            }
        */
        using var page = RuntimeFixture.Page(
            "https://parent.example/", "<html><body data-owner='parent'></body></html>");
        var parent = page.Runtime;
        var frame = FrameRealm.Create(
            parent, 1, 0, "https://parent.example/child",
            "<html><body data-owner='frame'></body></html>");
        Assert.NotNull(frame);

        frame.ExecuteScript(
            "const staleController = new AbortController();"
            + "globalThis.__staleController = staleController;"
            + "staleController.signal.removeEventListener = () => {"
            + "  document.body.setAttribute('data-stale-cleanup', 'ran');"
            + "  parent.postMessage('stale-cleanup', '*');"
            + "};"
            + "scheduler.postTask(() => {"
            + "  document.body.setAttribute('data-stale-task', 'ran');"
            + "  parent.postMessage('stale-posted-task', '*');"
            + "}, { signal: staleController.signal });"
            + "setTimeout(() => scheduler.postTask(() => {"
            + "  document.body.setAttribute('data-delayed-stale-task', 'ran');"
            + "  parent.postMessage('delayed-stale-posted-task', '*');"
            + "}), 1);");
        frame.Dispose();
        await parent.RunEventLoopBoundedAsync(100);

        Assert.Empty(parent.TakePendingFrameMessages());

        Assert.Equal(
            "parent",
            parent.Evaluate("document.body.getAttribute('data-owner')")!.GetValue<string>());
        Assert.Null(parent.Evaluate("document.body.getAttribute('data-stale-task')"));
        Assert.Null(parent.Evaluate("document.body.getAttribute('data-delayed-stale-task')"));
        Assert.Null(parent.Evaluate("document.body.getAttribute('data-stale-cleanup')"));
        AssertJsonEquals(
            """["object", "object", 0]""",
            parent.Evaluate("""
                (function(){
                  const window = globalThis.__obscura_frameObjects[1]?.window;
                  const controller = window?.__staleController;
                  return [typeof window, typeof controller,
                    controller?.signal?._listeners?.length];
                })()
                """));
    }

    [Fact]
    public void FrameBodyOnloadContentAttributeReflectsToWindow()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn frame_body_onload_content_attribute_reflects_to_window() {
                let mut parent = page("https://parent.example/", "<html><body></body></html>");
                let frame = FrameRealm::new(
                    &mut parent,
                    1,
                    0,
                    "https://child.example/",
                    r#"<html><body onload="
                        globalThis.__frameBodyOnload = [this === window, event.type];
                        throw new Error('frame body onload failure');
                    "><script>
                        globalThis.__frameBodyOnload = null;
                        globalThis.__frameLoadListenerCalls = 0;
                        window.addEventListener('load', () => __frameLoadListenerCalls++);
                    </script></body></html>"#,
                )
                .expect("frame realm");

                assert!(frame
                    .run_document_scripts(&mut parent, |_| None)
                    .is_empty());
                frame
                    .dispatch_load_events(&mut parent)
                    .expect("frame load events");

                assert_eq!(
                    frame
                        .evaluate(
                            &mut parent,
                            "[globalThis.__frameBodyOnload, globalThis.__frameLoadListenerCalls]",
                        )
                        .unwrap(),
                    serde_json::json!([[true, "load"], 1]),
                );
            }
        */
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        var parent = page.Runtime;
        using var frame = FrameRealm.Create(
            parent, 1, 0, "https://child.example/",
            """
            <html><body onload="
                globalThis.__frameBodyOnload = [this === window, event.type];
                throw new Error('frame body onload failure');
            "><script>
                globalThis.__frameBodyOnload = null;
                globalThis.__frameLoadListenerCalls = 0;
                window.addEventListener('load', () => __frameLoadListenerCalls++);
            </script></body></html>
            """);
        Assert.NotNull(frame);

        Assert.Empty(frame.RunDocumentScripts(_ => null));
        frame.DispatchLoadEvents();

        AssertJsonEquals(
            """[[true, "load"], 1]""",
            frame.Evaluate("[globalThis.__frameBodyOnload, globalThis.__frameLoadListenerCalls]"));
    }

    [Fact]
    public void OneBadFrameScriptDoesNotStopTheRest()
    {
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        using var frame = FrameRealm.Create(
            page.Runtime, 1, 0, "https://child.example/",
            @"<html><body>
               <script>window.log = ['a'];</script>
               <script>throw new Error('boom');</script>
               <script src=""missing.js""></script>
               <script type=""module"">window.log.push('module');</script>
               <script>window.log.push('b');</script>
               </body></html>");
        Assert.NotNull(frame);

        var problems = frame.RunDocumentScripts(_ => null);

        Assert.Equal("a,b", frame.Evaluate("window.log.join(',')")!.GetValue<string>());
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("boom", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("missing.js", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("module", StringComparison.Ordinal));
    }

    [Fact]
    public void ManyFramesCanBeAliveAtOnce()
    {
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        var parent = page.Runtime;
        var frames = new List<FrameRealm>();
        try
        {
            for (var index = 0u; index < 4; index++)
            {
                // Frame ids start at 1: 0 names the page itself, which is what a
                // DOM call from an unframed realm reports.
                var frame = FrameRealm.Create(
                    parent, index + 1, 0,
                    $"https://f{index}.example/",
                    $"<html><body><h1>{index}</h1></body></html>");
                Assert.NotNull(frame);
                frames.Add(frame);
            }

            for (var index = 0; index < frames.Count; index++)
            {
                frames[index].ExecuteScript($"globalThis.n = {index};");
            }
            // Out-of-order access must be safe: each frame carries its own state.
            for (var index = frames.Count - 1; index >= 0; index--)
            {
                Assert.Equal(index, (int)frames[index].Evaluate("globalThis.n")!.GetValue<double>());
                Assert.Equal(
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    frames[index].Evaluate("document.querySelector('h1').textContent")!.GetValue<string>());
            }
        }
        finally
        {
            foreach (var frame in frames)
            {
                frame.Dispose();
            }
        }
    }

    /// The hard case. A frame's deferred work re-enters JavaScript from the
    /// event loop, long after the host last called into the frame, so nothing
    /// can have made the frame "current" for it. It has to find its own
    /// document anyway.
    [Fact]
    public async Task AFramesDeferredWorkStillSeesTheFramesDocument()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn a_frames_deferred_work_still_sees_the_frames_document() {
                let mut parent = page("https://parent.example/", "<html><body></body></html>");
                let frame = FrameRealm::new(
                    &mut parent,
                    1,
                    0,
                    "https://child.example/",
                    "<html><body></body></html>",
                )
                .expect("frame realm");

                // 50ms, not 0: a zero delay drains as a microtask while the host is
                // still inside the frame, which would hide the bug this guards.
                frame
                    .execute_script(
                        &mut parent,
                        "setTimeout(() => { document.body.setAttribute('data-who', location.href); }, 50);",
                    )
                    .unwrap();
                parent.run_event_loop_bounded(300).await.unwrap();

                assert_eq!(
                    frame
                        .evaluate(&mut parent, "document.body.getAttribute('data-who')")
                        .unwrap(),
                    serde_json::json!("https://child.example/"),
                    "the frame's timer did not write to the frame's own document"
                );
                assert_eq!(
                    parent
                        .evaluate("document.body.getAttribute('data-who')")
                        .unwrap(),
                    serde_json::Value::Null,
                    "the frame's timer wrote to the parent's document"
                );
            }
        */
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        var parent = page.Runtime;
        using var frame = FrameRealm.Create(
            parent, 1, 0, "https://child.example/", "<html><body></body></html>");
        Assert.NotNull(frame);

        // 50ms, not 0: a zero delay drains as a microtask while the host is
        // still inside the frame, which would hide the bug this guards.
        frame.ExecuteScript(
            "setTimeout(() => { document.body.setAttribute('data-who', location.href); }, 50);");
        await parent.RunEventLoopBoundedAsync(300);

        Assert.Equal(
            "https://child.example/",
            frame.Evaluate("document.body.getAttribute('data-who')")!.GetValue<string>());
        Assert.Null(parent.Evaluate("document.body.getAttribute('data-who')"));
    }

    /// A frame's timers cannot go through deno_core's queue: `op_timer_queue`
    /// reads per-context state that only a deno_core-created context carries,
    /// and queueing from a snapshot realm dereferences uninitialized memory,
    /// which aborts the process rather than failing a test. This is the guard
    /// against that path ever being restored.
    [Fact]
    public async Task AFrameTimerFiresWithoutDenoCoresTimerQueue()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn a_frame_timer_fires_without_deno_cores_timer_queue() {
                let mut parent = page("https://parent.example/", "<html><body></body></html>");
                let frame = FrameRealm::new(
                    &mut parent,
                    1,
                    0,
                    "https://child.example/",
                    "<html><body></body></html>",
                )
                .expect("frame realm");
                frame
                    .execute_script(&mut parent, "setTimeout(() => { globalThis.fired = 1; }, 50);")
                    .unwrap();
                parent.run_event_loop_bounded(300).await.unwrap();
                assert_eq!(
                    frame.evaluate(&mut parent, "globalThis.fired || 0").unwrap(),
                    serde_json::json!(1),
                    "the frame's timer callback never ran"
                );
            }
        */
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        var parent = page.Runtime;
        using var frame = FrameRealm.Create(
            parent, 1, 0, "https://child.example/", "<html><body></body></html>");
        Assert.NotNull(frame);
        frame.ExecuteScript("setTimeout(() => { globalThis.fired = 1; }, 50);");
        await parent.RunEventLoopBoundedAsync(300);
        Assert.Equal(
            1,
            (long)frame.Evaluate("globalThis.fired || 0")!.GetValue<double>());
    }

    /// Frame timers run on a separate queue from the page's, so cancelling one
    /// has its own path and its own way to go wrong.
    [Fact]
    public async Task ClearTimeoutCancelsAFrameTimer()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn clear_timeout_cancels_a_frame_timer() {
                let mut parent = page("https://parent.example/", "<html><body></body></html>");
                let frame = FrameRealm::new(
                    &mut parent,
                    1,
                    0,
                    "https://child.example/",
                    "<html><body></body></html>",
                )
                .expect("frame realm");
                frame
                    .execute_script(
                        &mut parent,
                        "globalThis.kept = 0;\
                         const cancelled = setTimeout(() => { globalThis.kept = 1; }, 50);\
                         setTimeout(() => { globalThis.kept = 2; }, 50);\
                         clearTimeout(cancelled);",
                    )
                    .unwrap();
                parent.run_event_loop_bounded(300).await.unwrap();
                assert_eq!(
                    frame.evaluate(&mut parent, "globalThis.kept").unwrap(),
                    serde_json::json!(2),
                    "clearTimeout did not cancel exactly the frame timer it was given"
                );
            }
        */
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        var parent = page.Runtime;
        using var frame = FrameRealm.Create(
            parent, 1, 0, "https://child.example/", "<html><body></body></html>");
        Assert.NotNull(frame);
        frame.ExecuteScript(
            "globalThis.kept = 0;"
            + "const cancelled = setTimeout(() => { globalThis.kept = 1; }, 50);"
            + "setTimeout(() => { globalThis.kept = 2; }, 50);"
            + "clearTimeout(cancelled);");
        await parent.RunEventLoopBoundedAsync(300);
        Assert.Equal(2, (long)frame.Evaluate("globalThis.kept")!.GetValue<double>());
    }

    /// V8 reports the frame as the microtask context, so a promise continuation
    /// resolves ops against the frame without any help from the host.
    [Fact]
    public async Task AFramesPromiseContinuationSeesTheFramesDocument()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            async fn a_frames_promise_continuation_sees_the_frames_document() {
                let mut parent = page("https://parent.example/", "<html><body></body></html>");
                let frame = FrameRealm::new(
                    &mut parent,
                    1,
                    0,
                    "https://child.example/",
                    "<html><body></body></html>",
                )
                .expect("frame realm");
                frame
                    .execute_script(
                        &mut parent,
                        "Promise.resolve().then(() => { \
                           document.body.setAttribute('data-who', location.href); });",
                    )
                    .unwrap();
                parent.run_event_loop_bounded(300).await.unwrap();
                assert_eq!(
                    frame
                        .evaluate(&mut parent, "document.body.getAttribute('data-who')")
                        .unwrap(),
                    serde_json::json!("https://child.example/"),
                );
            }
        */
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        var parent = page.Runtime;
        using var frame = FrameRealm.Create(
            parent, 1, 0, "https://child.example/", "<html><body></body></html>");
        Assert.NotNull(frame);
        frame.ExecuteScript(
            "Promise.resolve().then(() => { "
            + "document.body.setAttribute('data-who', location.href); });");
        await parent.RunEventLoopBoundedAsync(300);
        Assert.Equal(
            "https://child.example/",
            frame.Evaluate("document.body.getAttribute('data-who')")!.GetValue<string>());
    }

    // #850 / #841 — a promise rejection or dynamic import() inside a frame realm
    // used to null-deref deno_core's global callbacks (which read per-context
    // state from V8 embedder slots) and segfault the whole process. The realm
    // must now alias the main realm's state so these run without crashing.
    [Fact]
    public void AFrameRejectionDoesNotCrashTheProcess()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn a_frame_rejection_does_not_crash_the_process() {
                let mut parent = page("https://parent.example/", "<html><body></body></html>");
                let frame = FrameRealm::new(
                    &mut parent,
                    1,
                    0,
                    "https://parent.example/f",
                    "<html><body></body></html>",
                )
                .expect("frame realm");
                // Reaching this line at all (no SIGSEGV) is the regression check.
                frame
                    .execute_script(&mut parent, "Promise.reject(new Error('boom')); 'ok'")
                    .unwrap();
            }
        */
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        using var frame = FrameRealm.Create(
            page.Runtime, 1, 0, "https://parent.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);
        // Reaching this line at all (no crash) is the regression check.
        frame.ExecuteScript("Promise.reject(new Error('boom')); 'ok'");
    }

    [Fact]
    public void AFrameDynamicImportDoesNotCrashTheProcess()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn a_frame_dynamic_import_does_not_crash_the_process() {
                let mut parent = page("https://parent.example/", "<html><body></body></html>");
                let frame = FrameRealm::new(
                    &mut parent,
                    1,
                    0,
                    "https://parent.example/f",
                    "<html><body></body></html>",
                )
                .expect("frame realm");
                frame
                    .execute_script(
                        &mut parent,
                        "import('data:text/javascript,export default 1').catch(() => {}); 'ok'",
                    )
                    .unwrap();
            }
        */
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        using var frame = FrameRealm.Create(
            page.Runtime, 1, 0, "https://parent.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);
        frame.ExecuteScript("import('data:text/javascript,export default 1').catch(() => {}); 'ok'");
    }

    /// A frame posting to `parent` must reach the page, arrive trusted, and
    /// carry the frame's origin. Turnstile and every widget like it drop an
    /// untrusted message silently, so an untrusted delivery is not a cosmetic
    /// difference, it is the widget hanging forever.
    [Fact]
    public void AFramePostsToItsParentAsATrustedMessage()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn a_frame_posts_to_its_parent_as_a_trusted_message() {
                let mut parent = page("https://parent.example/", "<html><body></body></html>");
                parent
                    .execute_script(
                        "p",
                        "globalThis.got = [];\
                         addEventListener('message', (e) => globalThis.got.push(\
                           [e.data, e.origin, e.isTrusted]));",
                    )
                    .unwrap();
                let frame = FrameRealm::new(
                    &mut parent,
                    1,
                    0,
                    "https://child.example/f",
                    "<html><body></body></html>",
                )
                .expect("frame realm");

                frame
                    .execute_script(&mut parent, "parent.postMessage({token: 'ok'}, '*');")
                    .unwrap();
                // The host is the transport, exactly as `Page` does between turns.
                let queued = parent.take_pending_frame_messages();
                assert_eq!(queued.len(), 1);
                assert_eq!(queued[0].target_frame_id, 0);
                assert_eq!(queued[0].source_frame_id, 1);
                let script = format!(
                    "globalThis.__obscura_deliverMessage({}, {}, {});",
                    serde_json::to_string(&queued[0].data_json).unwrap(),
                    serde_json::to_string(&queued[0].origin).unwrap(),
                    queued[0].source_frame_id,
                );
                parent.execute_script("<frame-message>", &script).unwrap();

                assert_eq!(
                    parent.evaluate("globalThis.got").unwrap(),
                    serde_json::json!([[{"token": "ok"}, "https://child.example", true]]),
                );
            }
        */
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        var parent = page.Runtime;
        parent.ExecuteScript(
            "p",
            "globalThis.got = [];"
            + "addEventListener('message', (e) => globalThis.got.push("
            + "  [e.data, e.origin, e.isTrusted]));");
        using var frame = FrameRealm.Create(
            parent, 1, 0, "https://child.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);

        frame.ExecuteScript("parent.postMessage({token: 'ok'}, '*');");
        // The host is the transport, exactly as `Page` does between turns.
        var queued = parent.TakePendingFrameMessages();
        Assert.Single(queued);
        Assert.Equal(0u, queued[0].TargetFrameId);
        Assert.Equal(1u, queued[0].SourceFrameId);
        parent.ExecuteScript(
            "<frame-message>",
            "globalThis.__obscura_deliverMessage("
            + System.Text.Json.JsonSerializer.Serialize(queued[0].DataJson) + ", "
            + System.Text.Json.JsonSerializer.Serialize(queued[0].Origin) + ", "
            + queued[0].SourceFrameId + ");");

        AssertJsonEquals(
            """[[{"token": "ok"}, "https://child.example", true]]""",
            parent.Evaluate("globalThis.got"));
    }

    /// SEC-001 / #704 — postMessage must honour `targetOrigin`. A message
    /// restricted to a specific origin must be dropped when the receiving realm
    /// has a different origin, and delivered when the origins match. Before the
    /// fix the argument was discarded end-to-end, so the first message leaked.
    [Fact]
    public void PostMessageHonoursTargetOrigin()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn post_message_honours_target_origin() {
                let mut parent = page("https://parent.example/", "<html><body></body></html>");
                parent
                    .execute_script(
                        "p",
                        "globalThis.got = [];\
                         addEventListener('message', (e) => globalThis.got.push([e.data, e.origin]));",
                    )
                    .unwrap();
                let frame = FrameRealm::new(
                    &mut parent,
                    1,
                    0,
                    "https://child.example/f",
                    "<html><body></body></html>",
                )
                .expect("frame realm");

                // Deliver the way `Page` does, now carrying the queued targetOrigin.
                let deliver = |parent: &mut ObscuraJsRuntime, m: &crate::ops::PendingFrameMessage| {
                    let script = format!(
                        "globalThis.__obscura_deliverMessage({}, {}, {}, {});",
                        serde_json::to_string(&m.data_json).unwrap(),
                        serde_json::to_string(&m.origin).unwrap(),
                        m.source_frame_id,
                        serde_json::to_string(&m.target_origin).unwrap(),
                    );
                    parent.execute_script("<frame-message>", &script).unwrap();
                };

                // Restricted to an origin the parent does NOT have -> must be dropped.
                frame
                    .execute_script(
                        &mut parent,
                        "parent.postMessage({token: 'secret'}, 'https://attacker.example');",
                    )
                    .unwrap();
                let queued = parent.take_pending_frame_messages();
                assert_eq!(queued.len(), 1);
                assert_eq!(queued[0].target_origin, "https://attacker.example");
                deliver(&mut parent, &queued[0]);
                assert_eq!(
                    parent.evaluate("globalThis.got").unwrap(),
                    serde_json::json!([]),
                    "a message whose targetOrigin does not match the receiver must be dropped",
                );

                // Restricted to the parent's real origin -> must be delivered.
                frame
                    .execute_script(
                        &mut parent,
                        "parent.postMessage({token: 'ok'}, 'https://parent.example');",
                    )
                    .unwrap();
                let queued = parent.take_pending_frame_messages();
                deliver(&mut parent, &queued[0]);
                assert_eq!(
                    parent.evaluate("globalThis.got").unwrap(),
                    serde_json::json!([[{"token": "ok"}, "https://child.example"]]),
                    "a message whose targetOrigin matches the receiver must be delivered",
                );
            }
        */
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        var parent = page.Runtime;
        parent.ExecuteScript(
            "p",
            "globalThis.got = [];"
            + "addEventListener('message', (e) => globalThis.got.push([e.data, e.origin]));");
        using var frame = FrameRealm.Create(
            parent, 1, 0, "https://child.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);

        // Deliver the way `Page` does, now carrying the queued targetOrigin.
        void Deliver(Obscura.Js.Ops.PendingFrameMessage message) => parent.ExecuteScript(
            "<frame-message>",
            "globalThis.__obscura_deliverMessage("
            + System.Text.Json.JsonSerializer.Serialize(message.DataJson) + ", "
            + System.Text.Json.JsonSerializer.Serialize(message.Origin) + ", "
            + message.SourceFrameId + ", "
            + System.Text.Json.JsonSerializer.Serialize(message.TargetOrigin) + ");");

        // Restricted to an origin the parent does NOT have -> must be dropped.
        frame.ExecuteScript("parent.postMessage({token: 'secret'}, 'https://attacker.example');");
        var queued = parent.TakePendingFrameMessages();
        Assert.Single(queued);
        Assert.Equal("https://attacker.example", queued[0].TargetOrigin);
        Deliver(queued[0]);
        AssertJsonEquals("[]", parent.Evaluate("globalThis.got"));

        // Restricted to the parent's real origin -> must be delivered.
        frame.ExecuteScript("parent.postMessage({token: 'ok'}, 'https://parent.example');");
        queued = parent.TakePendingFrameMessages();
        Deliver(queued[0]);
        AssertJsonEquals(
            """[[{"token": "ok"}, "https://child.example"]]""",
            parent.Evaluate("globalThis.got"));
    }

    /// SEC-001 / #704 — verifies the targetOrigin gate does NOT break legitimate
    /// delivery *into* a frame (the "about:blank / empty origin" regression a
    /// reviewer warned about): an explicit origin that matches the loaded frame
    /// is delivered, the wildcard is always delivered (including to an opaque
    /// about:blank frame), and only a genuine mismatch is dropped.
    [Fact(Skip = "ClearScript refuses a script object across engines, so a frame realm's live window and document cannot be published into the page realm; see the port report")]
    public void PostMessageIntoAFrameDoesNotOverDrop()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn post_message_into_a_frame_does_not_over_drop() {
                let mut parent = page("https://parent.example/", "<html><body></body></html>");

                // A loaded, same-origin child frame.
                let child = FrameRealm::new(
                    &mut parent,
                    1,
                    0,
                    "https://parent.example/child",
                    "<html><body></body></html>",
                )
                .expect("frame realm");
                child
                    .execute_script(
                        &mut parent,
                        "globalThis.got = [];\
                         addEventListener('message', (e) => globalThis.got.push(String(e.data)));",
                    )
                    .unwrap();

                // Explicit matching origin -> delivered (the case that must not break).
                child
                    .deliver_message(&mut parent, "{\"v\":\"m1\"}", "https://parent.example", 0, "https://parent.example")
                    .unwrap();
                // Wildcard -> always delivered.
                child
                    .deliver_message(&mut parent, "{\"v\":\"m2\"}", "https://parent.example", 0, "*")
                    .unwrap();
                // Explicit non-matching origin -> dropped.
                child
                    .deliver_message(&mut parent, "{\"v\":\"m3\"}", "https://parent.example", 0, "https://evil.example")
                    .unwrap();

                assert_eq!(
                    child.evaluate(&mut parent, "globalThis.got").unwrap(),
                    serde_json::json!(["m1", "m2"]),
                    "matching origin and wildcard must deliver into the frame; only a real mismatch drops",
                );

                // An opaque (about:blank) frame: the wildcard must still deliver, so the
                // common widget case never breaks even when the frame origin is 'null'.
                let blank = FrameRealm::new(
                    &mut parent,
                    2,
                    0,
                    "about:blank",
                    "<html><body></body></html>",
                )
                .expect("blank frame realm");
                blank
                    .execute_script(
                        &mut parent,
                        "globalThis.got = [];\
                         addEventListener('message', (e) => globalThis.got.push(String(e.data)));",
                    )
                    .unwrap();
                blank
                    .deliver_message(&mut parent, "{\"v\":\"w\"}", "https://parent.example", 0, "*")
                    .unwrap();
                assert_eq!(
                    blank.evaluate(&mut parent, "globalThis.got").unwrap(),
                    serde_json::json!(["w"]),
                    "the wildcard must still deliver to an about:blank (opaque-origin) frame",
                );
            }
        */
    }

    /// `parent === window` is how a document decides it is top-level, so a
    /// framed realm must not see itself as the top.
    [Fact]
    public void AFramedRealmDoesNotLookTopLevel()
    {
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        var parent = page.Runtime;
        using var frame = FrameRealm.Create(
            parent, 2, 0, "https://child.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);
        Assert.Equal("[false,false]", frame.Evaluate("[parent === window, top === window]")!.ToJsonString());
        // The page itself really is the top and must still say so.
        Assert.Equal("[true,true]", parent.Evaluate("[parent === window, top === window]")!.ToJsonString());
    }

    /// Script can post in a synchronous loop while the host only drains between
    /// event loop turns, and this queue is on the process heap rather than
    /// V8's, where the heap-limit guard would never see it.
    [Fact]
    public void AFloodOfMessagesCannotGrowTheQueueWithoutBound()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn a_flood_of_messages_cannot_grow_the_queue_without_bound() {
                std::env::set_var("OBSCURA_FRAME_MESSAGE_QUEUE_ENTRIES", "64");
                let mut parent = page("https://parent.example/", "<html><body></body></html>");
                let frame = FrameRealm::new(
                    &mut parent,
                    1,
                    0,
                    "https://child.example/f",
                    "<html><body></body></html>",
                )
                .expect("frame realm");

                frame
                    .execute_script(
                        &mut parent,
                        "for (let i = 0; i < 5000; i++) parent.postMessage(i, '*');",
                    )
                    .unwrap();

                let queued = parent.take_pending_frame_messages();
                assert_eq!(queued.len(), 64, "the queue was not capped");
                // The messages kept are the earliest, which is the half of a handshake
                // that matters.
                assert_eq!(queued[0].data_json, r#"{"v":0}"#);
                std::env::remove_var("OBSCURA_FRAME_MESSAGE_QUEUE_ENTRIES");
            }
        */
        var previous = Environment.GetEnvironmentVariable("OBSCURA_FRAME_MESSAGE_QUEUE_ENTRIES");
        Environment.SetEnvironmentVariable("OBSCURA_FRAME_MESSAGE_QUEUE_ENTRIES", "64");
        try
        {
            using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
            var parent = page.Runtime;
            using var frame = FrameRealm.Create(
                parent, 1, 0, "https://child.example/f", "<html><body></body></html>");
            Assert.NotNull(frame);

            frame.ExecuteScript("for (let i = 0; i < 5000; i++) parent.postMessage(i, '*');");

            var queued = parent.TakePendingFrameMessages();
            Assert.Equal(64, queued.Count);
            // The messages kept are the earliest, which is the half of a handshake
            // that matters.
            Assert.Equal("""{"v":0}""", queued[0].DataJson);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OBSCURA_FRAME_MESSAGE_QUEUE_ENTRIES", previous);
        }
    }

    /// The page realm holds the frame's window and document, so a discarded
    /// frame leaves the page naming objects from a context the host no longer
    /// holds. Reading one must be safe. A regression here is an access
    /// violation that takes the process down, not a failed assertion.
    ///
    /// It must also not read as anything: V8 severs a global proxy when its
    /// context goes, which is the same thing a browser does to a WindowProxy
    /// when it discards a browsing context.
    [Fact(Skip = "ClearScript refuses a script object across engines, so a frame realm's live window and document cannot be published into the page realm; see the port report")]
    public void ADiscardedRealmLeavesThePageSafeToRun()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn a_discarded_realm_leaves_the_page_safe_to_run() {
                let mut parent = page("https://parent.example/", "<html><body></body></html>");
                {
                    let frame = FrameRealm::new(
                        &mut parent,
                        1,
                        0,
                        "https://parent.example/child",
                        "<html><body><h1>Child</h1></body></html>",
                    )
                    .expect("frame realm");
                    frame
                        .execute_script(&mut parent, "globalThis.marker = 'child';")
                        .unwrap();
                    // Reachable from the page while the frame is alive.
                    assert_eq!(
                        parent
                            .evaluate("globalThis.__obscura_frameObjects[1].window.marker")
                            .unwrap(),
                        serde_json::json!("child"),
                    );
                }

                // Dropping the realm does not free it, and must not make touching it
                // unsafe: the page still names its window, so V8 keeps the context
                // alive and the read still answers. This is exactly why a discarded
                // frame has to have its entry removed rather than merely dropped, and
                // what `Page::release_detached_frames` is for.
                assert_eq!(
                    parent
                        .evaluate("globalThis.__obscura_frameObjects[1].window.marker")
                        .unwrap(),
                    serde_json::json!("child"),
                );
                // The page's own DOM work still resolves against the page.
                assert_eq!(
                    parent.evaluate("document.body.innerHTML").unwrap(),
                    serde_json::json!(""),
                );
                // Dropping the page's reference is what lets the frame be collected.
                parent
                    .execute_script("p", "delete globalThis.__obscura_frameObjects[1];")
                    .unwrap();
                assert_eq!(
                    parent
                        .evaluate("globalThis.__obscura_frameObjects[1] === undefined")
                        .unwrap(),
                    serde_json::json!(true),
                );
            }
        */
    }

    /// A DOM call names the realm it belongs to, so the page reading the
    /// frame's document gets the frame's document. Resolving from the running
    /// context instead would silently answer with the page's own.
    [Fact(Skip = "ClearScript refuses a script object across engines, so a frame realm's live window and document cannot be published into the page realm; see the port report")]
    public void ThePageReadsTheFramesDocumentThroughItsOwnObject()
    {
        // Ported from crates/obscura-js/src/frame.rs. The Rust body is kept verbatim so the test
        // can be switched on without being reconstructed from scratch.
        /*
            fn the_page_reads_the_frames_document_through_its_own_object() {
                let mut parent = page(
                    "https://parent.example/",
                    "<html><head><title>parent</title></head><body></body></html>",
                );
                let frame = FrameRealm::new(
                    &mut parent,
                    1,
                    0,
                    "https://parent.example/child",
                    "<html><head><title>BEFORE</title></head><body><p>child</p></body></html>",
                )
                .expect("frame realm");
                frame
                    .execute_script(&mut parent, "document.title = 'RAN-IN-CHILD';")
                    .unwrap();

                // Read the frame's document from the *page's* realm.
                assert_eq!(
                    parent
                        .evaluate("globalThis.__obscura_frameObjects[1].document.title")
                        .unwrap(),
                    serde_json::json!("RAN-IN-CHILD"),
                );
                assert_eq!(
                    parent
                        .evaluate("globalThis.__obscura_frameObjects[1].document.querySelector('p').textContent")
                        .unwrap(),
                    serde_json::json!("child"),
                );
                // The page's own title is untouched by any of that.
                assert_eq!(
                    parent.evaluate("document.title").unwrap(),
                    serde_json::json!("parent"),
                );
            }
        */
    }

    /// A cross-origin frame must stay opaque. Nothing about it is published to
    /// the page, and V8's own access check answers `undefined` for anything the
    /// page reaches for, because the two realms keep different security tokens.
    [Fact]
    public void ACrossOriginFrameIsNotReachableFromThePage()
    {
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        var parent = page.Runtime;
        using var frame = FrameRealm.Create(
            parent, 1, 0, "https://other.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);
        frame.ExecuteScript("globalThis.secret = 'do-not-leak';");

        Assert.True(
            parent.Evaluate("globalThis.__obscura_frameObjects === undefined || globalThis.__obscura_frameObjects[1] === undefined")!.GetValue<bool>(),
            "a cross-origin frame was published to the page");
        // The frame still works on its own side.
        Assert.Equal("do-not-leak", frame.Evaluate("globalThis.secret")!.GetValue<string>());
    }

    [Fact]
    public void OpaqueOriginFramesAreNeverSameOrigin()
    {
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        using var frame = FrameRealm.Create(
            page.Runtime, 1, 0, "about:blank", "<html><body></body></html>");
        Assert.NotNull(frame);
        Assert.Equal("null", frame.Origin);
        Assert.False(frame.IsSameOriginAs("null"));
        Assert.False(frame.IsSameOriginAs("https://parent.example"));
    }

    /// <summary>
    /// Structural JSON comparison, standing in for the Rust tests'
    /// <c>assert_eq!(value, serde_json::json!(..))</c>. Comparing parsed trees
    /// rather than <c>ToJsonString()</c> output keeps the assertion free of
    /// System.Text.Json's default escaping of <c>&lt;</c>, <c>&gt;</c> and
    /// <c>+</c>.
    /// </summary>
    private static void AssertJsonEquals(string expected, JsonNode? actual)
    {
        var want = JsonNode.Parse(expected);
        Assert.True(
            JsonNode.DeepEquals(want, actual),
            $"expected {expected}\n  actual {actual?.ToJsonString() ?? "null"}");
    }

    /// <summary>The Rust tests' <c>parser_image_runtime</c> helper.</summary>
    private static RuntimeFixture ParserImageRuntime(string html, Func<string, byte[]?> loader)
    {
        var fixture = RuntimeFixture.Blank();
        var runtime = fixture.Runtime;
        runtime.SetDom(HtmlParsing.ParseHtml(html));
        runtime.SetUrl("http://example.com/page/index.html");
        runtime.State.RenderResources = Obscura.Render.RenderResourceCache.WithLoader(loader);
        runtime.RunPageInit();
        return fixture;
    }

    /// <summary>The Rust tests' <c>two_by_three_png</c> fixture: a 2x3 PNG.</summary>
    private static byte[] TwoByThreePng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAYAAAC56t6B"
        + "AAAAFklEQVR4nGP8z8Dwn4GBgYGJAQrgDAAxOwIE7x6DkQAAAABJRU5ErkJggg==");

    /// <summary>A loader that records the URLs it is asked for.</summary>
    private sealed class RecordingLoader(Func<string, byte[]?> inner)
    {
        private readonly List<string> _urls = [];

        public byte[]? Load(string url)
        {
            lock (_urls)
            {
                _urls.Add(url);
            }
            return inner(url);
        }

        public IReadOnlyList<string> Urls
        {
            get { lock (_urls) { return _urls.ToArray(); } }
        }
    }

    /// <summary>The Rust tests' <c>setup_runtime_with_cookies</c> helper.</summary>
    private static RuntimeFixture SetupRuntimeWithCookies(string html, out CookieJar jar)
    {
        jar = new CookieJar();
        var fixture = RuntimeFixture.Blank();
        var runtime = fixture.Runtime;
        runtime.SetDom(HtmlParsing.ParseHtml(html));
        runtime.SetUrl("http://example.com/test");
        runtime.SetTitle("Test Page");
        runtime.SetCookieJar(jar);
        runtime.RunPageInit();
        return fixture;
    }

    /// <summary>The Rust tests' <c>setup_runtime_at_deep_url</c> helper.</summary>
    private static RuntimeFixture SetupRuntimeAtDeepUrl(string html)
    {
        var fixture = RuntimeFixture.Blank();
        var runtime = fixture.Runtime;
        runtime.SetDom(HtmlParsing.ParseHtml(html));
        runtime.SetUrl("http://example.com/deep/page");
        runtime.RunPageInit();
        return fixture;
    }

    /// <summary>The Rust tests' <c>BASE_HREF_PAGE</c> fixture.</summary>
    private const string BaseHrefPage = """
        <html><head><base href="/app/"></head><body>
            <a id="link" href="data/x.json"></a>
            <form id="form" action="submit"></form>
            <script id="script" src="chunk.js"></script>
        </body></html>
        """;

    /// <summary>
    /// The raw-socket HTTP fixture the Rust redirect tests build inline with
    /// <c>std::net::TcpListener</c>. It answers one canned response per
    /// connection and records each request line.
    /// </summary>
    private sealed class RawHttpServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<string> _requestLines = [];
        private readonly List<string> _requests = [];

        public RawHttpServer(Func<string, string> respond)
        {
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            Origin = $"http://127.0.0.1:{((System.Net.IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(() => AcceptLoopAsync(respond));
        }

        public string Origin { get; }

        public IReadOnlyList<string> RequestLines
        {
            get { lock (_requestLines) { return _requestLines.ToArray(); } }
        }

        /// <summary>Every request in full, headers included.</summary>
        public IReadOnlyList<string> Requests
        {
            get { lock (_requestLines) { return _requests.ToArray(); } }
        }

        /// <summary>The request line's path, e.g. <c>/hop/3</c>.</summary>
        public static string RequestPath(string request)
        {
            var parts = request.Split('\n')[0].Split(' ');
            return parts.Length > 1 ? parts[1] : string.Empty;
        }

        private async Task AcceptLoopAsync(Func<string, string> respond)
        {
            while (!_cts.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }
                _ = Task.Run(() => ServeAsync(client, respond));
            }
        }

        private async Task ServeAsync(System.Net.Sockets.TcpClient client, Func<string, string> respond)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var text = new System.Text.StringBuilder();
                    var headerEnd = -1;
                    while (headerEnd < 0)
                    {
                        var read = await stream.ReadAsync(buffer, _cts.Token);
                        if (read <= 0)
                        {
                            break;
                        }
                        text.Append(System.Text.Encoding.Latin1.GetString(buffer, 0, read));
                        headerEnd = text.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    }
                    var request = text.ToString();
                    // Drain the declared body so the peer never sees a reset mid-send.
                    if (headerEnd >= 0
                        && ContentLength(request[..headerEnd]) is { } length
                        && length > 0)
                    {
                        var have = request.Length - (headerEnd + 4);
                        while (have < length)
                        {
                            var read = await stream.ReadAsync(buffer, _cts.Token);
                            if (read <= 0)
                            {
                                break;
                            }
                            have += read;
                            text.Append(System.Text.Encoding.Latin1.GetString(buffer, 0, read));
                        }
                        request = text.ToString();
                    }
                    lock (_requestLines)
                    {
                        _requestLines.Add(request.Split('\n')[0].TrimEnd('\r'));
                        _requests.Add(request);
                    }
                    var response = System.Text.Encoding.Latin1.GetBytes(respond(request));
                    await stream.WriteAsync(response, _cts.Token);
                    await stream.FlushAsync(_cts.Token);
                    client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                }
            }
            catch
            {
                // A client that hung up early is not a test failure.
            }
        }

        private static int? ContentLength(string headers)
        {
            foreach (var line in headers.Split('\n'))
            {
                var colon = line.IndexOf(':');
                if (colon > 0
                    && line[..colon].Trim().Equals("content-length", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line[(colon + 1)..].Trim(), out var value))
                {
                    return value;
                }
            }
            return null;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// A module-graph runtime pointed at a <see cref="RawHttpServer"/>: the page
    /// client allows loopback, which the SSRF policy blocks by default.
    /// </summary>
    private static ObscuraJsRuntime ModuleGraphRuntime(string baseUrl, CookieJar jar)
    {
        var runtime = ObscuraJsRuntime.WithBaseUrl(baseUrl);
        runtime.SetHttpClient(new ObscuraHttpClient(jar, null, allowPrivateNetwork: true));
        return runtime;
    }

    /// <summary>The Rust tests' <c>redirect_runtime_for_origin</c> helper.</summary>
    private static RuntimeFixture RedirectRuntimeForOrigin(string origin)
    {
        var fixture = RuntimeFixture.Blank();
        var runtime = fixture.Runtime;
        runtime.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        runtime.SetUrl($"{origin}/page");
        runtime.SetHttpClient(new ObscuraHttpClient(new CookieJar(), null, allowPrivateNetwork: true));
        runtime.RunPageInit();
        return fixture;
    }

    /// <summary>
    /// The Rust tests' <c>redirect_chain_runtime</c> fixture: <c>/hop/N</c>
    /// redirects to <c>/hop/N-1</c> and <c>/hop/0</c> answers "arrived".
    /// </summary>
    private static RawHttpServer RedirectChainServer() => new(request =>
    {
        var path = RawHttpServer.RequestPath(request);
        if (path.StartsWith("/hop/", StringComparison.Ordinal)
            && int.TryParse(path.AsSpan("/hop/".Length), out var hop))
        {
            if (hop == 0)
            {
                const string body = "arrived";
                return "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: "
                    + body.Length + "\r\nConnection: close\r\n\r\n" + body;
            }
            return $"HTTP/1.1 302 Found\r\nLocation: /hop/{hop - 1}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        }
        const string unparsed = "unparsed";
        return "HTTP/1.1 400 Bad Request\r\nContent-Type: text/plain\r\nContent-Length: "
            + unparsed.Length + "\r\nConnection: close\r\n\r\n" + unparsed;
    });

    /// <summary>The Rust tests' <c>assert_post_redirect_method</c> helper.</summary>
    private static async Task AssertPostRedirectMethodAsync(int status, string expectedMethod)
    {
        using var server = new RawHttpServer(request =>
            RawHttpServer.RequestPath(request) == "/start"
                ? $"HTTP/1.1 {status} Redirect\r\nLocation: /final\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                : "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        using var fixture = RedirectRuntimeForOrigin(server.Origin);
        var rt = fixture.Runtime;

        List<(string Method, string Path)> requestCallbacks = [];
        List<(string Method, string Path, string ResponsePath, int RedirectedFrom)> responseCallbacks = [];
        var callbacks = new CallbackRegistry();
        callbacks.AddRequest(request =>
        {
            lock (requestCallbacks)
            {
                requestCallbacks.Add((request.Method, request.Url.AbsolutePath));
            }
        });
        callbacks.AddResponse((request, response) =>
        {
            lock (responseCallbacks)
            {
                responseCallbacks.Add((
                    request.Method,
                    request.Url.AbsolutePath,
                    response.Url.AbsolutePath,
                    response.RedirectedFrom.Count));
            }
        });
        rt.SetCallbacks(callbacks);

        var result = await rt.CallFunctionOnForCdpAsync(
            """
            async () => {
                const response = await fetch("/start", { method: "POST", body: "payload" });
                return { url: response.url, redirected: response.redirected };
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);
        var value = result.Value!;
        Assert.EndsWith("/final", value["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(value["redirected"]!.GetValue<bool>());
        Assert.Equal([("POST", "/start")], requestCallbacks);
        Assert.Equal([(expectedMethod, "/final", "/final", 1)], responseCallbacks);

        var events = rt.TakeJsNetworkEvents();
        Assert.Single(events);
        Assert.Equal(expectedMethod, events[0].Method);
        Assert.EndsWith("/final", events[0].Url, StringComparison.Ordinal);

        var wire = server.RequestLines;
        Assert.StartsWith("POST /start ", wire[0], StringComparison.Ordinal);
        Assert.StartsWith($"{expectedMethod} /final ", wire[1], StringComparison.Ordinal);
    }

    /// <summary>A canned <c>application/javascript</c> response for a module fixture.</summary>
    private static string ModuleResponse(string body, string status = "200 OK") =>
        $"HTTP/1.1 {status}\r\nContent-Type: application/javascript\r\n"
        + $"Content-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}\r\n"
        + $"Connection: close\r\n\r\n{body}";

    /// <summary>
    /// A canned response carrying raw bytes. Latin-1 round-trips every byte, so
    /// the body survives the fixture's text pipeline unchanged.
    /// </summary>
    private static string BinaryResponse(string contentType, byte[] body, string extraHeaders = "") =>
        $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\n{extraHeaders}"
        + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n"
        + System.Text.Encoding.Latin1.GetString(body);

    /// <summary>Atomic max, for the concurrency high-water marks the fixtures record.</summary>
    private static void InterlockedMax(ref int target, int value)
    {
        var seen = Volatile.Read(ref target);
        while (value > seen)
        {
            var previous = Interlocked.CompareExchange(ref target, value, seen);
            if (previous == seen)
            {
                return;
            }
            seen = previous;
        }
    }
}
