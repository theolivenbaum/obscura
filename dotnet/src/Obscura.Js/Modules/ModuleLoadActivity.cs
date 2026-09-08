using System.Diagnostics;

namespace Obscura.Js.Modules;

/// <summary>
/// Observable network activity for ES-module graphs.
/// </summary>
/// <remarks>
/// <para>
/// The browser lifecycle needs to distinguish a genuinely idle page from a graph
/// whose fetch is being advanced in short event-loop slices. A loader-owned
/// counter provides that signal <em>without</em> treating unrelated fetch/XHR
/// analytics as render-blocking work: nothing outside
/// <see cref="ObscuraModuleLoader"/> ever increments it, and the page's own
/// in-flight request count is a separate signal that the runtime keeps separate.
/// </para>
/// <para>
/// Only dynamic-import graphs are counted, matching the Rust loader's use of
/// deno_core's <c>is_dyn_import</c>. Static graphs are already accounted for by
/// the script queue that owns them.
/// </para>
/// </remarks>
public sealed class ModuleLoadActivity
{
    private readonly Lock _gate = new();
    private int _pending;
    private long _lastActivity;
    private bool _hasActivity;

    /// <summary>Loads currently registered but not yet finished.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>
    /// Register one in-flight module load. Dispose the returned guard exactly once,
    /// on success, on failure, and on cancellation alike; the Rust guard decrements
    /// through <c>Drop</c>, which navigation away from a graph also runs.
    /// </summary>
    public ModuleLoadGuard Begin()
    {
        lock (_gate)
        {
            _pending++;
            Touch();
        }

        return new ModuleLoadGuard(this);
    }

    /// <summary>
    /// Whether a module graph is fetching, or finished fetching within
    /// <paramref name="grace"/>. The short tail bridges the hand-off between one
    /// fetched module and the next dependency, which is otherwise invisible.
    /// </summary>
    public bool IsPendingOrRecent(TimeSpan grace)
    {
        if (Volatile.Read(ref _pending) != 0)
        {
            return true;
        }

        lock (_gate)
        {
            if (!_hasActivity)
            {
                return false;
            }

            var elapsed = Stopwatch.GetElapsedTime(_lastActivity);
            return elapsed <= grace;
        }
    }

    internal void End()
    {
        lock (_gate)
        {
            Debug.Assert(_pending > 0, "module load activity counter underflow");
            if (_pending > 0)
            {
                _pending--;
            }

            Touch();
        }
    }

    private void Touch()
    {
        _lastActivity = Stopwatch.GetTimestamp();
        _hasActivity = true;
    }
}

/// <summary>
/// The lifetime of one registered module load. Disposing is idempotent, because
/// the ClearScript loader disposes it in a <c>finally</c> and the Rust guard can
/// only drop once.
/// </summary>
public sealed class ModuleLoadGuard : IDisposable
{
    private ModuleLoadActivity? _owner;

    internal ModuleLoadGuard(ModuleLoadActivity owner) => _owner = owner;

    /// <inheritdoc/>
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.End();
}
