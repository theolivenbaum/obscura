// Port of the `layout_dom*` entry points and the container-query convergence loop in
// crates/obscura-render/src/dom.rs.
using Obscura.Dom;
using Obscura.Dom.Selectors;
using Obscura.Render.Css;

namespace Obscura.Render;

internal enum ContainerLayoutTermination : byte
{
    NoQueries,
    NoContainers,
    GeometryStable,
    SignatureStable,
    OscillationFallback,
    PassCapFallback,
}

internal readonly record struct ContainerLayoutTelemetry(
    int Passes,
    ContainerLayoutTermination Termination,
    ContainerQueryStats Query,
    int RetainedReused,
    int RetainedFresh,
    int RetainedFallback);

/// <summary>
/// Render-tree construction: build a taffy layout tree from a live <see cref="DomTree"/>, run
/// layout, and return border-box geometry keyed by <see cref="NodeId"/>.
/// </summary>
public static partial class RenderDom
{
    internal const int ContainerLayoutSafetyLimit = 512;

    /// <summary>Lay out a DOM tree within <paramref name="viewport"/> in CSS pixels.</summary>
    public static DomLayout LayoutDom(DomTree tree, (float Width, float Height) viewport) =>
        LayoutDomWithImages(tree, viewport, new Dictionary<NodeId, (float, float)>());

    /// <summary>
    /// Like <see cref="LayoutDom"/>, but <paramref name="intrinsic"/> supplies fetched
    /// intrinsic pixel sizes for replaced elements keyed by <see cref="NodeId"/>.
    /// </summary>
    public static DomLayout LayoutDomWithImages(
        DomTree tree,
        (float Width, float Height) viewport,
        IReadOnlyDictionary<NodeId, (float Width, float Height)> intrinsic) =>
        LayoutDomWithResources(tree, viewport, intrinsic, []);

    /// <summary>
    /// Like <see cref="LayoutDomWithImages"/>, with decoded OpenType web-font data loaded into
    /// the shaping database for this render pass.
    /// </summary>
    public static DomLayout LayoutDomWithResources(
        DomTree tree,
        (float Width, float Height) viewport,
        IReadOnlyDictionary<NodeId, (float Width, float Height)> intrinsic,
        IReadOnlyList<byte[]> fonts)
    {
        ArgumentNullException.ThrowIfNull(intrinsic);
        ArgumentNullException.ThrowIfNull(fonts);
        List<WebFont> webFonts = [];
        foreach (byte[] data in fonts)
        {
            webFonts.Add(new WebFont { Data = data });
        }

        Dictionary<NodeId, ReplacedIntrinsic> metadata = [];
        foreach ((NodeId nid, (float width, float height)) in intrinsic)
        {
            if (float.IsFinite(width) && float.IsFinite(height) && width > 0f && height > 0f)
            {
                metadata[nid] = ReplacedIntrinsic.FromDimensions(width, height);
            }
        }

        return LayoutDomWithWebFonts(tree, viewport, metadata, webFonts);
    }

    internal static DomLayout LayoutDomWithWebFonts(
        DomTree tree,
        (float Width, float Height) viewport,
        IReadOnlyDictionary<NodeId, ReplacedIntrinsic> intrinsic,
        IReadOnlyList<WebFont> fonts) =>
        LayoutDomWithWebFontsMeasured(tree, viewport, intrinsic, fonts).Layout;

    internal static DomLayout LayoutDomWithWebFontsAndStylesheetCache(
        DomTree tree,
        (float Width, float Height) viewport,
        IReadOnlyDictionary<NodeId, ReplacedIntrinsic> intrinsic,
        IReadOnlyList<WebFont> fonts,
        StylesheetCache stylesheetCache) =>
        LayoutDomWithWebFontsAndStylesheetCacheAtAnimationTime(
            tree, viewport, intrinsic, fonts, stylesheetCache, default);

    internal static DomLayout LayoutDomWithWebFontsAndStylesheetCacheAtAnimationTime(
        DomTree tree,
        (float Width, float Height) viewport,
        IReadOnlyDictionary<NodeId, ReplacedIntrinsic> intrinsic,
        IReadOnlyList<WebFont> fonts,
        StylesheetCache stylesheetCache,
        AnimationSampleTime animationSampleTime)
    {
        AnimationTimelineState timeline = new();
        return LayoutDomWithWebFontsAndStylesheetCacheWithAnimationState(
            tree,
            viewport,
            intrinsic,
            fonts,
            stylesheetCache,
            new AnimationSample(animationSampleTime, AnimationSampleMode.DocumentTime),
            timeline);
    }

    internal static DomLayout LayoutDomWithWebFontsAndStylesheetCacheWithAnimationState(
        DomTree tree,
        (float Width, float Height) viewport,
        IReadOnlyDictionary<NodeId, ReplacedIntrinsic> intrinsic,
        IReadOnlyList<WebFont> fonts,
        StylesheetCache stylesheetCache,
        AnimationSample animationSample,
        AnimationTimelineState animationTimeline) =>
        LayoutDomWithWebFontsAndStylesheetCacheForMediaWithAnimationState(
            tree,
            viewport,
            intrinsic,
            fonts,
            stylesheetCache,
            CssMediaType.Screen,
            animationSample,
            animationTimeline);

    internal static DomLayout LayoutDomWithWebFontsAndStylesheetCacheForMediaWithAnimationState(
        DomTree tree,
        (float Width, float Height) viewport,
        IReadOnlyDictionary<NodeId, ReplacedIntrinsic> intrinsic,
        IReadOnlyList<WebFont> fonts,
        StylesheetCache stylesheetCache,
        CssMediaType mediaType,
        AnimationSample animationSample,
        AnimationTimelineState animationTimeline) =>
        LayoutDomWithWebFontsPassLimitAtAnimationTime(
            tree,
            viewport,
            intrinsic,
            fonts,
            null,
            stylesheetCache,
            null,
            [],
            mediaType,
            animationSample,
            animationTimeline).Layout;

    internal static DomLayout LayoutDomWithWebFontsAndRetainedStyles(
        DomTree tree,
        (float Width, float Height) viewport,
        IReadOnlyDictionary<NodeId, ReplacedIntrinsic> intrinsic,
        IReadOnlyList<WebFont> fonts,
        StylesheetCache stylesheetCache,
        RetainedStyleMaps retained,
        IReadOnlyList<RetainedStyleMutation> mutations) =>
        LayoutDomWithWebFontsAndRetainedStylesAtAnimationTime(
            tree, viewport, intrinsic, fonts, stylesheetCache, retained, mutations, default);

    internal static DomLayout LayoutDomWithWebFontsAndRetainedStylesAtAnimationTime(
        DomTree tree,
        (float Width, float Height) viewport,
        IReadOnlyDictionary<NodeId, ReplacedIntrinsic> intrinsic,
        IReadOnlyList<WebFont> fonts,
        StylesheetCache stylesheetCache,
        RetainedStyleMaps retained,
        IReadOnlyList<RetainedStyleMutation> mutations,
        AnimationSampleTime animationSampleTime)
    {
        AnimationTimelineState timeline = new();
        return LayoutDomWithWebFontsAndRetainedStylesWithAnimationState(
            tree,
            viewport,
            intrinsic,
            fonts,
            stylesheetCache,
            retained,
            mutations,
            new AnimationSample(animationSampleTime, AnimationSampleMode.DocumentTime),
            timeline);
    }

    internal static DomLayout LayoutDomWithWebFontsAndRetainedStylesWithAnimationState(
        DomTree tree,
        (float Width, float Height) viewport,
        IReadOnlyDictionary<NodeId, ReplacedIntrinsic> intrinsic,
        IReadOnlyList<WebFont> fonts,
        StylesheetCache stylesheetCache,
        RetainedStyleMaps retained,
        IReadOnlyList<RetainedStyleMutation> mutations,
        AnimationSample animationSample,
        AnimationTimelineState animationTimeline) =>
        LayoutDomWithWebFontsPassLimitAtAnimationTime(
            tree,
            viewport,
            intrinsic,
            fonts,
            null,
            stylesheetCache,
            retained,
            mutations,
            CssMediaType.Screen,
            animationSample,
            animationTimeline).Layout;

    internal static (DomLayout Layout, ContainerLayoutTelemetry Telemetry)
        LayoutDomWithWebFontsMeasured(
            DomTree tree,
            (float Width, float Height) viewport,
            IReadOnlyDictionary<NodeId, ReplacedIntrinsic> intrinsic,
            IReadOnlyList<WebFont> fonts) =>
        LayoutDomWithWebFontsPassLimit(tree, viewport, intrinsic, fonts, null, null, null, []);

    internal static (DomLayout Layout, ContainerLayoutTelemetry Telemetry)
        LayoutDomWithWebFontsPassLimit(
            DomTree tree,
            (float Width, float Height) viewport,
            IReadOnlyDictionary<NodeId, ReplacedIntrinsic> intrinsic,
            IReadOnlyList<WebFont> fonts,
            int? passLimit,
            StylesheetCache? stylesheetCache,
            RetainedStyleMaps? retained,
            IReadOnlyList<RetainedStyleMutation> mutations)
    {
        AnimationTimelineState timeline = new();
        return LayoutDomWithWebFontsPassLimitAtAnimationTime(
            tree,
            viewport,
            intrinsic,
            fonts,
            passLimit,
            stylesheetCache,
            retained,
            mutations,
            CssMediaType.Screen,
            default,
            timeline);
    }

    internal static (DomLayout Layout, ContainerLayoutTelemetry Telemetry)
        LayoutDomWithWebFontsPassLimitAtAnimationTime(
            DomTree tree,
            (float Width, float Height) viewport,
            IReadOnlyDictionary<NodeId, ReplacedIntrinsic> intrinsic,
            IReadOnlyList<WebFont> fonts,
            int? passLimit,
            StylesheetCache? stylesheetCache,
            RetainedStyleMaps? retained,
            IReadOnlyList<RetainedStyleMutation> mutations,
            CssMediaType mediaType,
            AnimationSample animationSample,
            AnimationTimelineState animationTimeline)
    {
        ArgumentNullException.ThrowIfNull(tree);

        // Collect the text of every <style> block in document order.
        List<string> cssSources = [];
        foreach (NodeId nid in tree.Descendants(tree.Document))
        {
            if (tree.GetNode(nid) is not { } node || node.AsElement() is not { } element)
            {
                continue;
            }

            if (!string.Equals(element.Name.Local, "style", StringComparison.Ordinal))
            {
                continue;
            }

            string? media = node.GetAttribute("media");
            if (media is null
                || media.Trim().Length == 0
                || CssMediaQuery.AppliesForViewportAndType(media, viewport, mediaType))
            {
                cssSources.Add(tree.TextContent(nid));
            }
        }

        Stylesheet sheet;
        bool stylesheetCacheHit;
        if (stylesheetCache is not null)
        {
            (sheet, stylesheetCacheHit) =
                stylesheetCache.GetOrParse(tree, cssSources, viewport, mediaType);
        }
        else
        {
            sheet = Stylesheet.ParseForViewportAndMedia(tree, cssSources, viewport, mediaType);
            stylesheetCacheHit = false;
        }

        Dictionary<NodeId, Stylesheet> shadowSheets =
            DomCascade.CollectShadowStylesheets(tree, viewport, mediaType);

        int retainedRequested = retained?.Styles.Count ?? 0;
        (RetainedStyleMaps Maps, HashSet<NodeId> Fresh)? reuse = null;
        if (retained is not null)
        {
            reuse = PrepareRetainedStyles(
                tree, sheet, shadowSheets, retained, mutations, stylesheetCacheHit);
        }

        int retainedFresh = 0;
        if (reuse is { } prepared)
        {
            foreach (NodeId node in prepared.Maps.Styles.Keys)
            {
                if (prepared.Fresh.Contains(node))
                {
                    retainedFresh++;
                }
            }
        }

        int retainedReused = reuse is { } counted ? counted.Maps.Styles.Count - retainedFresh : 0;
        int retainedFallback = retainedRequested != 0 && reuse is null ? 1 : 0;

        var first = LayoutDomOnce(
            tree,
            viewport,
            intrinsic,
            fonts,
            sheet,
            shadowSheets,
            null,
            reuse,
            animationSample,
            animationTimeline);
        DomLayout laid = first.Layout;
        ContainerQueryStats query = first.QueryStats;

        if (!sheet.HasContainerQueries())
        {
            return (laid, new ContainerLayoutTelemetry(
                1,
                ContainerLayoutTermination.NoQueries,
                query,
                retainedReused,
                retainedFresh,
                retainedFallback));
        }

        ContainerSnapshot snapshot = DomStyleFixups.ContainerSnapshotOf(tree, laid);

        // A container condition has no matching query container when the initial cascade
        // produced neither container-type nor container-name.
        if (snapshot.Boxes.Count == 0)
        {
            return (laid, new ContainerLayoutTelemetry(
                1,
                ContainerLayoutTermination.NoContainers,
                query,
                retainedReused,
                retainedFresh,
                retainedFallback));
        }

        (DomLayout Layout, ContainerDecisionSignature Signature)? previousCandidate = null;
        List<ContainerDecisionSignature> seenSignatures = [];
        int passes = 1;
        ContainerLayoutTermination termination = ContainerLayoutTermination.PassCapFallback;

        // Gecko permits at most one CQ-triggered update per container element in one flush.
        // Scale the useful bound with DOM ancestry and nested conditional depth.
        Dictionary<NodeId, int> elementDepths = new() { [tree.Document] = 0 };
        int maxDomDepth = 1;
        foreach (NodeId id in DomTraversal.RenderedDescendants(tree, tree.Document))
        {
            if (tree.GetNode(id) is not { } node)
            {
                continue;
            }

            int parentDepth = 0;
            if (DomTraversal.RenderedParent(tree, id) is { } parent
                && elementDepths.TryGetValue(parent, out int found))
            {
                parentDepth = found;
            }

            int depth = parentDepth + (node.IsElement ? 1 : 0);
            elementDepths[id] = depth;
            maxDomDepth = Math.Max(maxDomDepth, depth);
        }

        int maxPasses = passLimit ?? Math.Clamp(
            maxDomDepth + sheet.ContainerConditionDepth() + 2,
            4,
            ContainerLayoutSafetyLimit);
        bool needsFallback = false;
        for (int pass = 2; pass <= maxPasses; pass++)
        {
            var next = LayoutDomOnce(
                tree,
                viewport,
                intrinsic,
                fonts,
                sheet,
                shadowSheets,
                snapshot,
                null,
                animationSample,
                animationTimeline);
            passes = pass;
            query = new ContainerQueryStats(
                query.Evaluations + next.QueryStats.Evaluations,
                query.CacheHits + next.QueryStats.CacheHits,
                query.AncestorSteps + next.QueryStats.AncestorSteps);
            ContainerSnapshot nextSnapshot = DomStyleFixups.ContainerSnapshotOf(tree, next.Layout);
            ContainerDecisionSignature signature = next.Signature
                ?? throw new InvalidOperationException("container pass must produce a signature");
            ContainerLayoutTermination? reason = ContainerIterationTermination(
                nextSnapshot.Equals(snapshot),
                signature,
                previousCandidate?.Signature);
            if (reason is { } resolved)
            {
                termination = resolved;

                // Equal adjacent signatures prove that the *previous* candidate's applied
                // decisions match an evaluation of its own final snapshot.
                laid = resolved == ContainerLayoutTermination.SignatureStable
                    ? previousCandidate!.Value.Layout
                    : next.Layout;
                break;
            }

            bool seen = false;
            foreach (ContainerDecisionSignature candidate in seenSignatures)
            {
                if (candidate.Equals(signature))
                {
                    seen = true;
                    break;
                }
            }

            if (seen)
            {
                termination = ContainerLayoutTermination.OscillationFallback;
                needsFallback = true;
                break;
            }

            seenSignatures.Add(signature);
            previousCandidate = (next.Layout, signature);
            snapshot = nextSnapshot;
            if (pass == maxPasses)
            {
                needsFallback = true;
            }
        }

        if (needsFallback)
        {
            // Author-controlled CSS must never crash rendering, but neither may we return a
            // layout whose conditional declarations contradict the geometry used to choose
            // them.
            var fallback = LayoutDomOnce(
                tree,
                viewport,
                intrinsic,
                fonts,
                sheet,
                shadowSheets,
                null,
                null,
                animationSample,
                animationTimeline);
            laid = fallback.Layout;
            passes++;
            query = new ContainerQueryStats(
                query.Evaluations + fallback.QueryStats.Evaluations,
                query.CacheHits + fallback.QueryStats.CacheHits,
                query.AncestorSteps + fallback.QueryStats.AncestorSteps);
        }

        return (laid, new ContainerLayoutTelemetry(
            passes,
            termination,
            query,
            retainedReused,
            retainedFresh,
            retainedFallback));
    }

    internal static ContainerLayoutTermination? ContainerIterationTermination<T>(
        bool geometryStable,
        T signature,
        T? previousSignature)
        where T : class
    {
        if (geometryStable)
        {
            return ContainerLayoutTermination.GeometryStable;
        }

        return previousSignature is not null && previousSignature.Equals(signature)
            ? ContainerLayoutTermination.SignatureStable
            : null;
    }

    private static (RetainedStyleMaps Maps, HashSet<NodeId> Fresh)? PrepareRetainedStyles(
        DomTree tree,
        Stylesheet sheet,
        IReadOnlyDictionary<NodeId, Stylesheet> shadowSheets,
        RetainedStyleMaps retained,
        IReadOnlyList<RetainedStyleMutation> mutations,
        bool stylesheetCacheHit)
    {
        // Until shadow sheets have their own retained cache keys and invalidation maps,
        // reusing computed styles after DOM/style damage in a document with a native root
        // could preserve stale shadow rules or inherited host custom properties.
        bool resourceOnly = mutations.Count != 0;
        foreach (RetainedStyleMutation mutation in mutations)
        {
            if (mutation is not RetainedStyleMutation.Resource)
            {
                resourceOnly = false;
                break;
            }
        }

        if (shadowSheets.Count != 0 && !resourceOnly)
        {
            return null;
        }

        if (!stylesheetCacheHit)
        {
            return null;
        }

        HashSet<NodeId> activeContainers = [];
        foreach ((NodeId node, LayoutStyle style) in retained.Styles)
        {
            if (style.ContainerType != ContainerType.Normal)
            {
                activeContainers.Add(node);
            }
        }

        HashSet<NodeId> connected = [tree.Document];
        foreach (NodeId node in DomTraversal.RenderedDescendants(tree, tree.Document))
        {
            connected.Add(node);
        }

        List<NodeId> stale = [];
        foreach (NodeId node in retained.Styles.Keys)
        {
            if (!connected.Contains(node))
            {
                stale.Add(node);
            }
        }

        foreach (NodeId node in stale)
        {
            retained.Styles.Remove(node);
        }

        stale.Clear();
        foreach (NodeId node in retained.CustomProperties.Keys)
        {
            if (!connected.Contains(node))
            {
                stale.Add(node);
            }
        }

        foreach (NodeId node in stale)
        {
            retained.CustomProperties.Remove(node);
        }

        if (RetainedStylePlanner.Plan(tree, sheet, mutations) is not RetainedStylePlan.Reuse plan)
        {
            return null;
        }

        HashSet<NodeId> dirty = plan.Dirty;
        if (activeContainers.Count != 0 && sheet.HasContainerQueries())
        {
            Matcher matcher = tree.CreateMatcher();
            RetainedStylePlanner.AddContainerQueryResetScopes(
                tree, tree.Document, sheet, matcher, activeContainers, false, false, dirty);
        }

        // Once animation damage reaches at least half of the retained style graph, sparse
        // reuse no longer offsets dirty-set bookkeeping and branch checks.
        if (plan.HasAnimationDamage && dirty.Count * 2 >= retained.Styles.Count)
        {
            return null;
        }

        return (retained, dirty);
    }
}
