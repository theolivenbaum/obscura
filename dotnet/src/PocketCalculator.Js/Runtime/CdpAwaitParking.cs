using Microsoft.ClearScript;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// Lets an awaited <c>Runtime.evaluate</c> / <c>Runtime.callFunctionOn</c> stop waiting
/// when the connection has other work, and be answered later.
/// </summary>
/// <remarks>
/// Chromium answers an <c>awaitPromise</c> command when its promise settles and serves
/// the connection's other commands meanwhile. A promise that calls an exposed binding
/// settles only after the client answers <c>Runtime.bindingCalled</c> with a command of
/// its own, so a server that waits inline deadlocks. The CDP server sets
/// <see cref="YieldWhen"/> around a command it is able to answer later; while it is
/// set and returns true, an unsettled await throws <see cref="CdpAwaitParkedException"/>
/// instead of waiting. Unset (a direct caller, a nested command), the await is inline
/// as before. Port addition: the Rust engine awaits inline and has the same deadlock.
/// </remarks>
public static class CdpAwaitParking
{
    private static readonly AsyncLocal<Func<bool>?> YieldPredicate = new();

    /// <summary>The flowing predicate; null when this command cannot be answered later.</summary>
    public static Func<bool>? YieldWhen
    {
        get => YieldPredicate.Value;
        set => YieldPredicate.Value = value;
    }
}

/// <summary>An awaited CDP command whose promise had not settled when the connection had other work.</summary>
public sealed class CdpAwaitParkedException(ParkedCdpAwait parked)
    : Exception($"{parked.Method} is waiting for its promise")
{
    public ParkedCdpAwait Parked { get; } = parked;
}

/// <summary>The realm a parked command's promise lived in has gone away.</summary>
public sealed class CdpContextGoneException(string message) : Exception(message);

/// <summary>A parked <c>awaitPromise</c> command: the wrapper keeps running in its realm.</summary>
public sealed class ParkedCdpAwait
{
    private readonly PocketCalculatorJsRuntime _runtime;
    private readonly CdpScope _scope;
    private readonly string _done;
    private readonly Func<ScriptObject, RemoteObjectInfo> _finish;

    internal ParkedCdpAwait(
        PocketCalculatorJsRuntime runtime,
        CdpScope scope,
        string done,
        string method,
        ulong timeoutMs,
        long deadline,
        Func<ScriptObject, RemoteObjectInfo> finish)
    {
        _runtime = runtime;
        _scope = scope;
        _done = done;
        _finish = finish;
        Method = method;
        TimeoutMs = timeoutMs;
        Deadline = deadline;
    }

    /// <summary><c>Runtime.evaluate</c> or <c>Runtime.callFunctionOn</c>.</summary>
    public string Method { get; }

    public ulong TimeoutMs { get; }

    /// <summary>The <see cref="Environment.TickCount64"/> after which the command fails.</summary>
    public long Deadline { get; }

    /// <summary>The runtime the promise lives in.</summary>
    public PocketCalculatorJsRuntime Runtime => _runtime;

    /// <summary>The message the inline await reports when the promise does not settle in time.</summary>
    public string TimeoutMessage => $"{Method} promise did not settle within {TimeoutMs}ms";

    /// <summary>
    /// The command's result once its promise has settled, or null while it has not.
    /// Throws <see cref="CdpContextGoneException"/> when the realm has gone away.
    /// </summary>
    public RemoteObjectInfo? TryComplete()
    {
        if (_runtime.IsDisposed)
        {
            throw new CdpContextGoneException(TargetNavigated);
        }

        ScriptObject? outcome;
        try
        {
            outcome = _scope.Outcomes.GetProperty(_done) as ScriptObject;
        }
        catch (Exception error) when (error is ScriptEngineException or InvalidOperationException or ObjectDisposedException)
        {
            throw new CdpContextGoneException(ContextDestroyed);
        }

        if (outcome is null)
        {
            return null;
        }

        _scope.Outcomes.DeleteProperty(_done);
        return _finish(outcome);
    }

    /// <summary>Chromium's answer to an awaited command whose context went away.</summary>
    public const string ContextDestroyed = "Execution context was destroyed.";

    /// <summary>Chromium's answer to an awaited command whose page navigated or closed.</summary>
    public const string TargetNavigated = "Inspected target navigated or closed";
}
