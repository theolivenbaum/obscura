namespace Obscura.Js.Ops;

/// <summary>
/// Exception containment for op bodies.
/// </summary>
/// <remarks>
/// <para>
/// The Rust engine wraps every op body in <c>catch_unwind</c> because unwinding
/// into V8's FFI frame aborts the process. The managed equivalent is no less
/// load-bearing: an exception thrown inside a host delegate that V8 invoked
/// crosses the interop boundary, and the shim is not written to survive it. The
/// shim expects the op's documented failure value instead - usually an empty
/// string or the literal <c>"null"</c>.
/// </para>
/// <para>
/// Every op must be routed through one of these helpers. The correct failure
/// value differs per op; take it from <c>op_dom_inner</c> and the individual op
/// bodies in <c>crates/obscura-js/src/ops.rs</c>, not from intuition.
/// </para>
/// </remarks>
public static class OpGuard
{
    /// <summary>Raised for every contained exception, for diagnostics.</summary>
    public static event Action<string, Exception>? OpFailed;

    /// <summary>Runs an op body, returning <paramref name="onFailure"/> if it throws.</summary>
    public static T Run<T>(string opName, Func<T> body, T onFailure)
    {
        try
        {
            return body();
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            OpFailed?.Invoke(opName, ex);
            return onFailure;
        }
    }

    /// <summary>Runs a void op body, swallowing any non-fatal exception.</summary>
    public static void Run(string opName, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            OpFailed?.Invoke(opName, ex);
        }
    }

    /// <summary>Runs an async op body, returning <paramref name="onFailure"/> if it throws.</summary>
    public static async Task<T> RunAsync<T>(string opName, Func<Task<T>> body, T onFailure)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            OpFailed?.Invoke(opName, ex);
            return onFailure;
        }
    }

    /// <summary>
    /// Exceptions that must not be contained. A script interrupt is how the
    /// watchdog terminates a runaway page, so swallowing it would defeat the
    /// termination path; the rest signal the process is already unrecoverable.
    /// </summary>
    private static bool IsFatal(Exception ex) => ex is
        Microsoft.ClearScript.ScriptInterruptedException or
        StackOverflowException or
        OutOfMemoryException or
        System.Threading.ThreadAbortException;
}
