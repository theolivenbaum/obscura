using Microsoft.ClearScript.V8;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// Applies user-supplied V8 flags, which V8 only honors before the first
/// isolate exists.
/// </summary>
/// <remarks>
/// <para>
/// The Rust engine hands the raw flag string to
/// <c>v8::V8::set_flags_from_string</c>. ClearScript does not expose that
/// entry point: it surfaces the same underlying settings as typed
/// <see cref="V8RuntimeConstraints"/> properties and a small
/// <see cref="V8GlobalFlags"/> enum. So the flag string is parsed here and
/// mapped onto those, and any flag with no typed equivalent is reported through
/// <see cref="Warned"/> and ignored rather than silently dropped.
/// </para>
/// <para>
/// The late-call behavior is preserved exactly, and it matters: once an isolate
/// exists, V8's flag setter does not quietly ignore the call as the platform
/// documentation implies. It calls V8_Fatal and aborts the whole process with
/// SIGTRAP. Refusing the late call keeps a misconfigured embedder alive.
/// </para>
/// </remarks>
public static class V8Flags
{
    private static readonly Lock Gate = new();
    private static bool _applied;
    private static bool _platformStarted;

    /// <summary>Diagnostics: a refused late call, or an unmapped flag.</summary>
    public static event Action<string>? Warned;

    /// <summary>
    /// Heap and global settings accumulated from <see cref="Set"/>, applied when
    /// the first runtime is constructed. Null until a flag maps onto one.
    /// </summary>
    internal static V8RuntimeConstraints? Constraints { get; private set; }

    internal static V8GlobalFlags GlobalFlags { get; private set; } = V8GlobalFlags.None;

    /// <summary>
    /// Records that a runtime is being constructed, so a later <see cref="Set"/>
    /// is refused. Called at the top of runtime construction, before the V8
    /// platform is initialized.
    /// </summary>
    internal static void MarkPlatformStarted()
    {
        lock (Gate)
        {
            _platformStarted = true;
        }
    }

    /// <summary>
    /// Applies raw V8 flags exactly once, before the first isolate is created.
    /// </summary>
    /// <param name="flags">
    /// A raw V8 flag string in the form V8, Chromium and Node accept, for
    /// example <c>"--max-old-space-size=4096 --max-semi-space-size=64"</c>. An
    /// empty or whitespace-only string is a no-op and does not consume the
    /// one-shot guard, so a later non-empty call still takes effect.
    /// </param>
    public static void Set(string flags)
    {
        var trimmed = flags.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        lock (Gate)
        {
            if (_platformStarted)
            {
                Warned?.Invoke(
                    $"set_v8_flags(\"{trimmed}\") ignored: a JS runtime already exists - " +
                    "V8 flags must be set before the first PocketCalculatorJsRuntime is constructed");
                return;
            }
            if (_applied)
            {
                return;
            }
            _applied = true;
            Apply(trimmed);
        }
    }

    private static void Apply(string flags)
    {
        var constraints = Constraints ?? new V8RuntimeConstraints();
        var touchedConstraints = false;
        var globals = GlobalFlags;

        foreach (var token in flags.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var body = token.TrimStart('-');
            var eq = body.IndexOf('=', StringComparison.Ordinal);
            var name = eq < 0 ? body : body[..eq];
            var value = eq < 0 ? null : body[(eq + 1)..];

            switch (name)
            {
                // Sizes are megabytes in V8's command line and in ClearScript.
                case "max-old-space-size" when TryMegabytes(value, out var oldSpace):
                    constraints.MaxOldSpaceSize = oldSpace;
                    touchedConstraints = true;
                    break;
                case "max-semi-space-size" when TryMegabytes(value, out var semiSpace):
                    // V8 sizes the young generation as several semi-spaces;
                    // ClearScript exposes the young-space total, so scale to
                    // keep the requested semi-space achievable rather than
                    // under-provisioning it.
                    constraints.MaxNewSpaceSize = semiSpace * 2;
                    touchedConstraints = true;
                    break;
                case "max-young-generation-size" when TryMegabytes(value, out var young):
                    constraints.MaxYoungSpaceSize = young;
                    touchedConstraints = true;
                    break;
                case "jitless" or "no-opt":
                    globals |= V8GlobalFlags.DisableJITCompilation;
                    break;
                case "single-threaded" or "no-concurrent-recompilation":
                    globals |= V8GlobalFlags.DisableBackgroundWork;
                    break;
                case "harmony-top-level-await":
                    globals |= V8GlobalFlags.EnableTopLevelAwait;
                    break;
                default:
                    Warned?.Invoke(
                        $"V8 flag \"{token}\" has no ClearScript equivalent and was ignored; " +
                        "ClearScript exposes V8 settings as typed constraints rather than a flag string");
                    break;
            }
        }

        if (touchedConstraints)
        {
            Constraints = constraints;
        }
        GlobalFlags = globals;
        V8Settings.GlobalFlags = globals;
    }

    private static bool TryMegabytes(string? value, out int megabytes) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out megabytes) && megabytes > 0;

    private static bool _wasmCapApplied;

    /// <summary>
    /// The per-memory ceiling V8 enforces, in bytes, once
    /// <see cref="ApplyWasmMemoryCap"/> has set it; null when it could not be set.
    /// </summary>
    internal static long? NativeWasmMemoryCapBytes { get; private set; }

    /// <summary>
    /// Sets <c>--wasm-max-mem-pages</c> from <paramref name="bytes"/>, once per process,
    /// if V8 is not initialized yet; zero or less leaves V8's own limit.
    /// </summary>
    /// <remarks>
    /// The flag is the only limit on memory a module declares for itself and on
    /// <c>memory.grow</c> run inside WebAssembly, where no script wrapper can see it. V8
    /// then fails the reservation with the <c>RangeError</c> Chromium gives when it runs
    /// out ("could not allocate memory", "Unable to grow instance memory"). Where the flag
    /// cannot be set (see <see cref="V8NativeFlags"/>) bootstrap.js's per-isolate budget
    /// still covers memory created and grown through the JavaScript API.
    /// </remarks>
    internal static void ApplyWasmMemoryCap(long bytes)
    {
        lock (Gate)
        {
            if (_wasmCapApplied)
            {
                return;
            }
            _wasmCapApplied = true;
            if (bytes <= 0)
            {
                return;
            }

            const long PageBytes = 64 * 1024;
            long pages = Math.Clamp(bytes / PageBytes, 1, 65536);
            string flag = "--wasm-max-mem-pages=" + pages.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (V8NativeFlags.TryApplyBeforeInitialization(flag))
            {
                NativeWasmMemoryCapBytes = pages * PageBytes;
            }
            else
            {
                Warned?.Invoke(
                    $"V8 flag \"{flag}\" could not be set on this platform; WebAssembly memory is " +
                    "capped only where script creates or grows it");
            }
        }
    }

    /// <summary>Test seam: forget any applied flags and platform state.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _applied = false;
            _platformStarted = false;
            Constraints = null;
            GlobalFlags = V8GlobalFlags.None;
        }
    }
}
