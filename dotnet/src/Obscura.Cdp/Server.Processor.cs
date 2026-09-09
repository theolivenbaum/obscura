using System.Text.Json.Nodes;
using System.Threading.Channels;
using Obscura.Browser;
using Obscura.Js.Ops;

namespace Obscura.Cdp;

public static partial class CdpServer
{
    /// <summary>
    /// Chromium's PageHandler receives compositor video frames continuously.
    /// Obscura has no separate compositor thread yet, so active screencasts get a
    /// bounded 30 Hz opportunity on this connection's thread.
    /// </summary>
    private const int ScreencastTickMs = 33;

    /// <summary>Per-connection CDP processor.</summary>
    /// <remarks>
    /// Each connection runs its own processor (with its own <see cref="CdpContext"/>
    /// and pages) on its own OS thread, so every page's V8 isolate is confined to a
    /// single thread. This removes the #430 abort by construction: V8's
    /// per-thread isolate invariant means two connections' isolates can never
    /// collide. All processors own an isolated <see cref="BrowserContext"/> (cookie
    /// jar and HTTP client); cookie deltas are merged into the persistence template
    /// when the connection thread exits.
    /// </remarks>
    internal static async Task CdpProcessorAsync(
        ChannelReader<ServerMessage> rx,
        BrowserContext defaultContext,
        CancellationToken shutdown,
        CancellationToken stop)
    {
        var ctx = CdpContext.NewWithSharedContext(defaultContext);
        var intercepted = Channel.CreateUnbounded<InterceptedRequest>(
            new UnboundedChannelOptions { SingleReader = true });
        ctx.InterceptSink = new ChannelInterceptSink(intercepted.Writer);
        var interceptRx = intercepted.Reader;
        Dictionary<string, TaskCompletionSource<InterceptResolution>> interceptedPaused =
            new(StringComparer.Ordinal);

        // Issue #19 follow-up: messages deferred from inside the interception path
        // because routing them through dispatch while a navigation was in flight
        // would have tripped V8's per-thread isolate invariant. Drained at the top
        // of each outer iteration so they are processed sequentially with no other
        // navigation in flight.
        Queue<ServerMessage> deferred = new();

        var screencastDue = Environment.TickCount64 + ScreencastTickMs;
        ChannelWriter<string>? connectionReplyTx = null;

        // A real browser renderer continues servicing timers, networking, posted
        // tasks, and animation callbacks while its DevTools client is silent. Keep
        // one wake-driven turn armed after work may have been scheduled. Full idle
        // disarms it until the next command or navigation, so static pages consume
        // no polling budget.
        var runtimePumpArmed = false;
        var runtimePumpErrorStreak = 0;

        using var loopStop = CancellationTokenSource.CreateLinkedTokenSource(shutdown, stop);
        try
        {
            while (true)
            {
                // Drain any deferred messages from the previous interception window
                // before pulling new ones off the wire. Each is processed with no
                // navigation task in flight, so this connection's only entered
                // isolate is the one dispatch is about to touch.
                ServerMessage? msg = null;
                if (deferred.Count != 0)
                {
                    msg = deferred.Dequeue();
                }
                else if (rx.TryRead(out var ready))
                {
                    // `biased` in the Rust select: an inbound frame outranks every
                    // other arm, so it is checked before anything can park.
                    msg = ready;
                }
                else if (rx.Completion.IsCompleted)
                {
                    // `rx.recv()` answering None: the reader side is finished, so
                    // the processor is done regardless of the other arms.
                    break;
                }
                else if (loopStop.IsCancellationRequested)
                {
                    if (shutdown.IsCancellationRequested)
                    {
                        CdpLog.Info("Shutdown signal received (connection processor)");
                    }

                    break;
                }
                else if (runtimePumpArmed)
                {
                    // One wake-driven turn. Unlike the Rust select arm this is
                    // awaited to completion rather than cancelled when a frame
                    // arrives: a turn is already bounded (it parks for at most 50ms
                    // before re-pumping), and abandoning a half-run turn would leave
                    // this page's isolate entered while dispatch enters another.
                    bool reachedIdle;
                    try
                    {
                        reachedIdle = await PumpLivePageEventLoopAsync(ctx).ConfigureAwait(false);
                        runtimePumpErrorStreak = 0;
                        runtimePumpArmed = !reachedIdle;
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        if (runtimePumpErrorStreak < int.MaxValue)
                        {
                            runtimePumpErrorStreak++;
                        }

                        runtimePumpArmed = runtimePumpErrorStreak <= 3 && AnyPageHasJs(ctx);
                        CdpLog.Warn($"autonomous page task failed: {error.Message}");
                        await Task.Yield();
                    }

                    SyncLivePageNetworkEvents(ctx);
                    Dispatcher.DrainRuntimeEvents(ctx);
                    Dispatcher.DrainBindingCalls(ctx);
                    Dispatcher.DrainFrameEvents(ctx);
                    ForwardPendingEvents(ctx, connectionReplyTx);
                    if (connectionReplyTx is { } pumpReply &&
                        TakeLivePendingNavigation(ctx) is { } pendingNav)
                    {
                        var (navSession, navUrl, navMethod, navBody) = pendingNav;
                        var navigation = CdpJson.Serialize(new JsonObject
                        {
                            ["id"] = 0,
                            ["method"] = "Page.navigate",
                            ["params"] = new JsonObject
                            {
                                ["url"] = navUrl,
                                ["__method"] = navMethod,
                                ["__body"] = navBody,
                            },
                            ["sessionId"] = navSession,
                        });
                        await ProcessWithInterceptionAsync(
                            navigation, ctx, pumpReply, rx, interceptRx, interceptedPaused,
                            deferred, false).ConfigureAwait(false);
                        runtimePumpArmed = AnyPageHasJs(ctx);
                    }

                    continue;
                }
                else if (interceptRx.TryRead(out var request))
                {
                    HandleInterceptedRequest(ctx, request, connectionReplyTx, interceptedPaused);
                    continue;
                }
                else if (ctx.Screencasts.Count != 0 && Environment.TickCount64 >= screencastDue)
                {
                    // MissedTickBehavior::Skip: a tick that could not be serviced
                    // is dropped rather than replayed back to back.
                    screencastDue = Environment.TickCount64 + ScreencastTickMs;
                    await PumpAndForwardScreencastFramesAsync(ctx, connectionReplyTx)
                        .ConfigureAwait(false);
                    continue;
                }
                else
                {
                    var frame = rx.WaitToReadAsync(loopStop.Token).AsTask();
                    var intercept = interceptRx.WaitToReadAsync(loopStop.Token).AsTask();
                    List<Task> arms = [frame, intercept];
                    if (ctx.Screencasts.Count != 0)
                    {
                        var delay = Math.Max(0, screencastDue - Environment.TickCount64);
                        arms.Add(Task.Delay((int)delay, loopStop.Token));
                    }

                    try
                    {
                        await Task.WhenAny(arms).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    ObserveFaulted(frame);
                    ObserveFaulted(intercept);
                    continue;
                }

                switch (msg)
                {
                    case ServerMessage.NewConnection newConnection:
                        connectionReplyTx = newConnection.ReplyTx;
                        newConnection.ReplyTx.TryWrite(
                            CdpJson.Serialize(new JsonObject { ["__init"] = true }));
                        break;

                    case ServerMessage.Cdp cdpMsg:
                    {
                        // Route every Page.navigate through the spawn-and-defer
                        // path, not just intercepted ones. Holding the V8 lock
                        // across a multi-second navigate inside the regular
                        // dispatch wedges the entire processor (40-site sweep:
                        // 39/40 timeouts). Spawning the navigation lets the
                        // processor keep multiplexing other CDP messages;
                        // unrelated requests get deferred only briefly and are
                        // drained as soon as the navigation settles.
                        if (ServerSupport.IsNavigateMethod(cdpMsg.Text))
                        {
                            await ProcessWithInterceptionAsync(
                                cdpMsg.Text, ctx, cdpMsg.ReplyTx, rx, interceptRx,
                                interceptedPaused, deferred, true).ConfigureAwait(false);
                        }
                        else
                        {
                            var fetchWasResolved =
                                cdpMsg.Text.Contains("Fetch.", StringComparison.Ordinal) &&
                                ServerSupport.HandleFetchResolution(
                                    cdpMsg.Text, ctx, cdpMsg.ReplyTx, interceptedPaused);
                            if (!fetchWasResolved)
                            {
                                await ProcessCdpMessageAsync(cdpMsg.Text, ctx, cdpMsg.ReplyTx)
                                    .ConfigureAwait(false);
                            }
                        }

                        break;
                    }
                }

                // Dispatch may have created a page or scheduled new asynchronous
                // work. A single live isolate is the connection's current active
                // target; the pump will park cheaply if its next task is a distant
                // timer.
                runtimePumpArmed = AnyPageHasJs(ctx);
                runtimePumpErrorStreak = 0;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // The connection thread merges this context's cookie delta into the
            // persistence template after the processor stops.
            foreach (var page in ctx.Pages)
            {
                page.Dispose();
            }

            ctx.Pages.Clear();
        }
    }

    /// <summary>Surface a faulted wait so it does not become an unobserved exception.</summary>
    private static void ObserveFaulted(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
        }
    }

    private static void HandleInterceptedRequest(
        CdpContext ctx,
        InterceptedRequest request,
        ChannelWriter<string>? replyTx,
        Dictionary<string, TaskCompletionSource<InterceptResolution>> interceptedPaused)
    {
        var route = LivePageRoute(ctx);
        if (route is { } live && replyTx is { } reply)
        {
            ServerSupport.EmitInterceptedRequest(
                request, live.FrameId, live.SessionId, reply, interceptedPaused);
        }
        else
        {
            request.Resolver.TrySetResult(new InterceptResolution.Fail("Aborted"));
        }
    }

    private static bool AnyPageHasJs(CdpContext ctx)
    {
        foreach (var page in ctx.Pages)
        {
            if (page.HasJs)
            {
                return true;
            }
        }

        return false;
    }

    private static Page? LiveJsPage(CdpContext ctx)
    {
        foreach (var page in ctx.Pages)
        {
            if (page.HasJs)
            {
                return page;
            }
        }

        return null;
    }

    private static string? SessionForPage(CdpContext ctx, string pageId)
    {
        foreach (var (sessionId, owner) in ctx.Sessions)
        {
            if (string.Equals(owner, pageId, StringComparison.Ordinal))
            {
                return sessionId;
            }
        }

        return null;
    }

    private static (string SessionId, string FrameId)? LivePageRoute(CdpContext ctx)
    {
        if (LiveJsPage(ctx) is not { } page)
        {
            return null;
        }

        return SessionForPage(ctx, page.Id) is { } sessionId ? (sessionId, page.FrameId) : null;
    }

    internal static async Task<bool> PumpLivePageEventLoopAsync(CdpContext ctx)
    {
        if (LiveJsPage(ctx) is not { } page)
        {
            return true;
        }

        return await page.RunAutonomousEventLoopTurnAsync().ConfigureAwait(false);
    }

    private static void SyncLivePageNetworkEvents(CdpContext ctx)
    {
        if (LiveJsPage(ctx) is not { } live ||
            SessionForPage(ctx, live.Id) is not { } sessionId)
        {
            return;
        }

        var pageId = live.Id;
        var frameId = live.FrameId;
        var pageUrl = live.UrlString();

        if (ctx.GetPageMut(pageId) is not { } page)
        {
            return;
        }

        page.SyncJsNetworkEvents();
        List<NetworkEvent> networkEvents = [.. page.NetworkEvents];
        page.NetworkEvents.Clear();

        Domains.Page.EmitRuntimeNetworkEvents(
            ctx, sessionId, frameId, pageUrl, pageId, networkEvents);
    }

    private static (string SessionId, string Url, string Method, string Body)?
        TakeLivePendingNavigation(CdpContext ctx)
    {
        if (LiveJsPage(ctx) is not { } page ||
            SessionForPage(ctx, page.Id) is not { } sessionId ||
            page.TakePendingNavigation() is not { } pending)
        {
            return null;
        }

        var (url, method, body) = pending;
        return (sessionId, url, method, body);
    }

    private static void ForwardPendingEvents(CdpContext ctx, ChannelWriter<string>? replyTx)
    {
        if (replyTx is null)
        {
            // No client yet: the events stay queued rather than being dropped,
            // which is what the Rust early return does.
            return;
        }

        foreach (var pending in ctx.PendingEvents)
        {
            replyTx.TryWrite(pending.ToJson());
        }

        ctx.PendingEvents.Clear();
    }

    internal static async Task PumpAndForwardScreencastFramesAsync(
        CdpContext ctx,
        ChannelWriter<string>? replyTx)
    {
        await Domains.Page.PumpScreencastFramesAsync(ctx).ConfigureAwait(false);
        ForwardPendingEvents(ctx, replyTx);
    }

    private sealed class ChannelInterceptSink(ChannelWriter<InterceptedRequest> writer)
        : IInterceptSink
    {
        public bool TrySend(InterceptedRequest request) => writer.TryWrite(request);
    }
}
