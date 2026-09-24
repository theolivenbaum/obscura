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
/// <para>
/// The key is the CDP context id, so an object id minted in the world
/// (<c>{"injectedScriptId":key,...}</c>) names the world it lives in. Key 1 is never a
/// world: the page realm's object ids carry 1, and the first context a CDP connection
/// allocates is always a default one.
/// </para>
/// <para>
/// <c>FrameId</c> names the document: 0 for the page's own, or a child frame's realm id,
/// whose worlds run over that frame's document. With <see cref="IsFrameMainWorld"/> set
/// the target is not a world at all but the child frame's own realm (its default CDP
/// context), which is addressed the same way so its object ids route by key too.
/// </para>
/// </remarks>
public sealed record IsolatedWorldTarget(long Key, string Name, IReadOnlyList<string> Preloads, uint FrameId = 0)
{
    /// <summary>The target is child frame <see cref="FrameId"/>'s own realm, not a world.</summary>
    public bool IsFrameMainWorld { get; init; }

    /// <summary>The default CDP context of child frame <paramref name="frameId"/>, as context <paramref name="key"/>.</summary>
    public static IsolatedWorldTarget ForFrameMainWorld(long key, uint frameId) =>
        new(key, string.Empty, [], frameId) { IsFrameMainWorld = true };
}

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
/// <para>
/// A child frame's worlds are built the same way over the frame's document: their ops
/// resolve against the frame's state, their node-state calls reach the frame's own realm,
/// and they are disposed with the frame (<see cref="FrameRealm.Dispose"/>).
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
    private readonly FrameRealm? _frame;
    private DenoCoreShim? _shim;
    private bool _disposed;

    private IsolatedWorld(PocketCalculatorJsRuntime parent, V8ScriptEngine engine, IsolatedWorldTarget target, FrameRealm? frame)
    {
        _parent = parent;
        _engine = engine;
        _frame = frame;
        Key = target.Key;
        Name = target.Name;
        FrameId = frame?.FrameId ?? 0;
        Document = frame?.State ?? parent.State;
        Preloads = [.. target.Preloads];
        Scope = new CdpScope(
            parent, engine, target.Key, new(StringComparer.Ordinal), new(StringComparer.Ordinal), this,
            realmState: frame?.State);
    }

    /// <summary>The document the world is over: 0 for the page's, or a child frame's realm id.</summary>
    public uint FrameId { get; }

    /// <summary>The state of the document the world is over.</summary>
    internal PocketCalculator.Js.Ops.PocketCalculatorState Document { get; }

    /// <summary>
    /// The host helpers of the realm that owns the document: the page realm for a
    /// main-frame world, the frame's realm for a child frame's. Node state kept in
    /// JavaScript (form values, focus, listeners) is read and written there.
    /// </summary>
    internal ScriptObject? DocumentRealmHelpers => _frame is { } frame ? frame.HostHelpers : _parent.MainHostHelpers;

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

    /// <summary>
    /// Builds a world over the page's document, or over <paramref name="frame"/>'s when
    /// one is given, or throws <see cref="JsRuntimeException"/>.
    /// </summary>
    internal static IsolatedWorld Create(PocketCalculatorJsRuntime parent, IsolatedWorldTarget target, FrameRealm? frame = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Key <= CdpScope.MainInjectedScriptId)
        {
            throw new JsRuntimeException($"invalid isolated world id {target.Key.ToString(CultureInfo.InvariantCulture)}");
        }

        var engine = parent.CreateRealmEngine();
        var world = new IsolatedWorld(parent, engine, target, frame);
        try
        {
            world._shim = BootstrapLoader.Install(
                engine, ops => parent.BindWorldOps(ops, world), frameId: world.FrameId, isolatedWorld: true);
            world.CopyGlobalsFromPage();
            // A child frame's world is a realm of that frame: the same frame ids, so the
            // ops that take one resolve the frame's document, and the frame's viewport.
            var frameIds = frame is null
                ? string.Empty
                : $"__obscura_host.vars.__obscura_frameId = {frame.FrameId.ToString(CultureInfo.InvariantCulture)};"
                    + $"__obscura_host.vars.__obscura_parentFrameId = {frame.ParentFrameId.ToString(CultureInfo.InvariantCulture)};"
                    + world.FrameViewportGlobals();
            world.Scope.RunHost(
                "<obscura:world-init>",
                frameIds + "__obscura_host.vars.__obscura_isolated_world = true; __obscura_host.init();");
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
                world.ExecutePreloadScript(source);
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

    /// <summary>
    /// Runs one new-document entry in the world: installs a <see cref="BindingPreload"/>
    /// binding, or runs a script.
    /// </summary>
    public void ExecutePreloadScript(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (BindingPreload.NameOf(source) is { } binding)
        {
            Scope.RunHost("<binding>", BindingPreload.InstallStatement(binding));
            return;
        }
        Scope.Run("<preload>", source);
    }

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

    /// <summary>The frame realm's viewport, as the globals <c>__obscura_init</c> reads it from.</summary>
    private string FrameViewportGlobals()
    {
        if (_frame is null)
        {
            return string.Empty;
        }
        var builder = new System.Text.StringBuilder();
        foreach (var (from, to) in new[] { ("innerWidth", "__obscura_viewport_w"), ("innerHeight", "__obscura_viewport_h") })
        {
            if (_frame.Engine.Evaluate($"globalThis.{from}") is double number && double.IsFinite(number) && number > 0)
            {
                builder.Append(CultureInfo.InvariantCulture, $"__obscura_host.vars.{to} = {number.ToString("R", CultureInfo.InvariantCulture)};");
            }
            else if (_frame.Engine.Evaluate($"globalThis.{from}") is int whole && whole > 0)
            {
                builder.Append(CultureInfo.InvariantCulture, $"__obscura_host.vars.{to} = {whole.ToString(CultureInfo.InvariantCulture)};");
            }
        }
        return builder.ToString();
    }

    private void CopyGlobalsFromPage() =>
        HostVariables.Copy(_parent.MainHostHelpers, _shim?.HostHelpers, CopiedGlobals);
}

/// <summary>
/// One realm's CDP object store: the engine it evaluates in, the ids it minted, and the
/// expressions that can rebuild them.
/// </summary>
/// <remarks>
/// <c>helpers</c> names the realm's host helpers when it is neither the page realm nor an
/// isolated world (a child frame's own realm), and <c>realmState</c> the state host calls
/// into the realm run as, so an op that asks which realm is running answers with that
/// frame's.
/// </remarks>
internal sealed class CdpScope(
    PocketCalculatorJsRuntime runtime,
    V8ScriptEngine engine,
    long injectedScriptId,
    Dictionary<string, string> store,
    Dictionary<string, string> recipes,
    IsolatedWorld? world = null,
    Func<ScriptObject?>? helpers = null,
    PocketCalculator.Js.Ops.PocketCalculatorState? realmState = null)
{
    /// <summary>The <c>injectedScriptId</c> the page realm's object ids carry.</summary>
    internal const long MainInjectedScriptId = 1;

    public V8ScriptEngine Engine { get; } = engine;

    public Dictionary<string, string> Store { get; } = store;

    public Dictionary<string, string> Recipes { get; } = recipes;

    public ulong Counter { get; set; }

    /// <summary>0 for the page realm, the context key for an isolated world or a child frame's own realm.</summary>
    public long WorldKey => injectedScriptId == MainInjectedScriptId ? 0 : injectedScriptId;

    public string MakeOid(ulong counter) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"injectedScriptId\":{injectedScriptId},\"id\":{counter}}}");

    public object? Run(string name, string source)
    {
        world?.SyncBeforeRun();
        return AsRealm(() => runtime.ExecuteIn(Engine, name, source));
    }

    /// <summary>
    /// <see cref="Run"/> for host-authored statements that use this realm's host helpers as
    /// <c>__obscura_host</c> (<see cref="HostScript"/>). Never client- or page-supplied code.
    /// </summary>
    public object? RunHost(string name, string source)
    {
        world?.SyncBeforeRun();
        var realmHelpers = world is not null ? world.HostHelpers
            : helpers is not null ? helpers()
            : runtime.MainHostHelpers;
        return AsRealm(() => runtime.InvokeIn(Engine, name, HostScript.WrapStatements(source), realmHelpers));
    }

    /// <summary>Runs <paramref name="call"/> with the realm's state as the running one, when it has its own.</summary>
    private object? AsRealm(Func<object?> call)
    {
        if (realmState is null)
        {
            return call();
        }
        var previous = runtime.RealmStates.Current;
        runtime.RealmStates.Current = realmState;
        try
        {
            return call();
        }
        finally
        {
            runtime.RealmStates.Current = previous;
        }
    }

    /// <summary>The name the host's CDP wrappers give the realm's store (bootstrap.js <c>_cdpHost</c>).</summary>
    public const string Parameter = "__obscura_cdp";

    /// <summary>The expression a wrapper reads handle <paramref name="objectId"/> with.</summary>
    public static string Retrieval(string objectId) =>
        $"{Parameter}.objects[{PocketCalculatorJsRuntime.JsStringLiteral(objectId)}]";

    private ScriptObject? _helpers;
    private ScriptObject? _cdp;

    /// <summary>
    /// The realm's CDP store object, taken from its host helpers: closure state of
    /// bootstrap.js that page script has no reference to (SECURITY.md L10).
    /// </summary>
    public ScriptObject Cdp
    {
        get
        {
            var current = world is not null ? world.HostHelpers
                : helpers is not null ? helpers()
                : runtime.MainHostHelpers;
            if (_cdp is null || !ReferenceEquals(current, _helpers))
            {
                _helpers = current;
                _cdp = current?.GetProperty("cdp") as ScriptObject
                    ?? throw new JsRuntimeException("the realm has no CDP store");
            }
            return _cdp;
        }
    }

    /// <summary>Handle id to value.</summary>
    public ScriptObject Objects => (ScriptObject)Cdp.GetProperty("objects");

    /// <summary>Awaiting wrappers' outcomes, by counter.</summary>
    public ScriptObject Outcomes => (ScriptObject)Cdp.GetProperty("outcomes");

    /// <summary>
    /// Runs host-authored <paramref name="body"/> as a strict function of the realm's store,
    /// which it names <c>__obscura_cdp</c>, and returns what it returns. Client source goes
    /// in as a string for <c>__obscura_cdp.eval</c>, never pasted into the body.
    /// </summary>
    public object? Call(string name, string body)
    {
        world?.SyncBeforeRun();
        return AsRealm(() => runtime.InvokeIn(Engine, name, $"(function({Parameter}) {{ \"use strict\";\n{body}\n}})", Cdp));
    }

    /// <summary>Drops one handle.</summary>
    public void Delete(string objectId)
    {
        try
        {
            Objects.DeleteProperty(objectId);
        }
        catch (Exception error) when (error is ScriptEngineException or JsRuntimeException or InvalidOperationException)
        {
            // A release of a handle the realm can no longer reach is a no-op.
        }
    }

    /// <summary>Drops every handle.</summary>
    public void Clear()
    {
        try
        {
            Cdp.InvokeMethod("clear");
        }
        catch (Exception error) when (error is ScriptEngineException or JsRuntimeException or InvalidOperationException)
        {
            // As Delete.
        }
    }
}
