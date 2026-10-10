using System.Runtime.ExceptionServices;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// The page's op completion queue: a <see cref="SynchronizationContext"/> whose posts are run
/// by the page's event loop, between tasks, with no script on the stack.
/// </summary>
/// <remarks>
/// <para>
/// DEVIATION from nothing in crates/obscura-js, which needs no such thing: deno_core drives an
/// op's future inside <c>poll_event_loop</c> and resolves its promise there, on the thread that
/// owns the isolate. ClearScript turns a <see cref="Task"/>-returning op into a promise with
/// <c>task.ContinueWith(resolve, ExecuteSynchronously, scheduler)</c>, so without a context the
/// promise was resolved on whichever thread-pool thread completed the op, and resolving it enters
/// V8, which needs the isolate lock. While the page ran script every completing op parked a pool
/// thread on that lock, and while the page thread itself waited on the network inside script (a
/// module graph's static imports) the fetch's own continuations found no pool thread left: one
/// GitHub module took 12 s instead of 0.3 s. The port papered over it with a process-wide
/// <c>ThreadPool.SetMinThreads(64)</c>.
/// </para>
/// <para>
/// The runtime creates its engines with <c>V8ScriptEngineFlags.UseSynchronizationContexts</c>
/// and makes this context current while an async op is called (<see cref="AsyncOpBinding"/>),
/// which is when ClearScript captures <c>TaskScheduler.FromCurrentSynchronizationContext()</c>
/// for the promise. The resolution is then posted here, and the event loop runs it as a task of
/// its own (<c>PocketCalculatorJsRuntime.PumpTick</c>), ahead of posted tasks and timers as
/// deno_core resolves ops ahead of its macrotasks. Host code an op needs to run against page
/// state after its <c>await</c> comes here too (<see cref="InvokeAsync{T}"/>).
/// </para>
/// <para>
/// The page's "thread" is the logical one that drives its loop: the loop awaits with
/// <c>ConfigureAwait(false)</c> and so moves between pool threads, but only one of them runs it
/// at a time, and nothing posted here runs anywhere else. A post never runs inline and never
/// waits; one made after <see cref="Dispose"/> is dropped (an item from
/// <see cref="InvokeAsync{T}"/> is cancelled instead, so its awaiter is released).
/// </para>
/// </remarks>
public sealed class OpCompletionContext : SynchronizationContext, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Queue<Item> _items = new();
    private TaskCompletionSource? _signal;
    private bool _disposed;

    [ThreadStatic]
    private static Stack<SynchronizationContext?>? t_saved;

    /// <summary>Items posted and not yet run.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <inheritdoc/>
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        Enqueue(new Item(d, state, null));
    }

    /// <summary>
    /// Not supported: running <paramref name="d"/> inline would run it off the page's loop, and
    /// waiting for the loop from a thread the loop may be waiting on deadlocks.
    /// </summary>
    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("The page's op completion context only accepts posts.");

    /// <inheritdoc/>
    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Run <paramref name="body"/> on the page's loop and complete the returned task there, so
    /// whatever continues from it synchronously (the op's own promise resolution) runs there too.
    /// Cancelled when the context is disposed before the body runs.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        // Continuations run synchronously on purpose: the op awaiting this resumes inside the
        // item, completes, and ClearScript's continuation (scheduled on this context, which is
        // current while the item runs) resolves the promise in the same step.
        var completion = new TaskCompletionSource<T>();
        Enqueue(new Item(
            static boxed =>
            {
                var (work, done) = ((Func<T>, TaskCompletionSource<T>))boxed!;
                T result;
                try
                {
                    result = work();
                }
                catch (Exception error)
                {
                    done.TrySetException(error);
                    return;
                }

                done.TrySetResult(result);
            },
            (body, completion),
            () => completion.TrySetCanceled()));
        return completion.Task;
    }

    /// <summary>
    /// Run the oldest posted item on the calling thread, with this context current. False when
    /// there was none. An exception the item throws is rethrown here.
    /// </summary>
    public bool RunOne()
    {
        Item item;
        lock (_gate)
        {
            if (_disposed || !_items.TryDequeue(out item))
            {
                return false;
            }
        }

        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            item.Callback(item.State);
        }
        finally
        {
            SetSynchronizationContext(previous);
        }

        return true;
    }

    /// <summary>
    /// A task that completes once something is posted (at once when something already is, or
    /// when the context is disposed). Never faults.
    /// </summary>
    public Task WhenPosted()
    {
        lock (_gate)
        {
            if (_disposed || _items.Count > 0)
            {
                return Task.CompletedTask;
            }

            _signal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _signal.Task;
        }
    }

    /// <summary>
    /// Make this context current on the calling thread until the matching <see cref="Leave"/>.
    /// Nests; the two must pair on one thread with no <c>await</c> between them.
    /// </summary>
    public void Enter()
    {
        (t_saved ??= new Stack<SynchronizationContext?>()).Push(Current);
        SetSynchronizationContext(this);
    }

    /// <summary>Restore the context <see cref="Enter"/> replaced.</summary>
    public static void Leave()
    {
        if (t_saved is { Count: > 0 } saved)
        {
            SetSynchronizationContext(saved.Pop());
        }
    }

    /// <summary>
    /// Run <paramref name="body"/> with no synchronization context, so a blocking wait inside it
    /// cannot wait on a continuation that was posted here and needs the waiting thread to run.
    /// </summary>
    public static T WithoutContext<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var previous = Current;
        if (previous is null)
        {
            return body();
        }

        SetSynchronizationContext(null);
        try
        {
            return body();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    /// <summary>Drop everything queued; later posts are dropped too.</summary>
    public void Dispose()
    {
        Item[] dropped;
        TaskCompletionSource? signal;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            dropped = [.. _items];
            _items.Clear();
            signal = _signal;
            _signal = null;
        }

        signal?.TrySetResult();
        List<Exception>? failures = null;
        foreach (var item in dropped)
        {
            try
            {
                item.Dropped?.Invoke();
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }

        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }
        else if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }

    private void Enqueue(Item item)
    {
        TaskCompletionSource? signal;
        lock (_gate)
        {
            if (!_disposed)
            {
                _items.Enqueue(item);
                signal = _signal;
                _signal = null;
            }
            else
            {
                signal = null;
                item = item with { Callback = null! };
            }
        }

        if (item.Callback is null)
        {
            item.Dropped?.Invoke();
            return;
        }

        signal?.TrySetResult();
    }

    private readonly record struct Item(SendOrPostCallback Callback, object? State, Action? Dropped);
}
