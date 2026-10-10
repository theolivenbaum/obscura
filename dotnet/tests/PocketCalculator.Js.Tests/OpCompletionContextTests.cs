// No Rust counterpart: deno_core resolves every op inside poll_event_loop on the isolate's own
// thread. The port posts ClearScript's promise resolutions to OpCompletionContext and runs them
// from the event loop. See "Known deviations" in todo.md.
using System.Diagnostics;
using System.Net;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Js.Tests;

public sealed class OpCompletionContextTests
{
    [Fact]
    public void PostsRunInOrderOnlyWhenRunAndWithTheContextCurrent()
    {
        using var context = new OpCompletionContext();
        List<string> log = [];
        SynchronizationContext? seen = null;
        context.Post(_ => log.Add("a"), null);
        context.Post(_ => { log.Add("b"); seen = SynchronizationContext.Current; }, null);

        Assert.Empty(log);
        Assert.Equal(2, context.Pending);
        Assert.True(context.RunOne());
        Assert.True(context.RunOne());
        Assert.False(context.RunOne());
        Assert.Equal(["a", "b"], log);
        Assert.Same(context, seen);
        Assert.NotSame(context, SynchronizationContext.Current);
    }

    [Fact]
    public void AWaitTakenBeforeAPostIsWokenByItAndOneTakenAfterIsAlreadyComplete()
    {
        using var context = new OpCompletionContext();
        var before = context.WhenPosted();
        Assert.False(before.IsCompleted);

        // From another thread, as a transport completing an op would.
        Task.Run(() => context.Post(_ => { }, null)).Wait();
        Assert.True(before.Wait(TimeSpan.FromSeconds(10)), "a post must wake a waiter");
        Assert.True(context.WhenPosted().IsCompleted, "a wait taken with work queued must not park");

        Assert.True(context.RunOne());
        Assert.False(context.WhenPosted().IsCompleted);
    }

    [Fact]
    public async Task DisposalDropsQueuedPostsCancelsHostWorkAndRefusesLaterPosts()
    {
        var context = new OpCompletionContext();
        var ran = 0;
        context.Post(_ => ran++, null);
        var tail = context.InvokeAsync(() => ++ran);
        var waiter = context.WhenPosted();

        context.Dispose();

        Assert.Equal(0, ran);
        Assert.Equal(0, context.Pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tail);
        Assert.True(waiter.IsCompleted);

        context.Post(_ => ran++, null);
        var late = context.InvokeAsync(() => ++ran);
        Assert.False(context.RunOne());
        Assert.Equal(0, ran);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late);
        Assert.True(context.WhenPosted().IsCompleted, "a disposed context never parks a waiter");
    }

    [Fact]
    public void HostWorkCompletesOnTheThreadThatRunsTheQueueAndContinuationsFollowIt()
    {
        using var context = new OpCompletionContext();
        var runner = Environment.CurrentManagedThreadId;
        int? bodyThread = null;
        int? continuationThread = null;
        var task = context.InvokeAsync(() =>
        {
            bodyThread = Environment.CurrentManagedThreadId;
            return 7;
        });
        var continued = task.ContinueWith(
            _ => continuationThread = Environment.CurrentManagedThreadId,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Assert.False(task.IsCompleted);
        Assert.True(context.RunOne());
        Assert.Equal(7, task.Result);
        Assert.Equal(runner, bodyThread);
        Assert.True(continued.IsCompleted);
        Assert.Equal(runner, continuationThread);
    }

    [Fact]
    public void ATaskSchedulerFromTheContextQueuesWorkInsteadOfRunningItInline()
    {
        // ClearScript's own path: TaskScheduler.FromCurrentSynchronizationContext() captured
        // while the op is called, and a continuation completed on a pool thread.
        using var context = new OpCompletionContext();
        context.Enter();
        TaskScheduler scheduler;
        try
        {
            scheduler = TaskScheduler.FromCurrentSynchronizationContext();
        }
        finally
        {
            OpCompletionContext.Leave();
        }

        Assert.Null(SynchronizationContext.Current);
        var source = new TaskCompletionSource();
        var ran = 0;
        var continuation = source.Task.ContinueWith(
            _ => ran++, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, scheduler);
        Task.Run(source.SetResult).Wait();

        Assert.Equal(0, ran);
        Assert.Equal(1, context.Pending);
        Assert.True(context.RunOne());
        Assert.Equal(1, ran);
        Assert.True(continuation.IsCompletedSuccessfully);
    }

    [Fact]
    public void EnterAndLeaveNestAndSendIsRefused()
    {
        using var outer = new OpCompletionContext();
        using var inner = new OpCompletionContext();
        outer.Enter();
        inner.Enter();
        Assert.Same(inner, SynchronizationContext.Current);
        OpCompletionContext.Leave();
        Assert.Same(outer, SynchronizationContext.Current);
        OpCompletionContext.Leave();
        Assert.Null(SynchronizationContext.Current);

        Assert.Throws<NotSupportedException>(() => outer.Send(_ => { }, null));
    }

    // ------------------------------------------------------------- in the runtime

    [Fact]
    public async Task AnOpThatCompletesRunsNoPageScriptUntilTheLoopRunsIt()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "op-completion-deferred",
            "globalThis.__opDelivered = false;"
            + "__obscura_test_ops.op_sleep(50).then(() => { globalThis.__opDelivered = true; });");

        // The op's task completes on a pool thread long before this; ClearScript used to
        // resolve the promise there, entering V8 beside whatever the page thread was doing.
        Assert.True(
            SpinWait.SpinUntil(() => rt.OpCompletions.Pending > 0, TimeSpan.FromSeconds(10)),
            "the op's completion should be queued for the loop");
        Thread.Sleep(50);
        Assert.False(rt.Evaluate("globalThis.__opDelivered")!.GetValue<bool>());

        Assert.True(await EventLoopWait.UntilAsync(rt, "globalThis.__opDelivered"));
        Assert.Equal(0, rt.OpCompletions.Pending);
    }

    [Fact]
    public async Task OpCompletionsRunAheadOfTimersInATurn()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "op-completion-order",
            "globalThis.__log = [];"
            + "__obscura_test_ops.op_sleep(50).then(() => __log.push('op'));"
            + "setTimeout(() => __log.push('timer'), 0);");
        Assert.True(
            SpinWait.SpinUntil(() => rt.OpCompletions.Pending > 0, TimeSpan.FromSeconds(10)),
            "the op's completion should be queued for the loop");
        Thread.Sleep(20);

        await rt.RunCooperativeEventLoopTickAsync();

        Assert.Equal("op,timer", rt.Evaluate("__log.join()")!.GetValue<string>());
    }

    [Fact]
    public async Task AParkedLoopWakesForAnOpCompletion()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        // A far timer keeps the loop parking; the op completing must not wait it out.
        rt.ExecuteScript(
            "op-completion-wake",
            "globalThis.__opDelivered = false; setTimeout(() => {}, 60000);"
            + "__obscura_test_ops.op_sleep(30).then(() => { globalThis.__opDelivered = true; });");

        var clock = Stopwatch.StartNew();
        await rt.ResolvePromisesUntilAsync(
            r => r.Evaluate("globalThis.__opDelivered")!.GetValue<bool>(), 10_000);
        Assert.True(rt.Evaluate("globalThis.__opDelivered")!.GetValue<bool>());
        Assert.True(clock.ElapsedMilliseconds < 2_000, $"delivered after {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ADisposedRuntimeDropsItsQueuedCompletions()
    {
        var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "op-completion-dispose",
            "__obscura_test_ops.op_sleep(50).then(() => { globalThis.__x = 1; });");
        Assert.True(
            SpinWait.SpinUntil(() => rt.OpCompletions.Pending > 0, TimeSpan.FromSeconds(10)),
            "the op's completion should be queued for the loop");

        fixture.Dispose();

        Assert.Equal(0, rt.OpCompletions.Pending);
        Assert.True(rt.OpCompletions.IsDisposed);
    }
}

/// <summary>
/// The starvation the op completion queue fixes: page script waiting on the network with the
/// isolate held while op settlements fill the thread pool waiting for that isolate.
/// </summary>
/// <remarks>
/// GitHub's home page: twenty preload fetches settling while a module graph was evaluated, and
/// one module took 12 s instead of 0.3 s, because every settlement parked a pool thread on the
/// isolate lock and each of the graph's fetches waited for the pool to grow. The port then
/// raised the pool's floor to 64 threads for the whole process. These run with the pool at its
/// default minimum (the processor count) and assert that nothing is parked.
/// </remarks>
public sealed class OpCompletionStarvationTests : IDisposable
{
    private const int ModuleDelayMs = 60;
    private const int FetchDelayMs = 150;
    private const int ChainLength = 8;
    private const int ConcurrentFetches = 64;

    private readonly HttpListener _listener;
    private readonly string _origin;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _serving;
    private readonly PocketCalculatorHttpClient _client;

    public OpCompletionStarvationTests()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _origin = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_origin + "/");
        _listener.Start();
        _serving = Task.Run(ServeAsync);
        _client = new PocketCalculatorHttpClient(new CookieJar(), null, allowPrivateNetwork: true);
    }

    [Fact]
    public async Task AStaticGraphEvaluatesPromptlyWhileManyFetchesSettle()
    {
        AssertPoolAtItsDefault();
        using var fixture = Page();
        var rt = fixture.Runtime;
        StartFetches(rt);

        var clock = Stopwatch.StartNew();
        await rt.LoadInlineModuleAsync(
            "import { v } from '/static/m0.js'; globalThis.__static = v;", _origin + "/page", 30_000);
        var elapsed = clock.ElapsedMilliseconds;

        Assert.Equal(ChainLength, (int)rt.Evaluate("globalThis.__static")!.GetValue<double>());
        // Healthy: about ChainLength * ModuleDelayMs (480 ms). Starved: tens of seconds.
        Assert.True(elapsed < 5_000, $"the module graph took {elapsed} ms");
        Assert.True(
            await EventLoopWait.UntilAsync(rt, $"globalThis.__fetched === {ConcurrentFetches}"),
            $"fetched {rt.Evaluate("globalThis.__fetched")}, queued {rt.OpCompletions.Pending}, errors {rt.Evaluate("JSON.stringify(globalThis.__errors ?? [])")}");
    }

    [Fact]
    public async Task ADynamicImportWaitedForInsideScriptEvaluatesPromptlyWhileManyFetchesSettle()
    {
        AssertPoolAtItsDefault();
        using var fixture = Page();
        var rt = fixture.Runtime;
        StartFetches(rt);
        // Let the fetches get under way so their completions land while the import waits.
        Thread.Sleep(FetchDelayMs / 2);

        // import() fetches its graph synchronously, inside this script, with the isolate held:
        // the one place the page thread still waits on the network.
        var clock = Stopwatch.StartNew();
        rt.ExecuteScript(
            "dynamic-import-under-load",
            "import('/dynamic/m0.js').then(m => { globalThis.__dynamic = m.v; });");
        var elapsed = clock.ElapsedMilliseconds;

        Assert.True(await EventLoopWait.UntilAsync(rt, "globalThis.__dynamic !== undefined"));
        Assert.Equal(ChainLength, (int)rt.Evaluate("globalThis.__dynamic")!.GetValue<double>());
        Assert.True(elapsed < 5_000, $"the dynamic import held the page for {elapsed} ms");
        Assert.True(
            await EventLoopWait.UntilAsync(rt, $"globalThis.__fetched === {ConcurrentFetches}"),
            $"fetched {rt.Evaluate("globalThis.__fetched")}, queued {rt.OpCompletions.Pending}, errors {rt.Evaluate("JSON.stringify(globalThis.__errors ?? [])")}");
    }

    private static void AssertPoolAtItsDefault()
    {
        ThreadPool.GetMinThreads(out var workers, out _);
        Assert.True(
            workers <= Math.Max(Environment.ProcessorCount, 4),
            $"the thread pool floor is {workers}; the engine must not need a raised one");
    }

    private RuntimeFixture Page()
    {
        var fixture = RuntimeFixture.Blank();
        fixture.Runtime.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        fixture.Runtime.SetUrl(_origin + "/page");
        fixture.Runtime.RebaseModuleLoader(_origin + "/page");
        fixture.Runtime.SetHttpClient(_client);
        fixture.Runtime.RunPageInit();
        return fixture;
    }

    /// <summary>
    /// Page fetches (six at a time, FetchConcurrency) and as many other ops settling at once
    /// (op_sleep standing in for GitHub's preload and image loads, which are not capped).
    /// </summary>
    private static void StartFetches(PocketCalculatorJsRuntime rt) =>
        rt.ExecuteScript(
            "settling-fetches",
            "globalThis.__fetched = 0; globalThis.__slept = 0;"
            + $"for (let i = 0; i < {ConcurrentFetches}; i++) {{"
            + "fetch('/slow?i=' + i).then(r => r.text()).then(() => { globalThis.__fetched++; }, e => { (globalThis.__errors ??= []).push(String(e)); });"
            + $"__obscura_test_ops.op_sleep({FetchDelayMs} + i).then(() => {{ globalThis.__slept++; }}); }}");

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => RespondAsync(context));
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var response = context.Response;
        try
        {
            string body;
            string type = "text/javascript";
            if (path == "/slow")
            {
                await Task.Delay(FetchDelayMs).ConfigureAwait(false);
                body = "ok";
                type = "text/plain";
            }
            else if (path.StartsWith("/static/m", StringComparison.Ordinal)
                || path.StartsWith("/dynamic/m", StringComparison.Ordinal))
            {
                await Task.Delay(ModuleDelayMs).ConfigureAwait(false);
                var dir = path[..(path.LastIndexOf('/') + 1)];
                var index = int.Parse(path[(path.LastIndexOf('m') + 1)..^3], System.Globalization.CultureInfo.InvariantCulture);
                body = index + 1 < ChainLength
                    ? $"import {{ v as next }} from '{dir}m{index + 1}.js'; export const v = next;"
                    : $"export const v = {ChainLength};";
            }
            else
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            response.StatusCode = 200;
            response.ContentType = type;
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            // The client went away.
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
        try
        {
            _serving.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _client.Dispose();
    }
}
