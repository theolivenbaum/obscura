using System.Runtime.ExceptionServices;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// Runs host code while holding the page isolate's lock, so no script of this page (and no
/// op it calls) runs at the same time.
/// </summary>
/// <remarks>
/// <para>
/// DEVIATION from crates/obscura-js, which needs nothing like it: deno_core runs every op
/// reaction and every promise continuation on the one thread that owns the isolate and the
/// state. ClearScript resolves the promise of a <see cref="Task"/>-returning op from that
/// Task's continuation, on whichever thread completed it, and the reaction's script runs
/// there under the isolate lock. Host code the page thread runs outside script (a CDP
/// capture laying out and painting, an op's continuation after its <c>await</c>) did not
/// take that lock, so page script could create nodes and invalidate the retained render in
/// the middle of it: samsung.com's <c>Page.captureScreenshot</c> failed with "Collection was
/// modified" from <c>DomTree.Count</c> under the painter, and with "the page has no retained
/// DOM surface to render" when the resolved scroll was cleared between the capture's check
/// and its read.
/// </para>
/// <para>
/// Op settlements and the op tails that touch page state now run on the page's own loop
/// (<see cref="OpCompletionContext"/>, <see cref="RunOnPageAsync{T}"/>), so the page thread no
/// longer races them and the lock is uncontended in the engine's own use. It is kept, held by
/// captures, parser steps and the render-resource service step, because it costs a reentrant
/// <c>Locker</c> on the thread that already owns the isolate and because an embedder may still
/// call into the runtime from a thread of its own while a capture runs.
/// </para>
/// <para>
/// The lock is V8's own <c>Locker</c>, which ClearScript takes for every call into an engine
/// and which is reentrant on its thread, so the body may call into script and the ops it
/// calls are unaffected. It is entered through a one-line trampoline function that calls the
/// body back. Lock order: the isolate lock is taken before any host lock
/// (<c>AsyncResourceGate</c>), as an op called from script takes them.
/// </para>
/// </remarks>
public sealed class IsolateLock
{
    /// <summary>No isolate: the body runs as it is (a state with no runtime, in tests).</summary>
    public static IsolateLock None { get; } = new(null, null);

    private readonly ScriptObject? _trampoline;
    private readonly OpCompletionContext? _pageThread;

    private IsolateLock(ScriptObject? trampoline, OpCompletionContext? pageThread)
    {
        _trampoline = trampoline;
        _pageThread = pageThread;
    }

    /// <summary>
    /// A lock on <paramref name="engine"/>'s isolate, whose page loop runs
    /// <paramref name="pageThread"/>'s posts.
    /// </summary>
    internal static IsolateLock For(V8ScriptEngine engine, OpCompletionContext? pageThread = null) =>
        new((ScriptObject)engine.Evaluate("<isolate-lock>", true, "(function (body) { body(); })"), pageThread);

    /// <summary>
    /// Run <paramref name="body"/> on the page's own event loop, between tasks, and complete the
    /// returned task there: the place for an async op's tail that touches page state after its
    /// <c>await</c>.
    /// </summary>
    /// <remarks>
    /// This is what deno_core does for every op, and what the port did with <see cref="Run{T}"/>
    /// from the thread that completed the op until op settlements moved to the page's loop
    /// (<see cref="OpCompletionContext"/>): waiting for the isolate lock there parked a
    /// thread-pool thread for as long as page script ran, which is how the pool starved. With
    /// no page loop (a state with no runtime) the body runs under <see cref="Run{T}"/> as before.
    /// Cancelled when the page goes away before the body runs.
    /// </remarks>
    public Task<T> RunOnPageAsync<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (_pageThread is { } pageThread)
        {
            return pageThread.InvokeAsync(body);
        }

        try
        {
            return Task.FromResult(Run(body));
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
    }

    /// <summary>Run <paramref name="body"/> holding the isolate lock and return its result.</summary>
    /// <remarks>
    /// When the isolate cannot be entered (disposed, or a termination is pending, which makes
    /// any call into it throw before the body starts) the body runs without the lock, as it
    /// did before: there is then no script that could run beside it either.
    /// </remarks>
    public T Run<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (_trampoline is not { } trampoline)
        {
            return body();
        }

        bool started = false;
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        try
        {
            trampoline.InvokeAsFunction(new Action(() =>
            {
                started = true;
                try
                {
                    result = body();
                }
                catch (Exception error)
                {
                    // Rethrown below, outside the script frame, as itself rather than
                    // wrapped in a ScriptEngineException.
                    failure = ExceptionDispatchInfo.Capture(error);
                }
            }));
        }
        catch (Exception error) when (!started
            && error is ScriptInterruptedException or ScriptEngineException
                or ObjectDisposedException or InvalidOperationException)
        {
            return body();
        }
        catch (ScriptInterruptedException) when (started && failure is null)
        {
            // The body finished; a termination requested while it ran (the command
            // watchdog) surfaced on the way out of the trampoline. Its outcome stands, and
            // the termination is the watchdog's to clear.
        }

        failure?.Throw();
        return result;
    }
}
