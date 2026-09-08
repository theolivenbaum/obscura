using Obscura.Dom;

namespace Obscura.Render;

/// <summary>
/// CSS animation sample time relative to the document timeline origin.
/// </summary>
/// <remarks>
/// Live capture uses elapsed document time, while deterministic comparison harnesses can
/// request an exact instant such as T=0.
/// </remarks>
public readonly record struct AnimationSampleTime(float Milliseconds);

/// <summary>Which clock an animation sample represents.</summary>
/// <remarks>
/// Live rendering subtracts each element's animation-instance start epoch from document time.
/// Deterministic comparison instead assigns one local time to every animation, matching Web
/// Animations <c>currentTime</c> rather than pretending all dynamically-created animations
/// began at navigation.
/// </remarks>
public enum AnimationSampleMode
{
    /// <summary>The default.</summary>
    DocumentTime = 0,

    LocalOverride,
}

public readonly record struct AnimationSample(AnimationSampleTime Time, AnimationSampleMode Mode)
{
    public static AnimationSample Document(float milliseconds) =>
        new(new AnimationSampleTime(milliseconds), AnimationSampleMode.DocumentTime);

    public static AnimationSample LocalOverride(float milliseconds) =>
        new(new AnimationSampleTime(milliseconds), AnimationSampleMode.LocalOverride);
}

/// <summary>Strongest renderer-visible consequence of an animation effect.</summary>
/// <remarks>
/// Ordering is intentional: aggregating with <c>max</c> keeps unknown or geometry-affecting
/// tracks conservative while still distinguishing compositor/paint-only effects from an
/// inactive animation. The numeric order None &lt; Paint &lt; Geometry is load-bearing.
/// </remarks>
public enum AnimationEffectImpact
{
    /// <summary>The default.</summary>
    None = 0,

    Paint = 1,
    Geometry = 2,
}

public enum AnimationDirection
{
    /// <summary>The default.</summary>
    Normal = 0,

    Reverse,
    Alternate,
    AlternateReverse,
}

public enum AnimationFillMode
{
    /// <summary>The default.</summary>
    None = 0,

    Forwards,
    Backwards,
    Both,
}

public enum AnimationPlayState
{
    /// <summary>The default.</summary>
    Running = 0,

    Paused,
}

/// <summary>Timing fields for the first CSS animation.</summary>
/// <remarks>
/// Milliseconds are used internally so <c>s</c>, <c>ms</c>, negative delays, and calculated
/// stagger values share one unit. Rust's <c>Default</c> sets <c>IterationCount</c> to
/// <c>1.0</c>, so <c>default(AnimationTiming)</c> is NOT the CSS initial value; use
/// <see cref="Default"/>. <c>IterationCount</c> is a finite non-negative count, or positive
/// infinity for <c>infinite</c>.
/// </remarks>
public readonly record struct AnimationTiming(
    float DurationMs,
    float DelayMs,
    float IterationCount,
    AnimationDirection Direction,
    AnimationFillMode FillMode,
    AnimationPlayState PlayState)
{
    public static readonly AnimationTiming Default = new(
        0f,
        0f,
        1f,
        AnimationDirection.Normal,
        AnimationFillMode.None,
        AnimationPlayState.Running);
}

internal readonly record struct AnimationInstanceKey(string Name);

internal sealed class AnimationInstance
{
    public AnimationInstanceKey Key;
    public float StartMs;
    public float? HoldTimeMs;
    public bool WasPaused;
}

/// <summary>A normalized property keyframe supplied through the Web Animations API.</summary>
/// <remarks>
/// Values stay in specified form until cascade time because transforms may contain relative
/// units whose meaning depends on the animated element.
/// </remarks>
public sealed class WaapiKeyframe
{
    public float Offset;
    public float? Opacity;
    public string? Transform;

    public WaapiKeyframe Clone() => new()
    {
        Offset = Offset,
        Opacity = Opacity,
        Transform = Transform,
    };
}

public enum WaapiPlayState
{
    Running,
    Paused,
    Finished,
}

/// <summary>Renderer-owned WAAPI effect.</summary>
/// <remarks>
/// JavaScript keeps the wrapper object identity, while this document-scoped record is the
/// source of truth for cascade and paint. It deliberately does not rewrite the element's inline
/// style.
/// </remarks>
public sealed class WaapiAnimation
{
    public ulong Id;
    public NodeId Node;
    public List<WaapiKeyframe> Keyframes = [];
    public AnimationTiming Timing = AnimationTiming.Default;

    /// <summary>Cubic Bezier timing function control points. <c>null</c> is linear.</summary>
    public float[]? Easing;

    /// <summary>CSS <c>linear()</c> output samples at evenly distributed input positions.</summary>
    public List<float>? LinearEasing;

    public float StartTimeMs;
    public float? HoldTimeMs;
    public WaapiPlayState PlayState;

    public WaapiAnimation Clone() => new()
    {
        Id = Id,
        Node = Node,
        Keyframes = [.. Keyframes.Select(static frame => frame.Clone())],
        Timing = Timing,
        Easing = Easing is null ? null : (float[])Easing.Clone(),
        LinearEasing = LinearEasing is null ? null : [.. LinearEasing],
        StartTimeMs = StartTimeMs,
        HoldTimeMs = HoldTimeMs,
        PlayState = PlayState,
    };
}

/// <summary>
/// Page-owned CSS animation instance history retained across layout rebuilds. Node ids are
/// document-scoped, so navigation must replace this value.
/// </summary>
public sealed class AnimationTimelineState
{
    private readonly Dictionary<NodeId, AnimationInstance> _instances = [];
    private readonly Dictionary<NodeId, float> _startCandidates = [];
    private readonly Dictionary<NodeId, float> _subtreeStartCandidates = [];

    // Rust uses a BTreeMap so replay order is by animation id; SortedDictionary preserves that.
    private readonly SortedDictionary<ulong, WaapiAnimation> _waapi = [];

    public void NoteStartCandidate(NodeId node, float documentTimeMs)
    {
        if (float.IsFinite(documentTimeMs) && documentTimeMs >= 0f)
        {
            _startCandidates[node] = documentTimeMs;
        }
    }

    /// <summary>
    /// Defer expansion until the next style flush. This is needed for DOM string APIs whose
    /// imported descendants do not exist until after the mutation op has completed.
    /// </summary>
    public void NoteSubtreeStartCandidate(NodeId root, float documentTimeMs)
    {
        if (float.IsFinite(documentTimeMs) && documentTimeMs >= 0f)
        {
            _subtreeStartCandidates[root] = documentTimeMs;
        }
    }

    public void MaterializeStartCandidates(DomTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        KeyValuePair<NodeId, float>[] pending = [.. _subtreeStartCandidates];
        _subtreeStartCandidates.Clear();
        foreach ((NodeId root, float startMs) in pending)
        {
            if (tree.GetNode(root) is null)
            {
                continue;
            }

            _startCandidates[root] = startMs;
            foreach (NodeId node in tree.Descendants(root))
            {
                _startCandidates.TryAdd(node, startMs);
            }
        }
    }

    public void RemoveSubtree(IEnumerable<NodeId> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        foreach (NodeId node in nodes)
        {
            _instances.Remove(node);
            _startCandidates.Remove(node);
            _subtreeStartCandidates.Remove(node);
            RetainWaapi(animation => animation.Node != node);
        }
    }

    public void RegisterWaapi(WaapiAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        _waapi[animation.Id] = animation;
    }

    /// <summary>
    /// Target node for a registered Web Animation. Control operations use this before
    /// mutating/removing the record so retained style damage can stay scoped to the affected
    /// element.
    /// </summary>
    public NodeId? WaapiNode(ulong id) =>
        _waapi.TryGetValue(id, out WaapiAnimation? animation) ? animation.Node : null;

    /// <summary>
    /// Exact set of nodes currently targeted by registered Web Animations. The render
    /// preparation boundary additionally filters disconnected targets against the current DOM
    /// before producing restyle damage.
    /// </summary>
    public HashSet<NodeId> WaapiNodes()
    {
        HashSet<NodeId> nodes = [];
        foreach (WaapiAnimation animation in _waapi.Values)
        {
            nodes.Add(animation.Node);
        }

        return nodes;
    }

    /// <summary>
    /// New animation instance epochs must reach a style flush. Retained mutation planning
    /// cascades the affected nodes while clean branches keep their existing instances.
    /// </summary>
    public bool HasPendingStartCandidates() =>
        _startCandidates.Count != 0 || _subtreeStartCandidates.Count != 0;

    public bool CancelWaapi(ulong id) => _waapi.Remove(id);

    public bool SetWaapiCurrentTime(ulong id, float documentTimeMs, float localTimeMs)
    {
        if (!_waapi.TryGetValue(id, out WaapiAnimation? animation))
        {
            return false;
        }

        float local = F32.Max(localTimeMs, 0f);
        animation.HoldTimeMs = local;
        animation.StartTimeMs = documentTimeMs - local;
        return true;
    }

    public bool SetWaapiPlayState(ulong id, WaapiPlayState state, float documentTimeMs)
    {
        if (!_waapi.TryGetValue(id, out WaapiAnimation? animation))
        {
            return false;
        }

        float current = animation.HoldTimeMs ?? F32.Max(documentTimeMs - animation.StartTimeMs, 0f);
        switch (state)
        {
            case WaapiPlayState.Running:
                animation.StartTimeMs = documentTimeMs - current;
                animation.HoldTimeMs = null;
                break;
            default:
                animation.HoldTimeMs = current;
                break;
        }

        animation.PlayState = state;
        return true;
    }

    public bool FinishWaapi(ulong id)
    {
        if (!_waapi.TryGetValue(id, out WaapiAnimation? animation))
        {
            return false;
        }

        float end = animation.Timing.DelayMs
            + (animation.Timing.DurationMs * F32.Max(animation.Timing.IterationCount, 0f));
        animation.HoldTimeMs = F32.Max(end, 0f);
        animation.PlayState = WaapiPlayState.Finished;
        return true;
    }

    internal IEnumerable<(WaapiAnimation Animation, AnimationSampleTime Local)> WaapiForNode(
        NodeId node,
        AnimationSampleTime documentTime)
    {
        foreach (WaapiAnimation animation in _waapi.Values)
        {
            if (animation.Node != node)
            {
                continue;
            }

            float local = animation.HoldTimeMs
                ?? F32.Max(documentTime.Milliseconds - animation.StartTimeMs, 0f);
            yield return (animation, new AnimationSampleTime(local));
        }
    }

    internal bool HasActiveWaapi(AnimationSampleTime documentTime)
    {
        foreach (WaapiAnimation animation in _waapi.Values)
        {
            if (WaapiIsActive(animation, documentTime))
            {
                return true;
            }
        }

        return false;
    }

    internal AnimationEffectImpact ActiveWaapiEffectImpact(AnimationSampleTime documentTime)
    {
        AnimationEffectImpact best = AnimationEffectImpact.None;
        bool any = false;
        foreach (WaapiAnimation animation in _waapi.Values)
        {
            if (!WaapiIsActive(animation, documentTime))
            {
                continue;
            }

            any = true;
            AnimationEffectImpact impact = AnimationEffectImpact.None;
            foreach (WaapiKeyframe frame in animation.Keyframes)
            {
                if (frame.Transform is not null)
                {
                    // A specified WAAPI transform remains geometry-affecting even when this
                    // renderer cannot parse its value.
                    impact = AnimationEffectImpact.Geometry;
                    break;
                }
            }

            if (impact == AnimationEffectImpact.None)
            {
                foreach (WaapiKeyframe frame in animation.Keyframes)
                {
                    if (frame.Opacity is not null)
                    {
                        impact = AnimationEffectImpact.Paint;
                        break;
                    }
                }
            }

            if (impact > best)
            {
                best = impact;
            }
        }

        return any ? best : AnimationEffectImpact.None;
    }

    internal AnimationSampleTime SampleFor(
        NodeId node,
        AnimationInstanceKey key,
        AnimationPlayState playState,
        AnimationSample sample)
    {
        if (sample.Mode == AnimationSampleMode.LocalOverride)
        {
            return sample.Time;
        }

        float documentTimeMs = sample.Time.Milliseconds;
        float? transitionTimeMs = null;
        if (_startCandidates.Remove(node, out float candidateTime))
        {
            transitionTimeMs = candidateTime;
        }

        if (!_instances.TryGetValue(node, out AnimationInstance? instance) || !instance.Key.Equals(key))
        {
            float candidate = transitionTimeMs ?? 0f;
            bool startPaused = playState == AnimationPlayState.Paused;
            instance = new AnimationInstance
            {
                Key = key,
                StartMs = startPaused ? documentTimeMs : candidate,
                HoldTimeMs = startPaused ? 0f : null,
                WasPaused = startPaused,
            };
            _instances[node] = instance;
        }

        bool paused = playState == AnimationPlayState.Paused;
        if (paused && !instance.WasPaused)
        {
            float pausedAt = transitionTimeMs ?? documentTimeMs;
            instance.HoldTimeMs = F32.Max(pausedAt - instance.StartMs, 0f);
        }
        else if (!paused && instance.WasPaused)
        {
            float held = instance.HoldTimeMs ?? 0f;
            instance.HoldTimeMs = null;
            float resumedAt = transitionTimeMs ?? documentTimeMs;
            instance.StartMs = resumedAt - held;
        }

        instance.WasPaused = paused;
        return new AnimationSampleTime(
            instance.HoldTimeMs ?? F32.Max(documentTimeMs - instance.StartMs, 0f));
    }

    internal void ClearAnimation(NodeId node, AnimationSample sample)
    {
        if (sample.Mode == AnimationSampleMode.DocumentTime)
        {
            _instances.Remove(node);
        }
    }

    public void RetainNodes(Func<NodeId, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        RetainKeys(_instances, keep);
        RetainKeys(_startCandidates, keep);
        RetainKeys(_subtreeStartCandidates, keep);
        RetainWaapi(animation => keep(animation.Node));
    }

    public void ClearStartCandidates()
    {
        _startCandidates.Clear();
        _subtreeStartCandidates.Clear();
    }

    private static void RetainKeys<TValue>(Dictionary<NodeId, TValue> map, Func<NodeId, bool> keep)
    {
        if (map.Count == 0)
        {
            return;
        }

        List<NodeId>? drop = null;
        foreach (NodeId node in map.Keys)
        {
            if (!keep(node))
            {
                (drop ??= []).Add(node);
            }
        }

        if (drop is null)
        {
            return;
        }

        foreach (NodeId node in drop)
        {
            map.Remove(node);
        }
    }

    private void RetainWaapi(Func<WaapiAnimation, bool> keep)
    {
        if (_waapi.Count == 0)
        {
            return;
        }

        List<ulong>? drop = null;
        foreach (KeyValuePair<ulong, WaapiAnimation> entry in _waapi)
        {
            if (!keep(entry.Value))
            {
                (drop ??= []).Add(entry.Key);
            }
        }

        if (drop is null)
        {
            return;
        }

        foreach (ulong id in drop)
        {
            _waapi.Remove(id);
        }
    }

    internal static bool WaapiIsActive(WaapiAnimation animation, AnimationSampleTime documentTime)
    {
        if (animation.PlayState != WaapiPlayState.Running
            || animation.Timing.DurationMs <= 0f
            || animation.Timing.IterationCount <= 0f)
        {
            return false;
        }

        float local = animation.HoldTimeMs
            ?? F32.Max(documentTime.Milliseconds - animation.StartTimeMs, 0f);
        float end = animation.Timing.DelayMs
            + (animation.Timing.DurationMs * animation.Timing.IterationCount);
        return local < F32.Max(end, 0f);
    }
}
