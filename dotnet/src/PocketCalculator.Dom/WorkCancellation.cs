using System.Runtime.CompilerServices;

namespace PocketCalculator.Dom;

/// <summary>
/// The cancellation token of the synchronous DOM, style, layout or paint pass running on
/// this thread, so the walks inside it can stop when their caller's deadline passes.
/// </summary>
/// <remarks>
/// <para>
/// The V8 watchdog interrupts script with <c>V8ScriptEngine.Interrupt()</c>, which V8 only
/// acts on once control is back in JavaScript. An op that spends its time in C# (one
/// <c>getBoundingClientRect()</c> over a pathological tree, one <c>querySelectorAll</c>
/// with an expensive selector, one screenshot) never gets there, so the interrupt waited
/// for the op to finish however long that took (SECURITY.md H8). Every entry point that
/// starts such a pass takes a <see cref="CancellationToken"/> and opens a scope with
/// <see cref="Enter"/>; the loops inside call <see cref="ThrowIfCancellationRequested"/>.
/// </para>
/// <para>
/// A scope rather than a parameter on every recursive function: the pipeline has hundreds
/// of them, a pass is synchronous on one thread, and the check has to cost next to
/// nothing when no token is set, which a thread-static read of a default token does. Code
/// that crosses threads inside a pass (<c>StackGuard.RunWithStackFor</c>) carries
/// <see cref="Current"/> across with it. Asynchronous code passes tokens explicitly.
/// </para>
/// </remarks>
public static class WorkCancellation
{
    [ThreadStatic]
    private static CancellationToken _current;

    /// <summary>The token of the innermost scope on this thread, or none.</summary>
    public static CancellationToken Current => _current;

    /// <summary>
    /// Make <paramref name="token"/> the current token until the returned scope is disposed.
    /// A scope opened inside another observes both tokens.
    /// </summary>
    public static Scope Enter(CancellationToken token)
    {
        CancellationToken previous = _current;
        if (!token.CanBeCanceled || token == previous)
        {
            return new Scope(previous, null, active: false);
        }

        if (!previous.CanBeCanceled)
        {
            _current = token;
            return new Scope(previous, null, active: true);
        }

        // Nested and different: rare (an op reached from a command that has its own
        // deadline), so the linked source's allocation is acceptable here.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(previous, token);
        _current = linked.Token;
        return new Scope(previous, linked, active: true);
    }

    /// <summary>
    /// Throw <see cref="OperationCanceledException"/> if the current pass has been
    /// cancelled. Cheap enough to call once per node.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ThrowIfCancellationRequested()
    {
        if (_current.IsCancellationRequested)
        {
            Throw();
        }
    }

    /// <summary>Whether the current pass has been cancelled.</summary>
    public static bool IsCancellationRequested
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _current.IsCancellationRequested;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw() => throw new OperationCanceledException(
        "The operation exceeded its time budget and was cancelled.", _current);

    /// <summary>Restores the previous token when disposed.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly CancellationToken _previous;
        private readonly CancellationTokenSource? _linked;
        private readonly bool _active;

        internal Scope(CancellationToken previous, CancellationTokenSource? linked, bool active)
        {
            _previous = previous;
            _linked = linked;
            _active = active;
        }

        public void Dispose()
        {
            if (!_active)
            {
                return;
            }

            _current = _previous;
            _linked?.Dispose();
        }
    }
}
