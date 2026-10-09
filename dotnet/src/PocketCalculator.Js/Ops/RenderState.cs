using PocketCalculator.Dom;
using PocketCalculator.Render;
using PocketCalculator.Render.Css;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// The retained render/layout/scroll state machine the render-facing ops share.
/// </summary>
/// <remarks>
/// These are the free functions of <c>ops.rs</c> that sit between the ops and
/// <c>obscura-render</c>: preparing (or reusing) a layout, sampling the document
/// timeline at a task boundary, and resolving scroll offsets into the current
/// layout's dense scroll topology.
/// </remarks>
public static class RenderState
{
    /// <summary>
    /// Ensure a prepared render that is exact for the current animation sample,
    /// rebuilding it from the retained style graph when that is possible.
    /// </summary>
    public static PreparedRender? EnsurePreparedRender(PocketCalculatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // Memoized, not the direct read: the uncached one runs the selector engine
        // over the whole tree looking for `base[href]`, which makes this an
        // O(nodes) call. It sits on the reuse check every getBoundingClientRect()
        // takes, so on a 5000-node document that alone was 2.5ms per repeated
        // rect read. The memo is keyed on the document and activity generations,
        // so inserting a <base> still invalidates it.
        var baseUrl = StateHelpers.DocumentBaseUrlMemoized(state);
        var viewport = state.Viewport;
        var renderMedia = state.RenderMedia;
        var animationSample = state.AnimationSample;
        var incompatible = state.PreparedRender is { } current
            && (current.Viewport() != viewport
                || !string.Equals(current.BaseUrl(), baseUrl, StringComparison.Ordinal));
        var needsRebuild = state.PreparedRender is not { } prepared
            || incompatible
            || prepared.AnimationSample() != animationSample
            || state.PendingStyleMutations.Count != 0;
        if (!needsRebuild)
        {
            return state.PreparedRender;
        }

        if (state.Dom is { } candidateDom)
        {
            state.AnimationTimeline.MaterializeStartCandidates(candidateDom);
        }

        if (animationSample.Mode == AnimationSampleMode.DocumentTime)
        {
            state.AnimationTimeline.ResolvePendingWaapiStarts(animationSample.Time.Milliseconds);
        }

        PreparedRender? previous = null;
        if (!incompatible && renderMedia == CssMediaType.Screen)
        {
            previous = state.PreparedRender;
            state.PreparedRender = null;
        }

        var mutations = state.PendingStyleMutations.ToArray();
        state.PendingStyleMutations.Clear();
        if (PrepTrace && state.Dom is { } traced)
        {
            TracePrepare(traced, mutations, previous is not null);
        }

        if (state.Dom is not { } dom)
        {
            return state.PreparedRender;
        }

        PreparedRender? built;
        if (previous is not null)
        {
            built = RenderPaint.PrepareDomWithRetainedStylesWithAnimationState(
                dom,
                viewport,
                baseUrl,
                state.RenderResources,
                state.DynamicFonts,
                state.StylesheetCache,
                previous,
                mutations,
                animationSample,
                state.AnimationTimeline)
                ?? RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCacheWithAnimationState(
                    dom,
                    viewport,
                    baseUrl,
                    state.RenderResources,
                    state.DynamicFonts,
                    state.StylesheetCache,
                    animationSample,
                    state.AnimationTimeline);
        }
        else
        {
            built = renderMedia == CssMediaType.Screen
                ? RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCacheWithAnimationState(
                    dom,
                    viewport,
                    baseUrl,
                    state.RenderResources,
                    state.DynamicFonts,
                    state.StylesheetCache,
                    animationSample,
                    state.AnimationTimeline)
                : RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCacheForMediaWithAnimationState(
                    dom,
                    viewport,
                    baseUrl,
                    state.RenderResources,
                    state.DynamicFonts,
                    state.StylesheetCache,
                    renderMedia,
                    animationSample,
                    state.AnimationTimeline);
        }

        if (built is null)
        {
            return state.PreparedRender;
        }

        if (animationSample.Mode == AnimationSampleMode.DocumentTime)
        {
            state.AnimationTimeline.ClearStartCandidates();
        }

        // Built on the first question only: a document with no animation state asks none, and
        // every forced layout read used to pay a whole-tree walk and set for it.
        HashSet<NodeId>? connected = null;
        state.AnimationTimeline.RetainNodes(
            node => (connected ??= StateHelpers.ShadowIncludingConnectedNodes(dom)).Contains(node));

        state.PreparedRender = built;
        state.ResolvedScroll = null;
        return state.PreparedRender;
    }

    /// <summary>
    /// Prepare enough state for a geometry-only CSSOM consumer.
    /// </summary>
    /// <remarks>
    /// A forward sample with only paint effects may read the retained layout without
    /// resampling its styles. <c>AnimationSample</c> on the prepared render stays
    /// behind intentionally, so a later paint or computed-style consumer takes the
    /// exact path in <see cref="EnsurePreparedRender"/>.
    /// </remarks>
    public static PreparedRender? EnsurePreparedGeometry(PocketCalculatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // Memoized for the same reason as EnsurePreparedRender above: this is the
        // geometry fast path, so an O(nodes) selector query here defeats the point
        // of having a fast path at all.
        var baseUrl = StateHelpers.DocumentBaseUrlMemoized(state);
        var reusable = state.PendingStyleMutations.Count == 0
            && !state.AnimationTimeline.HasPendingStartCandidates()
            && state.PreparedRender is { } prepared
            && prepared.Viewport() == state.Viewport
            && string.Equals(prepared.BaseUrl(), baseUrl, StringComparison.Ordinal)
            && (prepared.AnimationSample() == state.AnimationSample
                || prepared.CanReuseGeometryForAnimationSample(state.AnimationSample));
        return reusable ? state.PreparedRender : EnsurePreparedRender(state);
    }

    /// <summary>
    /// A diagnostic: <c>POCKETCALCULATOR_PREP_TRACE=1</c> writes, for every prepare, the op that
    /// forced it and the mutations it consumes to stderr.
    /// </summary>
    internal static readonly bool PrepTrace =
        Environment.GetEnvironmentVariable("POCKETCALCULATOR_PREP_TRACE") == "1";

    /// <summary>The node the read being traced asked about (<see cref="PrepTrace"/> only).</summary>
    [ThreadStatic]
    internal static NodeId? TraceQuery;

    /// <summary>Who dropped the prepared render (<see cref="PrepTrace"/> only).</summary>
    internal static void TraceDrop()
    {
        var frames = Environment.StackTrace.Split('\n')
            .Where(frame => frame.Contains("PocketCalculator.", StringComparison.Ordinal)
                && !frame.Contains("TraceDrop", StringComparison.Ordinal)
                && !frame.Contains("set_PreparedRender", StringComparison.Ordinal))
            .Take(3)
            .Select(frame => frame.Trim().Split('(')[0].Replace("at PocketCalculator.", "", StringComparison.Ordinal));
        Console.Error.WriteLine($"DROP {string.Join(" <- ", frames)}");
    }

    private static void TracePrepare(DomTree dom, RetainedStyleMutation[] mutations, bool retained)
    {
        string op = "?";
        foreach (string frame in Environment.StackTrace.Split('\n'))
        {
            int at = frame.IndexOf("Ops.RenderOps.", StringComparison.Ordinal);
            if (at < 0)
            {
                at = frame.IndexOf("Ops.DomOps.", StringComparison.Ordinal);
            }

            if (at >= 0)
            {
                op = frame[(at + 4)..].Split('(')[0].Trim();
                break;
            }
        }

        string Describe(NodeId node)
        {
            if (dom.GetNode(node) is not { } n)
            {
                return $"#{node.Index}(gone)";
            }

            if (n.AsElement() is { } element)
            {
                string? id = n.GetAttribute("id");
                string? cls = n.GetAttribute("class");
                return $"{element.Name.Local}{(id is null ? "" : "#" + id)}{(cls is null ? "" : "." + cls.Split(' ')[0])}";
            }

            return n.IsText ? "text" : "node";
        }

        var parts = new List<string>();
        foreach (RetainedStyleMutation mutation in mutations.Take(12))
        {
            parts.Add(mutation switch
            {
                RetainedStyleMutation.Attribute a => $"attr {Describe(a.Mutation.Node)} {a.Mutation.Name}={(dom.GetNode(a.Mutation.Node)?.GetAttribute(a.Mutation.Name) is { } v ? v[..Math.Min(v.Length, 80)] : "-")}",
                RetainedStyleMutation.Tree { Mutation: TreeStyleMutation.Insert i } => $"insert {Describe(i.Node)} into {Describe(i.NewParent)}",
                RetainedStyleMutation.Tree { Mutation: TreeStyleMutation.Remove r } => $"remove {Describe(r.Node)} from {Describe(r.OldParent)}",
                RetainedStyleMutation.Tree { Mutation: TreeStyleMutation.Text t } => $"text in {(t.Parent is { } p ? Describe(p) : "?")}",
                RetainedStyleMutation.Resource => "resource",
                _ => mutation.GetType().Name,
            });
        }

        string query = TraceQuery is { } asked ? Describe(asked) : "?";
        Console.Error.WriteLine($"PREP {(retained ? "retained" : "full")} by {op} of {query} n={mutations.Length}: {string.Join(" | ", parts)}");
    }

    /// <summary>
    /// The prepared render a box-existence or <c>offsetParent</c> read may consult while style
    /// mutations are still pending (see <see cref="PreparedRender.TryRetainedOffsetParent"/>),
    /// or null when the read should prepare as usual: nothing is pending (the geometry fast
    /// path already answers), or the render is stale for any reason other than those mutations.
    /// </summary>
    internal static PreparedRender? PreparedWithPendingMutations(PocketCalculatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // Pending animation start candidates are not checked, unlike EnsurePreparedGeometry: they
        // are birth times for animations of the elements the next cascade restyles, and the
        // answer is taken only for elements whose styles that cascade cannot change.
        if (state.PendingStyleMutations.Count == 0
            || state.PreparedRender is not { } prepared
            || prepared.Viewport() != state.Viewport
            || state.RenderMedia != CssMediaType.Screen)
        {
            return null;
        }

        var baseUrl = StateHelpers.DocumentBaseUrlMemoized(state);
        return string.Equals(prepared.BaseUrl(), baseUrl, StringComparison.Ordinal)
            && (prepared.AnimationSample() == state.AnimationSample
                || prepared.CanReuseGeometryForAnimationSample(state.AnimationSample))
            ? prepared
            : null;
    }

    /// <summary>Samples the live document timeline once per host/HTML task.</summary>
    public static void SampleLiveDocumentAnimations(PocketCalculatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.AnimationSampledTaskGeneration == state.AnimationTaskGeneration)
        {
            return;
        }

        state.AnimationSampledTaskGeneration = state.AnimationTaskGeneration;
        var sample = AnimationSample.Document(
            (float)Math.Min(state.AnimationTimelineElapsedMilliseconds, float.MaxValue));
        if (state.AnimationSample == sample)
        {
            return;
        }

        if (sample.Time.Milliseconds > state.AnimationSample.Time.Milliseconds
            && state.AnimationSample.Mode == AnimationSampleMode.DocumentTime
            && state.PendingStyleMutations.Count == 0
            && state.PreparedRender is { } prepared
            && prepared.AdvanceInactiveAnimationSampleTime(sample.Time))
        {
            state.AnimationSample = sample;
            return;
        }

        var forwardDocumentSample = sample.Mode == AnimationSampleMode.DocumentTime
            && state.AnimationSample.Mode == AnimationSampleMode.DocumentTime
            && sample.Time.Milliseconds > state.AnimationSample.Time.Milliseconds;
        state.AnimationSample = sample;
        if (!forwardDocumentSample)
        {
            state.PreparedRender = null;
            state.PendingStyleMutations.Clear();
        }

        state.ResolvedScroll = null;
    }

    /// <summary>Opens a new host/HTML task epoch for document-timeline sampling.</summary>
    public static void BeginAnimationTask(PocketCalculatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.AnimationTaskGeneration = unchecked(state.AnimationTaskGeneration + 1);
    }

    public static bool EnsureResolvedScroll(PocketCalculatorState state) =>
        EnsureResolvedScrollForConsumer(state, geometryOnly: false);

    internal static bool EnsureResolvedScrollForGeometry(PocketCalculatorState state) =>
        EnsureResolvedScrollForConsumer(state, geometryOnly: true);

    private static bool EnsureResolvedScrollForConsumer(PocketCalculatorState state, bool geometryOnly)
    {
        ArgumentNullException.ThrowIfNull(state);
        var prepared = geometryOnly ? EnsurePreparedGeometry(state) : EnsurePreparedRender(state);
        if (prepared is null)
        {
            return false;
        }

        if (state.ResolvedScroll is { } resolved && resolved.Generation == state.ScrollGeneration)
        {
            return true;
        }

        if (state.PreparedRender is not { } layout || state.Dom is not { } dom)
        {
            return false;
        }

        var valid = new HashSet<NodeId>(layout.ScrollContainerNodes());
        var snapshot = layout.ResolveScrollState(dom, state.ScrollOffset, state.ElementScrollOffsets);
        state.ScrollOffset = snapshot.RootOffset();
        foreach (var node in valid)
        {
            var offset = layout.ElementScrollMetrics(node, snapshot)?.Offset ?? (0f, 0f);
            if (offset == (0f, 0f))
            {
                state.ElementScrollOffsets.Remove(node);
            }
            else
            {
                state.ElementScrollOffsets[node] = offset;
            }
        }

        state.ResolvedScroll = (state.ScrollGeneration, snapshot);
        return true;
    }

    public static (float X, float Y) ClampScrollOffset(PocketCalculatorState state, (float X, float Y) requested) =>
        ClampScrollOffsetForConsumer(state, requested, geometryOnly: false);

    internal static (float X, float Y) ClampScrollOffsetForGeometry(
        PocketCalculatorState state,
        (float X, float Y) requested) =>
        ClampScrollOffsetForConsumer(state, requested, geometryOnly: true);

    private static (float X, float Y) ClampScrollOffsetForConsumer(
        PocketCalculatorState state,
        (float X, float Y) requested,
        bool geometryOnly)
    {
        ArgumentNullException.ThrowIfNull(state);
        var prepared = geometryOnly ? EnsurePreparedGeometry(state) : EnsurePreparedRender(state);
        var clamped = prepared?.ClampScroll(requested) ?? (0f, 0f);
        if (state.ScrollOffset != clamped)
        {
            state.ScrollOffset = clamped;
            state.ActivityGeneration = unchecked(state.ActivityGeneration + 1);
            state.ScrollGeneration = unchecked(state.ScrollGeneration + 1);
            state.ResolvedScroll = null;
        }

        return state.ScrollOffset;
    }
}
