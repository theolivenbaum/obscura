using System.Globalization;
using Microsoft.ClearScript;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// Host-side record of the async ops that are still owed a JavaScript continuation.
/// </summary>
/// <remarks>
/// deno_core resolves an op's promise inside <c>poll_event_loop</c>, so
/// <c>has_pending_ops</c> cannot go false before the page's reaction has been
/// delivered. The port hands ClearScript a <see cref="Task"/> instead and gets the
/// promise resolution from that Task's continuation, which is not something the host
/// can observe. <see cref="AsyncOpBinding"/> closes the gap by counting from JavaScript;
/// the runtime implements this and the event loop reads the count.
/// </remarks>
public interface IAsyncOpTracker
{
    /// <summary>An async op was called and has not settled its promise yet.</summary>
    void OpStarted();

    /// <summary>The op's promise has settled and its reactions are running.</summary>
    void OpSettled();
}

/// <summary>
/// Binds a <see cref="Task"/>-returning op through a JavaScript shim that keeps the
/// op counted until its promise reaction runs.
/// </summary>
/// <remarks>
/// <para>
/// DEVIATION from <c>crates/obscura-js</c>: Rust has nothing equivalent because
/// deno_core owns both halves. It drives the op future and resolves the op's promise
/// in the same <c>poll_event_loop</c> turn, so the loop is never able to observe
/// itself as idle between a transport finishing and the page's <c>.then</c> running.
/// </para>
/// <para>
/// Here an op is a <see cref="Task"/> whose promise ClearScript resolves from that
/// Task's continuation, off the loop. Every host-side signal the loop had - a fetch's
/// <c>PocketCalculatorState.PageInFlight</c>, a realm sleep's timer - is released inside or
/// before the op body's <c>finally</c>, which runs before the Task completes and so
/// before the promise resolves. A settle landing in that window saw no timers, no
/// posted tasks and nothing in flight, reported idle, and returned with its budget
/// unspent and the continuation never delivered. <c>58e0640</c> patched the one case
/// it could name (dynamically inserted external scripts) by re-checking the page; this
/// is the general form.
/// </para>
/// <para>
/// The counter is incremented where the op is called and decremented from a reaction
/// attached to the op's own promise. A promise reaction can only run inside a
/// microtask checkpoint, and the loop evaluates its idle verdict after the checkpoint
/// it performs, never during one - so the count outlives the promise resolution for
/// exactly as long as deno_core's does. The reaction is registered before the shim's,
/// so the page's continuation runs in the same checkpoint drain and anything it
/// schedules is registered before the loop asks whether it is idle.
/// </para>
/// <para>
/// Attaching a rejection handler marks the op's own promise handled. That costs
/// nothing observable: every async op in <c>bootstrap.js</c> is either awaited or
/// given both reactions, so an op rejection already surfaces on the shim's promise
/// rather than on the op's, and that promise is untouched here.
/// </para>
/// </remarks>
internal static class AsyncOpBinding
{
    /// <summary>
    /// Binds <paramref name="name"/> with tracking when it is an async op and a
    /// tracker is installed. False means the caller should bind it normally.
    /// </summary>
    public static bool TryBind(ScriptObject ops, string name, object function, IAsyncOpTracker? tracker)
    {
        if (tracker is null
            || function is not Delegate op
            || !typeof(Task).IsAssignableFrom(op.Method.ReturnType))
        {
            return false;
        }

        var parameters = string.Join(
            ", ",
            Enumerable.Range(0, op.Method.GetParameters().Length)
                .Select(index => "a" + index.ToString(CultureInfo.InvariantCulture)));
        // The parameter list is spelled out rather than forwarded with `arguments`,
        // because `inner` is a host delegate of fixed arity and calling it with a
        // different count fails at the marshaller.
        // Promise.prototype.then and Reflect.apply are taken when the table is bound, before
        // any page script runs: a page that replaced `then` with one that never calls back
        // left every async op counted forever, so the realm never went idle and a frame's
        // timers (op_sleep continuations) never fired.
        var source = $$"""
            (function (inner, started, settled) {
                var then = Promise.prototype.then, apply = Reflect.apply;
                return function ({{parameters}}) {
                    var promise = inner({{parameters}});
                    if (promise !== null && typeof promise === "object") {
                        // A reaction never runs synchronously, so counting after it is
                        // attached cannot miss the settle; a non-promise throws here.
                        var attached = false;
                        try {
                            apply(then, promise, [function () { settled(); }, function () { settled(); }]);
                            attached = true;
                        } catch (e) {}
                        if (attached) started();
                    }
                    return promise;
                };
            })
            """;

        if (ops.Engine.Evaluate("<async-op-binding>", true, source) is not ScriptObject factory)
        {
            return false;
        }

        ops.SetProperty(
            name,
            factory.InvokeAsFunction(
                function,
                (Action)(() => OpGuard.Run(name + " started", tracker.OpStarted)),
                (Action)(() => OpGuard.Run(name + " settled", tracker.OpSettled))));
        return true;
    }
}
