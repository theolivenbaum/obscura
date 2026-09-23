namespace PocketCalculator.Js.Runtime;

/// <summary>
/// The last resort behind the V8 watchdogs: a callback for when an interrupted command
/// still has not returned after a grace period.
/// </summary>
/// <remarks>
/// <para>
/// <c>V8ScriptEngine.Interrupt()</c> only unwinds JavaScript. A thread stuck in C# code
/// beneath an op (a pathological layout, a selector match, anything the budgets in the
/// render and DOM layers do not yet cover) never returns to V8, so the watchdog fires and
/// nothing happens. The one-shot CLI has <c>HardDeadline</c> for that; a long-running
/// <c>serve</c> or <c>mcp</c> process had nothing.
/// </para>
/// <para>
/// When <see cref="Handler"/> is set, every watchdog that fires registers here, and one
/// that is not disarmed within <see cref="Grace"/> calls it once with a description. The
/// CLI sets it only when <c>POCKETCALCULATOR_HANG_EXIT_MS</c> is configured, and then
/// exits the process so a supervisor can restart it: that ends every session the process
/// holds, which is why it is opt-in. With no handler nothing is tracked, so library and
/// CDP embedders see no change. Deviation from Rust, which has no equivalent (its CLI
/// hard deadline covers <c>fetch</c> only).
/// </para>
/// </remarks>
public static class HangEscalation
{
    private static readonly object Gate = new();
    private static readonly Dictionary<object, (long Deadline, string What)> Pending =
        new(ReferenceEqualityComparer.Instance);

    private static Thread? _worker;
    private static Action<string>? _handler;

    /// <summary>Called once for an interrupted command that stayed stuck; null disables tracking.</summary>
    public static Action<string>? Handler
    {
        get => Volatile.Read(ref _handler);
        set => Volatile.Write(ref _handler, value);
    }

    /// <summary>How long an interrupted command may take to return before <see cref="Handler"/> runs.</summary>
    public static TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A watchdog identified by <paramref name="key"/> fired; start its grace period.</summary>
    internal static void Fired(object key, string what)
    {
        if (Handler is null)
        {
            return;
        }

        long grace = (long)Math.Clamp(Grace.TotalMilliseconds, 0, int.MaxValue);
        lock (Gate)
        {
            Pending[key] = (Environment.TickCount64 + grace, what);
            if (_worker is null)
            {
                _worker = new Thread(Loop) { IsBackground = true, Name = "obscura-hang-escalation" };
                _worker.Start();
            }

            Monitor.Pulse(Gate);
        }
    }

    /// <summary>The watchdog identified by <paramref name="key"/> was disarmed: the command returned.</summary>
    internal static void Settled(object key)
    {
        lock (Gate)
        {
            if (Pending.Remove(key))
            {
                Monitor.Pulse(Gate);
            }
        }
    }

    /// <summary>Whether the watchdog <paramref name="key"/> fired and is still in its grace period.</summary>
    internal static bool IsPending(object key)
    {
        lock (Gate)
        {
            return Pending.ContainsKey(key);
        }
    }

    private static void Loop()
    {
        while (true)
        {
            string? overdue = null;
            lock (Gate)
            {
                long now = Environment.TickCount64;
                long? next = null;
                object? expired = null;
                foreach ((object key, (long deadline, string what)) in Pending)
                {
                    if (deadline <= now)
                    {
                        expired = key;
                        overdue = what;
                        break;
                    }

                    long remaining = deadline - now;
                    next = next is null ? remaining : Math.Min(next.Value, remaining);
                }

                if (expired is not null)
                {
                    Pending.Remove(expired);
                }
                else if (next is null)
                {
                    Monitor.Wait(Gate);
                }
                else
                {
                    Monitor.Wait(Gate, (int)Math.Clamp(next.Value, 1, int.MaxValue));
                }
            }

            if (overdue is not null)
            {
                try
                {
                    Handler?.Invoke(overdue);
                }
                catch
                {
                    // The handler is the backstop; if it fails there is nothing left to try,
                    // and this thread must keep serving the other entries.
                }
            }
        }
    }
}
