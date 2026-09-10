using Obscura.Dom;
using Obscura.Render;
using Obscura.Render.Css;

namespace Obscura.Js.Ops;

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
    public static PreparedRender? EnsurePreparedRender(ObscuraState state)
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

        PreparedRender? previous = null;
        if (!incompatible && renderMedia == CssMediaType.Screen)
        {
            previous = state.PreparedRender;
            state.PreparedRender = null;
        }

        var mutations = state.PendingStyleMutations.ToArray();
        state.PendingStyleMutations.Clear();

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

        var connected = StateHelpers.ShadowIncludingConnectedNodes(dom);
        state.AnimationTimeline.RetainNodes(connected.Contains);

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
    public static PreparedRender? EnsurePreparedGeometry(ObscuraState state)
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

    /// <summary>Samples the live document timeline once per host/HTML task.</summary>
    public static void SampleLiveDocumentAnimations(ObscuraState state)
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
    public static void BeginAnimationTask(ObscuraState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.AnimationTaskGeneration = unchecked(state.AnimationTaskGeneration + 1);
    }

    public static bool EnsureResolvedScroll(ObscuraState state) =>
        EnsureResolvedScrollForConsumer(state, geometryOnly: false);

    internal static bool EnsureResolvedScrollForGeometry(ObscuraState state) =>
        EnsureResolvedScrollForConsumer(state, geometryOnly: true);

    private static bool EnsureResolvedScrollForConsumer(ObscuraState state, bool geometryOnly)
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

    public static (float X, float Y) ClampScrollOffset(ObscuraState state, (float X, float Y) requested) =>
        ClampScrollOffsetForConsumer(state, requested, geometryOnly: false);

    internal static (float X, float Y) ClampScrollOffsetForGeometry(
        ObscuraState state,
        (float X, float Y) requested) =>
        ClampScrollOffsetForConsumer(state, requested, geometryOnly: true);

    private static (float X, float Y) ClampScrollOffsetForConsumer(
        ObscuraState state,
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
