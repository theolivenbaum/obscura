using System.Globalization;
using PocketCalculator.Js.Modules;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// A new runtime was refused because the process is over its memory budget.
/// </summary>
public sealed class ProcessMemoryExceededException(long used, long limit)
    : InvalidOperationException($"process memory budget exceeded: {used} bytes in use, limit {limit}")
{
    public long Used { get; } = used;

    public long Limit { get; } = limit;
}

/// <summary>
/// The opt-in per-process memory backstop (SECURITY.md M7).
/// </summary>
/// <remarks>
/// <para>
/// The heap cap, the <c>ArrayBuffer</c> cap and the DOM budget each bound one page, but a
/// process running many pages can still grow until the OS kills it, taking every tenant with
/// it. With <c>POCKETCALCULATOR_MAX_PROCESS_BYTES</c> set, a timer samples the process working
/// set every <see cref="DefaultInterval"/>. Over the limit it collects once, and if the process
/// is still over, terminates the running work of every live isolate the way a watchdog does
/// (script stops with "execution terminated", C# work inside an op stops at its next check).
/// While over, a new runtime (a new page or navigation) is refused with
/// <see cref="ProcessMemoryExceededException"/>. Off by default. Rust has no equivalent;
/// Chromium's renderer is killed by the OS or its own OOM handler instead.
/// </para>
/// <para>
/// The working set cannot be attributed to an isolate, so every running page is stopped, not
/// just the one that grew: this is a last resort to keep the process, not a fair scheduler.
/// </para>
/// </remarks>
public sealed class ProcessMemoryGuard
{
    /// <summary>How often the default guard samples the process.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    private readonly Func<long> _probe;
    private readonly TimeSpan _interval;
    private readonly Lock _gate = new();
    private readonly List<WeakReference<IIsolateHandle>> _isolates = [];
    private Timer? _timer;
    private long _trips;
    private int _ticking;

    /// <summary>The guard every runtime registers with.</summary>
    public static ProcessMemoryGuard Default { get; } = new(ReadLimit(), () => Environment.WorkingSet, DefaultInterval);

    internal ProcessMemoryGuard(long limitBytes, Func<long> probe, TimeSpan interval)
    {
        LimitBytes = Math.Max(0, limitBytes);
        _probe = probe;
        _interval = interval;
    }

    /// <summary>The limit in bytes; zero when the guard is off.</summary>
    public long LimitBytes { get; }

    /// <summary>How many times the guard has terminated running work.</summary>
    public long Trips => Interlocked.Read(ref _trips);

    /// <summary>Raised on the timer thread when the guard terminates running work.</summary>
    public static event Action<long, long>? Exceeded;

    private static long ReadLimit()
    {
        string? raw = Environment.GetEnvironmentVariable("POCKETCALCULATOR_MAX_PROCESS_BYTES");
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes) && bytes > 0
            ? bytes
            : 0;
    }

    /// <summary>Refuses new page work while the process is over the limit.</summary>
    public void ThrowIfOverLimit()
    {
        if (LimitBytes == 0)
        {
            return;
        }

        long used = _probe();
        if (used > LimitBytes)
        {
            throw new ProcessMemoryExceededException(used, LimitBytes);
        }
    }

    /// <summary>Watches <paramref name="isolate"/> until the returned handle is disposed.</summary>
    public IDisposable Register(IIsolateHandle isolate)
    {
        ArgumentNullException.ThrowIfNull(isolate);
        if (LimitBytes == 0)
        {
            return Registration.None;
        }

        var entry = new WeakReference<IIsolateHandle>(isolate);
        lock (_gate)
        {
            _isolates.Add(entry);
            _timer ??= new Timer(static state => ((ProcessMemoryGuard)state!).Tick(), this, _interval, _interval);
        }

        return new Registration(this, entry);
    }

    private void Unregister(WeakReference<IIsolateHandle> entry)
    {
        lock (_gate)
        {
            _isolates.Remove(entry);
            if (_isolates.Count == 0)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) != 0)
        {
            return;
        }

        try
        {
            TickOnce();
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private void TickOnce()
    {
        long used = _probe();
        if (used <= LimitBytes)
        {
            return;
        }

        // Garbage is not a reason to stop a page: collect first and look again.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        used = _probe();
        if (used <= LimitBytes)
        {
            return;
        }

        List<IIsolateHandle> live = [];
        lock (_gate)
        {
            _isolates.RemoveAll(entry => !entry.TryGetTarget(out _));
            foreach (var entry in _isolates)
            {
                if (entry.TryGetTarget(out var isolate))
                {
                    live.Add(isolate);
                }
            }
        }

        foreach (var isolate in live)
        {
            isolate.TerminateExecution();
        }

        Interlocked.Increment(ref _trips);
        Exceeded?.Invoke(used, LimitBytes);
    }

    private sealed class Registration(ProcessMemoryGuard? owner, WeakReference<IIsolateHandle>? entry) : IDisposable
    {
        internal static readonly Registration None = new(null, null);

        private int _disposed;

        public void Dispose()
        {
            if (owner is not null && entry is not null && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unregister(entry);
            }
        }
    }
}
