using System.Collections.Concurrent;

namespace Obscura.Cdp;

/// <summary>
/// A one-thread cooperative scheduler: <c>tokio</c>'s current-thread runtime
/// plus <c>LocalSet</c>.
/// </summary>
/// <remarks>
/// <para>
/// The CDP server gives each connection its own OS thread so every page's V8
/// isolate is confined to a single thread. That only holds if every
/// continuation of that connection's work resumes on the same thread; the
/// default .NET behaviour is to resume on a thread-pool thread, which would put
/// two connections' isolates on one thread and reintroduce exactly the abort
/// (#430) the thread-per-connection layout removed.
/// </para>
/// <para>
/// Installing this as the current <see cref="SynchronizationContext"/> makes
/// every <c>await</c> inside a connection post its continuation back to the
/// owning thread, so a connection's processor and its frame reader interleave at
/// await points and never actually run in parallel.
/// </para>
/// </remarks>
public sealed class SingleThreadedSynchronizationContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
            // The pump has already shut down (ObjectDisposedException derives
            // from this one, so a disposed queue lands here too); the
            // continuation belongs to work this connection has abandoned.
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Post(
            _ =>
            {
                try
                {
                    d(state);
                }
                finally
                {
                    done.Set();
                }
            },
            null);
        done.Wait();
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Run <paramref name="body"/> on the calling thread, pumping continuations
    /// until it completes. Exceptions from <paramref name="body"/> propagate.
    /// </summary>
    public static void Run(Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var previous = Current;
        var context = new SingleThreadedSynchronizationContext();
        SetSynchronizationContext(context);
        try
        {
            var task = body();
            task.ContinueWith(
                static (_, state) => ((SingleThreadedSynchronizationContext)state!).Complete(),
                context,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            context.Pump();
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
            context._queue.Dispose();
        }
    }

    private void Complete() => _queue.CompleteAdding();

    private void Pump()
    {
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            callback(state);
        }
    }
}
