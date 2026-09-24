using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.ClearScript;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Net;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// The runtime's half of the per-page state hub.
/// </summary>
/// <remarks>
/// Everything here forwards to <see cref="PocketCalculatorState"/>, which the ops layer
/// owns. The Rust engine holds it as
/// <c>Rc&lt;RefCell&lt;PocketCalculatorState&gt;&gt;</c> and shares that handle with the
/// op state; the port shares a plain object reference, because the engine is
/// single-threaded per page.
/// </remarks>
public sealed partial class PocketCalculatorJsRuntime
{
    /// <summary>
    /// The bound op table, and the page state it acts on.
    /// </summary>
    /// <remarks>
    /// This is the port of <c>build_extension()</c>: deno_core binds ops through
    /// an extension into the main context only, ClearScript through a plain
    /// object the bootstrap loader exposes as <c>Deno.core.ops</c>. One instance
    /// serves every realm of this page - the ops resolve the calling realm from
    /// the frame id its bootstrap closure passes, which is what
    /// <c>share_ops_with_realm</c> achieves in Rust by handing the child realm
    /// the parent's bound function objects.
    /// </remarks>
    private readonly PocketCalculatorOps _ops = new(new PocketCalculatorState());

    /// <summary>The page realm's state. Frame realms have their own.</summary>
    public PocketCalculatorState State => _ops.Page;

    /// <summary>The table ops consult to find the calling realm's document.</summary>
    public RealmStates RealmStates => _ops.Realms;

    /// <summary>The op table itself, for a host that needs to reach an op directly.</summary>
    public PocketCalculatorOps Ops => _ops;

    partial void BindOps(ScriptObject ops, bool mainRealm)
    {
        _ = mainRealm;
        _ops.BindTo(ops);
    }

    /// <summary>
    /// Binds the op table into a frame realm. The same table: a frame's ops must
    /// see the same page state, the same pending-frame queue and the same
    /// in-flight counter as the page, and they select the frame's own document by
    /// the frame id the realm passes.
    /// </summary>
    internal void BindRealmOps(ScriptObject ops, PocketCalculatorState state)
    {
        ArgumentNullException.ThrowIfNull(ops);
        _ops.BindTo(ops);
        // The realm-sensitive ops must resolve against THIS frame's state, not the
        // page's. Sharing the page's bindings made every op that asks "which realm is
        // calling" answer "the page" once the call came from a promise continuation
        // rather than from inside FrameRealm.Run.
        _ops.BindRealmOverrides(ops, state);
        // A frame realm cannot use the host timer queue (see TimerQueue), so
        // bootstrap.js schedules every frame timer as `op_sleep(...).then(...)`.
        // That has to count as work in flight or the page's loop reports idle
        // and returns before any frame timer is due, which silently drops every
        // setTimeout a frame makes.
        _ops.BindRealmAsyncOp(ops, "op_sleep", (Func<object?, Task>)(millis => RealmSleepAsync(millis)));
    }

    /// <summary>
    /// <c>op_sleep</c> for a frame realm, counted as an async op in flight.
    /// </summary>
    /// <remarks>
    /// The op is counted by its binding, not here. This used to hold the op open
    /// for 5ms past the delay, because the promise resolves only once this task
    /// completes and the frame's callback runs in the microtask after that, so
    /// releasing it at completion let the loop observe idle in between and return
    /// with the callback still queued. That was the same defect every async op has,
    /// answered with a timeout; <see cref="PocketCalculator.Js.Ops.AsyncOpBinding"/> answers
    /// it by construction for all of them.
    /// </remarks>
    private static async Task RealmSleepAsync(object? millis)
    {
        var delay = millis switch
        {
            null => 0.0,
            double number => number,
            IConvertible convertible => convertible.ToDouble(CultureInfo.InvariantCulture),
            _ => 0.0,
        };
        if (!double.IsFinite(delay) || delay < 0)
        {
            delay = 0;
        }
        await Task.Delay(TimeSpan.FromMilliseconds(delay)).ConfigureAwait(false);
    }

    /// <summary>
    /// Queues one posted-task delivery onto this runtime's event loop.
    /// </summary>
    /// <remarks>
    /// <c>op_posted_task</c> is the shim's macrotask source. deno_core spawns it
    /// onto the Tokio local set; here it lands in a host queue the pump drains,
    /// which keeps it a task and not a microtask - the distinction the shim
    /// relies on to keep recursive schedulers from starving timers.
    /// </remarks>
    void IPostedTaskSpawner.Spawn(Action<double> deliver)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        _postedTasks.Enqueue(deliver);
    }

    partial void BeginAnimationTask() => RenderState.BeginAnimationTask(State);

    partial void ReadActivityGeneration(ref ulong generation) => generation = State.ActivityGeneration;

    partial void ReadPendingNetworkRequests(ref bool pending) => pending = State.PageInFlight.Value > 0;

    partial void SetRenderViewport(float width, float height)
    {
        var viewport = (width, height);
        if (State.Viewport == viewport)
        {
            return;
        }
        State.Viewport = viewport;
        State.PreparedRender = null;
        State.PendingStyleMutations.Clear();
        State.ResolvedScroll = null;
    }

    // ---------------------------------------------------------------- page state

    public void SetCookieJar(CookieJar jar) => State.CookieJar = jar;

    public void SetHttpClient(PocketCalculatorHttpClient client)
    {
        State.HttpClient = client;
        // A page transport makes the renderer cache-only; see FreshRenderResources for
        // why layout must not fetch by itself (upstream 97ff86d).
        State.RenderResources.SetSyncLoadingEnabled(false);
    }

    /// <summary>
    /// Install the owning page's passive on_request/on_response callback
    /// registry so scripted fetch()/XHR observation is page-scoped (#408).
    /// </summary>
    public void SetCallbacks(CallbackRegistry callbacks)
    {
        State.Callbacks = callbacks;
        // Network-layer console messages (mixed content) arrive on transport threads;
        // they queue here and join the console events on the next drain.
        callbacks.ConsoleSink = EnqueueHostConsole;
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Level, string Text, double Timestamp)>
        _hostConsole = new();

    private void EnqueueHostConsole(string level, string text)
    {
        while (_hostConsole.Count >= 1_024)
        {
            _hostConsole.TryDequeue(out _);
        }

        _hostConsole.Enqueue((level, text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private void DrainHostConsole()
    {
        while (_hostConsole.TryDequeue(out var entry))
        {
            if (!State.RuntimeEventsEnabled)
            {
                continue;
            }

            while (State.PendingRuntimeEvents.Count >= 1_024)
            {
                State.PendingRuntimeEvents.Dequeue();
            }

            State.PendingRuntimeEvents.Enqueue(new RuntimeEvent.Console(new RuntimeConsoleEvent(
                entry.Level,
                [new JsonObject { ["type"] = "string", ["value"] = entry.Text }],
                entry.Timestamp)));
        }
    }

    /// <summary>
    /// Install the stealth HTTP client so scripted fetch()/XHR is routed
    /// through it in stealth mode.
    /// </summary>
    public void SetStealthClient(IStealthHttpClient client)
    {
        State.StealthClient = client;
        if (client.IsAvailable)
        {
            State.RenderResources.SetSyncLoadingEnabled(false);
        }
    }

    /// <summary>Install a fresh document. A new document owns fresh page state.</summary>
    public void SetDom(DomTree dom)
    {
        DetachDomGc();
        State.Dom = dom;
        AttachDomGc(dom);
        State.DocumentGeneration = unchecked(State.DocumentGeneration + 1);
        State.ActivityGeneration = 0;
        State.PageInFlight = new InFlightCounter();
        State.AlreadyStartedScripts.Clear();
        // A document owns its own performance timeline, so the subresources the
        // previous one pulled in must not show up in this one's entries.
        State.ResourceTimings.Clear();
        // A new document owns a fresh retained scene and resource cache.
        State.PreparedRender = null;
        State.AnimationSample = default;
        State.AnimationTimeline = new PocketCalculator.Render.AnimationTimelineState();
        State.ResetAnimationTimelineOrigin();
        State.AnimationTaskGeneration = 0;
        State.AnimationSampledTaskGeneration = 0;
        State.PendingStyleMutations.Clear();
        State.RenderResources = FreshRenderResources(State);
        State.RenderImageInFlight.Clear();
        AbandonRenderResources();
        State.StylesheetCache = new PocketCalculator.Render.Css.StylesheetCache();
        State.DynamicFonts.Clear();
        State.CanvasSurfaces.Clear();
        State.ScrollOffset = (0.0f, 0.0f);
        State.ElementScrollOffsets.Clear();
        State.ScrollGeneration = 0;
        State.ResolvedScroll = null;
    }

    public void SetUrl(string url)
    {
        if (string.Equals(State.Url, url, StringComparison.Ordinal))
        {
            return;
        }
        State.Url = url;
        // Relative resources use the document URL when no <base> is present.
        // Keep already-fetched absolute bytes, but rebuild candidate selection
        // and layout against the new base.
        State.PreparedRender = null;
        State.PendingStyleMutations.Clear();
        State.ResolvedScroll = null;
    }

    /// <summary>
    /// Set the document's character encoding (WHATWG canonical name). Backs
    /// <c>document.characterSet</c> and the query-encoding override for
    /// <c>&lt;a&gt;</c>/<c>&lt;area&gt;</c> hrefs in legacy-charset documents.
    /// </summary>
    public void SetEncoding(string encoding) => State.Encoding = encoding;

    public void SetTitle(string title) => State.Title = title;

    /// <summary>
    /// Set the source document URL exposed as <c>document.referrer</c>.
    /// Navigation owns this value; it is not derived from the current URL,
    /// because direct and document-initiated navigations have different
    /// referrer semantics.
    /// </summary>
    public void SetReferrer(string referrer) => State.Referrer = referrer;

    public void SetBlockedUrls(IEnumerable<string> patterns)
    {
        State.BlockedUrls.Clear();
        State.BlockedUrls.AddRange(patterns);
    }

    /// <summary>
    /// Record that the host just dispatched activation-triggering user input, which gives
    /// the page transient activation for navigations it starts next.
    /// </summary>
    public void NoteUserActivation() => State.UserActivationTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

    public PendingNavigation? TakePendingNavigation()
    {
        var pending = State.PendingNavigation;
        State.PendingNavigation = null;
        return pending;
    }

    public IReadOnlyList<(string Name, string Payload)> TakePendingBindingCalls()
    {
        var calls = State.PendingBindingCalls.ToArray();
        State.PendingBindingCalls.Clear();
        State.PendingBindingCallBytes = 0;
        return calls;
    }

    /// <summary>
    /// Drain console calls and uncaught exceptions, retaining a retrieval
    /// expression for every console argument reported by reference so a later
    /// <c>Runtime.getProperties</c> can still reach it.
    /// </summary>
    public IReadOnlyList<RuntimeEvent> TakePendingRuntimeEvents()
    {
        DrainHostConsole();
        var events = State.PendingRuntimeEvents.ToArray();
        State.PendingRuntimeEvents.Clear();
        foreach (var entry in events)
        {
            if (entry is not RuntimeEvent.Console console)
            {
                continue;
            }
            foreach (var arg in console.Event.Args)
            {
                if (arg is not JsonObject obj
                    || !obj.TryGetPropertyValue("objectId", out var node)
                    || node?.GetValueKind() != System.Text.Json.JsonValueKind.String)
                {
                    continue;
                }
                var objectId = node.GetValue<string>();
                // A frame's console handle stays in that frame's realm, which the page
                // realm cannot reach (FrameRealm.PublishRealmObjects): it reads as
                // undefined here, as it did through the Rust engine's frame registry.
                _objectStore[objectId] = CdpScope.Retrieval(objectId);
            }
        }
        return events;
    }

    public void SetRuntimeEventsEnabled(bool enabled) => State.RuntimeEventsEnabled = enabled;

    partial void RecordUncaughtException(ScriptEngineException error, string fallbackUrl)
    {
        if (!State.RuntimeEventsEnabled)
        {
            return;
        }
        State.RuntimeExceptionCounter += 1;
        var frames = ParseStackFrames(error, fallbackUrl);
        var first = frames.Count > 0 ? frames[0] : null;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (State.PendingRuntimeEvents.Count >= 1_024)
        {
            State.PendingRuntimeEvents.Dequeue();
        }
        State.PendingRuntimeEvents.Enqueue(new RuntimeEvent.Exception(new RuntimeExceptionEvent(
            State.RuntimeExceptionCounter,
            ErrorName(error.Message),
            error.ErrorDetails ?? error.Message,
            first is null ? fallbackUrl : first.Url,
            first?.LineNumber ?? 0,
            first?.ColumnNumber ?? 0,
            frames.Select(frame => (JsonNode?)new JsonObject
            {
                ["functionName"] = frame.FunctionName,
                ["scriptId"] = string.Empty,
                ["url"] = frame.Url,
                ["lineNumber"] = frame.LineNumber,
                ["columnNumber"] = frame.ColumnNumber,
            }).ToList(),
            timestamp)));
    }

    private sealed record StackFrame(string FunctionName, string Url, long LineNumber, long ColumnNumber);

    /// <summary>
    /// Rebuild CDP stack frames from ClearScript's error details.
    /// </summary>
    /// <remarks>
    /// deno_core hands the reference a structured <c>JsError</c> with parsed
    /// frames. ClearScript hands over V8's rendered stack text, so the frames
    /// are recovered from it. CDP line and column numbers are zero-based while
    /// V8's are one-based, which is where the subtraction comes from.
    /// </remarks>
    private static List<StackFrame> ParseStackFrames(ScriptEngineException error, string fallbackUrl)
    {
        var frames = new List<StackFrame>();
        var details = error.ErrorDetails;
        if (string.IsNullOrEmpty(details))
        {
            return frames;
        }
        foreach (var raw in details.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("at ", StringComparison.Ordinal))
            {
                continue;
            }
            var body = line[3..];
            var arrow = body.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                body = body[..arrow];
            }
            var functionName = string.Empty;
            var open = body.IndexOf(" (", StringComparison.Ordinal);
            if (open >= 0 && body.EndsWith(')'))
            {
                functionName = body[..open];
                body = body[(open + 2)..^1];
            }
            var (url, lineNumber, columnNumber) = SplitLocation(body, fallbackUrl);
            frames.Add(new StackFrame(functionName, url, lineNumber, columnNumber));
        }
        return frames;
    }

    private static (string Url, long Line, long Column) SplitLocation(string location, string fallbackUrl)
    {
        var lastColon = location.LastIndexOf(':');
        if (lastColon <= 0)
        {
            return (location.Length == 0 ? fallbackUrl : location, 0, 0);
        }
        var previousColon = location.LastIndexOf(':', lastColon - 1);
        if (previousColon <= 0
            || !long.TryParse(location[(lastColon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var column)
            || !long.TryParse(location[(previousColon + 1)..lastColon], NumberStyles.Integer, CultureInfo.InvariantCulture, out var line))
        {
            return (location, 0, 0);
        }
        var url = location[..previousColon];
        return (url.Length == 0 ? fallbackUrl : url, Math.Max(0, line - 1), Math.Max(0, column - 1));
    }

    private static string ErrorName(string message)
    {
        var colon = message.IndexOf(':', StringComparison.Ordinal);
        var name = colon > 0 ? message[..colon] : message;
        return name.Length == 0 || name.Contains(' ', StringComparison.Ordinal) ? "Error" : name;
    }

    public StoredNetworkResponseBody? GetNetworkResponseBody(string requestId) =>
        State.NetworkResponseBodies.GetValueOrDefault(requestId);

    public void ClearNetworkResponseBodies()
    {
        State.NetworkResponseBodies.Clear();
        State.NetworkResponseBodyOrder.Clear();
    }

    /// <summary>
    /// Wire up the interception channel without enabling interception.
    /// </summary>
    /// <remarks>
    /// The two were entangled before, so every navigation auto-enabled
    /// interception and <c>fetch()</c> from page JS hung forever waiting for a
    /// CDP client to answer Fetch.requestPaused events it never asked for.
    /// </remarks>
    public void SetInterceptSink(IInterceptSink sink) => State.InterceptTx = sink;

    public void SetInterceptEnabled(bool enabled) => State.InterceptEnabled = enabled;

    /// <summary>
    /// Frame documents fetched by any realm that still need one of their own.
    /// The op queues onto the page's state whichever frame asked, so a frame
    /// nested inside a frame is drained here too.
    /// </summary>
    public IReadOnlyList<PendingFrame> TakePendingFrames()
    {
        State.PendingFrameBytes = 0;
        var frames = State.PendingFrames.ToArray();
        State.PendingFrames.Clear();
        return frames;
    }

    /// <summary>postMessage traffic waiting to be delivered to another realm.</summary>
    public IReadOnlyList<PendingFrameMessage> TakePendingFrameMessages()
    {
        State.PendingFrameMessageBytes = 0;
        var messages = State.PendingFrameMessages.ToArray();
        State.PendingFrameMessages.Clear();
        return messages;
    }

    /// <summary>
    /// Generation of observable connected-document mutations. Excludes detached
    /// construction and no-op writes, which cannot affect a screenshot or a DOM
    /// dump.
    /// </summary>
    public ulong ActivityGeneration => State.ActivityGeneration;

    /// <summary>
    /// Absolute URLs the page requested via fetch()/XHR, in request order
    /// (#301). Backs <c>--dump assets</c>.
    /// </summary>
    public IReadOnlyList<string> FetchedUrls => State.FetchedUrls.ToArray();

    /// <summary>
    /// Drain the network events recorded for script-initiated requests. The
    /// Page moves these into its own network events so the CDP layer emits
    /// Network events for them (#406).
    /// </summary>
    public IReadOnlyList<JsNetworkEvent> TakeJsNetworkEvents()
    {
        var events = State.JsNetworkEvents.ToArray();
        State.JsNetworkEvents.Clear();
        return events;
    }

    /// <summary>
    /// Record one subresource the browser transport fetched for this document, so
    /// <c>performance.getEntriesByType('resource')</c> reports it.
    /// </summary>
    /// <remarks>
    /// Scripted fetch()/XHR records itself from inside <c>op_fetch_url</c>; this is
    /// the entry point for the fetches the Page owns - external scripts, stylesheets,
    /// images and fonts - which page JS never sees go out. Timestamps are unix-epoch
    /// milliseconds: the shim, not the host, knows the document's
    /// <c>performance.timeOrigin</c>.
    /// </remarks>
    public void RecordResourceTiming(
        string url,
        string initiatorType,
        int status,
        double startedAtUnixMs,
        double endedAtUnixMs,
        long decodedBodySize,
        long encodedBodySize,
        string contentType) =>
        PerformanceOps.RecordResourceTiming(
            State,
            new ResourceTimingRecord
            {
                Url = url,
                InitiatorType = initiatorType,
                Status = status,
                StartedAtUnixMs = startedAtUnixMs,
                EndedAtUnixMs = endedAtUnixMs,
                DecodedBodySize = decodedBodySize,
                EncodedBodySize = encodedBodySize,
                ContentType = contentType,
            });

    /// <summary>The subresources recorded for the current document, in completion order.</summary>
    public IReadOnlyList<ResourceTimingRecord> ResourceTimings => State.ResourceTimings;

    public DomTree? TakeDom()
    {
        State.PreparedRender = null;
        State.PendingStyleMutations.Clear();
        State.RenderResources = FreshRenderResources(State);
        AbandonRenderResources();
        State.StylesheetCache = new PocketCalculator.Render.Css.StylesheetCache();
        State.DynamicFonts.Clear();
        State.ElementScrollOffsets.Clear();
        State.ResolvedScroll = null;
        DetachDomGc();
        var dom = State.Dom;
        State.Dom = null;
        return dom;
    }

    /// <summary>
    /// Export document-owned script preparation state before the runtime realm
    /// is temporarily destroyed. Page suspension keeps the DOM alive, so the
    /// HTML "already started" flags travel with it rather than resetting like
    /// window-global JavaScript state.
    /// </summary>
    public IReadOnlyList<uint> StartedScriptIds() =>
        State.AlreadyStartedScripts.Select(id => id.Raw).Order().ToArray();

    /// <summary>
    /// Restore script preparation state only onto script nodes in the current
    /// DOM. For a DomTree surviving a suspend/resume cycle only; ordinary
    /// navigation starts from an empty set.
    /// </summary>
    public void RestoreStartedScriptIds(IEnumerable<uint> ids)
    {
        if (State.Dom is not { } dom)
        {
            return;
        }
        foreach (var id in ids)
        {
            var nodeId = NodeId.New(id);
            if (StateHelpers.NodeIsScript(dom, nodeId))
            {
                State.AlreadyStartedScripts.Add(nodeId);
            }
        }
    }

    public T? WithDom<T>(Func<DomTree, T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return State.Dom is { } dom ? body(dom) : default;
    }

    public DomTree? DomRef => State.Dom;

    /// <summary>
    /// Parse and merge an inline document import map. Rules which would alter
    /// already-observed module resolutions are discarded while unrelated new
    /// rules remain available, matching Chromium's multiple-map model.
    /// </summary>
    public void AddImportMap(string source, string baseUrl)
    {
        if (!_moduleLoader.ImportMap.MergeParsed(source, baseUrl, out var error))
        {
            throw new JsRuntimeException(error ?? "Import map could not be parsed");
        }
    }

    /// <summary>
    /// The origin of the document this runtime is running, or <c>"null"</c> for
    /// a scheme that has no tuple origin.
    /// </summary>
    public string PageOrigin => FrameRealm.OriginOf(State.Url);

    /// <summary>
    /// Gives a frame's state the resources the page owns: cookie jar, HTTP
    /// client, callbacks and the stealth transport. A frame shares these with
    /// its page, exactly as it shares them in a browser.
    /// </summary>
    public void ShareResourcesWith(PocketCalculatorState frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        frame.CookieJar = State.CookieJar;
        frame.HttpClient = State.HttpClient;
        frame.Callbacks = State.Callbacks;
        frame.Encoding = State.Encoding;
        frame.BlockedUrls.Clear();
        frame.BlockedUrls.AddRange(State.BlockedUrls);
        frame.InterceptEnabled = State.InterceptEnabled;
        frame.PageInFlight = State.PageInFlight;
        frame.StealthClient = State.StealthClient;
        // A frame realm shares the page transport, so its renderer cache must not open
        // synchronous requests either (upstream 97ff86d). Frame geometry resolves
        // against the main document's renderer state, so no frame-scoped loading.
        if (HasTransport(State))
        {
            frame.RenderResources.SetSyncLoadingEnabled(false);
        }
    }

    // -------------------------------------------------------------- render state

    /// <summary>Current clamped root scroll offset shared by CSSOM geometry and paint.</summary>
    public (float X, float Y) ScrollOffset => RenderState.ClampScrollOffset(State, State.ScrollOffset);

    /// <summary>
    /// Select the CSS media type for the next synchronous render flush.
    /// Changing media invalidates geometry and the compiled stylesheet key but
    /// leaves the live DOM, scroll offsets and resource bytes untouched.
    /// </summary>
    public PocketCalculator.Render.Css.CssMediaType SetRenderMedia(PocketCalculator.Render.Css.CssMediaType media)
    {
        var previous = State.RenderMedia;
        if (previous != media)
        {
            State.RenderMedia = media;
            State.PreparedRender = null;
            State.ResolvedScroll = null;
        }
        return previous;
    }

    public PocketCalculator.Render.AnimationSampleTime AnimationSampleTime => State.AnimationSample.Time;

    public PocketCalculator.Render.AnimationSample LiveAnimationSample =>
        PocketCalculator.Render.AnimationSample.Document(
            (float)Math.Min(State.AnimationTimelineElapsedMilliseconds, float.MaxValue));

    public void ResetAnimationTimeline()
    {
        State.ResetAnimationTimelineOrigin();
        State.AnimationTimeline = new PocketCalculator.Render.AnimationTimelineState();
        State.AnimationSample = default;
        State.PreparedRender = null;
        State.PendingStyleMutations.Clear();
        State.ResolvedScroll = null;
    }

    /// <summary>
    /// Read animation damage from the last prepared frame without causing a
    /// style or layout flush. Screencast scheduling uses this to avoid
    /// rasterizing static pages on every compositor tick.
    /// </summary>
    public bool PreparedHasActiveCssAnimations =>
        State.PreparedRender?.HasActiveCssAnimations() ?? false;
}
