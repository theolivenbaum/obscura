using System.Globalization;
using System.Text.Json.Nodes;
using Obscura.Js.Modules;
using Obscura.Js.Ops;

namespace Obscura.Cdp;

/// <summary>Method routing: one CDP request in, one CDP response out.</summary>
public static class Dispatcher
{
    /// <summary>
    /// Whether a CDP method can be served WITHOUT acquiring the per-connection V8
    /// lock.
    /// </summary>
    /// <remarks>
    /// Methods listed here were audited to confirm they do not transitively call
    /// into a JS runtime. They either do not touch any <c>Page</c> at all, or use
    /// only the immutable <see cref="CdpContext.GetSessionPage"/> accessor and
    /// managed field reads. <see cref="CdpContext.GetSessionPageMut"/> triggers
    /// suspend/resume of an isolate and must stay behind the lock.
    /// </remarks>
    internal static bool IsV8FreeMethod(string method) => method switch
    {
        "Target.getTargets"
            or "Target.setDiscoverTargets"
            or "Target.attachToTarget"
            or "Target.attachToBrowserTarget"
            or "Target.setAutoAttach"
            or "Target.getBrowserContexts"
            or "Target.createBrowserContext"
            or "Target.disposeBrowserContext"
            or "Target.getTargetInfo"
            or "Target.detachFromTarget"
            or "Target.activateTarget"
            or "Browser.getVersion"
            or "Browser.close"
            or "Browser.getWindowForTarget"
            or "Browser.setDownloadBehavior"
            or "Browser.getWindowBounds"
            or "Browser.setWindowBounds"
            or "Page.enable"
            or "Page.disable"
            or "Page.getFrameTree"
            or "Page.setDownloadBehavior"
            or "Page.setLifecycleEventsEnabled"
            or "Page.addScriptToEvaluateOnNewDocument"
            or "Page.removeScriptToEvaluateOnNewDocument"
            or "Page.setInterceptFileChooserDialog"
            or "Page.getNavigationHistory"
            or "Page.resetNavigationHistory"
            or "Page.captureSnapshot"
            or "Page.stopScreencast"
            or "Page.screencastFrameAck"
            or "Page.createIsolatedWorld"
            or "Runtime.enable"
            or "Runtime.disable"
            or "Runtime.runIfWaitingForDebugger"
            or "Runtime.getExceptionDetails"
            or "Runtime.discardConsoleEntries"
            or "Network.enable"
            or "Network.disable"
            or "Network.setCacheDisabled"
            or "Network.setRequestInterception"
            or "Network.setBlockedURLs"
            or "Network.setExtraHTTPHeaders"
            or "Network.setUserAgentOverride"
            or "Network.getCookies"
            or "Network.getAllCookies"
            or "Network.setCookie"
            or "Network.setCookies"
            or "Network.deleteCookies"
            or "Network.clearBrowserCookies"
            or "Network.getResponseBody"
            or "Fetch.continueRequest"
            or "Fetch.fulfillRequest"
            or "Fetch.failRequest"
            or "Fetch.getResponseBody"
            or "IO.read"
            or "IO.close"
            or "Storage.getCookies"
            or "Storage.setCookies"
            or "Storage.clearCookies"
            or "Storage.deleteCookies" => true,
        _ => false,
    };

    public static async Task<CdpResponse> DispatchAsync(CdpRequest req, CdpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(ctx);

        // headless_chrome (and older Puppeteer) wrap every CDP call inside
        // Target.sendMessageToTarget. Unwrap and recurse BEFORE acquiring the
        // per-connection V8 lock: the recursive dispatch acquires it for the
        // inner call, and the lock is not reentrant.
        if (string.Equals(req.Method, "Target.sendMessageToTarget", StringComparison.Ordinal))
        {
            return await DispatchSendMessageToTargetAsync(req, ctx).ConfigureAwait(false);
        }

        // Issue #430: keep this connection's V8 work serialized on its own thread.
        //
        // Every CDP handler below may call into a per-Page JS runtime (each
        // owning its own V8 isolate). With the thread-per-connection server each
        // connection runs on its own OS thread, so isolates never collide across
        // connections. Within one connection, though, a navigation task spawned
        // by the server runs while this processor keeps pumping other CDP
        // messages, so two of this connection's pages could still interleave V8
        // work on that one thread. The per-connection lock keeps each handler
        // contiguous: V8 fully exits one isolate before the next of this
        // connection's pages is allowed in. It is per-connection, not
        // process-wide, so other connections run in parallel.
        //
        // Optimization: methods that demonstrably never touch V8 bypass the lock
        // (Puppeteer's newPage() setup issues ~8 such calls). Each listed method
        // was audited to confirm it never reaches script execution or DOM
        // mutation that re-enters V8; GetSessionPageMut (which can trigger
        // suspend/resume) is NOT in the list.
        var v8Free = IsV8FreeMethod(req.Method);
        if (!v8Free)
        {
            await ctx.V8Lock.WaitAsync().ConfigureAwait(false);
        }

        try
        {
            return await DispatchLockedAsync(req, ctx, v8Free).ConfigureAwait(false);
        }
        finally
        {
            if (!v8Free)
            {
                ctx.V8Lock.Release();
            }
        }
    }

    private static async Task<CdpResponse> DispatchLockedAsync(
        CdpRequest req,
        CdpContext ctx,
        bool v8Free)
    {
        // Per-command V8 watchdog. The lock above keeps each handler contiguous
        // on the thread, but it does not bound how long a handler runs: a hung
        // page (a runaway Runtime.evaluate, a synchronous DOM op) would hold this
        // connection's V8 lock and wedge its other sessions forever. The one-shot
        // CLI uses a process-level hard deadline for this; the long-running server
        // cannot force-exit, so we terminate just the offending isolate instead.
        // OBSCURA_CDP_COMMAND_TIMEOUT_MS tunes the bound (0 disables); the default
        // leaves headroom for the slowest legitimate navigation, which already
        // self-bounds via OBSCURA_NAV_TIMEOUT_MS plus the watchdog-bounded settle.
        var budgetMs = CommandBudgetMs();
        ArmedWatchdog? watchdog = null;
        if (budgetMs != 0 && !v8Free)
        {
            if (ctx.GetSessionPage(req.SessionId)?.IsolateHandle is { } handle)
            {
                watchdog = CdpWatchdog.Arm(handle, TimeSpan.FromMilliseconds(budgetMs));
            }
        }

        var separator = req.Method.IndexOf('.', StringComparison.Ordinal);
        if (separator < 0)
        {
            return CdpResponse.Failure(
                req.Id,
                -32601,
                $"Invalid method format: {req.Method}",
                req.SessionId);
        }

        var domain = req.Method[..separator];
        var method = req.Method[(separator + 1)..];

        DomainResult result;
        try
        {
            result = domain switch
            {
                "Target" => await Domains.Target.HandleAsync(method, req.Params, ctx, req.SessionId)
                    .ConfigureAwait(false),
                "Browser" => await Domains.Browser.HandleAsync(method, req.Params)
                    .ConfigureAwait(false),
                "Page" => await Domains.Page.HandleAsync(method, req.Params, ctx, req.SessionId)
                    .ConfigureAwait(false),
                "DOM" => await Domains.Dom.HandleAsync(method, req.Params, ctx, req.SessionId)
                    .ConfigureAwait(false),
                "DOMSnapshot" => await Domains.DomSnapshot.HandleAsync(method, req.Params, ctx, req.SessionId)
                    .ConfigureAwait(false),
                "Runtime" => await Domains.Runtime.HandleAsync(method, req.Params, ctx, req.SessionId)
                    .ConfigureAwait(false),
                "Network" => await Domains.Network.HandleAsync(method, req.Params, ctx, req.SessionId)
                    .ConfigureAwait(false),
                "Fetch" => await Domains.Fetch.HandleAsync(method, req.Params, ctx, req.SessionId)
                    .ConfigureAwait(false),
                "IO" => await Domains.Io.HandleAsync(method, req.Params, ctx).ConfigureAwait(false),
                "Input" => await Domains.Input.HandleAsync(method, req.Params, ctx, req.SessionId)
                    .ConfigureAwait(false),
                "Emulation" => await Domains.Emulation.HandleAsync(method, req.Params, ctx, req.SessionId)
                    .ConfigureAwait(false),
                "Storage" => await Domains.Storage.HandleAsync(method, req.Params, ctx, req.SessionId)
                    .ConfigureAwait(false),
                "LP" => await Domains.Lp.HandleAsync(method, req.Params, ctx, req.SessionId)
                    .ConfigureAwait(false),
                "Accessibility" => await Domains.Accessibility.HandleAsync(method, req.Params, ctx, req.SessionId)
                    .ConfigureAwait(false),

                // Accepted but no-op. Puppeteer's FrameManager.initialize calls
                // Audits.enable on connect - refusing it breaks
                // puppeteer.connect() before any user code runs.
                "Log" or "Performance" or "Security" or "CSS" or "ServiceWorker" or "Inspector"
                    or "Debugger" or "Profiler" or "HeapProfiler" or "Overlay" or "Audits" =>
                    DomainResult.Empty(),
                _ => DomainResult.Err($"Unknown domain: {domain}"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A handler fault is still a protocol outcome; Rust's `Result` has no
            // third arm and a panic here would take the connection down.
            result = DomainResult.Err(ex.Message);
        }

        if (result.IsOk && Domains.Page.CommandCanChangeScreencastFrame(req.Method))
        {
            if (Domains.Page.QueueScreencastFrame(ctx, req.SessionId, false) is { } screencastError)
            {
                // Frame delivery is an asynchronous side effect in Chromium; it
                // must not rewrite an otherwise successful command response.
                CdpLog.Warn(
                    $"could not produce command-driven screencast frame for {req.Method}: {screencastError}");
            }
        }

        // Stop the per-command watchdog. If it fired (the handler held V8 past
        // the budget), V8 is left in a terminating state, so clear that flag
        // before the next command runs on this page.
        if (watchdog is not null && CdpWatchdog.Disarm(watchdog))
        {
            CdpLog.Warn(
                $"CDP command {req.Method} held V8 past {budgetMs}ms; terminated the isolate to free the dispatcher");
            ctx.GetSessionPageMut(req.SessionId)?.CancelV8Termination();
        }

        DrainRuntimeEvents(ctx);
        DrainBindingCalls(ctx);
        DrainFrameEvents(ctx);

        if (result.IsOk)
        {
            return CdpResponse.Success(req.Id, result.Value, req.SessionId);
        }

        CdpLog.Warn($"CDP error for {req.Method}: {result.Error}");
        return CdpResponse.Failure(req.Id, -32601, result.Error!, req.SessionId);
    }

    private static ulong CommandBudgetMs()
    {
        var raw = Environment.GetEnvironmentVariable("OBSCURA_CDP_COMMAND_TIMEOUT_MS");
        if (raw is not null &&
            ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 60_000;
    }

    public static void DrainRuntimeEvents(CdpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<(string PageId, IReadOnlyList<RuntimeEvent> Events)> drained = [];
        foreach (var page in ctx.Pages)
        {
            var runtimeEvents = page.TakePendingRuntimeEvents();
            if (runtimeEvents.Count != 0)
            {
                drained.Add((page.Id, runtimeEvents));
            }
        }

        if (drained.Count == 0)
        {
            return;
        }

        var pageToSessions = SessionsByPage(ctx, ctx.RuntimeEnabledSessions);
        List<CdpEvent> events = [];
        foreach (var (pageId, runtimeEvents) in drained)
        {
            if (!pageToSessions.TryGetValue(pageId, out var sessions))
            {
                continue;
            }

            var executionContextId = ctx.DefaultContextId(pageId) ?? 1;
            foreach (var runtimeEvent in runtimeEvents)
            {
                foreach (var sessionId in sessions)
                {
                    var (method, parameters) = runtimeEvent switch
                    {
                        RuntimeEvent.Console console => (
                            "Runtime.consoleAPICalled",
                            (JsonNode)new JsonObject
                            {
                                ["type"] = console.Event.Kind,
                                ["args"] = CloneArray(console.Event.Args),
                                ["executionContextId"] = executionContextId,
                                ["timestamp"] = console.Event.Timestamp,
                            }),
                        RuntimeEvent.Exception exception => (
                            "Runtime.exceptionThrown",
                            (JsonNode)new JsonObject
                            {
                                ["timestamp"] = exception.Event.Timestamp,
                                ["exceptionDetails"] = new JsonObject
                                {
                                    ["exceptionId"] = exception.Event.ExceptionId,
                                    ["text"] = "Uncaught",
                                    ["lineNumber"] = exception.Event.LineNumber,
                                    ["columnNumber"] = exception.Event.ColumnNumber,
                                    ["scriptId"] = "",
                                    ["url"] = exception.Event.Url,
                                    ["stackTrace"] = new JsonObject
                                    {
                                        ["callFrames"] = CloneArray(exception.Event.StackTrace),
                                    },
                                    ["executionContextId"] = executionContextId,
                                    ["exception"] = new JsonObject
                                    {
                                        ["type"] = "object",
                                        ["subtype"] = "error",
                                        ["className"] = exception.Event.Name,
                                        ["description"] = exception.Event.Description,
                                    },
                                },
                            }),
                        _ => throw new InvalidOperationException("unreachable runtime event kind"),
                    };

                    events.Add(new CdpEvent
                    {
                        Method = method,
                        Params = parameters,
                        SessionId = sessionId,
                    });
                }
            }
        }

        ctx.PendingEvents.AddRange(events);
    }

    /// <summary>
    /// Drain every page's binding-call queue and turn each entry into a
    /// <c>Runtime.bindingCalled</c> event.
    /// </summary>
    /// <remarks>
    /// The queue is filled when page JS invokes a <c>Runtime.addBinding</c> shim.
    /// Called after every dispatch: binding calls only land in the queue while V8
    /// is running inside a CDP handler, so there is no window in which they could
    /// pile up without a draining opportunity.
    /// </remarks>
    public static void DrainBindingCalls(CdpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<(string PageId, IReadOnlyList<(string Name, string Payload)> Calls)> drained = [];
        foreach (var page in ctx.Pages)
        {
            var calls = page.TakePendingBindingCalls();
            if (calls.Count != 0)
            {
                drained.Add((page.Id, calls));
            }
        }

        if (drained.Count == 0)
        {
            return;
        }

        // page id -> every session on that page. A page commonly has more than
        // one: Target.createTarget opens a session and the Target.attachToTarget
        // that follows opens another, so a client that reaches a page the ordinary
        // way holds two and uses the second.
        var pageToSessions = SessionsByPage(ctx, ctx.Sessions.Keys);
        List<CdpEvent> events = [];
        foreach (var (pageId, calls) in drained)
        {
            if (!pageToSessions.TryGetValue(pageId, out var pageSessions))
            {
                // No session attached - drop the calls; there is no client to
                // deliver them to.
                continue;
            }

            var executionContextId = ctx.DefaultContextId(pageId) ?? 1;
            foreach (var (name, payload) in calls)
            {
                // The sessions that asked for this binding, narrowed to the page
                // the call came from. Falling back to every session of the page
                // keeps a binding that was installed without a session (a preload,
                // or a direct embedder) deliverable rather than silently dropped.
                var registered = ctx.BindingSessions.GetValueOrDefault(name);
                List<string> targets = [];
                foreach (var session in pageSessions)
                {
                    if (registered is null || registered.Contains(session, StringComparer.Ordinal))
                    {
                        targets.Add(session);
                    }
                }

                if (targets.Count == 0)
                {
                    targets = pageSessions;
                }

                foreach (var sessionId in targets)
                {
                    events.Add(new CdpEvent
                    {
                        Method = "Runtime.bindingCalled",
                        Params = new JsonObject
                        {
                            ["name"] = name,
                            ["payload"] = payload,
                            ["executionContextId"] = executionContextId,
                        },
                        SessionId = sessionId,
                    });
                }
            }
        }

        ctx.PendingEvents.AddRange(events);
    }

    /// <summary>
    /// Announce child frames the client has not been told about yet, and retract
    /// the ones that are gone.
    /// </summary>
    /// <remarks>
    /// A frame is built when the page settles, which is not necessarily during the
    /// navigation that created it: script can add an iframe at any time, and the
    /// settle that gives it a realm may belong to a later command. Diffing here,
    /// after every dispatch, reports a frame whenever it actually appears instead
    /// of only at navigation, and is the same drain point binding calls use.
    /// </remarks>
    public static void DrainFrameEvents(CdpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Every session on the page, not just one: a client that reaches a page
        // the ordinary way holds two of them, because Target.createTarget opens a
        // session and the Target.attachToTarget that follows opens another. A
        // client drops any event whose sessionId is not the one it attached with,
        // so announcing to an arbitrary session is the same as not announcing.
        var pageToSessions = SessionsByPage(ctx, ctx.Sessions.Keys);

        List<CdpEvent> events = [];
        Dictionary<string, List<string>> announced = new(StringComparer.Ordinal);
        List<(string PageId, string FrameId, List<string> Sessions)> detached = [];
        foreach (var page in ctx.Pages)
        {
            if (!pageToSessions.TryGetValue(page.Id, out var sessionIds))
            {
                continue;
            }

            var live = Domains.Page.ChildFrameValues(page);
            var known = ctx.AnnouncedFrames.GetValueOrDefault(page.Id);
            List<string> liveIds = [];
            foreach (var frame in live)
            {
                liveIds.Add(frame.Get("id").AsStringOr(string.Empty));
            }

            foreach (var frame in live)
            {
                var id = frame.Get("id").AsStringOr(string.Empty);
                if (known is not null && known.Contains(id, StringComparer.Ordinal))
                {
                    continue;
                }

                foreach (var sessionId in sessionIds)
                {
                    // Attach before navigate: a client builds its frame from the
                    // attach event and treats a navigation of a frame it has never
                    // seen as a protocol error.
                    events.Add(new CdpEvent
                    {
                        Method = "Page.frameAttached",
                        Params = new JsonObject
                        {
                            ["frameId"] = id,
                            ["parentFrameId"] = frame.Get("parentId").AsStringOr(string.Empty),
                        },
                        SessionId = sessionId,
                    });
                    events.Add(new CdpEvent
                    {
                        Method = "Page.frameNavigated",
                        Params = new JsonObject
                        {
                            ["frame"] = frame.Clone(),
                            ["type"] = "Navigation",
                        },
                        SessionId = sessionId,
                    });

                    // The frame's document scripts have already run by the time it
                    // is in this list, so it is not still loading.
                    events.Add(new CdpEvent
                    {
                        Method = "Page.frameStoppedLoading",
                        Params = new JsonObject { ["frameId"] = id },
                        SessionId = sessionId,
                    });
                }
            }

            if (known is not null)
            {
                foreach (var id in known)
                {
                    if (!liveIds.Contains(id, StringComparer.Ordinal))
                    {
                        detached.Add((page.Id, id, [.. sessionIds]));
                    }
                }
            }

            announced[page.Id] = liveIds;
        }

        foreach (var (pageId, frameId, pageSessions) in detached)
        {
            var removed = ctx.RemoveFrameContexts(pageId, frameId);
            var runtimeSessions = ctx.RuntimeSessionsForPage(pageId);
            foreach (var context in removed)
            {
                foreach (var sessionId in runtimeSessions)
                {
                    events.Add(CdpEvent.WithSession(
                        "Runtime.executionContextDestroyed",
                        new JsonObject
                        {
                            ["executionContextId"] = context.Id,
                            ["executionContextUniqueId"] = context.UniqueId,
                        },
                        sessionId));
                }
            }

            foreach (var sessionId in pageSessions)
            {
                events.Add(new CdpEvent
                {
                    Method = "Page.frameDetached",
                    Params = new JsonObject { ["frameId"] = frameId, ["reason"] = "remove" },
                    SessionId = sessionId,
                });
            }
        }

        foreach (var (pageId, liveIds) in announced)
        {
            ctx.AnnouncedFrames[pageId] = liveIds;
        }

        ctx.PendingEvents.AddRange(events);
    }

    /// <summary>
    /// page id -&gt; its sessions, sorted. <c>ctx.Sessions</c> is a dictionary, so
    /// the order the events come out in has to be fixed here to be assertable.
    /// </summary>
    private static Dictionary<string, List<string>> SessionsByPage(
        CdpContext ctx,
        IEnumerable<string> sessionIds)
    {
        Dictionary<string, List<string>> pageToSessions = new(StringComparer.Ordinal);
        foreach (var sessionId in sessionIds)
        {
            if (!ctx.Sessions.TryGetValue(sessionId, out var pageId))
            {
                continue;
            }

            if (!pageToSessions.TryGetValue(pageId, out var owned))
            {
                owned = [];
                pageToSessions[pageId] = owned;
            }

            owned.Add(sessionId);
        }

        foreach (var sessions in pageToSessions.Values)
        {
            sessions.Sort(StringComparer.Ordinal);
        }

        return pageToSessions;
    }

    private static JsonArray CloneArray(IReadOnlyList<JsonNode?> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value?.DeepClone());
        }

        return array;
    }

    private static async Task<CdpResponse> DispatchSendMessageToTargetAsync(
        CdpRequest req,
        CdpContext ctx)
    {
        var sessionId = req.Params.Get("sessionId").AsString();
        if (req.Params.Get("message").AsString() is not { } message)
        {
            return CdpResponse.Failure(
                req.Id,
                -32602,
                "sendMessageToTarget requires a message string",
                req.SessionId);
        }

        var inner = CdpRequest.TryParse(message, out var parseError);
        if (inner is null)
        {
            return CdpResponse.Failure(
                req.Id,
                -32700,
                $"sendMessageToTarget message is not a valid CDP request: {parseError}",
                req.SessionId);
        }

        // Override the inner session with the one supplied by the wrapper so the
        // inner dispatch routes against the right page.
        var innerWithSession = new CdpRequest
        {
            Id = inner.Id,
            Method = inner.Method,
            Params = inner.Params,
            SessionId = sessionId ?? inner.SessionId,
        };
        var innerResponse = await DispatchAsync(innerWithSession, ctx).ConfigureAwait(false);

        // Re-emit the inner response as the legacy event headless_chrome (and
        // older Puppeteer) listen for instead of correlating responses by id.
        ctx.PendingEvents.Add(new CdpEvent
        {
            Method = "Target.receivedMessageFromTarget",
            Params = new JsonObject
            {
                ["sessionId"] = sessionId ?? string.Empty,
                ["message"] = innerResponse.ToJson(),
                ["targetId"] = sessionId ?? string.Empty,
            },
            SessionId = req.SessionId,
        });

        return CdpResponse.Success(req.Id, new JsonObject(), req.SessionId);
    }
}
