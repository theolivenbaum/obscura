using System.Globalization;
using System.Text;
using Microsoft.ClearScript;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// The JavaScript side of the DOM collector (DomTree.Gc.cs): which detached nodes the realms
/// over a document still hold, and the host tables that hold node ids for them.
/// </summary>
/// <remarks>
/// <para>
/// Port addition. Rust keeps every wrapper in a strong map and never frees a node, so a
/// long-running page leaks both.
/// </para>
/// <para>
/// A collection at a task boundary asks each realm for the nids it has wrappers for, tells
/// it which of them are in a candidate component, and lets it weaken those wrappers
/// (<c>_gcWeaken</c> in bootstrap.js). One exhaustive V8 collection then clears every
/// wrapper nothing references, and each realm reports the components some wrapper of which
/// survived (<c>_gcSurvivors</c>); those are kept, together with every component a host table
/// here names. The rest is freed. A collection inside an op cannot run V8's collector with
/// script on the stack, so it keeps every component any realm has a wrapper for.
/// </para>
/// </remarks>
internal sealed class RealmDomGc(
    PocketCalculatorJsRuntime runtime,
    PocketCalculatorState state,
    Func<IReadOnlyList<ScriptObject>?> realms) : IDomGcParticipant
{
    public PocketCalculatorState State => state;

    public void MarkRoots(DomCollection collection)
    {
        MarkHostRoots(collection);
        if (collection.CandidateCount == 0)
        {
            return;
        }

        var helpers = realms();
        if (helpers is null)
        {
            // A realm over this document cannot be asked yet (bootstrap has not handed
            // over its helpers), so nothing it may hold can be told apart from garbage.
            collection.KeepAll();
            return;
        }

        try
        {
            MarkRealmRoots(collection, helpers);
        }
        catch (ScriptInterruptedException)
        {
            throw;
        }
        catch (ScriptEngineException)
        {
            // A realm that could not answer may hold anything.
            collection.KeepAll();
        }
    }

    private void MarkRealmRoots(DomCollection collection, IReadOnlyList<ScriptObject> helpers)
    {
        if (collection.InOperation)
        {
            foreach (var realm in helpers)
            {
                foreach (var nid in ParseIds(realm.InvokeMethod("gcCachedNids") as string))
                {
                    collection.Keep(NodeId.New(nid));
                }
            }

            return;
        }

        var weakened = new List<ScriptObject>(helpers.Count);
        var kept = new HashSet<int>();
        try
        {
            foreach (var realm in helpers)
            {
                if (collection.CandidateCount == 0)
                {
                    break;
                }

                var nids = ParseIds(realm.InvokeMethod("gcCachedNids") as string);
                var components = new StringBuilder(nids.Count * 3);
                var any = false;
                for (var i = 0; i < nids.Count; i++)
                {
                    if (i > 0)
                    {
                        components.Append(',');
                    }

                    var component = collection.ComponentOf(NodeId.New(nids[i]));
                    if (component >= 0)
                    {
                        any = true;
                    }

                    components.Append(component.ToString(CultureInfo.InvariantCulture));
                }

                if (!any)
                {
                    // Drops the key list the realm kept for _gcWeaken.
                    realm.InvokeMethod("gcWeaken", string.Empty);
                    continue;
                }

                weakened.Add(realm);
                realm.InvokeMethod("gcWeaken", components.ToString());
            }

            if (weakened.Count > 0)
            {
                // One exhaustive pass over the isolate: every realm of a page lives in it.
                runtime.Isolate.CollectGarbage(true);
            }
        }
        finally
        {
            // Always, even if the collection above failed: _gcWeaken took the entries out
            // of the realm's wrapper cache and only this puts the survivors back.
            foreach (var realm in weakened)
            {
                try
                {
                    foreach (var component in ParseIds(realm.InvokeMethod("gcSurvivors") as string))
                    {
                        kept.Add((int)component);
                    }
                }
                catch (ScriptEngineException)
                {
                    // It may still hold any of them: keep everything this time.
                    collection.KeepAll();
                }
            }
        }

        foreach (var component in kept)
        {
            collection.KeepComponent(component);
        }

        foreach (var realm in weakened)
        {
            try
            {
                realm.InvokeMethod("gcForget");
            }
            catch (ScriptEngineException)
            {
                // Only the realm's form-state entries for freed nodes stay behind.
            }
        }
    }

    public void OnFreed(DomTree tree, IReadOnlyList<NodeId> freed)
    {
        foreach (var id in freed)
        {
            state.CanvasSurfaces.Remove(id);
            state.ElementScrollOffsets.Remove(id);
            state.AlreadyStartedScripts.Remove(id);
        }

        // A queued style mutation may name a node that is about to stop resolving; a full
        // rebuild does not need it.
        if (state.PendingStyleMutations.Count != 0)
        {
            state.PreparedRender = null;
            state.PendingStyleMutations.Clear();
            state.ResolvedScroll = null;
        }
    }

    /// <summary>The node ids host code keeps across tasks for this document.</summary>
    private void MarkHostRoots(DomCollection collection)
    {
        state.WriteStream?.MarkRoots(collection);
        if (state.FrameId != 0)
        {
            return;
        }

        foreach (var world in runtime.IsolatedWorlds)
        {
            foreach (var record in world.PendingRecords)
            {
                collection.Keep(NodeId.New((uint)record.Target));
                KeepCsv(collection, record.Added);
                KeepCsv(collection, record.Removed);
            }
        }
    }

    private static void KeepCsv(DomCollection collection, string csv)
    {
        foreach (var nid in ParseIds(csv))
        {
            collection.Keep(NodeId.New(nid));
        }
    }

    private static List<uint> ParseIds(string? csv)
    {
        var result = new List<uint>();
        if (string.IsNullOrEmpty(csv))
        {
            return result;
        }

        var span = csv.AsSpan();
        while (!span.IsEmpty)
        {
            var comma = span.IndexOf(',');
            var part = comma < 0 ? span : span[..comma];
            if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                result.Add(value);
            }

            span = comma < 0 ? [] : span[(comma + 1)..];
        }

        return result;
    }
}

public sealed partial class PocketCalculatorJsRuntime
{
    private RealmDomGc? _pageDomGc;

    /// <summary>
    /// Registers the collector's JavaScript half on the page document and lets ops collect
    /// it on their own. Called whenever the page gets a document.
    /// </summary>
    private void AttachDomGc(DomTree dom)
    {
        DetachDomGc();
        _pageDomGc = new RealmDomGc(this, State, PageRealmHelpers);
        dom.AddGcParticipant(_pageDomGc);
        dom.AutomaticCollection = true;
    }

    /// <summary>
    /// The document is leaving this runtime (a suspension or a new document): without its
    /// realms nothing here can say which of its nodes script holds, so it stops collecting.
    /// </summary>
    private void DetachDomGc()
    {
        if (_pageDomGc is { } gc && State.Dom is { } dom)
        {
            dom.RemoveGcParticipant(gc);
            dom.AutomaticCollection = false;
        }

        _pageDomGc = null;
    }

    internal static RealmDomGc AttachFrameDomGc(PocketCalculatorJsRuntime runtime, PocketCalculatorState frame, Func<ScriptObject?> helpers)
    {
        var gc = new RealmDomGc(runtime, frame, () => helpers() is { } h ? [h] : null);
        if (frame.Dom is { } dom)
        {
            dom.AddGcParticipant(gc);
            dom.AutomaticCollection = true;
        }

        return gc;
    }

    private IReadOnlyList<ScriptObject>? PageRealmHelpers()
    {
        if (_shim.HostHelpers is not { } page)
        {
            return null;
        }

        var result = new List<ScriptObject>(1 + _worlds.Count) { page };
        foreach (var world in _worlds.Values)
        {
            if (world.HostHelpers is not { } helpers)
            {
                return null;
            }

            result.Add(helpers);
        }

        return result;
    }

    /// <summary>
    /// A task boundary: script holds node ids only through wrappers now. Collects any
    /// document of this runtime whose collection is due.
    /// </summary>
    private void CollectDomAtTaskBoundary()
    {
        CollectIfDue(State.Dom);
        foreach (var realm in _realms)
        {
            CollectIfDue(realm.State.Dom);
        }
    }

    private static void CollectIfDue(DomTree? dom)
    {
        if (dom is null)
        {
            return;
        }

        dom.AdvanceExposureEpoch();
        if (dom.AutomaticCollection && dom.CollectionDue(inOperation: false))
        {
            dom.CollectGarbage();
        }
    }

    /// <summary>
    /// Collects the page document now, V8's collector included, as a task boundary would.
    /// For hosts and tests; call it with no script running.
    /// </summary>
    public DomCollectionResult CollectDomGarbage()
    {
        if (State.Dom is not { } dom)
        {
            return default;
        }

        dom.AdvanceExposureEpoch();
        return dom.CollectGarbage();
    }
}
