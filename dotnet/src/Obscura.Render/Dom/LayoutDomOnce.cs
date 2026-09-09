// Port of `layout_dom_once` in crates/obscura-render/src/dom.rs.
using Obscura.Dom;
using Obscura.Dom.Selectors;
using Obscura.Render.Css;
using TaffyAlignItems = Obscura.Render.Layout.AlignItems;
using TaffyAvailableSpace = Obscura.Render.Layout.AvailableSpace;
using TaffyDimension = Obscura.Render.Layout.Dimension;
using TaffyDirection = Obscura.Render.Layout.Direction;
using TaffyDisplay = Obscura.Render.Layout.Display;
using TaffyFlexDirection = Obscura.Render.Layout.FlexDirection;
using TaffyGridPlacementKind = Obscura.Render.Layout.GridPlacementKind;
using TaffyGridTemplateComponent = Obscura.Render.Layout.GridTemplateComponent;
using TaffyLengthPercentageAuto = Obscura.Render.Layout.LengthPercentageAuto;
using TaffyMaxTrack = Obscura.Render.Layout.MaxTrackSizingFunction;
using TaffyMinTrack = Obscura.Render.Layout.MinTrackSizingFunction;
using TaffyNodeId = Obscura.Render.Layout.NodeId;
using TaffyPosition = Obscura.Render.Layout.Position;
using TaffyStyle = Obscura.Render.Layout.Style;
using TaffyTrackSizingFunction = Obscura.Render.Layout.TrackSizingFunction;
using TaffyTree = Obscura.Render.Layout.TaffyTree<int?>;

namespace Obscura.Render;

public static partial class RenderDom
{
    /// <summary>
    /// Headless Chromium's classic scrollbar gutter is 15 CSS pixels.
    /// </summary>
    private const float ClassicScrollbarGutter = 15f;

    private sealed class Inherited
    {
        internal Display Display = Display.Inline;
        internal TaffyDirection Direction = TaffyDirection.Ltr;
        internal bool DisplayContents;
        internal bool IsInlineBlock;
        internal bool FlowRoot;
        internal bool IsTableBox;
        internal bool IsTableCellBox;
        internal RgbaColor? Color;
        internal float? FontSize;
        internal ushort FontWeight = 400;
        internal string? FontFamily;
        internal FontOpticalSizing FontOpticalSizing = Render.FontOpticalSizing.Auto;
        internal List<FontVariationSetting> FontVariationSettings = [];
        internal float LetterSpacing;
        internal bool LetterSpacingNonNormal;
        internal ContainerType ContainerType = ContainerType.Normal;
        internal List<string> ContainerNames = [];
        internal TaffyAlignItems? TextAlign;
        internal Dimension TextIndent = Dimension.Px(0f);
        internal bool LegacyCenter;
        internal bool VisibilityHidden;
        internal bool HasZeroOpacity;
        internal ListStyle ListStyle = ListStyle.Disc;
        internal LineHeight LineHeight = LineHeight.Normal;
        internal WhiteSpace WhiteSpace = WhiteSpace.Normal;
        internal OverflowWrap OverflowWrap = OverflowWrap.Normal;
        internal WordBreak WordBreak = WordBreak.Normal;
        internal TextWrapStyle TextWrapStyle = TextWrapStyle.Auto;
        internal TextTransform TextTransform = TextTransform.None;
        internal bool Italic;
        internal BoxSizing BoxSizing = BoxSizing.ContentBox;
        internal bool BorderCollapse;
        internal VerticalAlign? TableVerticalAlign;
        internal byte OverflowX;
        internal byte OverflowY;

        /// <summary>
        /// Containing-block width in px for the current element, carried down so percentage
        /// padding/margin can be turned into px before taffy layout.
        /// </summary>
        internal float CbWidth;

        /// <summary>Whether the containing block has a definite height.</summary>
        internal bool CbHeightDefinite;

        internal Inherited Clone() => new()
        {
            Display = Display,
            Direction = Direction,
            DisplayContents = DisplayContents,
            IsInlineBlock = IsInlineBlock,
            FlowRoot = FlowRoot,
            IsTableBox = IsTableBox,
            IsTableCellBox = IsTableCellBox,
            Color = Color,
            FontSize = FontSize,
            FontWeight = FontWeight,
            FontFamily = FontFamily,
            FontOpticalSizing = FontOpticalSizing,
            FontVariationSettings = [.. FontVariationSettings],
            LetterSpacing = LetterSpacing,
            LetterSpacingNonNormal = LetterSpacingNonNormal,
            ContainerType = ContainerType,
            ContainerNames = [.. ContainerNames],
            TextAlign = TextAlign,
            TextIndent = TextIndent,
            LegacyCenter = LegacyCenter,
            VisibilityHidden = VisibilityHidden,
            HasZeroOpacity = HasZeroOpacity,
            ListStyle = ListStyle,
            LineHeight = LineHeight,
            WhiteSpace = WhiteSpace,
            OverflowWrap = OverflowWrap,
            WordBreak = WordBreak,
            TextWrapStyle = TextWrapStyle,
            TextTransform = TextTransform,
            Italic = Italic,
            BoxSizing = BoxSizing,
            BorderCollapse = BorderCollapse,
            TableVerticalAlign = TableVerticalAlign,
            OverflowX = OverflowX,
            OverflowY = OverflowY,
            CbWidth = CbWidth,
            CbHeightDefinite = CbHeightDefinite,
        };
    }

    private static List<object> GridCalcBucket(LayoutStyle style, int index) =>
        style.GridCalcExpressions is { } buckets ? buckets[index] : [];

    private static void SetGridCalcBucket(LayoutStyle style, int index, List<object> value)
    {
        style.GridCalcExpressions ??= [[], [], [], []];
        style.GridCalcExpressions[index] = value;
    }

    internal static (
        DomLayout Layout,
        ContainerDecisionSignature? Signature,
        ContainerQueryStats QueryStats) LayoutDomOnce(
        DomTree tree,
        (float Width, float Height) viewport,
        IReadOnlyDictionary<NodeId, ReplacedIntrinsic> intrinsic,
        IReadOnlyList<WebFont> fonts,
        Stylesheet sheet,
        IReadOnlyDictionary<NodeId, Stylesheet> shadowSheets,
        ContainerSnapshot? snapshot,
        (RetainedStyleMaps Maps, HashSet<NodeId> Fresh)? retained,
        AnimationSample animationSample,
        AnimationTimelineState animationTimeline)
    {
        Matcher matcher = tree.CreateMatcher();
        Dictionary<NodeId, LayoutStyle> styles = retained?.Maps.Styles ?? [];
        Dictionary<NodeId, IReadOnlyDictionary<string, string>> customProperties =
            retained?.Maps.CustomProperties ?? [];
        HashSet<NodeId>? freshStyles = retained?.Fresh;
        Dictionary<string, string> rootProps = new(StringComparer.Ordinal);
        ContainerQueryEvaluator? evaluator = snapshot is not null
            ? new ContainerQueryEvaluator(tree, snapshot)
            : null;

        bool quirksMode = true;
        foreach (NodeId id in tree.Descendants(tree.Document))
        {
            if (tree.GetNode(id)?.Data is DoctypeData)
            {
                quirksMode = false;
                break;
            }
        }

        DomCascade.CascadeContext cascadeContext = new()
        {
            Tree = tree,
            DocumentSheet = sheet,
            ShadowSheets = shadowSheets,
            Styles = styles,
            CustomProperties = customProperties,
            QuirksMode = quirksMode,
            Viewport = viewport,
            AnimationSample = animationSample,
            AnimationTimeline = animationTimeline,
            FreshStyles = freshStyles,
        };
        DomCascade.CascadeWalk(
            cascadeContext, tree.Document, sheet, matcher, rootProps, evaluator, null, false);
        DomCascade.ResolveCssCounters(tree, styles);

        ContainerDecisionSignature? signature = null;
        ContainerQueryStats queryStats = default;
        if (evaluator is not null)
        {
            (signature, queryStats) = evaluator.Finish();
        }

        DomStyleFixups.GrowTrailingAutoCells(tree, styles);

        List<NodeId> descendants = tree.Descendants(tree.Document);
        bool needsEmojiFont = false;
        foreach (NodeId id in descendants)
        {
            if (tree.GetNode(id)?.TextContentOfTextNode is { } contents
                && FontAssets.TextMayNeedEmojiFont(contents))
            {
                needsEmojiFont = true;
                break;
            }
        }

        if (!needsEmojiFont)
        {
            foreach (LayoutStyle style in styles.Values)
            {
                if ((style.BeforeContent is { } before && FontAssets.TextMayNeedEmojiFont(before))
                    || (style.AfterContent is { } after && FontAssets.TextMayNeedEmojiFont(after)))
                {
                    needsEmojiFont = true;
                    break;
                }
            }
        }

        TaffyTree taffyTree = TaffyStyleMapping.NewTaffyTree<int?>();
        Dictionary<TaffyNodeId, NodeId> idMap = [];
        Dictionary<TaffyNodeId, (NodeId Source, string Word)> words = [];
        TextEngine engine = new(fonts, needsEmojiFont);
        IfcRegistry ifcItems = new();

        // The document node itself is not an element; lay out from the first element
        // descendant (the <html> root).
        NodeId? root = null;
        foreach (NodeId id in descendants)
        {
            if (tree.GetNode(id)?.IsElement == true)
            {
                root = id;
                break;
            }
        }

        Dictionary<NodeId, Rect> rects = [];
        Dictionary<NodeId, List<Rect>> inlineFragments = [];
        Dictionary<NodeId, List<(Rect Rect, string Text)>> textRuns = [];
        Dictionary<int, Rect> anonRects = [];
        Rect?[] generatedRects = [];

        if (root is { } rootId)
        {
            int rootGutters = styles.TryGetValue(rootId, out LayoutStyle? gutterStyle)
                ? Math.Min(gutterStyle.ScrollbarGutters, (byte)2)
                : 0;
            float initialCbWidth = F32.Max(
                viewport.Width - (ClassicScrollbarGutter * rootGutters),
                0f);
            float initialCbX = rootGutters == 2 ? ClassicScrollbarGutter : 0f;

            float vw = viewport.Width / 100f;
            float vh = viewport.Height / 100f;
            float rootFs = ResolveRootFontSize(styles, rootId, vw, vh);

            // The root element's containing block is the initial containing block.
            Inherited rootInherited = new() { CbWidth = initialCbWidth, CbHeightDefinite = true };

            // Computed definiteness after walking the real containing-block chain.
            HashSet<NodeId> definiteHeightNodes = [];
            ResolveComputedValues(
                tree,
                rootId,
                styles,
                freshStyles,
                definiteHeightNodes,
                rootInherited,
                rootFs,
                vw,
                vh,
                viewport,
                initialCbWidth);

            // Root/body overflow propagated to the viewport leaves the source element itself
            // overflow-visible for Taffy and BFC decisions.
            DomTransforms.MarkViewportOverflowSource(tree, rootId, styles);

            // Border-collapse is inherited, so only distribute a table's effective spacing
            // after the computed top-down values are known.
            DomStyleFixups.PropagateBorderSpacing(tree, styles);

            ApplyNativeControlSizes(tree, styles);

            DomStyleFixups.ResolveGridAreas(tree, rootId, styles);

            // The root's outer display is always blockified.
            if (styles.TryGetValue(rootId, out LayoutStyle? rootStyleBlockify))
            {
                LayoutStyleExtensions.BlockifyOuterDisplay(rootStyleBlockify);
            }

            // CSS Display blockification changes only the outer display.
            List<NodeId> itemParents = [];
            foreach ((NodeId id, LayoutStyle style) in styles)
            {
                if (style.Display == Display.Grid
                    || (style.Display == Display.Flex && !style.InternalFlexContainer))
                {
                    itemParents.Add(id);
                }
            }

            foreach (NodeId pid in itemParents)
            {
                DomStyleFixups.BlockifyLayoutChildren(tree, pid, styles);
            }

            // In an auto-height column flex container, a zero flex basis still participates in
            // intrinsic main-size calculation through the item's automatic minimum size.
            List<NodeId> intrinsicColumnFlexParents = [];
            foreach ((NodeId id, LayoutStyle style) in styles)
            {
                if (style.Display == Display.Flex
                    && style.FlexDirection == TaffyFlexDirection.Column
                    && style.Height.IsAuto)
                {
                    intrinsicColumnFlexParents.Add(id);
                }
            }

            foreach (NodeId parent in intrinsicColumnFlexParents)
            {
                foreach (NodeId child in tree.Children(parent))
                {
                    if (!styles.TryGetValue(child, out LayoutStyle? childStyle))
                    {
                        continue;
                    }

                    bool zeroBasis = childStyle.FlexBasis.Kind
                            is DimensionKind.Px or DimensionKind.Percent
                        && childStyle.FlexBasis.Value == 0f;
                    if (zeroBasis && childStyle.Height.IsAuto && childStyle.MinHeight.IsAuto)
                    {
                        childStyle.FlexBasis = Dimension.Auto;
                    }
                }
            }

            // Absolute/fixed boxes and floats are blockified externally but retain their inner
            // formatting mode.
            foreach (LayoutStyle style in styles.Values)
            {
                if (style.Position == TaffyPosition.Absolute || style.Float is not null)
                {
                    LayoutStyleExtensions.BlockifyOuterDisplay(style);
                }
            }

            DomStyleFixups.BlockifyGeneratedPseudos(styles);

            // Capture the definite containing width for ratio-only auto/auto replaced boxes
            // before installing their metadata.
            Dictionary<NodeId, float> ratioOnlyAvailableWidths = [];
            foreach ((NodeId nid, ReplacedIntrinsic metadata) in intrinsic)
            {
                if (!styles.TryGetValue(nid, out LayoutStyle? style))
                {
                    continue;
                }

                if (metadata.Width is null
                    && metadata.Height is null
                    && metadata.Ratio is not null
                    && style.Width.IsAuto
                    && style.Height.IsAuto
                    && DomTableSupport.ReliableRatioOnlyAvailableWidth(
                        tree, nid, styles, initialCbWidth) is { } width)
                {
                    ratioOnlyAvailableWidths[nid] = width;
                }
            }

            // Apply fetched intrinsic image sizes.
            foreach ((NodeId nid, ReplacedIntrinsic metadata) in intrinsic)
            {
                if (metadata.NaturalSize() is null)
                {
                    continue;
                }

                if (!styles.TryGetValue(nid, out LayoutStyle? style))
                {
                    continue;
                }

                style.IntrinsicSize = metadata.NaturalSize();
                style.ReplacedIntrinsic = metadata;
                style.RatioOnlyAvailableWidth =
                    ratioOnlyAvailableWidths.TryGetValue(nid, out float available)
                        ? available
                        : null;
                if ((style.AspectRatio is null || style.AspectRatioIsMapped)
                    && metadata.Ratio is not null)
                {
                    style.AspectRatio = metadata.Ratio;
                    style.AspectRatioIsMapped = false;
                    style.AspectRatioIsIntrinsic = true;
                }
            }

            List<DeferredCyclicInlineSize> deferredCyclicInlineSizes =
                DomSubgridPasses.DeferCyclicFlexInlineSizes(tree, styles, rootFs, vw, vh);

            BuildContext buildContext = new()
            {
                Tree = tree,
                TaffyTree = taffyTree,
                IdMap = idMap,
                Words = words,
                Engine = engine,
                Ifc = ifcItems,
                Styles = styles,
            };

            if (DomBuild.Build(buildContext, rootId) is { } taffyRoot)
            {
                // Taffy has no outer display type and only gives an auto-width Block root the
                // initial-containing-block width. CSS blockifies Flex/Grid roots too.
                if (styles.TryGetValue(rootId, out LayoutStyle? rootStyle)
                    && rootStyle.Width.IsAuto
                    && rootStyle.Display is Display.Flex or Display.Grid)
                {
                    TaffyStyle adjusted = taffyTree.GetStyle(taffyRoot).Clone();
                    float outer = F32.Max(
                        initialCbWidth - rootStyle.Margin.Left - rootStyle.Margin.Right,
                        0f);
                    float declared = rootStyle.BoxSizing == BoxSizing.ContentBox
                        ? F32.Max(
                            outer
                            - rootStyle.Padding.Left
                            - rootStyle.Padding.Right
                            - rootStyle.Border.Left
                            - rootStyle.Border.Right,
                            0f)
                        : outer;
                    Layout.Size<TaffyDimension> size = adjusted.Size;
                    size.Width = TaffyDimension.FromLength(declared);
                    adjusted.Size = size;
                    taffyTree.SetStyle(taffyRoot, adjusted);
                }

                // CSS 2.1 10.1: model the initial containing block explicitly as an
                // out-of-flow, viewport-sized box anchored at the root element's origin.
                TaffyStyle icbStyle = TaffyStyle.Default;
                icbStyle.Display = TaffyDisplay.Block;
                icbStyle.Position = TaffyPosition.Absolute;
                icbStyle.Inset = new Layout.Rect<TaffyLengthPercentageAuto>(
                    TaffyLengthPercentageAuto.FromLength(0f),
                    TaffyLengthPercentageAuto.Auto,
                    TaffyLengthPercentageAuto.FromLength(0f),
                    TaffyLengthPercentageAuto.Auto);
                icbStyle.Size = new Layout.Size<TaffyDimension>(
                    TaffyDimension.FromLength(initialCbWidth),
                    TaffyDimension.FromLength(viewport.Height));
                TaffyNodeId initialContainingBlock = taffyTree.NewLeaf(icbStyle);
                taffyTree.AddChild(taffyRoot, initialContainingBlock);

                List<StaticPositionCandidate> staticPositionCandidates =
                    DomPasses.ReparentInsetPositionedNodes(
                        tree, taffyTree, initialContainingBlock, idMap, styles);
                Layout.Size<TaffyAvailableSpace> available = new(
                    TaffyAvailableSpace.Definite(initialCbWidth),
                    TaffyAvailableSpace.Definite(viewport.Height));

                Layout.Size<float> Measure(
                    Layout.Size<float?> known,
                    Layout.Size<TaffyAvailableSpace> avail,
                    TaffyNodeId node,
                    int? ctx,
                    TaffyStyle _) =>
                    ctx is { } index
                        ? engine.MeasureTaffy(index, known, avail)
                        : new Layout.Size<float>(0f, 0f);

                float? IntrinsicWidth(TaffyTree t, TaffyNodeId node, TaffyAvailableSpace width)
                {
                    t.ComputeLayoutWithMeasure(
                        node,
                        new Layout.Size<TaffyAvailableSpace>(width, TaffyAvailableSpace.MaxContent),
                        Measure);
                    return t.GetLayout(node).Size.Width;
                }

                ApplyTableUsedWidths(
                    tree, taffyTree, taffyRoot, idMap, styles, ifcItems, initialCbWidth, available, Measure);

                taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                if (deferredCyclicInlineSizes.Count == 0
                    && DomPasses.ApplyFitContentWidths(
                        taffyTree, idMap, styles, initialCbWidth, IntrinsicWidth))
                {
                    taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                }

                if (DomPasses.ResolveAtomicPercentageHeights(
                        tree, taffyTree, taffyRoot, idMap, styles, definiteHeightNodes))
                {
                    taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                }

                if (DomPasses.RepairIntrinsicColumnFlexNegativeMargins(taffyTree, idMap, styles))
                {
                    taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                }

                DomSubgridPasses.ResolveDeferredFlexInlineSizes(
                    tree,
                    taffyTree,
                    idMap,
                    styles,
                    deferredCyclicInlineSizes,
                    rootFs,
                    vw,
                    vh,
                    (t, resolvedStyles, phase) =>
                    {
                        if (phase == DeferredFlexReflowPhase.Layout)
                        {
                            t.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                        }
                        else if (DomPasses.ApplyFitContentWidths(
                            t, idMap, resolvedStyles, initialCbWidth, IntrinsicWidth))
                        {
                            t.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                        }
                    });

                if (DomPasses.ApplyMulticolBalance(taffyTree, ifcItems.Multicol))
                {
                    taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                }

                if (DomPasses.ApplyFloatContinuations(tree, taffyTree, idMap, styles, ifcItems))
                {
                    taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                }

                if (DomPasses.ApplyTableRowGeometry(taffyTree, idMap, styles, ifcItems))
                {
                    taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                }

                if (DomSubgridPasses.ApplyFullSpanColumnSubgrids(
                        tree,
                        taffyTree,
                        idMap,
                        styles,
                        (t, node) =>
                        {
                            t.ComputeLayoutWithMeasure(
                                node,
                                new Layout.Size<TaffyAvailableSpace>(
                                    TaffyAvailableSpace.MaxContent,
                                    TaffyAvailableSpace.MaxContent),
                                Measure);
                            return t.GetLayout(node).Size.Width;
                        }))
                {
                    taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                }

                if (DomPasses.ApplyTableCellBlockAlignment(tree, taffyTree, idMap, styles))
                {
                    taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                }

                // A fully-auto positioned axis uses the box's static position in its original
                // formatting context. Harvest that coordinate only after every intrinsic,
                // table and fragmentation repair has produced final in-flow geometry.
                if (staticPositionCandidates.Count != 0)
                {
                    DomPasses.ResolveStaticPositionsAndReparent(taffyTree, staticPositionCandidates);
                    taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                }

                DomPasses.SyncResolvedPercentagePadding(
                    taffyTree, taffyRoot, initialCbWidth, idMap, ifcItems.Generated, styles);
                Dictionary<TaffyNodeId, int> generatedNodes = [];
                for (int index = 0; index < ifcItems.Generated.Count; index++)
                {
                    generatedNodes[ifcItems.Generated[index].Node] = index;
                }

                generatedRects = new Rect?[ifcItems.Generated.Count];
                DomPasses.ComputeAbsoluteRects(
                    taffyTree,
                    taffyRoot,
                    initialCbX,
                    0f,
                    idMap,
                    words,
                    rects,
                    textRuns,
                    anonRects,
                    generatedNodes,
                    generatedRects);
                inlineFragments = SynthesizeOrdinaryInlineFragments(rects, styles, engine);
                DomTableSupport.SynthesizeRowRects(tree, rects);
            }
        }

        DomPasses.SyncPositionedPseudoPercentagePadding(rects, styles);

        Dictionary<NodeId, OverflowClip?> clipRects = [];
        Dictionary<NodeId, (float X, float Y)> translates = [];
        Dictionary<NodeId, Affine2> transforms = [];
        if (root is { } clipRootId)
        {
            float rootFontSize = styles.TryGetValue(clipRootId, out LayoutStyle? clipRootStyle)
                ? clipRootStyle.FontSize ?? 16f
                : 16f;
            DomTransforms.ResolveClipRects(
                tree,
                clipRootId,
                null,
                0f,
                0f,
                rects,
                styles,
                clipRects,
                translates,
                Affine2.Identity,
                transforms,
                rootFontSize,
                viewport);
        }

        FinalizeShapedItems(
            engine, ifcItems, rects, styles, clipRects, translates, anonRects, viewport);

        // Pure-text IFC descendants do not own Taffy nodes. Once their shared buffer has its
        // final line breaks, derive their real continuations from shaping provenance.
        if (engine.HasInlineOwners())
        {
            Dictionary<NodeId, List<Rect>> canonical =
                SynthesizeShapedInlineFragments(tree, rects, styles, engine);
            if (canonical.Count != 0)
            {
                Dictionary<NodeId, (float X, float Y)> relativeOffsets = [];
                foreach (NodeId owner in canonical.Keys)
                {
                    if (FoldedInlineRelativeOffset(tree, owner, rects, styles) is { } offset
                        && offset != (0f, 0f))
                    {
                        relativeOffsets[owner] = offset;
                    }
                }

                engine.SetInlineOwnerOffsets(relativeOffsets);
                foreach ((NodeId owner, List<Rect> fragments) in canonical)
                {
                    inlineFragments[owner] = fragments;
                }

                clipRects.Clear();
                translates.Clear();
                transforms.Clear();
                if (root is { } reclipRootId)
                {
                    float rootFontSize = styles.TryGetValue(reclipRootId, out LayoutStyle? style)
                        ? style.FontSize ?? 16f
                        : 16f;
                    DomTransforms.ResolveClipRects(
                        tree,
                        reclipRootId,
                        null,
                        0f,
                        0f,
                        rects,
                        styles,
                        clipRects,
                        translates,
                        Affine2.Identity,
                        transforms,
                        rootFontSize,
                        viewport);
                }

                foreach ((NodeId nid, int idx) in ifcItems.Whole)
                {
                    engine.SetClip(
                        idx,
                        LayoutDomInternals.ShapedItemClip(
                            nid, rects, styles, clipRects, translates, viewport));
                }

                foreach ((NodeId parent, List<int> items) in ifcItems.Runs)
                {
                    Rect? clip = LayoutDomInternals.ShapedItemClip(
                        parent, rects, styles, clipRects, translates, viewport);
                    foreach (int idx in items)
                    {
                        engine.SetClip(idx, clip);
                    }
                }
            }
        }

        List<GeneratedBox> generatedBoxes = [];
        for (int index = 0; index < ifcItems.Generated.Count; index++)
        {
            if (index < generatedRects.Length && generatedRects[index] is { } rect)
            {
                GeneratedBoxBuild build = ifcItems.Generated[index];
                generatedBoxes.Add(new GeneratedBox(build.Host, build.Kind, rect));
            }
        }

        DomLayout layout = new()
        {
            Rects = rects,
            InlineFragments = inlineFragments,
            Styles = styles,
            CustomProperties = customProperties,
            ClipRects = clipRects,
            Translates = translates,
            Transforms = transforms,
            TextRuns = textRuns,
            TextEngine = engine,
            IfcItems = ifcItems.Whole,
            RunIfcItems = ifcItems.Runs,
            WordIfcItems = ifcItems.WordItems,
            GeneratedBoxes = generatedBoxes,
        };

        return (layout, signature, queryStats);
    }

    private static float ResolveRootFontSize(
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        NodeId rootId,
        float vw,
        float vh)
    {
        if (!styles.TryGetValue(rootId, out LayoutStyle? style))
        {
            return 16f;
        }

        if (style.FontSize is { } px)
        {
            return px;
        }

        if (style.FontSizeExpression is { } expression)
        {
            return ComputedStyle.ResolveContextualLength(expression, 16f, 16f, vw, vh, 16f) ?? 16f;
        }

        if (style.FontSizeRaw is { } raw)
        {
            Dimension resolved = raw.Resolve(16f, 16f, vw, vh);
            return resolved.Kind switch
            {
                DimensionKind.Px => resolved.Value,
                DimensionKind.Percent => 16f * resolved.Value,
                _ => 16f,
            };
        }

        return 16f;
    }

    /// <summary>
    /// Convert Taffy's ordinary-inline line surrogate into the element's actual visual
    /// fragment box.
    /// </summary>
    internal static Dictionary<NodeId, List<Rect>> SynthesizeOrdinaryInlineFragments(
        Dictionary<NodeId, Rect> rects,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        TextEngine engine)
    {
        Dictionary<NodeId, List<Rect>> fragments = [];
        List<(NodeId Id, Rect Union)> unions = [];
        foreach ((NodeId id, LayoutStyle style) in styles)
        {
            if (!style.IgnoresUsedBoxSizes())
            {
                continue;
            }

            if (!rects.TryGetValue(id, out Rect flowRect))
            {
                continue;
            }

            float fontHeight = F32.Max(engine.InlineFontBoxHeight(style), 0f);
            float lineHeight = F32.Max(engine.SelectedLineHeight(style), 0f);

            // Taffy's surrogate may be taller than this element's own strut when a nested
            // inline enlarges the line.
            float logicalTop = flowRect.Y + ((flowRect.Height - lineHeight) / 2f);
            Rect fragment = new(
                flowRect.X,
                logicalTop + ((lineHeight - fontHeight) / 2f) - style.Padding.Top - style.Border.Top,
                flowRect.Width,
                fontHeight
                    + style.Padding.Top
                    + style.Padding.Bottom
                    + style.Border.Top
                    + style.Border.Bottom);
            fragments[id] = [fragment];
            unions.Add((id, fragment));
        }

        foreach ((NodeId id, Rect union) in unions)
        {
            rects[id] = union;
        }

        return fragments;
    }

    internal static Dictionary<NodeId, List<Rect>> SynthesizeShapedInlineFragments(
        DomTree tree,
        Dictionary<NodeId, Rect> rects,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        TextEngine engine)
    {
        Dictionary<NodeId, List<((int Item, int Line) Order, Rect Rect)>> fragments = [];
        foreach (InlineOwnerLineFragment shaped in engine.InlineOwnerLineFragments())
        {
            if (!styles.TryGetValue(shaped.Owner, out LayoutStyle? style))
            {
                continue;
            }

            (float ascent, float descent) = engine.InlineFontBoxMetrics(style);
            if (FoldedInlineRelativeOffset(tree, shaped.Owner, rects, styles) is not { } relative)
            {
                // Keep the pre-existing Taffy surrogate geometry when an inset cannot be
                // resolved faithfully.
                continue;
            }

            Rect rect = new(
                shaped.X + relative.X,
                shaped.BaselineY - ascent - style.Padding.Top - style.Border.Top + relative.Y,
                shaped.Width,
                ascent
                    + descent
                    + style.Padding.Top
                    + style.Padding.Bottom
                    + style.Border.Top
                    + style.Border.Bottom);
            if (!fragments.TryGetValue(shaped.Owner, out var pieces))
            {
                pieces = [];
                fragments[shaped.Owner] = pieces;
            }

            pieces.Add(((shaped.ItemIndex, shaped.LineIndex), rect));
        }

        Dictionary<NodeId, List<Rect>> canonical = new(fragments.Count);
        foreach ((NodeId owner, var pieces) in fragments)
        {
            pieces.Sort((a, b) =>
            {
                int itemCompare = a.Order.Item.CompareTo(b.Order.Item);
                if (itemCompare != 0)
                {
                    return itemCompare;
                }

                int lineCompare = a.Order.Line.CompareTo(b.Order.Line);
                return lineCompare != 0 ? lineCompare : a.Rect.X.CompareTo(b.Rect.X);
            });

            List<Rect> ordered = [];
            foreach ((_, Rect rect) in pieces)
            {
                ordered.Add(rect);
            }

            Rect union = ordered[0];
            for (int index = 1; index < ordered.Count; index++)
            {
                Rect fragment = ordered[index];
                float left = F32.Min(union.X, fragment.X);
                float top = F32.Min(union.Y, fragment.Y);
                float right = F32.Max(union.X + union.Width, fragment.X + fragment.Width);
                float bottom = F32.Max(union.Y + union.Height, fragment.Y + fragment.Height);
                union = new Rect(left, top, F32.Max(right - left, 0f), F32.Max(bottom - top, 0f));
            }

            rects[owner] = union;
            canonical[owner] = ordered;
        }

        return canonical;
    }

    internal static (float X, float Y)? FoldedInlineRelativeOffset(
        DomTree tree,
        NodeId owner,
        IReadOnlyDictionary<NodeId, Rect> rects,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        (float Width, float Height)? ContainingBlockSize(NodeId from)
        {
            NodeId? ancestor = DomTraversal.RenderedParent(tree, from);
            while (ancestor is { } id)
            {
                if (!styles.TryGetValue(id, out LayoutStyle? style))
                {
                    return null;
                }

                if (!style.IgnoresUsedBoxSizes() && !style.DisplayContents)
                {
                    if (!rects.TryGetValue(id, out Rect rect))
                    {
                        return null;
                    }

                    return (
                        F32.Max(
                            rect.Width
                            - style.Border.Left
                            - style.Border.Right
                            - style.Padding.Left
                            - style.Padding.Right,
                            0f),
                        F32.Max(
                            rect.Height
                            - style.Border.Top
                            - style.Border.Bottom
                            - style.Padding.Top
                            - style.Padding.Bottom,
                            0f));
                }

                ancestor = DomTraversal.RenderedParent(tree, id);
            }

            return null;
        }

        (float X, float Y) offset = (0f, 0f);
        NodeId? current = owner;
        while (current is { } id)
        {
            if (!styles.TryGetValue(id, out LayoutStyle? style))
            {
                return null;
            }

            if (!style.IgnoresUsedBoxSizes())
            {
                break;
            }

            if (style.Position == TaffyPosition.Relative && !style.PositionSticky)
            {
                (float Width, float Height)? basis = ContainingBlockSize(id);
                bool failed = false;
                float? Resolve(Dimension? value, bool horizontal)
                {
                    switch (value)
                    {
                        case null:
                            return null;
                        case { Kind: DimensionKind.Auto }:
                            return null;
                        case { Kind: DimensionKind.Px } px when float.IsFinite(px.Value):
                            return px.Value;
                        case { Kind: DimensionKind.Percent } percent when float.IsFinite(percent.Value):
                            if (basis is { } size)
                            {
                                return percent.Value * (horizontal ? size.Width : size.Height);
                            }

                            failed = true;
                            return null;
                        default:
                            failed = true;
                            return null;
                    }
                }

                // Failed calc()/var() resolution is represented by an expression with no
                // corresponding used Dimension.
                for (int index = 0; index < 4; index++)
                {
                    if (style.InsetExpressions[index] is not null && style.Inset[index] is null)
                    {
                        return null;
                    }
                }

                float? left = Resolve(style.Inset[3], true);
                float? right = Resolve(style.Inset[1], true);
                float? top = Resolve(style.Inset[0], false);
                float? bottom = Resolve(style.Inset[2], false);
                if (failed)
                {
                    return null;
                }

                if (left is { } l)
                {
                    offset.X += l;
                }
                else if (right is { } r)
                {
                    offset.X -= r;
                }

                if (top is { } t)
                {
                    offset.Y += t;
                }
                else if (bottom is { } b)
                {
                    offset.Y -= b;
                }
            }

            current = DomTraversal.RenderedParent(tree, id);
        }

        return offset;
    }

    private static void FinalizeShapedItems(
        TextEngine engine,
        IfcRegistry ifcItems,
        IReadOnlyDictionary<NodeId, Rect> rects,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        IReadOnlyDictionary<NodeId, OverflowClip?> clipRects,
        IReadOnlyDictionary<NodeId, (float X, float Y)> translates,
        IReadOnlyDictionary<int, Rect> anonRects,
        (float Width, float Height) viewport)
    {
        // Pin each shaped inline context to its final content-box origin/width.
        foreach ((NodeId nid, int idx) in ifcItems.Whole)
        {
            if (!rects.TryGetValue(nid, out Rect rect) || !styles.TryGetValue(nid, out LayoutStyle? style))
            {
                continue;
            }

            (float X, float Y) origin = Inline.ContentOrigin(rect, style);
            float cw = Inline.ContentWidth(rect, style);

            // A table cell stretched taller than its text aligns its content per
            // vertical-align; the pure-text leaf path has no inner box to align.
            if (style.VerticalAlign is VerticalAlign.Middle or VerticalAlign.Bottom)
            {
                (_, float th) = engine.Measure(idx, cw);
                float contentH = rect.Height
                    - style.Padding.Top
                    - style.Padding.Bottom
                    - style.Border.Top
                    - style.Border.Bottom;
                float free = F32.Max(contentH - th, 0f);
                origin.Y += style.VerticalAlign == VerticalAlign.Middle ? free / 2f : free;
            }

            // The shaped text is this container's own content, so an `overflow: hidden` on the
            // container clips it. Clips live in screen space.
            OverflowClip? inherited = clipRects.TryGetValue(nid, out OverflowClip? found)
                ? found?.Clone()
                : null;
            OverflowClip? clip = inherited;
            if (style.OverflowHidden)
            {
                (float tx, float ty) = translates.TryGetValue(nid, out var translate)
                    ? translate
                    : (0f, 0f);
                OverflowClip own = OverflowClip.ForBox(rect, style, tx, ty);
                clip = inherited is null ? own : inherited.Intersect(own);
            }

            engine.Finalize(idx, origin, cw, clip?.ViewportRect(viewport));
        }

        // Anonymous run leaves have no DOM node and no border/padding of their own: the leaf
        // rect IS the content box.
        foreach ((NodeId parent, List<int> items) in ifcItems.Runs)
        {
            OverflowClip? inherited = clipRects.TryGetValue(parent, out OverflowClip? found)
                ? found?.Clone()
                : null;
            OverflowClip? clip = inherited;
            if (styles.TryGetValue(parent, out LayoutStyle? style)
                && rects.TryGetValue(parent, out Rect prect)
                && style.OverflowHidden)
            {
                (float tx, float ty) = translates.TryGetValue(parent, out var translate)
                    ? translate
                    : (0f, 0f);
                OverflowClip own = OverflowClip.ForBox(prect, style, tx, ty);
                clip = inherited is null ? own : inherited.Intersect(own);
            }

            Rect? viewportClip = clip?.ViewportRect(viewport);
            foreach (int idx in items)
            {
                if (anonRects.TryGetValue(idx, out Rect rect))
                {
                    engine.Finalize(idx, (rect.X, rect.Y), rect.Width, viewportClip);
                }
            }
        }

        // General word-fallback items retain one independently wrapped Taffy rect per token.
        foreach ((NodeId textNode, List<int> items) in ifcItems.WordItems)
        {
            OverflowClip? clip = clipRects.TryGetValue(textNode, out OverflowClip? found)
                ? found?.Clone()
                : null;
            Rect? viewportClip = clip?.ViewportRect(viewport);
            foreach (int idx in items)
            {
                if (anonRects.TryGetValue(idx, out Rect rect))
                {
                    engine.Finalize(idx, (rect.X, rect.Y), rect.Width, viewportClip);
                }
            }
        }
    }
}
