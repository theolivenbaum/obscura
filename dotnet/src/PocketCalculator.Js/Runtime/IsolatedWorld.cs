using System.Globalization;
using System.Text.Json;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// Names the isolated world a CDP command targets: the execution context id the client
/// was given, the world's name, and the scripts <c>Page.addScriptToEvaluateOnNewDocument</c>
/// registered for that name.
/// </summary>
/// <remarks>
/// The key is the CDP context id, so an object id minted in the world
/// (<c>{"injectedScriptId":key,...}</c>) names the world it lives in. Key 1 is never a
/// world: the page realm's object ids carry 1, and the first context a CDP connection
/// allocates is always a default one.
/// </remarks>
public sealed record IsolatedWorldTarget(long Key, string Name, IReadOnlyList<string> Preloads);

/// <summary>
/// One CDP isolated world: its own realm over the page's own document.
/// </summary>
/// <remarks>
/// <para>
/// Port addition (SECURITY.md M6). The Rust engine records a context for
/// <c>Page.createIsolatedWorld</c> and runs every command addressed to it in the page
/// realm, so Puppeteer's and Playwright's utility scripts run beside page script: a page
/// that replaces <c>document.querySelector</c>, <c>Array.prototype.map</c> or
/// <c>JSON.stringify</c> changes what the automation reads, and the utility script sees
/// page globals. In Chromium an isolated world is a separate JavaScript context over the
/// same DOM: its own global object and built-ins, distinct wrappers for the same nodes,
/// and nothing of it visible to the page.
/// </para>
/// <para>
/// Built the way <see cref="FrameRealm"/> is: a second <see cref="V8ScriptEngine"/> on the
/// page's <see cref="V8Runtime"/> that runs bootstrap.js with its own op table. Unlike a
/// frame, its ops resolve against the page's own state (frame id 0), so every DOM call
/// reaches the page's document. The state bootstrap.js keeps in JavaScript rather than
/// in the host (form values, focus, selection, event listeners) is per realm, so the
/// world reaches the page realm's through <c>op_world_call</c> and its dispatches and
/// mutations are forwarded (see the isolated-world section of bootstrap.js and
/// <see cref="WorldMutationForwarder"/>).
/// </para>
/// <para>
/// Created on the first command that names it and disposed with the page's runtime, which
/// a navigation replaces, so a world never outlives its document.
/// </para>
/// </remarks>
public sealed class IsolatedWorld : IDisposable
{
    /// <summary>Page-realm globals a world starts with, so it reports the same identity.</summary>
    private static readonly string[] CopiedGlobals =
    [
        "__obscura_ua",
        "__obscura_platform",
        "__obscura_ua_platform",
        "__obscura_ua_platform_version",
        "__obscura_stealth",
        "__obscura_geo_lat",
        "__obscura_geo_lon",
        "__obscura_viewport_w",
        "__obscura_viewport_h",
        "__obscura_screen_w",
        "__obscura_screen_h",
        "__obscura_screen_emulated",
    ];

    private readonly PocketCalculatorJsRuntime _parent;
    private readonly V8ScriptEngine _engine;
    private DenoCoreShim? _shim;
    private bool _disposed;

    private IsolatedWorld(PocketCalculatorJsRuntime parent, V8ScriptEngine engine, IsolatedWorldTarget target)
    {
        _parent = parent;
        _engine = engine;
        Key = target.Key;
        Name = target.Name;
        Preloads = [.. target.Preloads];
        Scope = new CdpScope(parent, engine, target.Key, new(StringComparer.Ordinal), new(StringComparer.Ordinal), this);
    }

    /// <summary>The CDP execution context id this world answers to.</summary>
    public long Key { get; }

    /// <summary>The world name the client gave <c>Page.createIsolatedWorld</c>.</summary>
    public string Name { get; }

    /// <summary>The per-document scripts the world ran when it was created.</summary>
    internal IReadOnlyList<string> Preloads { get; }

    /// <summary>The world's CDP object store.</summary>
    internal CdpScope Scope { get; }

    internal V8ScriptEngine Engine => _engine;

    /// <summary>The world's host helpers (<c>notifyMutation</c> and the rest).</summary>
    internal ScriptObject? HostHelpers => _shim?.HostHelpers;

    /// <summary>
    /// Whether script in this world has registered a <c>MutationObserver</c>, so mutations
    /// other realms make are worth delivering here.
    /// </summary>
    internal bool Observing { get; set; }

    /// <summary>
    /// Another realm changed the document since this world last caught up: 0 no, 1 the
    /// tree was left alone, 2 the tree changed. bootstrap.js keys caches on epochs its
    /// own _dom bumps, so the world bumps them before its next task
    /// (<c>_worldSyncEpochs</c>).
    /// </summary>
    internal int StaleEpochs { get; set; }

    /// <summary>Records waiting for this world's observers, delivered at its next microtask checkpoint.</summary>
    internal List<WorldMutationRecord> PendingRecords { get; } = [];

    /// <summary>Whether a drain of <see cref="PendingRecords"/> is queued in the world.</summary>
    internal bool DrainScheduled { get; set; }

    /// <summary>Notes a mutation another realm made.</summary>
    internal void NoteExternalMutation(bool tree, in WorldMutationRecord record)
    {
        StaleEpochs = Math.Max(StaleEpochs, tree ? 2 : 1);
        if (!Observing || HostHelpers is not { } helpers)
        {
            return;
        }
        PendingRecords.Add(record);
        if (!DrainScheduled)
        {
            DrainScheduled = true;
            helpers.InvokeMethod("worldScheduleDrain");
        }
    }

    /// <summary>Answers <c>_worldSyncEpochs</c>: what the world missed, and clears it.</summary>
    internal string TakeStaleEpochs()
    {
        var stale = StaleEpochs;
        StaleEpochs = 0;
        return stale switch { 2 => "t", 1 => "d", _ => string.Empty };
    }

    /// <summary>Answers <c>_worldDrainMutations</c>: the pending records as JSON.</summary>
    internal string TakePendingRecords()
    {
        DrainScheduled = false;
        if (PendingRecords.Count == 0)
        {
            return "[]";
        }
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var record in PendingRecords)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(record.Type);
                writer.WriteNumberValue(record.Target);
                writer.WriteStringValue(record.Added);
                writer.WriteStringValue(record.Removed);
                writer.WriteStringValue(record.AttributeName ?? string.Empty);
                if (record.OldValue is { } old)
                {
                    writer.WriteStringValue(old);
                }
                else
                {
                    writer.WriteNullValue();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        PendingRecords.Clear();
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Catches the world up before host-initiated script runs in it, so the script does
    /// not read a parent or connectedness cached before another realm's mutation.
    /// </summary>
    internal void SyncBeforeRun()
    {
        if (StaleEpochs != 0 && HostHelpers is { } helpers)
        {
            helpers.InvokeMethod("worldSync");
        }
    }

    /// <summary>Builds a world, or throws <see cref="JsRuntimeException"/>.</summary>
    internal static IsolatedWorld Create(PocketCalculatorJsRuntime parent, IsolatedWorldTarget target)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Key <= CdpScope.MainInjectedScriptId)
        {
            throw new JsRuntimeException($"invalid isolated world id {target.Key.ToString(CultureInfo.InvariantCulture)}");
        }

        var engine = parent.CreateRealmEngine();
        var world = new IsolatedWorld(parent, engine, target);
        try
        {
            world._shim = BootstrapLoader.Install(engine, ops => parent.BindWorldOps(ops, world), frameId: 0, isolatedWorld: true);
            world.CopyGlobalsFromPage();
            PocketCalculatorJsRuntime.InitializeObjectStore(engine);
            world.Scope.Run(
                "<obscura:world-init>",
                "globalThis.__obscura_isolated_world = true; globalThis.__obscura_init();");
        }
        catch (Exception error) when (error is ScriptEngineException or JsRuntimeException)
        {
            world.Dispose();
            throw new JsRuntimeException($"could not create isolated world: {error.Message}");
        }

        // Chromium runs the scripts registered for the world's name on every new
        // document; here the world exists from the first command that names it, and runs
        // them then. One throwing does not stop the others, as with separate scripts.
        foreach (var source in world.Preloads)
        {
            if (source.Length == 0)
            {
                continue;
            }
            try
            {
                world.Scope.Run("<preload>", source);
            }
            catch (JsRuntimeException)
            {
                // A failing init script is the client's script failing.
            }
        }
        return world;
    }

    /// <summary>Runs a classic script in the world, reporting a throw as a <see cref="JsRuntimeException"/>.</summary>
    public void ExecuteScript(string name, string source) => Scope.Run(name, source);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // Detach V8's promise-reject hook before the engine goes, for the reason
        // PocketCalculatorJsRuntime.Dispose gives.
        _shim?.Detach();
        _shim = null;
        _engine.Dispose();
    }

    private void CopyGlobalsFromPage()
    {
        foreach (var name in CopiedGlobals)
        {
            var value = _parent.Engine.Evaluate($"globalThis.{name}");
            if (value is null or Undefined or VoidResult)
            {
                continue;
            }
            var literal = value switch
            {
                bool flag => flag ? "true" : "false",
                string text => JsonSerializer.Serialize(text),
                double number when double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
                int or long or float => Convert.ToString(value, CultureInfo.InvariantCulture),
                _ => null,
            };
            if (literal is null)
            {
                continue;
            }
            _engine.Execute("<copy-identity>", $"globalThis.{name} = {literal};");
        }
    }
}

/// <summary>
/// One realm's CDP object store: the engine it evaluates in, the ids it minted, and the
/// expressions that can rebuild them.
/// </summary>
internal sealed class CdpScope(
    PocketCalculatorJsRuntime runtime,
    V8ScriptEngine engine,
    long injectedScriptId,
    Dictionary<string, string> store,
    Dictionary<string, string> recipes,
    IsolatedWorld? world = null)
{
    /// <summary>The <c>injectedScriptId</c> the page realm's object ids carry.</summary>
    internal const long MainInjectedScriptId = 1;

    public V8ScriptEngine Engine { get; } = engine;

    public Dictionary<string, string> Store { get; } = store;

    public Dictionary<string, string> Recipes { get; } = recipes;

    public ulong Counter { get; set; }

    /// <summary>0 for the page realm, the world's key for an isolated world.</summary>
    public long WorldKey => injectedScriptId == MainInjectedScriptId ? 0 : injectedScriptId;

    public string MakeOid(ulong counter) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"injectedScriptId\":{injectedScriptId},\"id\":{counter}}}");

    public object? Run(string name, string source)
    {
        world?.SyncBeforeRun();
        return runtime.ExecuteIn(Engine, name, source);
    }
}
