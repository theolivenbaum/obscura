namespace Obscura.Js.Ops;

/// <summary>
/// Which document belongs to which realm.
/// </summary>
/// <remarks>
/// <para>
/// An op has to read the state of the realm that <em>called</em> it. Making a realm
/// "current" around the host's own calls into it is not enough: a frame's deferred
/// work - a timer firing or a promise settling - re-enters JavaScript from the event
/// loop, where nothing had the chance to swap anything. Without this a frame's
/// <c>setTimeout</c> callback runs with the frame's globals but writes to the
/// <em>parent's</em> DOM.
/// </para>
/// <para>
/// Rust asks V8 for the entered-or-microtask context. ClearScript exposes no such
/// hook, so the runtime tells this registry which realm is running by setting
/// <see cref="Current"/> when it enters one; every realm's bootstrap closure also
/// knows its own frame id and passes it to the ops that take one, which is the path
/// Rust prefers anyway because it is both correct for cross-realm access and cheaper.
/// </para>
/// </remarks>
public sealed class RealmStates
{
    private readonly List<(object Context, uint FrameId, ObscuraState State)> _entries = [];

    /// <summary>The realm whose script is running, when the runtime has named one.</summary>
    public ObscuraState? Current { get; set; }

    public bool IsEmpty => _entries.Count == 0;

    public void Register(object context, uint frameId, ObscuraState state)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);
        _entries.Add((context, frameId, state));
    }

    public void Forget(object context)
    {
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_entries[i].Context, context))
            {
                _entries.RemoveAt(i);
            }
        }
    }

    public ObscuraState? ByFrameId(uint frameId)
    {
        foreach (var entry in _entries)
        {
            if (entry.FrameId == frameId)
            {
                return entry.State;
            }
        }

        return null;
    }

    public ObscuraState? ByContext(object context)
    {
        foreach (var entry in _entries)
        {
            if (ReferenceEquals(entry.Context, context))
            {
                return entry.State;
            }
        }

        return null;
    }
}
