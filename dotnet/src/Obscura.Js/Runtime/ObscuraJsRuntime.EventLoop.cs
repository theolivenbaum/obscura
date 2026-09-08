using System.Diagnostics;
using Microsoft.ClearScript;
using Obscura.Js.Modules;

namespace Obscura.Js.Runtime;

/// <summary>
/// The host event loop.
/// </summary>
/// <remarks>
/// <para>
/// deno_core owns a Tokio-backed loop that drains timers, async ops and
/// microtasks and reports idle. ClearScript owns none of that, so the loop is
/// driven here: <see cref="TimerQueue"/> is the timer task source,
/// <see cref="ObscuraJsRuntime.TrackAsyncOp"/> is the async-op counter, and V8's
/// automatic microtask policy performs the checkpoint at the end of every
/// script the host runs.
/// </para>
/// <para>
/// The one deno_core behavior that does not survive: a <c>Poll::Pending</c>
/// parked on a real waker. The port cannot register a waker inside V8, so a
/// loop with only future work parks on a timed wait sized to the next timer.
/// A page with no timers and no in-flight ops is idle immediately, which is the
/// same answer, reached by polling rather than by being woken.
/// </para>
/// </remarks>
public sealed partial class ObscuraJsRuntime
{
    private enum LoopTick
    {
        /// <summary>Nothing is scheduled and nothing is in flight.</summary>
        Idle,

        /// <summary>At least one task ran this turn.</summary>
        Progressed,

        /// <summary>Work exists but is not due yet.</summary>
        Waiting,
    }

    /// <summary>
    /// Performs a microtask checkpoint.
    /// </summary>
    /// <remarks>
    /// A browser performs one at the end of each task, and the reference calls
    /// V8's <c>PerformMicrotaskCheckpoint</c> explicitly because deno_core may
    /// return from the loop with an already-resolved promise continuation
    /// stranded. ClearScript leaves V8 on its automatic microtask policy and
    /// exposes no checkpoint entry point, so the checkpoint is triggered by
    /// running an empty script: V8 drains the microtask queue when the JS call
    /// depth returns to zero.
    /// </remarks>
    public void PerformMicrotaskCheckpoint()
    {
        try
        {
            _engine.Execute("<microtask-checkpoint>", ";");
        }
        catch (ScriptInterruptedException)
        {
            throw;
        }
        catch (ScriptEngineException)
        {
            // A microtask that throws is page-local noise, exactly as in a
            // browser: the checkpoint itself cannot fail the page.
        }
    }

    private LoopTick PumpTick(out string? taskError)
    {
        taskError = null;
        PerformMicrotaskCheckpoint();

        // Posted tasks first: the shim treats op_posted_task as the task source
        // that must run before the next timer batch, which is what keeps a
        // rendering checkpoint ahead of the callbacks it schedules.
        var posted = DrainPostedTasks();
        foreach (var deliver in posted)
        {
            try
            {
                deliver(0);
            }
            catch (ScriptInterruptedException)
            {
                throw;
            }
            catch (ScriptEngineException error)
            {
                taskError ??= $"Event loop error: {error.Message}";
            }
            PerformMicrotaskCheckpoint();
        }

        var due = Timers.TakeDue();
        if (due.Count > 0)
        {
            foreach (var callback in due)
            {
                try
                {
                    (callback as ScriptObject)?.InvokeAsFunction();
                }
                catch (ScriptInterruptedException)
                {
                    throw;
                }
                catch (ScriptEngineException error)
                {
                    // A timer callback that throws is a page-local task error:
                    // report it and keep scheduling (#699).
                    taskError ??= $"Event loop error: {error.Message}";
                }
                PerformMicrotaskCheckpoint();
            }
            return LoopTick.Progressed;
        }

        if (posted.Length > 0)
        {
            return LoopTick.Progressed;
        }

        return Timers.Count > 0 || _postedTasks.Count > 0 || HasPendingAsyncOps || HasPendingNetworkRequests()
            ? LoopTick.Waiting
            : LoopTick.Idle;
    }

    private Action<double>[] DrainPostedTasks()
    {
        if (_postedTasks.Count == 0)
        {
            return [];
        }
        // Snapshot before running: a task that posts another belongs to the next
        // turn, or a self-reposting scheduler starves everything after it.
        var drained = _postedTasks.ToArray();
        _postedTasks.Clear();
        return drained;
    }

    private async Task<LoopTick> ParkAsync(TimeSpan cap)
    {
        var next = Timers.NextDelayMs();
        var delay = next is { } ms ? TimeSpan.FromMilliseconds(Math.Max(1, ms)) : TimeSpan.FromMilliseconds(1);
        if (delay > cap)
        {
            delay = cap;
        }
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay).ConfigureAwait(false);
        }
        return LoopTick.Waiting;
    }

    /// <summary>Run the loop until it goes idle.</summary>
    public async Task RunEventLoopAsync()
    {
        BeginJavaScriptTask();
        PerformMicrotaskCheckpoint();
        string? firstError = null;
        while (true)
        {
            var tick = PumpTick(out var error);
            firstError ??= error;
            if (tick == LoopTick.Idle)
            {
                break;
            }
            if (tick == LoopTick.Waiting)
            {
                await ParkAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            }
        }
        PerformMicrotaskCheckpoint();
        if (firstError is not null)
        {
            throw new JsRuntimeException(firstError);
        }
    }

    /// <summary>
    /// Drive one cooperative event-loop turn. True only when the loop reached
    /// full idle.
    /// </summary>
    /// <remarks>
    /// The reference deliberately turns the wake for a second deno_core tick
    /// into a return to the caller, so a page that keeps the loop continuously
    /// ready (zero-delay schedulers, streaming traffic, a framework work queue)
    /// cannot starve the embedder's deadline. The port keeps that shape: one
    /// batch of due timers per turn, then back to the caller.
    /// </remarks>
    public async Task<bool> RunCooperativeEventLoopTickAsync()
    {
        BeginJavaScriptTask();
        PerformMicrotaskCheckpoint();
        var tick = PumpTick(out var error);
        if (error is not null)
        {
            throw new JsRuntimeException(error);
        }
        if (tick == LoopTick.Waiting)
        {
            await ParkAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            return false;
        }
        return tick == LoopTick.Idle;
    }

    /// <summary>
    /// Drive one cooperative turn for browser lifecycle code that re-checks an
    /// external readiness predicate after every wake.
    /// </summary>
    public Task<bool> RunLoadDelayingEventLoopTickAsync() => RunCooperativeEventLoopTickAsync();

    /// <summary>
    /// Drive one browser task under the shared task-budget watchdog. The
    /// long-lived server counterpart to bounded settling.
    /// </summary>
    public async Task<bool> RunAutonomousEventLoopTurnAsync()
    {
        const ulong AutonomousTaskWatchdogMs = SynchronousTaskFloorMs + WatchdogSchedulingMarginMs;

        BeginJavaScriptTask();

        // The shared CDP watchdog (cdp_watchdog.rs), not the per-call one: this
        // turn is armed only around synchronous V8 entry, and a long parked
        // timer must not look like a hung task merely because the runtime is
        // asleep waiting for it.
        var checkpointWatchdog = CdpWatchdog.Arm(
            IsolateHandleForWatchdog, TimeSpan.FromMilliseconds(AutonomousTaskWatchdogMs));
        try
        {
            PerformMicrotaskCheckpoint();
        }
        catch (ScriptInterruptedException)
        {
            CdpWatchdog.Disarm(checkpointWatchdog);
            CancelTermination();
            throw new JsRuntimeException("autonomous microtask checkpoint exceeded its task budget");
        }
        if (CdpWatchdog.Disarm(checkpointWatchdog))
        {
            CancelTermination();
            throw new JsRuntimeException("autonomous microtask checkpoint exceeded its task budget");
        }

        var watchdog = CdpWatchdog.Arm(
            IsolateHandleForWatchdog, TimeSpan.FromMilliseconds(AutonomousTaskWatchdogMs));
        LoopTick tick;
        string? error;
        try
        {
            tick = PumpTick(out error);
        }
        catch (ScriptInterruptedException)
        {
            CdpWatchdog.Disarm(watchdog);
            CancelTermination();
            throw new JsRuntimeException("autonomous browser task exceeded its task budget");
        }
        if (CdpWatchdog.Disarm(watchdog))
        {
            CancelTermination();
            throw new JsRuntimeException("autonomous browser task exceeded its task budget");
        }
        if (error is not null && IsFatalEventLoopError(error))
        {
            throw new JsRuntimeException(error);
        }
        if (tick == LoopTick.Waiting)
        {
            await ParkAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            return false;
        }
        return tick == LoopTick.Idle && error is null;
    }

    /// <summary>
    /// Drive the loop for at most <paramref name="budgetMs"/>, bounded against
    /// both async idle and synchronous hangs.
    /// </summary>
    /// <remarks>
    /// The deadline is observed between browser tasks: Chromium does not
    /// terminate the JavaScript task that happens to be active when a
    /// screenshot delay expires, so a task already running gets a five-second
    /// completion allowance plus a scheduling margin before the watchdog
    /// interrupts it. A well-behaved page returns as soon as the loop is idle.
    /// </remarks>
    public async Task RunEventLoopBoundedAsync(ulong budgetMs)
    {
        if (budgetMs == 0)
        {
            await RunEventLoopAsync().ConfigureAwait(false);
            return;
        }

        var clock = Stopwatch.StartNew();
        var token = ArmWatchdog(
            TimeSpan.FromMilliseconds(budgetMs + SynchronousTaskFloorMs + WatchdogSchedulingMarginMs));
        string? fatal = null;
        try
        {
            while (clock.Elapsed.TotalMilliseconds < budgetMs)
            {
                LoopTick tick;
                string? error;
                try
                {
                    tick = PumpTick(out error);
                }
                catch (ScriptInterruptedException)
                {
                    break;
                }
                if (error is not null)
                {
                    if (IsFatalEventLoopError(error))
                    {
                        fatal = error;
                        break;
                    }
                    // A page task threw or a promise rejected without a handler.
                    // Chrome reports it and keeps scheduling; the pump must do
                    // the same, or one throwing script starves every later task.
                }
                if (tick == LoopTick.Idle)
                {
                    break;
                }
                var remaining = budgetMs - clock.Elapsed.TotalMilliseconds;
                if (remaining <= 0)
                {
                    break;
                }
                if (tick == LoopTick.Waiting)
                {
                    await ParkAsync(TimeSpan.FromMilliseconds(Math.Min(remaining, 50))).ConfigureAwait(false);
                }
                else
                {
                    // End-of-task microtasks belong to this turn, but work queued
                    // from them belongs to a subsequent cooperative turn. Yield so
                    // the wall deadline stays observable even when every turn
                    // immediately schedules another one.
                    PerformMicrotaskCheckpoint();
                    await Task.Yield();
                }
            }
        }
        finally
        {
            var fired = DisarmWatchdog(token);
            if (fired)
            {
                fatal = null;
            }
        }
        if (fatal is not null && fatal.Contains("heap limit exceeded", StringComparison.Ordinal))
        {
            throw new JsRuntimeException(fatal);
        }
    }

    /// <summary>
    /// Drive page tasks for a fixed observation interval without asking a
    /// run-to-idle future to own that whole interval.
    /// </summary>
    public Task RunEventLoopForDurationAsync(ulong budgetMs) =>
        budgetMs == 0 ? Task.CompletedTask : RunEventLoopBoundedAsync(budgetMs);

    /// <summary>
    /// Pump deferred work until the loop is idle, or until the page has had no
    /// connected-document mutation, relevant request or dynamic-script work, or
    /// near-term one-shot timeout for <paramref name="quietMs"/>.
    /// </summary>
    /// <remarks>
    /// Network and script work gets a bounded post-load grace period, so
    /// ordinary app hydration is retained without letting analytics, telemetry
    /// or a hung endpoint consume the caller's whole budget. Long timers and
    /// perpetual visual mutations are bounded the same way.
    /// <paramref name="budgetMs"/> remains an absolute wall-clock bound.
    /// </remarks>
    public async Task RunEventLoopUntilQuiescentAsync(ulong budgetMs, ulong quietMs)
    {
        if (budgetMs == 0)
        {
            return;
        }

        // A one-second grace covers the common load -> fetch -> framework commit
        // path. Requests still pending after it are no longer readiness evidence
        // by themselves; their eventual DOM mutation is still observed during
        // the bounded activity tail.
        const double ExternalWorkGraceMs = 1_000;
        const double ObservableActivityTailMs = 500;

        var budget = (double)budgetMs;
        var quiet = Math.Min(Math.Max(quietMs, 1), budgetMs);
        var clock = Stopwatch.StartNew();
        var externalWorkDeadline = Math.Min(ExternalWorkGraceMs, budget);
        var activityDeadline = Math.Min(budget, ObservableActivityTailMs);

        var token = ArmWatchdog(
            TimeSpan.FromMilliseconds(budget + SynchronousTaskFloorMs + WatchdogSchedulingMarginMs));
        var generation = ActivityGenerationValue;
        double? quietSince = null;
        string? fatal = null;
        try
        {
            while (true)
            {
                var now = clock.Elapsed.TotalMilliseconds;
                if (now >= budget)
                {
                    break;
                }
                var nextGeneration = ActivityGenerationValue;
                // One-shot timers up to two quiet windows away are commonly app
                // hydration or debounce work. Intervals are excluded on purpose,
                // and distant one-shots are treated like Chromium after load.
                var nearTimeout = NextPendingTimeoutDelayMs() is { } delay && delay <= quiet * 2.0;
                var externalWorkPending = now < externalWorkDeadline
                    && (HasPendingNetworkRequests() || HasPendingDynamicScripts());

                if (externalWorkPending)
                {
                    activityDeadline = Math.Min(budget, externalWorkDeadline + ObservableActivityTailMs);
                    generation = nextGeneration;
                    quietSince = null;
                }
                else if (now < activityDeadline && nearTimeout)
                {
                    generation = nextGeneration;
                    quietSince = null;
                }
                else
                {
                    if (now < activityDeadline && nextGeneration != generation)
                    {
                        // A mutation starts a fresh quiet interval at its observed
                        // delivery time.
                        quietSince = now;
                    }
                    generation = nextGeneration;
                    quietSince ??= now;
                    if (now - quietSince.Value >= quiet)
                    {
                        break;
                    }
                }

                var policyDeadline = Math.Min(
                    budget,
                    externalWorkPending
                        ? externalWorkDeadline
                        : now < activityDeadline && nearTimeout
                            ? activityDeadline
                            : quietSince is { } since ? since + quiet : budget);

                var tickWatchdog = ArmWatchdog(TimeSpan.FromMilliseconds(
                    Math.Max(0, policyDeadline - now) + SynchronousTaskFloorMs + WatchdogSchedulingMarginMs));
                LoopTick tick;
                string? error = null;
                var interrupted = false;
                try
                {
                    tick = PumpTick(out error);
                }
                catch (ScriptInterruptedException)
                {
                    tick = LoopTick.Idle;
                    interrupted = true;
                }
                if (DisarmWatchdog(tickWatchdog) || interrupted)
                {
                    break;
                }
                PerformMicrotaskCheckpoint();
                if (error is not null && IsFatalEventLoopError(error))
                {
                    fatal = error;
                    break;
                }
                if (tick == LoopTick.Idle)
                {
                    break;
                }
                if (tick == LoopTick.Waiting)
                {
                    var remaining = Math.Max(1, policyDeadline - clock.Elapsed.TotalMilliseconds);
                    await ParkAsync(TimeSpan.FromMilliseconds(Math.Min(remaining, 50))).ConfigureAwait(false);
                }
                else
                {
                    await Task.Yield();
                }
            }
        }
        finally
        {
            if (DisarmWatchdog(token))
            {
                fatal = null;
            }
        }
        if (fatal is not null && fatal.Contains("heap limit exceeded", StringComparison.Ordinal))
        {
            throw new JsRuntimeException(fatal);
        }
    }

    /// <summary>Default settle: pump until idle or five seconds.</summary>
    public async Task ResolvePromisesAsync()
    {
        BeginJavaScriptTask();
        try
        {
            await RunEventLoopBoundedAsync(5_000).ConfigureAwait(false);
        }
        catch (JsRuntimeException)
        {
            // The reference discards the outcome here.
        }
    }

    /// <summary>
    /// Pump until <paramref name="doneCheck"/> returns true, or
    /// <paramref name="maxTotalMs"/> elapses. Returns whether the predicate
    /// completed before the deadline.
    /// </summary>
    /// <remarks>
    /// Running to idle is not good enough: page JS routinely schedules long
    /// timeouts (an IntersectionObserver re-fire at 7s, requestIdleCallback)
    /// that the caller does not care about, and waiting for them added seconds
    /// per click. This checks the caller's own sentinel between short pump
    /// slices, backing off to 50ms so a hung promise does not burn CPU.
    /// </remarks>
    public async Task<bool> ResolvePromisesUntilAsync(Func<ObscuraJsRuntime, bool> doneCheck, ulong maxTotalMs)
    {
        ArgumentNullException.ThrowIfNull(doneCheck);
        var clock = Stopwatch.StartNew();
        var tickMs = 1UL;
        while (true)
        {
            BeginJavaScriptTask();
            if (doneCheck(this))
            {
                return true;
            }
            if (clock.Elapsed.TotalMilliseconds >= maxTotalMs)
            {
                return false;
            }
            try
            {
                await RunEventLoopBoundedAsync(tickMs).ConfigureAwait(false);
            }
            catch (JsRuntimeException error) when (error.Message.Contains("heap limit exceeded", StringComparison.Ordinal))
            {
                return false;
            }
            catch (JsRuntimeException)
            {
                // Page-local task noise; the predicate decides when to stop.
            }
            if (tickMs < 50)
            {
                tickMs = Math.Min(tickMs * 2, 50);
            }
        }
    }

    /// <summary>
    /// Whether the serialized dynamic-script queue is still fetching or
    /// evaluating a script.
    /// </summary>
    /// <remarks>
    /// The queue stays private to the bootstrap closure; the host reads it
    /// through a hidden status function so page declarations cannot collide
    /// with or overwrite the queue itself.
    /// </remarks>
    public bool HasPendingDynamicScripts() =>
        EvaluateBool("globalThis.__obscura_hasPendingDynamicScripts?.() === true")
        || _moduleLoader.Activity.IsPendingOrRecent(TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Whether a connected dynamic script prepared before the document load
    /// event still has fetch, evaluation or load-or-error work outstanding.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes <c>import()</c> and scripts created by a load
    /// handler: those are ordinary post-load enhancement work and are only
    /// driven when an automation caller explicitly asks the page to settle.
    /// </remarks>
    public bool HasPendingLoadDelayingScripts() =>
        EvaluateBool("globalThis.__obscura_hasPendingLoadDelayingScripts?.() === true");

    private double? NextPendingTimeoutDelayMs()
    {
        try
        {
            var value = Evaluate("globalThis.__obscura_nextPendingTimeoutDelay?.() ?? -1");
            if (value?.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            {
                var delay = value.GetValue<double>();
                return delay >= 0 ? delay : null;
            }
        }
        catch (JsRuntimeException)
        {
            // No shim in this build.
        }
        // The host queue is authoritative for a page whose shim did not install
        // the accessor.
        return Timers.NextDelayMs();
    }

    private bool EvaluateBool(string expression)
    {
        try
        {
            return Evaluate(expression)?.GetValueKind() == System.Text.Json.JsonValueKind.True;
        }
        catch (JsRuntimeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Generation of observable connected-document mutations, as the settle
    /// policy samples it. Zero when no state hub is attached.
    /// </summary>
    private ulong ActivityGenerationValue
    {
        get
        {
            ulong generation = 0;
            ReadActivityGeneration(ref generation);
            return generation;
        }
    }

    partial void ReadActivityGeneration(ref ulong generation);

    private bool HasPendingNetworkRequests()
    {
        var pending = false;
        ReadPendingNetworkRequests(ref pending);
        return pending;
    }

    partial void ReadPendingNetworkRequests(ref bool pending);
}
