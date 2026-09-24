using System.Globalization;
using Microsoft.ClearScript;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// CDP isolated worlds (port addition, SECURITY.md M6). See <see cref="IsolatedWorld"/>.
/// </summary>
public sealed partial class PocketCalculatorJsRuntime
{
    /// <summary>The most isolated worlds one document may have at once.</summary>
    public const int MaxIsolatedWorlds = 32;

    /// <summary>Every world, of the page's document and of child frames', by CDP context id.</summary>
    private readonly Dictionary<long, IsolatedWorld> _worlds = [];

    /// <summary>How many worlds each document has, by frame id (0: the page's).</summary>
    private readonly Dictionary<uint, int> _worldsPerDocument = [];

    /// <summary>Child frames' own realms as CDP contexts, by context id.</summary>
    private readonly Dictionary<long, (uint FrameId, CdpScope Scope)> _frameScopes = [];

    /// <summary>Worlds a suspension took down, rebuilt when a command next names one.</summary>
    private readonly Dictionary<long, IsolatedWorldState> _suspendedWorlds = [];

    private readonly List<(long WorldKey, string Name, string Payload)> _worldBindingCalls = [];
    private long _worldBindingCallBytes;

    /// <summary>The worlds that exist now, in no particular order.</summary>
    public IReadOnlyCollection<IsolatedWorld> IsolatedWorlds => _worlds.Values;

    /// <summary>
    /// The world <paramref name="target"/> names, created (with its init scripts) if it
    /// does not exist yet.
    /// </summary>
    /// <exception cref="JsRuntimeException">The world could not be created, or its frame is gone.</exception>
    public IsolatedWorld GetOrCreateIsolatedWorld(IsolatedWorldTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.IsFrameMainWorld)
        {
            throw new JsRuntimeException("a frame's own realm is not an isolated world");
        }
        if (_worlds.TryGetValue(target.Key, out var existing))
        {
            return existing;
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
        FrameRealm? frame = null;
        if (target.FrameId != 0)
        {
            frame = FindFrameRealm(target.FrameId)
                ?? throw new JsRuntimeException("Cannot find context with specified id");
        }
        // Each world is an engine with its own copy of bootstrap.js's heap, so a client
        // naming world after world would grow the page without bound.
        if (_worldsPerDocument.GetValueOrDefault(target.FrameId) >= MaxIsolatedWorlds)
        {
            throw new JsRuntimeException(
                $"Too many isolated worlds: this document already has {MaxIsolatedWorlds}");
        }
        var world = IsolatedWorld.Create(this, target, frame);
        _worlds[target.Key] = world;
        _worldsPerDocument[world.FrameId] = _worldsPerDocument.GetValueOrDefault(world.FrameId) + 1;
        _ops.MutationForwarder ??= new WorldMutationForwarder(this);
        if (world.FrameId == 0 && _suspendedWorlds.Remove(target.Key, out var saved))
        {
            RestoreWorld(world, saved);
        }
        return world;
    }

    /// <summary>
    /// The object store <paramref name="target"/> names: an isolated world's, created if it
    /// does not exist yet, or a child frame's own realm's.
    /// </summary>
    /// <exception cref="JsRuntimeException">The world could not be created, or its frame is gone.</exception>
    internal CdpScope ScopeFor(IsolatedWorldTarget target)
    {
        if (!target.IsFrameMainWorld)
        {
            return GetOrCreateIsolatedWorld(target).Scope;
        }
        if (_frameScopes.TryGetValue(target.Key, out var known) && known.FrameId == target.FrameId)
        {
            return known.Scope;
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (target.Key <= CdpScope.MainInjectedScriptId || _worlds.ContainsKey(target.Key))
        {
            throw new JsRuntimeException("Cannot find context with specified id");
        }
        var frame = FindFrameRealm(target.FrameId)
            ?? throw new JsRuntimeException("Cannot find context with specified id");
        var scope = new CdpScope(
            this,
            frame.Engine,
            target.Key,
            new(StringComparer.Ordinal),
            new(StringComparer.Ordinal),
            helpers: () => frame.HostHelpers,
            realmState: frame.State);
        _frameScopes[target.Key] = (frame.FrameId, scope);
        return scope;
    }

    /// <summary>The world with CDP context id <paramref name="key"/>, if it exists or can be restored.</summary>
    public IsolatedWorld? FindIsolatedWorld(long key)
    {
        if (_worlds.TryGetValue(key, out var world))
        {
            return world;
        }
        return _suspendedWorlds.TryGetValue(key, out var saved)
            ? GetOrCreateIsolatedWorld(new IsolatedWorldTarget(key, saved.Name, saved.Preloads))
            : null;
    }

    private FrameRealm? FindFrameRealm(uint frameId)
    {
        foreach (var realm in _realms)
        {
            if (realm.FrameId == frameId)
            {
                return realm;
            }
        }
        return null;
    }

    /// <summary>
    /// The realm an object id was minted in, as the key of its CDP context, or 0 for the
    /// page realm (its own ids, console ids and <c>node-N</c> ids). Named for the isolated
    /// worlds it was written for; a child frame's own realm is keyed the same way.
    /// </summary>
    public static long IsolatedWorldKeyOf(string objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        const string prefix = "{\"injectedScriptId\":";
        if (!objectId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return 0;
        }
        var end = objectId.IndexOf(',', prefix.Length);
        if (end < 0
            || !long.TryParse(objectId.AsSpan(prefix.Length, end - prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var key)
            || key <= CdpScope.MainInjectedScriptId)
        {
            return 0;
        }
        return key;
    }

    /// <summary>
    /// The document an object id's realm is over: 0 for the page's (and for an id no realm
    /// answers for), or the child frame's realm id.
    /// </summary>
    public uint FrameIdOfObject(string objectId)
    {
        var key = IsolatedWorldKeyOf(objectId);
        if (key == 0)
        {
            return 0;
        }
        if (_worlds.TryGetValue(key, out var world))
        {
            return world.FrameId;
        }
        return _frameScopes.TryGetValue(key, out var frame) ? frame.FrameId : 0;
    }

    /// <summary>
    /// The object store an id belongs to: the page realm's for its own ids, the world's or
    /// the frame realm's for theirs, and null for a realm that is gone.
    /// </summary>
    private CdpScope? ScopeForObjectId(string objectId)
    {
        var key = IsolatedWorldKeyOf(objectId);
        if (key == 0)
        {
            return _mainScope;
        }
        if (_frameScopes.TryGetValue(key, out var frame))
        {
            return frame.Scope;
        }
        return FindIsolatedWorld(key)?.Scope;
    }

    /// <summary>Calls queued by a world's <c>Runtime.addBinding</c> shims, with the world they came from.</summary>
    public IReadOnlyList<(long WorldKey, string Name, string Payload)> TakePendingWorldBindingCalls()
    {
        if (_worldBindingCalls.Count == 0)
        {
            return [];
        }
        var calls = _worldBindingCalls.ToArray();
        _worldBindingCalls.Clear();
        _worldBindingCallBytes = 0;
        return calls;
    }

    /// <summary>The op table of a world: its document realm's, with the world's overrides.</summary>
    internal void BindWorldOps(ScriptObject ops, IsolatedWorld world)
    {
        ArgumentNullException.ThrowIfNull(ops);
        _ops.BindTo(ops);
        // Cookies, navigation and fetch act for the world's own document, whatever realm
        // is current when the world's script runs.
        _ops.BindRealmOverrides(ops, world.Document);
        // Timers: the host pumps only the page realm's timer queue, so a world schedules
        // through op_sleep as a frame does (bootstrap.js _scheduleAfter).
        _ops.BindRealmAsyncOp(ops, "op_sleep", (Func<object?, Task>)(millis => RealmSleepAsync(millis)));
        _ops.BindIsolatedWorldOverrides(
            ops,
            world,
            world.Document,
            (kind, nid, arg) => WorldCall(world, kind, nid, arg),
            (name, payload) => QueueWorldBindingCall(world.Key, name, payload));
    }

    /// <summary>
    /// <c>op_world_call</c>: a world reading or writing the node state its document's own
    /// realm keeps.
    /// </summary>
    private string WorldCall(IsolatedWorld world, string kind, double nid, string arg)
    {
        switch (kind)
        {
            case "observing":
                world.Observing = true;
                return string.Empty;
            case "sync":
                return world.TakeStaleEpochs();
            case "drain":
                return world.TakePendingRecords();
            case "tag":
                return world.Key.ToString(CultureInfo.InvariantCulture);
            case "listen":
                return WorldListen(world, nid, arg);
            case "unlisten":
                WorldUnlisten(world, nid, arg);
                return string.Empty;
        }
        if (world.DocumentRealmHelpers is not { } helpers)
        {
            return string.Empty;
        }
        return helpers.InvokeMethod("worldCall", kind, nid, arg) as string ?? string.Empty;
    }

    // ------------------------------------------------ world listeners (page-dispatched events)

    /// <summary>
    /// The most listeners the worlds of one document may register. A world's listener
    /// costs the document realm a host call on each dispatch that reaches it, so the index
    /// is bounded like the worlds themselves.
    /// </summary>
    internal const int MaxWorldListenersPerDocument = 4096;

    /// <summary>One world listener: its stamp on the document realm's counter, its world, and whether it captures (window only).</summary>
    private readonly record struct WorldListener(double Stamp, long WorldKey, bool Capture);

    /// <summary>Per document (frame id): (node id, type) to its world listeners in stamp order; node -1 is the window.</summary>
    private readonly Dictionary<uint, Dictionary<(double Nid, string Type), List<WorldListener>>> _worldListeners = [];

    /// <summary>
    /// <c>op_world_call('listen')</c>: a world registered a listener for <c>type</c>
    /// (<paramref name="arg"/> is <c>type\0capture</c>) on node <paramref name="nid"/>.
    /// Answers its stamp, taken from the document realm's counter so it orders among that
    /// realm's own listeners, or the empty string when refused.
    /// </summary>
    private string WorldListen(IsolatedWorld world, double nid, string arg)
    {
        var split = arg.IndexOf('\0', StringComparison.Ordinal);
        if (split <= 0 || world.DocumentRealmHelpers is not { } owner)
        {
            return string.Empty;
        }
        var type = arg[..split];
        var capture = nid < 0 && arg.AsSpan(split + 1).SequenceEqual("1");
        if (!_worldListeners.TryGetValue(world.FrameId, out var index))
        {
            index = [];
            _worldListeners[world.FrameId] = index;
        }
        var count = 0;
        foreach (var list in index.Values)
        {
            count += list.Count;
        }
        if (count >= MaxWorldListenersPerDocument)
        {
            return string.Empty;
        }
        if (owner.InvokeMethod("worldStamp") is not { } raw)
        {
            return string.Empty;
        }
        var stamp = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        var key = (nid < 0 ? -1 : nid, type);
        var before = TypeMask(index, type);
        if (!index.TryGetValue(key, out var entries))
        {
            entries = [];
            index[key] = entries;
        }
        entries.Add(new WorldListener(stamp, world.Key, capture));
        if (nid < 0 || TypeMask(index, type) != before)
        {
            PushWorldListenTypes(world.FrameId, owner);
        }
        return stamp.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary><c>op_world_call('unlisten')</c>: <paramref name="arg"/> is <c>type\0stamp</c>.</summary>
    private void WorldUnlisten(IsolatedWorld world, double nid, string arg)
    {
        var split = arg.IndexOf('\0', StringComparison.Ordinal);
        if (split <= 0
            || !double.TryParse(arg.AsSpan(split + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var stamp)
            || !_worldListeners.TryGetValue(world.FrameId, out var index))
        {
            return;
        }
        var type = arg[..split];
        var key = (nid < 0 ? -1 : nid, type);
        if (!index.TryGetValue(key, out var entries))
        {
            return;
        }
        var before = TypeMask(index, type);
        entries.RemoveAll(entry => entry.WorldKey == world.Key && entry.Stamp == stamp);
        if (entries.Count == 0)
        {
            index.Remove(key);
        }
        if ((nid < 0 || TypeMask(index, type) != before) && world.DocumentRealmHelpers is { } owner)
        {
            PushWorldListenTypes(world.FrameId, owner);
        }
    }

    /// <summary>Where the worlds of a document listen for <paramref name="type"/> (see <see cref="PushWorldListenTypes"/>).</summary>
    private static int TypeMask(Dictionary<(double Nid, string Type), List<WorldListener>> index, string type)
    {
        var mask = 0;
        foreach (var ((nid, listened), entries) in index)
        {
            if (!string.Equals(listened, type, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (var entry in entries)
            {
                mask |= nid >= 0 ? 4 : entry.Capture ? 1 : 2;
            }
        }
        return mask;
    }

    /// <summary>
    /// Tells the document realm which event types its worlds listen for now, and where:
    /// <c>type:mask</c> with 1 for capturing window listeners, 2 for other window
    /// listeners and 4 for node listeners, so a dispatch asks the host only at the steps
    /// some world listens at.
    /// </summary>
    /// <remarks>
    /// The window's listeners travel with the list (<c>[mask, capturing, other]</c>, each
    /// a <see cref="WorldListenersAt"/> string), since every dispatch of a type a world
    /// listens for on the window reaches them: the realm answers that step without a host
    /// call.
    /// </remarks>
    private void PushWorldListenTypes(uint frameId, ScriptObject owner)
    {
        var types = new SortedDictionary<string, object[]>(StringComparer.Ordinal);
        if (_worldListeners.TryGetValue(frameId, out var index))
        {
            foreach (var ((nid, type), entries) in index)
            {
                if (!types.TryGetValue(type, out var value))
                {
                    value = [0, string.Empty, string.Empty];
                    types[type] = value;
                }
                var mask = (int)value[0];
                foreach (var entry in entries)
                {
                    mask |= nid >= 0 ? 4 : entry.Capture ? 1 : 2;
                }
                value[0] = mask;
                if (nid < 0)
                {
                    value[1] = WorldListenersAt(frameId, "-1", type + "\01");
                    value[2] = WorldListenersAt(frameId, "-1", type + "\00");
                }
            }
        }
        try
        {
            owner.InvokeMethod(
                "worldListenTypes",
                types.Count == 0 ? string.Empty : System.Text.Json.JsonSerializer.Serialize(types));
        }
        catch (ScriptEngineException)
        {
            // A realm that cannot take the list keeps the one it had.
        }
    }

    /// <summary>Drops every listener a world registered, as it goes away.</summary>
    private void ForgetWorldListeners(IsolatedWorld world, bool tellOwner)
    {
        if (!_worldListeners.TryGetValue(world.FrameId, out var index))
        {
            return;
        }
        List<(double, string)>? empty = null;
        foreach (var (key, entries) in index)
        {
            if (entries.RemoveAll(entry => entry.WorldKey == world.Key) > 0 && entries.Count == 0)
            {
                (empty ??= []).Add(key);
            }
        }
        if (empty is null)
        {
            return;
        }
        foreach (var key in empty)
        {
            index.Remove(key);
        }
        if (index.Count == 0)
        {
            _worldListeners.Remove(world.FrameId);
        }
        if (tellOwner && world.DocumentRealmHelpers is { } owner)
        {
            PushWorldListenTypes(world.FrameId, owner);
        }
    }

    /// <summary>
    /// <c>op_dom('world_listeners')</c> from the realm that owns document
    /// <paramref name="frameId"/>: <c>stamp:worldKey</c> for each world listener for
    /// <paramref name="typeArg"/> on node <paramref name="nidArg"/>, comma-separated, in stamp
    /// order. For the window (-1) <paramref name="typeArg"/> is <c>type\0capture</c> and only
    /// listeners of that phase answer.
    /// </summary>
    internal string WorldListenersAt(uint frameId, string nidArg, string typeArg)
    {
        if (!_worldListeners.TryGetValue(frameId, out var index)
            || !double.TryParse(nidArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var nid))
        {
            return string.Empty;
        }
        var type = typeArg;
        bool? capture = null;
        var split = typeArg.IndexOf('\0', StringComparison.Ordinal);
        if (split >= 0)
        {
            type = typeArg[..split];
            capture = typeArg.AsSpan(split + 1).SequenceEqual("1");
        }
        if (!index.TryGetValue((nid < 0 ? -1 : nid, type), out var entries) || entries.Count == 0)
        {
            return string.Empty;
        }
        var builder = new System.Text.StringBuilder();
        foreach (var entry in entries)
        {
            if (capture is { } phase && entry.Capture != phase)
            {
                continue;
            }
            if (builder.Length > 0)
            {
                builder.Append(',');
            }
            builder.Append(entry.Stamp.ToString("R", CultureInfo.InvariantCulture))
                .Append(':')
                .Append(entry.WorldKey.ToString(CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>
    /// <c>op_dom('world_invoke')</c> from the realm that owns document
    /// <paramref name="frameId"/>: runs one listener of world <paramref name="worldKeyArg"/>
    /// for the dispatch <paramref name="spec"/> describes, and answers the propagation flags
    /// it left (<c>defaultPrevented</c>, stopped, stopped immediately; <c>000</c> when the
    /// world is gone).
    /// </summary>
    internal string WorldInvoke(uint frameId, string worldKeyArg, string spec)
    {
        if (!long.TryParse(worldKeyArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var key)
            || !_worlds.TryGetValue(key, out var world)
            || world.FrameId != frameId
            || world.HostHelpers is not { } helpers)
        {
            return "000";
        }
        world.SyncBeforeRun();
        var previous = RealmStates.Current;
        RealmStates.Current = world.Document;
        try
        {
            return helpers.InvokeMethod("worldInvoke", spec) as string ?? "000";
        }
        finally
        {
            RealmStates.Current = previous;
        }
    }

    /// <summary>The world's half of <c>op_binding_called</c>, under the page queue's limits.</summary>
    private void QueueWorldBindingCall(long worldKey, string name, string payload)
    {
        long size = (long)name.Length + payload.Length;
        if (_worldBindingCalls.Count >= CoreOps.BindingQueueEntryLimit()
            || _worldBindingCallBytes + size > CoreOps.BindingQueueByteLimit())
        {
            return;
        }
        _worldBindingCallBytes += size;
        _worldBindingCalls.Add((worldKey, name, payload));
    }

    /// <summary>The host helpers of the realm that owns document <paramref name="frameId"/>.</summary>
    private ScriptObject? DocumentRealmHelpers(uint frameId) =>
        frameId == 0 ? _shim.HostHelpers : FindFrameRealm(frameId)?.HostHelpers;

    /// <summary>
    /// Tells every realm over document <paramref name="frameId"/> except the one that
    /// made a mutation about it: the document's own realm when a world made it, and every
    /// other world of that document.
    /// </summary>
    /// <remarks>
    /// bootstrap.js keys parentNode, isConnected and layout snapshots on epochs its own
    /// _dom bumps, so every such realm has to bump them too. The document's realm is told
    /// at once, record included, because nothing tells the host whether its script
    /// observes; a world's mutations are few. A world is only marked stale, and handed
    /// its records in one batch at its next microtask checkpoint, because the page may
    /// mutate thousands of times in one task and a call into another engine per mutation
    /// costs more than the mutation.
    /// </remarks>
    internal void DeliverMutation(IsolatedWorld? source, uint frameId, bool tree, in WorldMutationRecord record)
    {
        if (source is not null && DocumentRealmHelpers(frameId) is { } owner)
        {
            owner.InvokeMethod(
                "externalMutation",
                tree,
                record.Type,
                record.Target,
                record.Added,
                record.Removed,
                record.AttributeName ?? string.Empty,
                record.OldValue!);
        }
        foreach (var world in _worlds.Values)
        {
            if (world.FrameId == frameId && !ReferenceEquals(world, source))
            {
                world.NoteExternalMutation(tree, record);
            }
        }
    }

    /// <summary>Whether a mutation of document <paramref name="frameId"/> made by <paramref name="source"/> has another realm to reach.</summary>
    internal bool MutationHasListeners(IsolatedWorld? source, uint frameId) =>
        source is not null || _worldsPerDocument.GetValueOrDefault(frameId) != 0;

    /// <summary>
    /// Disposes what belongs to a child frame that is going away: its worlds and its own
    /// realm's CDP store. Called from <see cref="FrameRealm.Dispose"/>.
    /// </summary>
    private void ForgetFrameWorlds(uint frameId)
    {
        List<long>? gone = null;
        foreach (var (key, world) in _worlds)
        {
            if (world.FrameId == frameId)
            {
                (gone ??= []).Add(key);
            }
        }
        if (gone is not null)
        {
            foreach (var key in gone)
            {
                if (_worlds.Remove(key, out var world))
                {
                    ForgetWorldListeners(world, tellOwner: false);
                    world.Dispose();
                }
            }
            _worldsPerDocument.Remove(frameId);
        }
        List<long>? scopes = null;
        foreach (var (key, entry) in _frameScopes)
        {
            if (entry.FrameId == frameId)
            {
                (scopes ??= []).Add(key);
            }
        }
        foreach (var key in scopes ?? [])
        {
            _frameScopes.Remove(key);
        }
        if (_worlds.Count == 0)
        {
            _ops.MutationForwarder = null;
        }
    }

    private void ReleaseIsolatedWorldObjects()
    {
        foreach (var world in _worlds.Values)
        {
            world.Scope.Clear();
            world.Scope.Store.Clear();
            world.Scope.Recipes.Clear();
        }
        foreach (var (_, scope) in _frameScopes.Values)
        {
            scope.Clear();
            scope.Store.Clear();
            scope.Recipes.Clear();
        }
        _suspendedWorlds.Clear();
    }

    /// <remarks>
    /// Only the page document's worlds come back: a suspension tears down every frame,
    /// and a frame rebuilt later is a new browsing context with a new CDP context.
    /// </remarks>
    private Dictionary<long, IsolatedWorldState> TakeIsolatedWorldStates()
    {
        var states = new Dictionary<long, IsolatedWorldState>(_suspendedWorlds);
        foreach (var world in _worlds.Values)
        {
            if (world.FrameId != 0)
            {
                continue;
            }
            states[world.Key] = new IsolatedWorldState(
                world.Name,
                world.Preloads,
                world.Scope.Counter,
                new Dictionary<string, string>(world.Scope.Recipes, StringComparer.Ordinal));
            world.Scope.Recipes.Clear();
        }
        return states;
    }

    private void RestoreIsolatedWorldStates(IReadOnlyDictionary<long, IsolatedWorldState> states)
    {
        foreach (var (key, state) in states)
        {
            if (_worlds.TryGetValue(key, out var world))
            {
                RestoreWorld(world, state);
            }
            else
            {
                _suspendedWorlds[key] = state;
            }
        }
    }

    private static void RestoreWorld(IsolatedWorld world, IsolatedWorldState state)
    {
        world.Scope.Counter = Math.Max(world.Scope.Counter, state.Counter);
        foreach (var (id, expression) in state.Recipes)
        {
            world.Scope.Recipes[id] = expression;
        }
    }

    private void DisposeIsolatedWorlds()
    {
        _ops.MutationForwarder = null;
        foreach (var world in _worlds.Values)
        {
            world.Dispose();
        }
        _worlds.Clear();
        _worldsPerDocument.Clear();
        _worldListeners.Clear();
        _frameScopes.Clear();
        _suspendedWorlds.Clear();
    }
}

/// <summary>What a world needs to come back after its page's runtime was suspended.</summary>
internal sealed record IsolatedWorldState(
    string Name,
    IReadOnlyList<string> Preloads,
    ulong Counter,
    Dictionary<string, string> Recipes);

/// <summary>One mutation record, in node ids.</summary>
internal readonly record struct WorldMutationRecord(
    string Type,
    double Target,
    string Added,
    string Removed,
    string? AttributeName,
    string? OldValue);

/// <summary>
/// Runs <c>op_dom</c> commands of a document that has isolated worlds (the page's or a child
/// frame's), forwards what the mutating ones changed to the other realms over it, and
/// answers the document realm's questions about its worlds' event listeners.
/// </summary>
/// <remarks>
/// Built from the op, not from the shim's own <c>__notifyMutation</c> calls: those go
/// through a page-writable global, so a page could otherwise forge records into a world.
/// A record here only ever describes a mutation the host performed.
/// </remarks>
internal sealed class WorldMutationForwarder(PocketCalculatorJsRuntime runtime) : IDomMutationForwarder
{
    public string OpDom(object? source, PocketCalculatorState page, string cmd, string arg1, string arg2)
    {
        var world = source as IsolatedWorld;
        // The document realm's dispatch reaching the worlds' listeners (bootstrap.js
        // _worldRunListeners). Only that realm's table asks; a world asking is ignored.
        if (cmd is "world_listeners" or "world_invoke")
        {
            if (world is not null)
            {
                return cmd == "world_invoke" ? "000" : string.Empty;
            }
            try
            {
                return cmd == "world_listeners"
                    ? runtime.WorldListenersAt(page.FrameId, arg1, arg2)
                    : runtime.WorldInvoke(page.FrameId, arg1, arg2);
            }
            catch (Exception error) when (error is not ScriptInterruptedException
                && error is ScriptEngineException or InvalidOperationException)
            {
                return cmd == "world_invoke" ? "000" : string.Empty;
            }
        }
        if (!IsMutation(cmd) || page.Dom is not { } dom || !runtime.MutationHasListeners(world, page.FrameId))
        {
            return DomOps.OpDom(page, cmd, arg1, arg2);
        }

        var record = Before(dom, cmd, arg1, arg2, out var childrenOf);
        var result = DomOps.OpDom(page, cmd, arg1, arg2);
        if (record is { } pending)
        {
            if (childrenOf is { } target)
            {
                pending = pending with { Added = Csv(dom, dom.Children(target)) };
            }
            try
            {
                runtime.DeliverMutation(world, page.FrameId, pending.Type == "childList", pending);
            }
            catch (Exception error) when (error is not ScriptInterruptedException)
            {
                // A realm that cannot take the record does not undo the mutation.
            }
        }
        return result;
    }

    private static bool IsMutation(string cmd) => cmd switch
    {
        "append_child" or "insert_before" or "remove_child"
            or "set_attribute" or "remove_attribute"
            or "set_text_content" or "set_inner_html" or "set_inner_html_context"
            or "set_fragment_html_executable" or "document_write" => true,
        _ => false,
    };

    /// <summary>
    /// The record the command will produce, from the tree as it is before the command
    /// runs. <paramref name="childrenOf"/> names a node whose children, read afterwards,
    /// are the record's added nodes.
    /// </summary>
    private static WorldMutationRecord? Before(DomTree dom, string cmd, string arg1, string arg2, out NodeId? childrenOf)
    {
        childrenOf = null;
        switch (cmd)
        {
            case "append_child":
                return uint.TryParse(arg1, out var parent) && uint.TryParse(arg2, out var child)
                    ? new WorldMutationRecord("childList", parent, Id(child), string.Empty, null, null)
                    : null;
            case "insert_before":
            {
                if (!uint.TryParse(arg1, out var inserted) || !uint.TryParse(arg2, out var reference)
                    || dom.GetNode(NodeId.New(reference))?.Parent is not { } into)
                {
                    return null;
                }
                return new WorldMutationRecord("childList", into.Raw, Id(inserted), string.Empty, null, null);
            }
            case "remove_child":
            {
                if (!uint.TryParse(arg1, out var removed) || dom.GetNode(NodeId.New(removed))?.Parent is not { } from)
                {
                    return null;
                }
                return new WorldMutationRecord("childList", from.Raw, string.Empty, Id(removed), null, null);
            }
            case "set_attribute":
            {
                var split = arg2.IndexOf('\0', StringComparison.Ordinal);
                if (split < 0 || !uint.TryParse(arg1, out var element))
                {
                    return null;
                }
                var name = arg2[..split];
                var old = dom.GetNode(NodeId.New(element))?.GetAttribute(name);
                return new WorldMutationRecord("attributes", element, string.Empty, string.Empty, name, old);
            }
            case "remove_attribute":
            {
                if (!uint.TryParse(arg1, out var element))
                {
                    return null;
                }
                var old = dom.GetNode(NodeId.New(element))?.GetAttribute(arg2);
                return new WorldMutationRecord("attributes", element, string.Empty, string.Empty, arg2, old);
            }
            case "set_text_content":
            {
                if (!uint.TryParse(arg1, out var raw) || dom.GetNode(NodeId.New(raw)) is not { } node)
                {
                    return null;
                }
                if (node.Data is TextData or CommentData or ProcessingInstructionData)
                {
                    return new WorldMutationRecord("characterData", raw, string.Empty, string.Empty, null, null);
                }
                childrenOf = NodeId.New(raw);
                return new WorldMutationRecord("childList", raw, string.Empty, Csv(dom, dom.Children(NodeId.New(raw))), null, null);
            }
            case "set_inner_html" or "set_inner_html_context" or "set_fragment_html_executable":
            {
                if (!uint.TryParse(arg1, out var raw) || raw == 0)
                {
                    return null;
                }
                childrenOf = NodeId.New(raw);
                return new WorldMutationRecord("childList", raw, string.Empty, Csv(dom, dom.Children(NodeId.New(raw))), null, null);
            }
            case "document_write":
                return new WorldMutationRecord("childList", dom.Document.Raw, string.Empty, string.Empty, null, null);
            default:
                return null;
        }
    }

    private static string Id(uint raw) => raw.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The ids as a record carries them. Stamped as exposed: a record names nodes a
    /// command is about to detach, and the collector must not free them before the
    /// record reaches a realm (DomTree.Gc.cs); once queued, a record is a root.
    /// </summary>
    private static string Csv(DomTree dom, List<NodeId> ids)
    {
        foreach (var id in ids)
        {
            dom.NoteExposed(id);
        }

        if (ids.Count == 0)
        {
            return string.Empty;
        }
        var builder = new System.Text.StringBuilder(ids.Count * 4);
        for (var i = 0; i < ids.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append(ids[i].Raw.ToString(CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}
