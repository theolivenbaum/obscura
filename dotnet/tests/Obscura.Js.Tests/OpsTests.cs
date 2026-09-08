using System.Net;
using System.Runtime.CompilerServices;
using System.Net.Sockets;
using System.Text;
using Obscura.Dom;
using Obscura.Js.Ops;
using Obscura.Js.Runtime;
using Obscura.Js.Url;
using Obscura.Render;
using Xunit;

namespace Obscura.Js.Tests;

/// <summary>
/// Port of the <c>#[cfg(test)] mod tests</c> block in
/// <c>crates/obscura-js/src/ops.rs</c>, in the same order and under the same names.
/// </summary>
public sealed class OpsTests
{
    // SEC-002 / #705 - fetched_urls (and the like) must not grow without bound.
    [Fact]
    public void Push_capped_bounds_the_list_and_keeps_the_newest()
    {
        List<string> list = [];
        for (var i = 0; i < 10; i++)
        {
            StateHelpers.PushCapped(list, $"u{i}", 4);
        }

        Assert.Equal(4, list.Count);
        Assert.Equal(["u6", "u7", "u8", "u9"], list);
    }

    // SEC-006 / #580 - PBKDF2 parameters arrive straight from page JS. Without
    // caps, a huge iteration count pins the single-threaded runtime and a huge
    // output length forces an unbounded allocation. The derivation must reject
    // both above the fixed maximums, and still work for ordinary inputs.

    [Fact]
    public void Pbkdf2_rejects_excessive_iterations()
    {
        var error = Assert.Throws<CryptoOperationException>(() => CryptoOps.Pbkdf2(
            "SHA-256",
            Encoding.ASCII.GetBytes("pw"),
            Encoding.ASCII.GetBytes("salt"),
            CryptoOps.Pbkdf2MaxIterations + 1,
            32));
        Assert.Contains("iteration", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pbkdf2_rejects_excessive_output_length()
    {
        var error = Assert.Throws<CryptoOperationException>(() => CryptoOps.Pbkdf2(
            "SHA-256",
            Encoding.ASCII.GetBytes("pw"),
            Encoding.ASCII.GetBytes("salt"),
            1_000,
            CryptoOps.Pbkdf2MaxOutputBytes + 1));
        Assert.Contains("length", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pbkdf2_derives_within_limits()
    {
        var dk = CryptoOps.Pbkdf2(
            "SHA-256",
            Encoding.ASCII.GetBytes("password"),
            Encoding.ASCII.GetBytes("salt"),
            1_000,
            32);
        Assert.Equal(32, dk.Length);
    }

    // SEC-005 / #581 - op_fetch_url must not buffer an unbounded response body.
    // ReadBodyCappedAsync streams the body and refuses anything larger than the
    // cap, covering a server that just keeps sending with no Content-Length.

    private static IPEndPoint ServeBodyOnce(int bodyLength, bool withContentLength)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        _ = Task.Run(async () =>
        {
            using (listener)
            {
                using var socket = await listener.AcceptSocketAsync();
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                var header = new StringBuilder("HTTP/1.1 200 OK\r\nConnection: close\r\n");
                if (withContentLength)
                {
                    header.Append("Content-Length: ").Append(bodyLength).Append("\r\n");
                }

                header.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()));
                var chunk = new byte[64 * 1024];
                Array.Fill(chunk, (byte)'a');
                var sent = 0;
                while (sent < bodyLength)
                {
                    var n = Math.Min(chunk.Length, bodyLength - sent);
                    try
                    {
                        await stream.WriteAsync(chunk.AsMemory(0, n));
                    }
                    catch (IOException)
                    {
                        break;
                    }

                    sent += n;
                }

                socket.Shutdown(SocketShutdown.Both);
            }
        });
        return endpoint;
    }

    [Fact]
    public async Task Read_body_capped_rejects_oversized_streamed_body()
    {
        // No Content-Length forces the streaming-cap branch (lying/chunked server).
        var endpoint = ServeBodyOnce(4 * 1024 * 1024, withContentLength: false);
        using var client = new HttpClient();
        using var response = await client.GetAsync(
            new Uri($"http://{endpoint}/"),
            HttpCompletionOption.ResponseHeadersRead);
        var error = await Assert.ThrowsAsync<OpException>(
            () => FetchOps.ReadBodyCappedAsync(response, 1024 * 1024));
        Assert.Contains("maximum", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_body_capped_reads_body_within_cap()
    {
        var endpoint = ServeBodyOnce(1024, withContentLength: true);
        using var client = new HttpClient();
        using var response = await client.GetAsync(
            new Uri($"http://{endpoint}/"),
            HttpCompletionOption.ResponseHeadersRead);
        var body = await FetchOps.ReadBodyCappedAsync(response, 1024 * 1024);
        Assert.Equal(1024, body.Length);
    }

    [Fact]
    public void Glob_match_handles_cdp_blocked_url_patterns()
    {
        Assert.True(FetchOps.GlobMatch(
            "*://*.google.com/maps/vt/*",
            "https://www.google.com/maps/vt/pb=!1m4!1m3"));
        Assert.True(FetchOps.GlobMatch(
            "*://*.gstatic.com/*.woff2",
            "https://fonts.gstatic.com/s/inter/v18/font.woff2"));
        Assert.True(FetchOps.GlobMatch(
            "https://example.com/assets/*",
            "https://example.com/assets/app.js"));
        Assert.False(FetchOps.GlobMatch(
            "https://example.com/assets/*",
            "https://cdn.example.com/assets/app.js"));
        Assert.False(FetchOps.GlobMatch(
            "*://*.gstatic.com/*.woff2",
            "https://fonts.gstatic.com/s/inter/v18/font.woff"));
    }

    [Fact]
    public void Fetch_credentials_gate_cookie_send_and_storage_per_request_origin()
    {
        const string pageOrigin = "https://www.example.com";
        const string sameOriginUrl = "https://www.example.com/api";
        const string explicitDefaultPort = "https://www.example.com:443/api";
        const string crossOriginUrl = "https://api.example.com/data";

        Assert.False(FetchCredentials.Omit.Allows(pageOrigin, sameOriginUrl));
        Assert.False(FetchCredentials.Omit.Allows(pageOrigin, crossOriginUrl));

        Assert.True(FetchCredentials.SameOrigin.Allows(pageOrigin, sameOriginUrl));
        Assert.True(FetchCredentials.SameOrigin.Allows(pageOrigin, explicitDefaultPort));
        Assert.False(FetchCredentials.SameOrigin.Allows(pageOrigin, crossOriginUrl));

        Assert.True(FetchCredentials.Include.Allows(pageOrigin, sameOriginUrl));
        Assert.True(FetchCredentials.Include.Allows(pageOrigin, crossOriginUrl));
    }

    [Fact]
    public void Credentialed_cors_requires_exact_origin_and_allow_credentials()
    {
        const string pageOrigin = "https://www.example.com";

        Assert.True(FetchOps.CorsResponseAllows(FetchCredentials.SameOrigin, pageOrigin, "*", ""));
        Assert.False(FetchOps.CorsResponseAllows(FetchCredentials.Include, pageOrigin, "*", "true"));
        Assert.False(FetchOps.CorsResponseAllows(FetchCredentials.Include, pageOrigin, pageOrigin, ""));
        Assert.True(FetchOps.CorsResponseAllows(FetchCredentials.Include, pageOrigin, pageOrigin, "true"));
    }

    [Fact]
    public void Fetch_url_validation_honors_per_context_private_network_opt_in()
    {
        var loopback = UrlRecord.Parse("http://127.0.0.1:8080/resource");
        Assert.NotNull(loopback);
        Assert.Null(FetchOps.ValidateFetchUrl(loopback, true));
    }

    // SEC-005 / #708 - fetch() must not accept file:// (deny-by-default, matching
    // Page.navigate / Target.createTarget). The transports can't fetch it, but it
    // should be rejected up front rather than short-circuiting the gate.
    [Fact]
    public void Fetch_url_validation_rejects_file_scheme()
    {
        var file = UrlRecord.Parse("file:///etc/passwd");
        Assert.NotNull(file);
        // Rejected even with private-network access granted.
        var error = FetchOps.ValidateFetchUrl(file, true);
        Assert.NotNull(error);
        Assert.Contains("scheme", error.ToLowerInvariant(), StringComparison.Ordinal);
    }

    private const string PostedTaskBlocker =
        "Blocked on the runtime: nothing sets ObscuraJsRuntime.OpTableBinder yet, and the "
        + "runtime exposes no V8 task-queue seam for IPostedTaskSpawner, so scheduler.postTask "
        + "never reaches op_posted_task. Un-skip once both are wired.";

    /*  Rust original:

        #[tokio::test(flavor = "current_thread")]
        async fn posted_task_chains_complete_without_zero_delay_timer_floor() {
            let mut runtime = ObscuraJsRuntime::new();
            runtime.set_dom(parse_html("<html><body></body></html>"));
            runtime.set_url("http://example.com/posted-task-test");
            runtime.run_page_init();
            runtime.execute_script("posted-task-throughput", r#" ... "#).unwrap();
            runtime.run_event_loop_bounded(100).await.unwrap();
            let result = runtime.evaluate(r#"[ ... ]"#).unwrap();
            let values = result.as_array().unwrap();
            assert!(values[..3].iter().all(|value| value.as_f64() == Some(100.0)));
            assert!(values[3].as_f64().is_some_and(|e| e >= 0.0 && e < 75.0));
        }
    */
    [Fact(Skip = PostedTaskBlocker)]
    public async Task Posted_task_chains_complete_without_zero_delay_timer_floor()
    {
        using var runtime = new ObscuraJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        runtime.SetUrl("http://example.com/posted-task-test");
        runtime.RunPageInit();
        runtime.ExecuteScript("posted-task-throughput", """
            globalThis.__postedTaskBench = {
                message: 0,
                postTask: 0,
                yields: 0,
                started: performance.now(),
                finished: 0,
            };
            const markFinished = () => {
                if (__postedTaskBench.message === 100 &&
                    __postedTaskBench.postTask === 100 &&
                    __postedTaskBench.yields === 100) {
                    __postedTaskBench.finished = performance.now();
                }
            };

            const channel = new MessageChannel();
            channel.port2.onmessage = () => {
                __postedTaskBench.message++;
                if (__postedTaskBench.message < 100) channel.port1.postMessage(null);
                else markFinished();
            };
            channel.port1.postMessage(null);

            const postNext = () => scheduler.postTask(() => {
                __postedTaskBench.postTask++;
                if (__postedTaskBench.postTask < 100) postNext();
                else markFinished();
            });
            postNext();

            scheduler.postTask(async () => {
                while (__postedTaskBench.yields < 100) {
                    await scheduler.yield();
                    __postedTaskBench.yields++;
                }
                markFinished();
            });
            """);

        await runtime.RunEventLoopBoundedAsync(100);
        var result = runtime.Evaluate("""
            [
                __postedTaskBench.message,
                __postedTaskBench.postTask,
                __postedTaskBench.yields,
                __postedTaskBench.finished - __postedTaskBench.started,
            ]
            """);
        var values = Assert.IsAssignableFrom<System.Text.Json.Nodes.JsonArray>(result);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(100.0, values[i]!.GetValue<double>());
        }

        var elapsed = values[3]!.GetValue<double>();
        Assert.InRange(elapsed, 0.0, 74.999);
    }

    /*  Rust original:

        #[tokio::test(flavor = "current_thread")]
        async fn shared_posted_task_queue_preserves_priority_fifo_and_microtasks() {
            ... asserts __sharedPostedOrder equals
            ["sync","initial-microtask","blocking","blocking-microtask","message-1",
             "message-1-microtask","visible","visible-microtask","message-2",
             "message-2-microtask","background"]
        }
    */
    [Fact(Skip = PostedTaskBlocker)]
    public async Task Shared_posted_task_queue_preserves_priority_fifo_and_microtasks()
    {
        using var runtime = new ObscuraJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        runtime.SetUrl("http://example.com/posted-task-order");
        runtime.RunPageInit();
        runtime.ExecuteScript("shared-posted-task-order", """
            globalThis.__sharedPostedOrder = ["sync"];
            const channel = new MessageChannel();
            channel.port2.onmessage = event => {
                __sharedPostedOrder.push("message-" + event.data);
                Promise.resolve().then(() => {
                    __sharedPostedOrder.push("message-" + event.data + "-microtask");
                });
            };
            channel.port1.postMessage(1);
            scheduler.postTask(() => {
                __sharedPostedOrder.push("visible");
                Promise.resolve().then(() => __sharedPostedOrder.push("visible-microtask"));
            });
            channel.port1.postMessage(2);
            scheduler.postTask(() => {
                __sharedPostedOrder.push("background");
            }, { priority: "background" });
            scheduler.postTask(() => {
                __sharedPostedOrder.push("blocking");
                Promise.resolve().then(() => __sharedPostedOrder.push("blocking-microtask"));
            }, { priority: "user-blocking" });
            Promise.resolve().then(() => __sharedPostedOrder.push("initial-microtask"));
            """);

        await runtime.RunEventLoopBoundedAsync(100);
        var order = Assert.IsAssignableFrom<System.Text.Json.Nodes.JsonArray>(
            runtime.Evaluate("__sharedPostedOrder"));
        Assert.Equal(
            [
                "sync",
                "initial-microtask",
                "blocking",
                "blocking-microtask",
                "message-1",
                "message-1-microtask",
                "visible",
                "visible-microtask",
                "message-2",
                "message-2-microtask",
                "background",
            ],
            order.Select(node => node!.GetValue<string>()).ToArray());
    }

    /*  Rust original:

        #[tokio::test(flavor = "current_thread")]
        async fn bulk_posted_task_batch_completes() {
            ... 4096 scheduler.postTask calls, run_event_loop_bounded(500),
            assert __bulkPosted.count == 4096
        }
    */
    [Fact(Skip = PostedTaskBlocker)]
    public async Task Bulk_posted_task_batch_completes()
    {
        using var runtime = new ObscuraJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        runtime.SetUrl("http://example.com/posted-task-bulk");
        runtime.RunPageInit();
        runtime.ExecuteScript("posted-task-bulk", """
            globalThis.__bulkPosted = { count: 0 };
            const tasks = [];
            for (let i = 0; i < 4096; i++) {
                tasks.push(scheduler.postTask(() => __bulkPosted.count++));
            }
            Promise.all(tasks);
            """);

        await runtime.RunEventLoopBoundedAsync(500);
        Assert.Equal(4096.0, runtime.Evaluate("__bulkPosted.count")!.GetValue<double>());
    }

    /*  Rust original:

        /// A network-op Promise reaction can schedule browser work while
        /// deno_core is dispatching an async-op result batch. Posted tasks must not
        /// recursively submit another async op through that borrowed driver.
        #[tokio::test(flavor = "current_thread")]
        async fn posted_task_from_async_op_resolution_avoids_driver_submission() { ... }
    */
    [Fact(Skip = PostedTaskBlocker)]
    public async Task Posted_task_from_async_op_resolution_avoids_driver_submission()
    {
        using var runtime = new ObscuraJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        runtime.SetUrl("http://example.com/posted-task-from-async-op");
        runtime.RunPageInit();
        runtime.ExecuteScript("posted-task-from-op-resolution", """
            globalThis.__postedFromOp = 0;
            Deno.core.ops.op_sleep(0).then(() => {
                const rearm = () => scheduler.postTask(() => {
                    __postedFromOp++;
                    if (__postedFromOp < 250) rearm();
                });
                rearm();
            });
            """);

        await runtime.RunEventLoopBoundedAsync(300);
        Assert.Equal(250.0, runtime.Evaluate("__postedFromOp")!.GetValue<double>());
    }

    /*  Rust original:

        #[tokio::test(flavor = "current_thread")]
        async fn posted_task_is_cancelled_when_its_document_is_replaced() { ... }
    */
    [Fact(Skip = PostedTaskBlocker)]
    public async Task Posted_task_is_cancelled_when_its_document_is_replaced()
    {
        using var runtime = new ObscuraJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml("<html><body data-document='old'></body></html>"));
        runtime.SetUrl("http://example.com/posted-task-old-document");
        runtime.RunPageInit();
        runtime.ExecuteScript(
            "posted-task-old-document",
            "const staleController = new AbortController();"
            + "scheduler.postTask("
            + "  () => document.body.setAttribute('data-stale-task', 'ran'),"
            + "  { signal: staleController.signal });");

        runtime.SetDom(HtmlParsing.ParseHtml("<html><body data-document='new'></body></html>"));
        runtime.ExecuteScript(
            "posted-task-new-document",
            "scheduler.postTask(() => document.body.setAttribute('data-fresh-task', 'ran'));");
        await runtime.RunEventLoopBoundedAsync(100);

        Assert.Null(runtime.Evaluate("document.body.getAttribute('data-stale-task')"));
        Assert.Equal("new", runtime.Evaluate("document.body.getAttribute('data-document')")!.GetValue<string>());
        Assert.Equal("ran", runtime.Evaluate("document.body.getAttribute('data-fresh-task')")!.GetValue<string>());
    }

    /*  Rust original:

        #[tokio::test(flavor = "current_thread")]
        async fn delayed_posted_task_keeps_its_creation_document_generation() { ... }
    */
    [Fact(Skip = PostedTaskBlocker)]
    public async Task Delayed_posted_task_keeps_its_creation_document_generation()
    {
        using var runtime = new ObscuraJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml("<html><body data-document='old'></body></html>"));
        runtime.SetUrl("http://example.com/delayed-posted-task-old-document");
        runtime.RunPageInit();
        runtime.ExecuteScript(
            "delayed-posted-task-old-document",
            "scheduler.postTask("
            + "  () => document.body.setAttribute('data-delayed-stale-task', 'ran'),"
            + "  { delay: 1 });");

        runtime.SetDom(HtmlParsing.ParseHtml("<html><body data-document='new'></body></html>"));
        await runtime.RunEventLoopBoundedAsync(100);

        Assert.Null(runtime.Evaluate("document.body.getAttribute('data-delayed-stale-task')"));
    }

    [Fact]
    public void Posted_task_owner_contention_is_panic_safe()
    {
        // Rust drops the last Rc here; the managed equivalent is letting the only
        // strong reference leave scope, which is why the owner lives in a separate
        // non-inlined frame.
        var weak = OwnerContentionScope();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.Equal(PostedTaskOwnerStatus.Gone.Instance, CoreOps.PostedTaskOwnerStatusOf(weak));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<ObscuraState> OwnerContentionScope()
    {
        var owner = new ObscuraState();
        var weak = new WeakReference<ObscuraState>(owner);
        var generation = owner.DocumentGeneration;

        using (owner.EnterExclusive())
        {
            Assert.Equal(PostedTaskOwnerStatus.Busy.Instance, CoreOps.PostedTaskOwnerStatusOf(weak));
        }

        Assert.Equal(
            new PostedTaskOwnerStatus.Generation(generation),
            CoreOps.PostedTaskOwnerStatusOf(weak));
        return weak;
    }

    [Fact]
    public void Connected_shadow_nodes_invalidate_without_entering_light_tree_retention()
    {
        var dom = HtmlParsing.ParseHtml(
            """<x-host id="host"></x-host><div id="source"><span id="shadow-child"></span></div>""");
        var host = dom.GetElementById("host")!.Value;
        var source = dom.GetElementById("source")!.Value;
        var child = dom.GetElementById("shadow-child")!.Value;
        var root = dom.AttachShadowRoot(host, ShadowRootMode.Open);
        dom.AppendChild(root, child);

        Assert.True(StateHelpers.NodeIsConnected(dom, child));
        Assert.Contains(child, StateHelpers.ShadowIncludingConnectedNodes(dom));
        Assert.Null(RenderInvalidation.RetainedMutation(
            dom,
            "set_attribute",
            child.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "class\0changed"));

        dom.AppendChild(source, host);
        Assert.True(StateHelpers.NodeIsConnected(dom, child));
        dom.Remove(source);
        Assert.False(StateHelpers.NodeIsConnected(dom, child));
        Assert.DoesNotContain(child, StateHelpers.ShadowIncludingConnectedNodes(dom));
    }

    [Fact]
    public void Repeated_inline_style_writes_share_one_retained_dirty_marker_per_node()
    {
        List<RetainedStyleMutation> pending = [];
        static RetainedStyleMutation StyleMutation(uint raw) =>
            new RetainedStyleMutation.Attribute(
                new AttributeStyleMutation(NodeId.New(raw), "style", null, null));

        // Motion/React commonly writes a connected element's serialized style twice
        // in one commit. The old queue reached its 256-record ceiling at only 128
        // elements and discarded the complete PreparedRender.
        for (uint raw = 1; raw <= 200; raw++)
        {
            Assert.True(RenderInvalidation.QueueRetainedStyleMutation(pending, StyleMutation(raw)));
            Assert.True(RenderInvalidation.QueueRetainedStyleMutation(pending, StyleMutation(raw)));
        }

        Assert.Equal(200, pending.Count);

        // The memory bound remains real: unique dirty nodes still consume one slot,
        // while an already-recorded node remains safe at the ceiling.
        for (uint raw = 201; raw <= RenderInvalidation.MaxPendingStyleMutations; raw++)
        {
            Assert.True(RenderInvalidation.QueueRetainedStyleMutation(pending, StyleMutation(raw)));
        }

        Assert.Equal(RenderInvalidation.MaxPendingStyleMutations, pending.Count);
        Assert.True(RenderInvalidation.QueueRetainedStyleMutation(pending, StyleMutation(1)));
        Assert.False(RenderInvalidation.QueueRetainedStyleMutation(
            pending,
            StyleMutation(RenderInvalidation.MaxPendingStyleMutations + 1)));
        Assert.Equal(RenderInvalidation.MaxPendingStyleMutations, pending.Count);
    }

    [Fact]
    public void Repeated_selector_attribute_writes_keep_only_the_rendered_transition()
    {
        var node = NodeId.New(7);
        RetainedStyleMutation Mutation(string old, string @new) =>
            new RetainedStyleMutation.Attribute(new AttributeStyleMutation(node, "class", old, @new));

        List<RetainedStyleMutation> pending = [];
        Assert.True(RenderInvalidation.QueueRetainedStyleMutation(
            pending, Mutation("before", "intermediate")));
        Assert.True(RenderInvalidation.QueueRetainedStyleMutation(
            pending, Mutation("intermediate", "after")));
        Assert.Equal(
            [new RetainedStyleMutation.Attribute(
                new AttributeStyleMutation(node, "class", "before", "after"))],
            pending);
    }

    [Fact]
    public void Repeated_animation_changes_share_one_retained_dirty_marker_per_node()
    {
        List<RetainedStyleMutation> pending = [];
        var first = NodeId.New(1);
        var second = NodeId.New(2);
        for (var i = 0; i < 300; i++)
        {
            Assert.True(RenderInvalidation.QueueRetainedStyleMutation(
                pending, new RetainedStyleMutation.Animation(first)));
        }

        Assert.True(RenderInvalidation.QueueRetainedStyleMutation(
            pending, new RetainedStyleMutation.Animation(second)));
        Assert.Equal(
            [
                new RetainedStyleMutation.Animation(first),
                new RetainedStyleMutation.Animation(second),
            ],
            pending);
    }

    [Fact]
    public void Repeated_resource_changes_share_one_retained_refresh_marker()
    {
        List<RetainedStyleMutation> pending =
            [new RetainedStyleMutation.Animation(NodeId.New(1))];
        for (var i = 0; i < 300; i++)
        {
            Assert.True(RenderInvalidation.QueueRetainedStyleMutation(
                pending, RetainedStyleMutation.Resource.Instance));
        }

        Assert.Equal(
            [
                new RetainedStyleMutation.Animation(NodeId.New(1)),
                RetainedStyleMutation.Resource.Instance,
            ],
            pending);

        List<RetainedStyleMutation> fullStyleBatch = [];
        for (var raw = 1; raw <= RenderInvalidation.MaxPendingStyleMutations; raw++)
        {
            fullStyleBatch.Add(new RetainedStyleMutation.Animation(NodeId.New((uint)raw)));
        }

        Assert.True(RenderInvalidation.QueueRetainedStyleMutation(
            fullStyleBatch, RetainedStyleMutation.Resource.Instance));
        Assert.Equal(RenderInvalidation.MaxPendingStyleMutations + 1, fullStyleBatch.Count);
        Assert.False(RenderInvalidation.QueueRetainedStyleMutation(
            fullStyleBatch, new RetainedStyleMutation.Animation(NodeId.New(5_000))));
    }

    [Fact]
    public void Geometry_consumer_defers_paint_only_sample_until_exact_consumer()
    {
        var dom = HtmlParsing.ParseHtml("""
            <style>
                @keyframes fade { from { opacity:0 } to { opacity:1 } }
                #box { width:40px;height:20px;animation:fade 1000ms linear both }
            </style><div id="box"></div>
            """);
        var boxNode = dom.GetElementById("box")!.Value;
        var state = new ObscuraState
        {
            Dom = dom,
            AnimationSample = AnimationSample.Document(0.0f),
        };
        Assert.NotNull(RenderState.EnsurePreparedRender(state));
        Assert.Equal(0.0f, state.PreparedRender!.Layout.Styles[boxNode].Opacity);

        state.AnimationSample = AnimationSample.Document(500.0f);
        var geometry = RenderState.EnsurePreparedGeometry(state);
        Assert.NotNull(geometry);
        Assert.Equal(0.0f, geometry.AnimationSampleTime().Milliseconds);
        Assert.Equal(40.0f, geometry.DocumentRect(boxNode)!.Value.Width);
        Assert.Equal(0.0f, geometry.Layout.Styles[boxNode].Opacity);

        var exact = RenderState.EnsurePreparedRender(state);
        Assert.NotNull(exact);
        Assert.Equal(500.0f, exact.AnimationSampleTime().Milliseconds);
        var opacity = exact.Layout.Styles[boxNode].Opacity!.Value;
        Assert.True(Math.Abs(opacity - 0.5f) < 0.01f, $"exact opacity was {opacity}");
        Assert.Equal(40.0f, exact.DocumentRect(boxNode)!.Value.Width);
    }

    [Fact]
    public void Geometry_consumer_materializes_geometry_animation_sample()
    {
        var dom = HtmlParsing.ParseHtml("""
            <style>
                @keyframes grow { from { width:20px } to { width:100px } }
                #box { height:20px;animation:grow 1000ms linear both }
            </style><div id="box"></div>
            """);
        var boxNode = dom.GetElementById("box")!.Value;
        var state = new ObscuraState
        {
            Dom = dom,
            AnimationSample = AnimationSample.Document(0.0f),
        };
        Assert.NotNull(RenderState.EnsurePreparedRender(state));

        state.AnimationSample = AnimationSample.Document(500.0f);
        var geometry = RenderState.EnsurePreparedGeometry(state);
        Assert.NotNull(geometry);
        Assert.Equal(500.0f, geometry.AnimationSampleTime().Milliseconds);
        var width = geometry.DocumentRect(boxNode)!.Value.Width;
        Assert.True(Math.Abs(width - 60.0f) < 0.1f, $"sampled width was {width}");
    }
}
