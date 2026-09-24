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

    private readonly Dictionary<long, IsolatedWorld> _worlds = [];

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
    /// <exception cref="JsRuntimeException">The world could not be created.</exception>
    public IsolatedWorld GetOrCreateIsolatedWorld(IsolatedWorldTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_worlds.TryGetValue(target.Key, out var existing))
        {
            return existing;
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Each world is an engine with its own copy of bootstrap.js's heap, so a client
        // naming world after world would grow the page without bound.
        if (_worlds.Count >= MaxIsolatedWorlds)
        {
            throw new JsRuntimeException(
                $"Too many isolated worlds: this document already has {MaxIsolatedWorlds}");
        }
        var world = IsolatedWorld.Create(this, target);
        _worlds[target.Key] = world;
        _ops.MutationForwarder ??= new WorldMutationForwarder(this);
        if (_suspendedWorlds.Remove(target.Key, out var saved))
        {
            RestoreWorld(world, saved);
        }
        return world;
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

    /// <summary>
    /// The isolated world an object id was minted in, or 0 for the page realm (its own
    /// ids, console ids and <c>node-N</c> ids).
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
    /// The object store an id belongs to: the page realm's for its own ids, the world's for
    /// a world's, and null for a world that is gone.
    /// </summary>
    private CdpScope? ScopeForObjectId(string objectId)
    {
        var key = IsolatedWorldKeyOf(objectId);
        return key == 0 ? _mainScope : FindIsolatedWorld(key)?.Scope;
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

    /// <summary>The op table of a world: the page realm's, with the world's overrides.</summary>
    internal void BindWorldOps(ScriptObject ops, IsolatedWorld world)
    {
        ArgumentNullException.ThrowIfNull(ops);
        _ops.BindTo(ops);
        // Cookies, navigation and fetch act for the page's own document, whatever realm
        // is current when the world's script runs.
        _ops.BindRealmOverrides(ops, _ops.Page);
        // Timers: the host pumps only the page realm's timer queue, so a world schedules
        // through op_sleep as a frame does (bootstrap.js _scheduleAfter).
        _ops.BindRealmAsyncOp(ops, "op_sleep", (Func<object?, Task>)(millis => RealmSleepAsync(millis)));
        _ops.BindIsolatedWorldOverrides(
            ops,
            world,
            (kind, nid, arg) => WorldCall(world, kind, nid, arg),
            (name, payload) => QueueWorldBindingCall(world.Key, name, payload));
    }

    /// <summary>
    /// <c>op_world_call</c>: a world reading or writing the page realm's node state.
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
        }
        if (_shim.HostHelpers is not { } helpers)
        {
            return string.Empty;
        }
        return helpers.InvokeMethod("worldCall", kind, nid, arg) as string ?? string.Empty;
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

    /// <summary>
    /// Tells every realm over the page's document except the one that made a mutation
    /// about it: the page realm when a world made it, and every other world.
    /// </summary>
    /// <remarks>
    /// bootstrap.js keys parentNode, isConnected and layout snapshots on epochs its own
    /// _dom bumps, so every such realm has to bump them too. The page realm is told at
    /// once, record included, because nothing tells the host whether page script
    /// observes; a world's mutations are few. A world is only marked stale, and handed
    /// its records in one batch at its next microtask checkpoint, because the page may
    /// mutate thousands of times in one task and a call into another engine per mutation
    /// costs more than the mutation.
    /// </remarks>
    internal void DeliverMutation(IsolatedWorld? source, bool tree, in WorldMutationRecord record)
    {
        if (source is not null && _shim.HostHelpers is { } page)
        {
            page.InvokeMethod(
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
            if (!ReferenceEquals(world, source))
            {
                world.NoteExternalMutation(tree, record);
            }
        }
    }

    /// <summary>Whether a mutation made by <paramref name="source"/> has another realm to reach.</summary>
    internal bool MutationHasListeners(IsolatedWorld? source) => source is not null || _worlds.Count != 0;

    private void ReleaseIsolatedWorldObjects()
    {
        foreach (var world in _worlds.Values)
        {
            TryRunIn(world.Scope, "<releaseGroup>", "globalThis.__obscura_objects = {};");
            world.Scope.Store.Clear();
            world.Scope.Recipes.Clear();
        }
        _suspendedWorlds.Clear();
    }

    private Dictionary<long, IsolatedWorldState> TakeIsolatedWorldStates()
    {
        var states = new Dictionary<long, IsolatedWorldState>(_suspendedWorlds);
        foreach (var world in _worlds.Values)
        {
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
/// Runs page-document <c>op_dom</c> commands and forwards what the mutating ones changed
/// to the other realms over the document.
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
        if (!IsMutation(cmd) || page.Dom is not { } dom || !runtime.MutationHasListeners(world))
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
                runtime.DeliverMutation(world, pending.Type == "childList", pending);
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
