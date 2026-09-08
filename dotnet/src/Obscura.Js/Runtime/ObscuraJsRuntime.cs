using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Obscura.Js.Modules;

namespace Obscura.Js.Runtime;

/// <summary>
/// The page's JavaScript runtime: one V8 isolate, the shared
/// <c>bootstrap.js</c> shim, the host event loop, module evaluation, and the
/// CDP object store.
/// </summary>
/// <remarks>
/// <para>
/// The Rust engine builds this on deno_core's <c>JsRuntime</c>; the port builds
/// it on ClearScript. The mapping is behavioral, not structural:
/// </para>
/// <list type="bullet">
/// <item>a deno_core <c>JsRuntime</c> is a <see cref="V8Runtime"/> (the isolate)
/// plus one <see cref="V8ScriptEngine"/> per realm (the context);</item>
/// <item><c>IsolateHandle::terminate_execution</c> is
/// <see cref="V8ScriptEngine.Interrupt"/> from a watchdog thread, with
/// interrupt propagation on so terminating the page also terminates its frame
/// realms;</item>
/// <item>deno_core's event loop is driven here, over
/// <see cref="TimerQueue"/> and host async ops.</item>
/// </list>
/// <para>
/// Unlike the Rust engine, which holds one isolate per process, ClearScript
/// supports many. The isolate is therefore never "entered" or "exited": that
/// whole dance (<c>EnteredRuntime</c>, <c>IsolateEntry</c>) exists in Rust only
/// because rusty_v8 leaves an isolate entered for life and a second page then
/// aborts the process. It has no counterpart here. Disposal is still
/// mandatory: a leaked <see cref="V8ScriptEngine"/> wedges the host.
/// </para>
/// </remarks>
public sealed partial class ObscuraJsRuntime : IDisposable, Obscura.Js.Ops.IPostedTaskSpawner
{
    private const ulong DefaultCdpAwaitTimeoutMs = 30_000;

    // Observation deadlines are checked between browser tasks. A task which has
    // already started receives this bounded completion allowance, matching the
    // fixed-wait path while retaining an absolute backstop for infinite script.
    private const ulong SynchronousTaskFloorMs = 5_000;
    private const ulong WatchdogSchedulingMarginMs = 500;
    private const long HeapLimitRecoveryHeadroomBytes = 64 * 1024 * 1024;

    private readonly V8Runtime _v8;
    private readonly V8ScriptEngine _engine;
    private readonly DenoCoreShim _shim;
    private readonly ObscuraModuleLoader _moduleLoader;
    private readonly V8IsolateHandle _isolateHandle;
    private readonly Dictionary<string, string> _objectStore = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _evaluationRecipes = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string?> _moduleEvaluations = [];
    private readonly Dictionary<string, string?> _evaluatedModuleSpecifiers = new(StringComparer.Ordinal);
    private readonly List<FrameRealm> _realms = [];
    private ulong _objectCounter;
    private int _pendingAsyncOps;
    private long _heapLimitBytes;
    private readonly Queue<Action<double>> _postedTasks = new();
    private bool _disposed;

    private ObscuraJsRuntime(string baseUrl, string? proxyUrl)
    {
        // A runtime is about to initialize the V8 platform; from here on a
        // SetV8Flags call must be refused rather than changing nothing silently.
        V8Flags.MarkPlatformStarted();

        var constraints = V8Flags.Constraints ?? new V8RuntimeConstraints();
        _v8 = new V8Runtime("obscura", constraints, V8RuntimeFlags.EnableDynamicModuleImports)
        {
            // A watchdog interrupt must reach every realm of this isolate, the
            // way terminate_execution does in Rust, not just the engine it was
            // armed on.
            EnableInterruptPropagation = true,
        };

        _moduleLoader = new ObscuraModuleLoader(baseUrl, proxyUrl);
        _engine = CreateRealmEngine();
        _isolateHandle = new V8IsolateHandle(_engine);
        ApplyHeapLimit(constraints);

        // The ops layer supplies the op table; a build without it still boots
        // bootstrap.js, which is what makes the runtime testable on its own.
        _ops.TaskSpawner = this;
        _shim = BootstrapLoader.Install(_engine, ops => BindOps(ops, mainRealm: true));
        InitializeObjectStore(_engine);
    }

    /// <summary>A runtime whose documents resolve against <c>about:blank</c>.</summary>
    public ObscuraJsRuntime() : this("about:blank", null) { }

    /// <summary>A runtime whose documents resolve against <paramref name="baseUrl"/>.</summary>
    public static ObscuraJsRuntime WithBaseUrl(string baseUrl) => new(baseUrl, null);

    /// <summary>
    /// A runtime whose ES-module loader routes dynamic imports through
    /// <paramref name="proxyUrl"/>. Null is equivalent to
    /// <see cref="WithBaseUrl"/> (direct connection).
    /// </summary>
    public static ObscuraJsRuntime WithBaseUrlAndProxy(string baseUrl, string? proxyUrl) =>
        new(baseUrl, proxyUrl);

    /// <summary>The main realm's script engine. Frames get their own.</summary>
    internal V8ScriptEngine Engine => _engine;

    /// <summary>The isolate every realm of this page shares.</summary>
    internal V8Runtime Isolate => _v8;

    /// <summary>
    /// This runtime's isolate, as the shared CDP watchdog names it. Stable for
    /// the isolate's life, so a per-command watchdog can be armed without
    /// borrowing the runtime.
    /// </summary>
    public IIsolateHandle IsolateHandleForWatchdog => _isolateHandle;

    /// <summary>The <c>Deno.core</c> shim installed in the main realm.</summary>
    internal DenoCoreShim Shim => _shim;

    /// <summary>The host timer queue behind <c>setTimeout</c>/<c>setInterval</c>.</summary>
    public TimerQueue Timers => _shim.Timers;

    /// <summary>
    /// The proxy the module loader routes dynamic imports through, if any.
    /// </summary>
    public string? ProxyUrl => _moduleLoader.ProxyUrl;

    /// <summary>
    /// Whether an event-loop task error is fatal to the page or page-local
    /// noise.
    /// </summary>
    /// <remarks>
    /// deno_core reports a task's uncaught exception and an unhandled promise
    /// rejection as an event-loop error, but a browser treats both as
    /// page-local: window.onerror / unhandledrejection fire and later tasks
    /// still run. Only engine-level failures (watchdog termination, the heap
    /// cap, an exhausted task budget) make the loop itself unusable. (#699)
    /// </remarks>
    public static bool IsFatalEventLoopError(string error) =>
        error.Contains("execution terminated", StringComparison.Ordinal)
        || error.Contains("heap limit exceeded", StringComparison.Ordinal)
        || error.Contains("task budget", StringComparison.Ordinal);

    // ---------------------------------------------------------------- realms

    /// <summary>
    /// Creates an additional realm in this isolate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Rust engine restores a second <c>v8::Context</c> from the startup
    /// snapshot, which arrives with the whole bootstrap already installed, so a
    /// realm costs a context restore rather than a re-parse. ClearScript
    /// exposes no snapshot API and no realm API, so the port creates a second
    /// <see cref="V8ScriptEngine"/> on the same <see cref="V8Runtime"/>: a
    /// second context in the same isolate, which is the part that matters for
    /// interrupt propagation and heap accounting, but one that has to re-run
    /// bootstrap.js. That is the single largest cost difference against the
    /// reference.
    /// </para>
    /// </remarks>
    internal V8ScriptEngine CreateRealmEngine()
    {
        var engine = _v8.CreateScriptEngine(
            V8ScriptEngineFlags.EnableTaskPromiseConversion
            | V8ScriptEngineFlags.EnableDynamicModuleImports
            | V8ScriptEngineFlags.EnableValueTaskPromiseConversion);
        // A JS number is a double. ClearScript otherwise narrows a lossless
        // double to float on the way out, which silently changes what a CDP
        // client sees for values like 0.1.
        engine.DisableFloatNarrowing = true;
        engine.EnableRuntimeInterruptPropagation = true;
        engine.AllowReflection = false;
        _moduleLoader.Install(engine);
        return engine;
    }

    private static void InitializeObjectStore(V8ScriptEngine engine) =>
        engine.Execute("<obscura:init>", "globalThis.__obscura_objects = {}; globalThis.__obscura_oid = 0;");

    // ------------------------------------------------------------- heap cap

    /// <summary>
    /// Applies the recoverable heap ceiling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rust installs a near-heap-limit callback that terminates the running
    /// script and raises the limit just enough for V8 to unwind instead of
    /// aborting the process, then restores the limit and re-arms. ClearScript
    /// exposes no near-heap-limit callback; its equivalent is
    /// <see cref="V8ScriptEngine.MaxRuntimeHeapSize"/> sampled every
    /// <see cref="V8ScriptEngine.RuntimeHeapSizeSampleInterval"/>.
    /// </para>
    /// <para>
    /// The policy choice is the load-bearing part, and it is the opposite of
    /// what it looks like.
    /// <see cref="V8RuntimeViolationPolicy.Exception"/> raises an ordinary
    /// script error, which page script catches - a hostile allocator can sit in
    /// <c>try { for(;;) alloc() } catch {}</c> and never yield, which is exactly
    /// what the cap exists to stop.
    /// <see cref="V8RuntimeViolationPolicy.Interrupt"/> is uncatchable, like
    /// Rust's terminate_execution, and the runtime it leaves behind is only
    /// *apparently* dead: raising the ceiling, collecting, and restoring it
    /// brings the isolate back, which is the same unwind-then-restore dance the
    /// Rust callback performs. See <see cref="RecoverHeapLimit"/>.
    /// </para>
    /// <para>
    /// The difference that remains: Rust's callback fires at V8's *own* default
    /// heap limit with no configuration, so an unconfigured Rust page is still
    /// protected. Here the ceiling only exists once <c>--max-old-space-size</c>
    /// is supplied (or <see cref="SetHeapLimit"/> is called); with no limit set,
    /// V8's internal OOM still aborts the process.
    /// </para>
    /// </remarks>
    private void ApplyHeapLimit(V8RuntimeConstraints constraints)
    {
        if (constraints.MaxOldSpaceSize > 0)
        {
            SetHeapLimit((long)constraints.MaxOldSpaceSize * 1024 * 1024);
        }
    }

    /// <summary>
    /// Caps the isolate's heap. Exceeding it terminates the running script with
    /// a recoverable error rather than aborting the process.
    /// </summary>
    public void SetHeapLimit(long bytes)
    {
        _heapLimitBytes = Math.Max(0, bytes);
        if (_heapLimitBytes == 0)
        {
            _engine.MaxRuntimeHeapSize = UIntPtr.Zero;
            return;
        }
        _engine.MaxRuntimeHeapSize = (UIntPtr)_heapLimitBytes;
        _engine.RuntimeHeapSizeSampleInterval = TimeSpan.FromMilliseconds(10);
        _engine.RuntimeHeapSizeViolationPolicy = V8RuntimeViolationPolicy.Interrupt;
    }

    private static bool IsHeapLimitFailure(Exception error) =>
        error.Message.Contains("exceeded its memory limit", StringComparison.Ordinal);

    /// <summary>
    /// Restore the configured heap ceiling after the emergency headroom has let
    /// a terminated allocation unwind, so a second hostile script cannot grow
    /// the isolate without bound.
    /// </summary>
    private bool RecoverHeapLimit()
    {
        if (_heapLimitBytes == 0)
        {
            return false;
        }
        // Raise first: collecting while still over the ceiling trips the guard
        // again and the isolate never comes back.
        _engine.MaxRuntimeHeapSize = (UIntPtr)(_heapLimitBytes + HeapLimitRecoveryHeadroomBytes);
        try
        {
            _engine.CollectGarbage(exhaustive: true);
        }
        catch (ScriptEngineException)
        {
            // Nothing more to reclaim; the restore below still re-arms the cap.
        }
        _engine.MaxRuntimeHeapSize = (UIntPtr)_heapLimitBytes;
        return true;
    }

    // ------------------------------------------------------------ watchdog

    /// <summary>
    /// Arm a hard wall-clock backstop on synchronous V8 work.
    /// </summary>
    /// <remarks>
    /// A page stuck in a synchronous loop or a microtask storm pins the thread
    /// inside V8, so a timeout observed only at await points never fires. This
    /// starts a watchdog thread that interrupts the isolate once
    /// <paramref name="budget"/> elapses, forcing V8 to raise an uncatchable
    /// error and hand control back. Always balance with
    /// <see cref="DisarmWatchdog"/>.
    /// </remarks>
    public WatchdogToken ArmWatchdog(TimeSpan budget) => Watchdog.Spawn(_engine, budget);

    /// <summary>
    /// Stop a watchdog armed by <see cref="ArmWatchdog"/>. If it had already
    /// fired, clear V8's interrupt so the isolate is usable again and return
    /// true.
    /// </summary>
    public bool DisarmWatchdog(WatchdogToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var fired = token.Stop();
        token.Dispose();
        if (fired)
        {
            CancelTermination();
        }
        return fired;
    }

    /// <summary>
    /// Clear a pending interrupt after a watchdog fired, so the isolate is
    /// usable for the next command. A no-op when nothing is pending.
    /// </summary>
    public void CancelTermination()
    {
        try
        {
            _engine.CancelInterrupt();
        }
        catch (ObjectDisposedException)
        {
            // Nothing left to un-interrupt.
        }
    }

    // ------------------------------------------------------- script execution

    /// <summary>
    /// Freeze the document timeline for one JavaScript task. Browser timelines
    /// update at task/rendering boundaries, not on each forced style or layout
    /// read.
    /// </summary>
    private void BeginJavaScriptTask() => BeginAnimationTask();

    partial void BeginAnimationTask();

    /// <summary>
    /// Binds the op table for a realm. Implemented by the state-owning half of
    /// this class; a build without the ops layer simply has no ops bound.
    /// </summary>
    partial void BindOps(ScriptObject ops, bool mainRealm);

    private object? ExecuteRuntimeScript(string name, string source)
    {
        try
        {
            return _engine.Evaluate(new DocumentInfo(name), source);
        }
        catch (ScriptInterruptedException)
        {
            throw new JsRuntimeException("JS error: Uncaught Error: execution terminated");
        }
        catch (ScriptEngineException error)
        {
            if (IsHeapLimitFailure(error))
            {
                RecoverHeapLimit();
                throw new JsRuntimeException("JavaScript heap limit exceeded; execution terminated");
            }
            throw new JsRuntimeException($"JS error: Uncaught {error.Message}");
        }
    }

    /// <summary>Runs a classic script, reporting a script error as a throw.</summary>
    /// <remarks>
    /// <paramref name="name"/> is the script URL, and V8 uses it as
    /// <c>import()</c>'s referrer, so it is passed through as the document
    /// name rather than replaced by a fixed placeholder.
    /// </remarks>
    public void ExecuteScript(string name, string source)
    {
        BeginJavaScriptTask();
        try
        {
            _engine.Execute(DocumentInfoFor(name), source);
        }
        catch (ScriptInterruptedException)
        {
            throw new JsRuntimeException("JS error: Uncaught Error: execution terminated");
        }
        catch (ScriptEngineException error)
        {
            if (IsHeapLimitFailure(error))
            {
                RecoverHeapLimit();
                throw new JsRuntimeException("JavaScript heap limit exceeded; execution terminated");
            }
            RecordUncaughtException(error, name);
            throw new JsRuntimeException($"JS error: Uncaught {error.Message}");
        }
    }

    partial void RecordUncaughtException(ScriptEngineException error, string fallbackUrl);

    /// <summary>
    /// Runs a classic script, bounding a large one with the default five-second
    /// watchdog. Small scripts are not worth a watchdog thread.
    /// </summary>
    public void ExecuteScriptGuarded(string name, string source)
    {
        if (source.Length < 10_000)
        {
            ExecuteScript(name, source);
        }
        else
        {
            ExecuteScriptWithTimeout(name, source, TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Runs a classic script under a watchdog. A script killed by the watchdog
    /// is reported as success, exactly as in the reference: the page is not
    /// broken, one script simply ran out of time.
    /// </summary>
    public void ExecuteScriptWithTimeout(string name, string source, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            ExecuteScript(name, source);
            return;
        }

        var token = ArmWatchdog(timeout);
        try
        {
            ExecuteScript(name, source);
        }
        catch (JsRuntimeException error)
        {
            if (!error.Message.Contains("Uncaught Error: execution terminated", StringComparison.Ordinal))
            {
                throw;
            }
        }
        finally
        {
            DisarmWatchdog(token);
        }
    }

    private static DocumentInfo DocumentInfoFor(string name) =>
        Uri.TryCreate(name, UriKind.Absolute, out var uri) ? new DocumentInfo(uri) : new DocumentInfo(name);

    /// <summary>Run <c>__obscura_init()</c> after every per-page property is set.</summary>
    public void RunPageInit()
    {
        try
        {
            ExecuteRuntimeScript("<obscura:page-init>", "globalThis.__obscura_init();");
        }
        catch (JsRuntimeException)
        {
            // The reference ignores the result here: page init runs against
            // whatever surfaces this build has.
        }
    }

    // ---------------------------------------------------------- profile setters

    public void SetUserAgent(string userAgent) =>
        RunSetter("<set-ua>", $"globalThis.__obscura_ua = {JsStringLiteral(userAgent)};");

    public void SetPlatform(string platform, string uaPlatform, string uaPlatformVersion) =>
        RunSetter(
            "<set-platform>",
            $"globalThis.__obscura_platform={JsStringLiteral(platform)};"
            + $"globalThis.__obscura_ua_platform={JsStringLiteral(uaPlatform)};"
            + $"globalThis.__obscura_ua_platform_version={JsStringLiteral(uaPlatformVersion)};");

    public void SetStealth(bool enabled) =>
        RunSetter("<set-stealth>", $"globalThis.__obscura_stealth = {(enabled ? "true" : "false")};");

    /// <summary>
    /// Override the coordinates the <c>navigator.geolocation</c> shim reports.
    /// Callers validate the range before calling.
    /// </summary>
    public void SetGeolocation(double latitude, double longitude) =>
        RunSetter(
            "<set-geo>",
            $"globalThis.__obscura_geo_lat={Number(latitude)};globalThis.__obscura_geo_lon={Number(longitude)};");

    /// <summary>
    /// Set the CSS viewport exposed to page JavaScript. Must run before
    /// <see cref="RunPageInit"/> for navigation-time responsive code; CDP
    /// emulation may also call it later to update the live window surfaces.
    /// </summary>
    public void SetViewport(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return;
        }
        SetRenderViewport((float)width, (float)height);
        RunSetter(
            "<set-viewport>",
            $"globalThis.__obscura_viewport_w={Number(width)};"
            + $"globalThis.__obscura_viewport_h={Number(height)};"
            + $"globalThis.innerWidth={Number(width)};globalThis.innerHeight={Number(height)};"
            + "if(globalThis.visualViewport){"
            + $"globalThis.visualViewport.width={Number(width)};"
            + $"globalThis.visualViewport.height={Number(height)};"
            + "}"
            + "if(typeof globalThis.__obscura_recompute_intersections==='function'){"
            + "globalThis.__obscura_recompute_intersections();"
            + "}"
            + "if(typeof globalThis.__obscura_recompute_resizes==='function'){"
            + "globalThis.__obscura_recompute_resizes();"
            + "}");
    }

    partial void SetRenderViewport(float width, float height);

    /// <summary>
    /// Override the physical screen metrics exposed to page JavaScript. Unlike
    /// the CSS viewport, CDP only changes these when both screen dimensions are
    /// supplied; null restores the native screen surface while keeping the
    /// viewport override intact.
    /// </summary>
    public void SetScreenSizeOverride((double Width, double Height)? size, bool emulated)
    {
        var script = size is { } value
            && double.IsFinite(value.Width) && double.IsFinite(value.Height)
            && value.Width > 0 && value.Height > 0
            ? $"globalThis.__obscura_set_screen_override({Number(value.Width)},{Number(value.Height)},{(emulated ? "true" : "false")});"
            : $"globalThis.__obscura_set_screen_override(null,null,{(emulated ? "true" : "false")});";
        RunSetter("<set-screen-size>", script);
    }

    private void RunSetter(string name, string source)
    {
        try
        {
            ExecuteRuntimeScript(name, source);
        }
        catch (JsRuntimeException)
        {
            // Every profile setter in the reference discards its result: a shim
            // that is not present in this build must not fail page setup.
        }
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Embed a string as a JS string literal for safe interpolation into
    /// generated script.
    /// </summary>
    /// <remarks>
    /// A hand-rolled quote replacement misses backslashes and control
    /// characters, which either breaks out of the literal or raises a
    /// SyntaxError. JSON string encoding covers both (SEC-301 / SEC-302 / #792).
    /// </remarks>
    internal static string JsStringLiteral(string value) => JsonSerializer.Serialize(value);

    // -------------------------------------------------------- async op tracking

    /// <summary>
    /// Records that a host async op is in flight, so the event loop knows the
    /// page is not idle. deno_core tracks this itself; here the ops layer
    /// reports it.
    /// </summary>
    public IDisposable TrackAsyncOp()
    {
        Interlocked.Increment(ref _pendingAsyncOps);
        return new AsyncOpScope(this);
    }

    private bool HasPendingAsyncOps => Volatile.Read(ref _pendingAsyncOps) > 0;

    private sealed class AsyncOpScope(ObscuraJsRuntime runtime) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Interlocked.Decrement(ref runtime._pendingAsyncOps);
            }
        }
    }

    // ------------------------------------------------------------------ dispose

    /// <summary>
    /// Disposes every realm engine and then the isolate.
    /// </summary>
    /// <remarks>
    /// Not optional: unlike the Rust engine, which holds one isolate per
    /// process, ClearScript supports many, and a leaked
    /// <see cref="V8ScriptEngine"/> wedges the host process.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var realm in _realms.ToArray())
        {
            realm.Dispose();
        }
        _realms.Clear();
        _engine.Dispose();
        _v8.Dispose();
    }

    internal void RegisterRealm(FrameRealm realm) => _realms.Add(realm);

    internal void ForgetRealm(FrameRealm realm) => _realms.Remove(realm);
}
