using System.Text.Json.Nodes;
using System.Threading.Channels;
using Obscura.Browser;
using Obscura.Js.Ops;

namespace Obscura.Cdp;

public static partial class CdpServer
{
    /// <summary>
    /// Run a <c>Page.navigate</c> off the processor's own stack so the connection
    /// keeps answering while the navigation is in flight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #19 follow-up (PR #36 maintainer's fetch-intercept repro): while the
    /// navigation task is executing V8 (potentially parked on the fetch op's
    /// resolution with its isolate still entered), we must NOT let the parent
    /// route foreign CDP messages through dispatch, because handlers there call
    /// <see cref="CdpContext.GetSessionPageMut"/>, which suspends OTHER pages and
    /// tears their runtimes down. That trips V8's per-thread isolate invariant.
    /// </para>
    /// <para>
    /// The connection's V8 lock does not save us here: it is released around
    /// awaits inside V8 ops, so it does not keep the enter/exit pair contiguous on
    /// the thread. Foreign messages are therefore parked in the outer deferral
    /// queue so the processor loop handles them after this navigation fully
    /// completes.
    /// </para>
    /// </remarks>
    internal static async Task ProcessWithInterceptionAsync(
        string text,
        CdpContext ctx,
        ChannelWriter<string> replyTx,
        ChannelReader<ServerMessage> rx,
        ChannelReader<InterceptedRequest> interceptRx,
        Dictionary<string, TaskCompletionSource<InterceptResolution>> interceptedPaused,
        Queue<ServerMessage> deferred,
        bool sendCommandResponse)
    {
        var req = CdpRequest.TryParse(text, out var parseError);
        if (req is null)
        {
            CdpLog.Warn($"Invalid CDP: {parseError}");
            return;
        }

        CdpLog.Info($"INTERCEPTION navigate: {req.Method} (id={req.Id})");

        string? pageId = null;
        if (req.SessionId is { } sid)
        {
            ctx.Sessions.TryGetValue(sid, out pageId);
        }

        if (pageId is null)
        {
            await ProcessCdpMessageAsync(text, ctx, replyTx).ConfigureAwait(false);
            return;
        }

        var pageIndex = ctx.Pages.FindIndex(p => string.Equals(p.Id, pageId, StringComparison.Ordinal));
        if (pageIndex < 0)
        {
            await ProcessCdpMessageAsync(text, ctx, replyTx).ConfigureAwait(false);
            return;
        }

        var page = ctx.Pages[pageIndex];
        ctx.Pages.RemoveAt(pageIndex);

        // Issue #19 follow-up: V8 only allows ONE entered isolate per OS thread.
        // The regular dispatch path enforces this via GetSessionPageMut, which
        // suspends every other page before letting the target page run JS. This
        // path bypasses that - it removes the target page and spawns a navigation
        // task - so the same invariant has to be enforced explicitly. Otherwise
        // the second navigation's runtime is constructed while the first page's is
        // still alive in ctx.Pages, and the next scope unwind aborts the process.
        foreach (var other in ctx.Pages)
        {
            if (other.HasJs)
            {
                other.SuspendJs();
            }
        }

        var url = req.Params.Get("url").AsStringOr(string.Empty);
        var waitUntil = Domains.Page.ParseWaitUntil(req.Params);
        var navMethod = req.Params.Get("__method").AsStringOr("GET");
        var navBody = req.Params.Get("__body").AsStringOr(string.Empty);

        List<string> preloadScripts = [];
        foreach (var (_, source) in ctx.PreloadScripts)
        {
            preloadScripts.Add(source);
        }

        if (ctx.InterceptSink is { } sink)
        {
            page.SetInterceptSink(sink);
        }

        var sessionForEvents = req.SessionId;
        var frameId = page.FrameId;
        var loaderId = $"loader-{Guid.NewGuid()}";

        var navigation = NavigateTaskAsync(ctx, page, url, waitUntil, navMethod, navBody, preloadScripts);

        while (true)
        {
            var frame = rx.WaitToReadAsync().AsTask();
            var intercept = interceptRx.WaitToReadAsync().AsTask();
            await Task.WhenAny(navigation, frame, intercept).ConfigureAwait(false);
            ObserveFaulted(frame);
            ObserveFaulted(intercept);

            if (navigation.IsCompleted)
            {
                break;
            }

            if (interceptRx.TryRead(out var request))
            {
                ServerSupport.EmitInterceptedRequest(
                    request, frameId, sessionForEvents, replyTx, interceptedPaused);
                await Task.Delay(50).ConfigureAwait(false);
                continue;
            }

            if (!rx.TryRead(out var msg))
            {
                continue;
            }

            CdpLog.Info("INTERCEPTION select: received CDP message during navigation");
            switch (msg)
            {
                case ServerMessage.NewConnection newConnection:
                {
                    // Safe: no V8 entry, just bookkeeping.
                    var newPageId = ctx.CreatePage();
                    var newSessionId = $"{newPageId}-session";
                    ctx.Sessions[newSessionId] = newPageId;
                    newConnection.ReplyTx.TryWrite(CdpJson.Serialize(new JsonObject
                    {
                        ["__init"] = true,
                        ["pageId"] = newPageId,
                        ["sessionId"] = newSessionId,
                    }));
                    break;
                }

                case ServerMessage.Cdp cdp:
                    if (cdp.Text.Contains("Fetch.continueRequest", StringComparison.Ordinal) ||
                        cdp.Text.Contains("Fetch.fulfillRequest", StringComparison.Ordinal) ||
                        cdp.Text.Contains("Fetch.failRequest", StringComparison.Ordinal))
                    {
                        // Safe: only completes a promise to resume the parked op
                        // inside the navigation task. No V8 entry on this side; the
                        // actual V8 work happens back inside that task.
                        ServerSupport.HandleFetchResolution(
                            cdp.Text, ctx, cdp.ReplyTx, interceptedPaused);
                    }
                    else if (deferred.Count >= MaxDeferredMessages)
                    {
                        // UNSAFE during navigation: would route through dispatch,
                        // which can suspend other pages and trip the V8 invariant.
                        CdpLog.Warn(
                            $"INTERCEPTION: deferred queue full ({MaxDeferredMessages}), returning error to client");
                        if (CdpRequest.TryParse(cdp.Text) is { } busy)
                        {
                            cdp.ReplyTx.TryWrite(CdpResponse.Failure(
                                busy.Id,
                                -32000,
                                "Server busy: navigation in progress, try again later",
                                busy.SessionId).ToJson());
                        }
                    }
                    else
                    {
                        CdpLog.Info("INTERCEPTION: deferring CDP message until nav completes");
                        deferred.Enqueue(cdp);
                    }

                    break;
            }
        }

        // Deferred messages are handled by the outer processor loop, which drains
        // them before pulling the next message off the wire.
        var navigateError = await navigation.ConfigureAwait(false);

        // Fold in network events for script-initiated requests (fetch/XHR/dynamic
        // resource) so they emit as Network.requestWillBeSent / responseReceived
        // alongside the static navigation subresources (#406).
        page.SyncJsNetworkEvents();
        List<NetworkEvent> networkEvents = [.. page.NetworkEvents];
        page.NetworkEvents.Clear();
        var pageUrl = page.UrlString();
        var pageIdForEvents = page.Id;
        var reachedNetworkIdle = page.Lifecycle.IsNetworkIdle();

        ctx.Pages.Add(page);

        var navigationSucceeded = navigateError is null;
        var response = navigationSucceeded
            ? CdpResponse.Success(
                req.Id,
                new JsonObject { ["frameId"] = frameId, ["loaderId"] = loaderId },
                req.SessionId)
            : CdpResponse.Failure(req.Id, -32000, navigateError!, req.SessionId);

        if (sendCommandResponse)
        {
            replyTx.TryWrite(response.ToJson());
        }

        // Shared event emission: includes the post-#190 Network.requestWillBeSent
        // -before-frameNavigated ordering, the #189 requestId=loaderId trick that
        // makes page.goto() resolve to a Response, and the #192 per-isolated-world
        // fresh context ids. Pushes to ctx.PendingEvents; we then drain to the
        // WebSocket reply channel.
        Domains.Page.EmitNavigationEvents(
            ctx,
            sessionForEvents,
            frameId,
            loaderId,
            pageUrl,
            pageIdForEvents,
            networkEvents,
            waitUntil,
            reachedNetworkIdle);

        if (navigationSucceeded &&
            Domains.Page.QueueScreencastFrame(ctx, sessionForEvents, false) is { } screencastError)
        {
            CdpLog.Warn(
                $"could not produce post-navigation screencast frame: {screencastError}");
        }

        ForwardPendingEvents(ctx, replyTx);
    }

    /// <summary>
    /// The navigation itself, serialized against this connection's other V8 work.
    /// </summary>
    /// <remarks>
    /// Issue #19: this task runs while the connection's processor keeps pumping
    /// other CDP messages (which take the same per-connection lock), so both sides
    /// coordinate on one page's isolate at a time on this thread. The lock is
    /// per-connection, so other connections are unaffected (#430).
    /// </remarks>
    private static async Task<string?> NavigateTaskAsync(
        CdpContext ctx,
        Page page,
        string url,
        WaitUntil waitUntil,
        string navMethod,
        string navBody,
        List<string> preloadScripts)
    {
        await ctx.V8Lock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Preloads (addBinding shims, addScriptToEvaluateOnNewDocument
            // sources) must run BEFORE the page's own scripts (CDP contract). Hand
            // them to the page so the navigation can inject them at the right
            // point.
            page.SetPreloadScripts(preloadScripts);
            if (string.Equals(navMethod, "POST", StringComparison.Ordinal) && navBody.Length != 0)
            {
                await page.NavigateWithWaitPostAsync(url, waitUntil, navMethod, navBody)
                    .ConfigureAwait(false);
            }
            else
            {
                await page.NavigateWithWaitAsync(url, waitUntil).ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return e.Message;
        }
        finally
        {
            ctx.V8Lock.Release();
        }
    }

    internal static async Task ProcessCdpMessageAsync(
        string text,
        CdpContext ctx,
        ChannelWriter<string> replyTx)
    {
        var req = CdpRequest.TryParse(text, out var parseError);
        if (req is null)
        {
            CdpLog.Warn($"Invalid CDP: {parseError}: {ServerSupport.Utf8Preview(text, 200)}");
            return;
        }

        CdpLog.Debug($"CDP: {req.Method} (id={req.Id}, s={req.SessionId ?? "None"})");

        var response = await Dispatcher.DispatchAsync(req, ctx).ConfigureAwait(false);

        // Chromium CDP semantics: events emitted as a side-effect of a command
        // (for example Target.targetCreated + Target.attachedToTarget from
        // Target.createTarget) MUST arrive BEFORE the command's response.
        // Playwright awaits the response and immediately reads state wired up by
        // those events; if the response lands first, accessing the page errors
        // with "Cannot read properties of undefined".
        ForwardPendingEvents(ctx, replyTx);
        replyTx.TryWrite(response.ToJson());

        if (CheckPendingNavigation(ctx, req.SessionId) is { } pending)
        {
            var (navUrl, navMethod, navBody) = pending;
            CdpLog.Info(
                $"JS-triggered nav: {navMethod} {navUrl} (body: {navBody.Length} bytes)");
            var navRequest = new CdpRequest
            {
                Id = 0,
                Method = "Page.navigate",
                Params = new JsonObject
                {
                    ["url"] = navUrl,
                    ["__method"] = navMethod,
                    ["__body"] = navBody,
                },
                SessionId = req.SessionId,
            };
            _ = await Dispatcher.DispatchAsync(navRequest, ctx).ConfigureAwait(false);
            ForwardPendingEvents(ctx, replyTx);
        }
    }

    private static (string Url, string Method, string Body)? CheckPendingNavigation(
        CdpContext ctx,
        string? sessionId)
    {
        if (sessionId is null || !ctx.Sessions.TryGetValue(sessionId, out var pageId))
        {
            return null;
        }

        return ctx.GetPage(pageId)?.TakePendingNavigation();
    }
}
