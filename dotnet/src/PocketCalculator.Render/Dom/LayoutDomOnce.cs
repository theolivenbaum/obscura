// Port of `layout_dom_once` in crates/obscura-render/src/dom.rs.
using PocketCalculator.Dom;
using PocketCalculator.Dom.Selectors;
using PocketCalculator.Render.Css;
using TaffyAlignItems = PocketCalculator.Render.Layout.AlignItems;
using TaffyAvailableSpace = PocketCalculator.Render.Layout.AvailableSpace;
using TaffyAvailableSpaceKind = PocketCalculator.Render.Layout.AvailableSpaceKind;
using TaffyDimension = PocketCalculator.Render.Layout.Dimension;
using TaffyDirection = PocketCalculator.Render.Layout.Direction;
using TaffyDisplay = PocketCalculator.Render.Layout.Display;
using TaffyFlexDirection = PocketCalculator.Render.Layout.FlexDirection;
using TaffyGridPlacementKind = PocketCalculator.Render.Layout.GridPlacementKind;
using TaffyGridTemplateComponent = PocketCalculator.Render.Layout.GridTemplateComponent;
using TaffyLengthPercentageAuto = PocketCalculator.Render.Layout.LengthPercentageAuto;
using TaffyMaxTrack = PocketCalculator.Render.Layout.MaxTrackSizingFunction;
using TaffyMinTrack = PocketCalculator.Render.Layout.MinTrackSizingFunction;
using TaffyNodeId = PocketCalculator.Render.Layout.NodeId;
using TaffyPosition = PocketCalculator.Render.Layout.Position;
using TaffyStyle = PocketCalculator.Render.Layout.Style;
using TaffyTrackSizingFunction = PocketCalculator.Render.Layout.TrackSizingFunction;
using TaffyTree = PocketCalculator.Render.Layout.TaffyTree<int?>;

namespace PocketCalculator.Render;

public static partial class RenderDom
{
    /// <summary>
    /// Headless Chromium's classic scrollbar gutter is 15 CSS pixels.
    /// </summary>
    private const float ClassicScrollbarGutter = 15f;

    internal sealed class Inherited
    {
        // The two lists below are only ever replaced, never changed in place, so a clone
        // shares them. Copying both per element, on top of the field initializers' own two,
        // was four lists for every element (about 20 MB on 50k elements).
        private static readonly List<FontVariationSetting> NoVariations = [];
        private static readonly List<string> NoContainerNames = [];

        internal Display Display = Display.Inline;
        internal TaffyDirection Direction = TaffyDirection.Ltr;
        internal bool DisplayContents;
        internal bool IsInlineBlock;
        internal bool FlowRoot;
        internal bool IsTableBox;
        internal bool IsTableCellBox;
        internal TableInternalDisplay AuthoredTableDisplay;
        internal (float Horizontal, float Vertical)? BorderSpacing;
        internal RgbaColor? Color;
        internal float? FontSize;
        internal ushort FontWeight = 400;
        internal string? FontFamily;
        internal string? FontFamilySpecified;
        internal string? Cursor = "auto";
        internal string? PointerEvents = "auto";
        internal FontOpticalSizing FontOpticalSizing = Render.FontOpticalSizing.Auto;
        internal List<FontVariationSetting> FontVariationSettings = NoVariations;
        internal float LetterSpacing;
        internal bool LetterSpacingNonNormal;
        internal ContainerType ContainerType = ContainerType.Normal;
        internal List<string> ContainerNames = NoContainerNames;
        internal TextAlignKeyword? TextAlign;
        internal TextAlignKeyword? TextAlignLast;
        internal Dimension TextIndent = Dimension.Px(0f);
        internal bool LegacyCenter;
        internal bool VisibilityHidden;
        internal bool HasZeroOpacity;
        internal SvgPaintValues Svg = SvgPaintValues.Initial;
        internal ListStyle ListStyle = ListStyle.Disc;
        internal LineHeight LineHeight = LineHeight.Normal;
        internal WhiteSpace WhiteSpace = WhiteSpace.Normal;
        internal OverflowWrap OverflowWrap = OverflowWrap.Normal;
        internal WordBreak WordBreak = WordBreak.Normal;
        internal TextWrapStyle TextWrapStyle = TextWrapStyle.Auto;
        internal TextTransform TextTransform = TextTransform.None;
        internal bool Italic;
        internal string FontVariantCaps = "normal";
        internal float FontStretch = 1f;
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

        /// <summary>
        /// Containing-block content-box height in px, meaningful only when
        /// <see cref="CbHeightDefinite"/>.
        /// </summary>
        /// <remarks>
        /// DEVIATION from <c>crates/obscura-render/src/dom.rs</c>, which tracks no such
        /// value and resolves every functional block-axis size against the viewport
        /// height. Taffy resolves a bare percentage height itself against the real
        /// containing block, but a functional one (<c>calc(100% - 4px)</c>) has to be
        /// flattened to px before layout, and the viewport is the wrong basis for it:
        /// Tesserae's <c>.tss-card</c> is <c>height: calc(100% - 4px)</c> in an
        /// auto-height parent, which Chromium computes to <c>auto</c> and the reference
        /// computes to a full viewport height. See "Known deviations" in todo.md.
        /// </remarks>
        internal float CbHeight;

        /// <summary>
        /// Whether <see cref="CbHeight"/> is a usable number, and not merely definite.
        /// Implies <see cref="CbHeightDefinite"/>.
        /// </summary>
        /// <remarks>
        /// A grid item's containing block is its grid area, whose size is only known once
        /// track sizing has run. Its percentage height is therefore definite (taffy
        /// resolves it against the area) while its pixel value is unavailable during this
        /// style pass, so a descendant's <c>calc(100% - 4px)</c> must stay <c>auto</c>
        /// rather than flatten against a basis we had to invent.
        /// </remarks>
        internal bool CbHeightKnown;

        /// <summary>
        /// Whether an ancestor establishes the containing block for fixed-position
        /// descendants (a transform, filter, contain and the like), so a fixed box here is
        /// not anchored to the initial containing block.
        /// </summary>
        internal bool InsideFixedCb;

        /// <summary>
        /// Whether <paramref name="other"/> holds the same values in every field, so the
        /// top-down pass of a subtree that receives either computes the same thing
        /// (<see cref="TopDownMemo"/>).
        /// </summary>
        internal bool SameAs(Inherited other) =>
            Display == other.Display
            && Direction == other.Direction
            && DisplayContents == other.DisplayContents
            && IsInlineBlock == other.IsInlineBlock
            && FlowRoot == other.FlowRoot
            && IsTableBox == other.IsTableBox
            && IsTableCellBox == other.IsTableCellBox
            && AuthoredTableDisplay == other.AuthoredTableDisplay
            && Nullable.Equals(BorderSpacing, other.BorderSpacing)
            && Nullable.Equals(Color, other.Color)
            && Nullable.Equals(FontSize, other.FontSize)
            && FontWeight == other.FontWeight
            && string.Equals(FontFamily, other.FontFamily, StringComparison.Ordinal)
            && string.Equals(FontFamilySpecified, other.FontFamilySpecified, StringComparison.Ordinal)
            && string.Equals(Cursor, other.Cursor, StringComparison.Ordinal)
            && string.Equals(PointerEvents, other.PointerEvents, StringComparison.Ordinal)
            && FontOpticalSizing == other.FontOpticalSizing
            && (ReferenceEquals(FontVariationSettings, other.FontVariationSettings)
                || FontVariationSettings.SequenceEqual(other.FontVariationSettings))
            && LetterSpacing.Equals(other.LetterSpacing)
            && LetterSpacingNonNormal == other.LetterSpacingNonNormal
            && ContainerType == other.ContainerType
            && (ReferenceEquals(ContainerNames, other.ContainerNames)
                || ContainerNames.SequenceEqual(other.ContainerNames, StringComparer.Ordinal))
            && Nullable.Equals(TextAlign, other.TextAlign)
            && Nullable.Equals(TextAlignLast, other.TextAlignLast)
            && TextIndent.Equals(other.TextIndent)
            && LegacyCenter == other.LegacyCenter
            && VisibilityHidden == other.VisibilityHidden
            && HasZeroOpacity == other.HasZeroOpacity
            && Equals(Svg, other.Svg)
            && ListStyle == other.ListStyle
            && LineHeight.Equals(other.LineHeight)
            && WhiteSpace == other.WhiteSpace
            && OverflowWrap == other.OverflowWrap
            && WordBreak == other.WordBreak
            && TextWrapStyle == other.TextWrapStyle
            && TextTransform == other.TextTransform
            && Italic == other.Italic
            && string.Equals(FontVariantCaps, other.FontVariantCaps, StringComparison.Ordinal)
            && FontStretch.Equals(other.FontStretch)
            && BoxSizing == other.BoxSizing
            && BorderCollapse == other.BorderCollapse
            && Nullable.Equals(TableVerticalAlign, other.TableVerticalAlign)
            && OverflowX == other.OverflowX
            && OverflowY == other.OverflowY
            && CbWidth.Equals(other.CbWidth)
            && CbHeightDefinite == other.CbHeightDefinite
            && CbHeight.Equals(other.CbHeight)
            && CbHeightKnown == other.CbHeightKnown
            && InsideFixedCb == other.InsideFixedCb;

        internal Inherited Clone() => new()
        {
            Display = Display,
            Direction = Direction,
            DisplayContents = DisplayContents,
            IsInlineBlock = IsInlineBlock,
            FlowRoot = FlowRoot,
            IsTableBox = IsTableBox,
            IsTableCellBox = IsTableCellBox,
            AuthoredTableDisplay = AuthoredTableDisplay,
            BorderSpacing = BorderSpacing,
            Color = Color,
            FontSize = FontSize,
            FontWeight = FontWeight,
            FontFamily = FontFamily,
            FontFamilySpecified = FontFamilySpecified,
            Cursor = Cursor,
            PointerEvents = PointerEvents,
            FontOpticalSizing = FontOpticalSizing,
            FontVariationSettings = FontVariationSettings,
            LetterSpacing = LetterSpacing,
            LetterSpacingNonNormal = LetterSpacingNonNormal,
            ContainerType = ContainerType,
            ContainerNames = ContainerNames,
            TextAlign = TextAlign,
            TextAlignLast = TextAlignLast,
            TextIndent = TextIndent,
            LegacyCenter = LegacyCenter,
            VisibilityHidden = VisibilityHidden,
            HasZeroOpacity = HasZeroOpacity,
            Svg = Svg,
            ListStyle = ListStyle,
            LineHeight = LineHeight,
            WhiteSpace = WhiteSpace,
            OverflowWrap = OverflowWrap,
            WordBreak = WordBreak,
            TextWrapStyle = TextWrapStyle,
            TextTransform = TextTransform,
            Italic = Italic,
            FontVariantCaps = FontVariantCaps,
            FontStretch = FontStretch,
            BoxSizing = BoxSizing,
            BorderCollapse = BorderCollapse,
            TableVerticalAlign = TableVerticalAlign,
            OverflowX = OverflowX,
            OverflowY = OverflowY,
            CbWidth = CbWidth,
            CbHeightDefinite = CbHeightDefinite,
            CbHeight = CbHeight,
            CbHeightKnown = CbHeightKnown,
            InsideFixedCb = InsideFixedCb,
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
        AnimationTimelineState animationTimeline,
        RetainedLayoutReuseCandidate? reuseCandidate = null,
        DomLayout? previousLayout = null,
        RetainedTaffyLayout? transplantSource = null,
        IReadOnlyList<RetainedStyleMutation>? layoutMutations = null)
    {
        Matcher matcher = tree.CreateMatcher();
        // Per-element maps are sized up front: growing them by doubling on a 50k-element page
        // left about 25 MB of discarded bucket arrays. About half a document's nodes are
        // elements; the rest are text. Capacity never changes what a map holds or the order
        // it enumerates in.
        int elementEstimate = (tree.SlotCount / 2) + 16;
        Dictionary<NodeId, LayoutStyle> styles = retained?.Maps.Styles ?? new(elementEstimate);
        Dictionary<NodeId, IReadOnlyDictionary<string, string>> customProperties =
            retained?.Maps.CustomProperties ?? new(elementEstimate);
        HashSet<NodeId>? freshStyles = retained?.Fresh;
        if (retained is not null)
        {
            DomPasses.RestorePaddingBeforeUsedSync(styles);
        }
        Dictionary<string, string> rootProps = new(StringComparer.Ordinal);
        ContainerQueryEvaluator? evaluator = snapshot is not null
            ? new ContainerQueryEvaluator(tree, snapshot)
            : null;

        // A doctype can only be a child of the document; listing every node of the document to
        // find it cost a whole-document walk per pass.
        bool quirksMode = true;
        foreach (NodeId id in tree.Children(tree.Document))
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
            VisitOnly = freshStyles is not null && RetainedTaffyLayout.Enabled
                ? DomCascade.StylePaths(tree, freshStyles)
                : null,
        };
        DomCascade.CascadeWalk(
            cascadeContext, tree.Document, sheet, matcher, rootProps, evaluator, null, false);
        LayoutPhaseProfile.Mark("cascade");
        DomCascade.ResolveCssCounters(tree, styles);
        LayoutPhaseProfile.Mark("counters");

        ContainerDecisionSignature? signature = null;
        ContainerQueryStats queryStats = default;
        if (evaluator is not null)
        {
            (signature, queryStats) = evaluator.Finish();
        }

        DomStyleFixups.GrowTrailingAutoCells(tree, styles);
        LayoutPhaseProfile.Mark("growcells");

        List<NodeId> descendants = tree.Descendants(tree.Document);
        bool needsEmojiFont = false;
        bool needsCjkFont = false;
        foreach (NodeId id in descendants)
        {
            if (tree.GetNode(id)?.TextContentOfTextNode is { } contents)
            {
                (bool emoji, bool cjk) = FontAssets.TextMayNeedOptionalFaces(contents);
                needsEmojiFont |= emoji;
                needsCjkFont |= cjk;
                if (needsEmojiFont && needsCjkFont)
                {
                    break;
                }
            }
        }

        if (!needsEmojiFont || !needsCjkFont)
        {
            string? lastFamily = null;
            foreach (LayoutStyle style in styles.Values)
            {
                if (style.BeforeContent is { } before)
                {
                    needsEmojiFont = needsEmojiFont || FontAssets.TextMayNeedEmojiFont(before);
                    needsCjkFont = needsCjkFont || FontAssets.TextMayNeedCjkFont(before);
                }

                if (style.AfterContent is { } after)
                {
                    needsEmojiFont = needsEmojiFont || FontAssets.TextMayNeedEmojiFont(after);
                    needsCjkFont = needsCjkFont || FontAssets.TextMayNeedCjkFont(after);
                }

                // A family list naming the CJK face selects it for Latin text too. Styles share
                // their inherited family string, so only a new one is looked at.
                if (!needsCjkFont
                    && style.FontFamily is { } family
                    && !ReferenceEquals(family, lastFamily))
                {
                    lastFamily = family;
                    needsCjkFont = FontAssets.FamilyNamesCjkFace(family);
                }

                if (needsEmojiFont && needsCjkFont)
                {
                    break;
                }
            }
        }

        LayoutPhaseProfile.Mark("fontscan");
        TaffyTree taffyTree = TaffyStyleMapping.NewTaffyTree<int?>(tree.SlotCount + 16);
        Dictionary<TaffyNodeId, NodeId> idMap = new(styles.Count);
        Dictionary<TaffyNodeId, (NodeId Source, string Word)> words = [];
        TextEngine engine = new(fonts, needsEmojiFont, needsCjkFont);
        LayoutPhaseProfile.Mark("engine");

        // A layout-affecting restyle cannot keep its layout, but shaping does not depend on
        // layout: it is a pure function of the text, its attributes and the tab width. Carry the
        // previous pass's shaped paragraphs over when the font set is unchanged.
        engine.AdoptShapeCache(previousLayout?.TextEngine);
        IfcRegistry ifcItems = new();
        TaffyNodeId? builtRoot = null;
        int transplanted = 0;

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

        Dictionary<NodeId, Rect> rects = new(styles.Count);
        Dictionary<NodeId, SubpixelRect> subpixelRects = [];
        Dictionary<NodeId, List<Rect>> inlineFragments = [];
        Dictionary<NodeId, List<(Rect Rect, string Text)>> textRuns = [];
        Dictionary<int, Rect> anonRects = [];
        Rect?[] generatedRects = [];
        Dictionary<NodeId, GridTrackSizes> gridTracks = [];

        TopDownMemo? currentTopDown = null;
        int topDownVisits = 0;
        if (root is { } rootId)
        {
            int rootGutters = styles.TryGetValue(rootId, out LayoutStyle? gutterStyle)
                ? Math.Min(gutterStyle.ScrollbarGutters, (byte)2)
                : 0;
            if (gutterStyle is not null)
            {
                // The root's gutter comes out of the initial containing block just below, so
                // the taffy mapping must not reserve it a second time on the root's own box.
                gutterStyle.GutterReservedByViewport = true;
            }
            float initialCbWidth = F32.Max(
                viewport.Width - (ClassicScrollbarGutter * rootGutters),
                0f);
            float initialCbX = rootGutters == 2 ? ClassicScrollbarGutter : 0f;

            float vw = viewport.Width / 100f;
            float vh = viewport.Height / 100f;
            float rootFs = ResolveRootFontSize(styles, rootId, vw, vh);

            // The root element's containing block is the initial containing block.
            Inherited rootInherited = new()
            {
                CbWidth = initialCbWidth,
                CbHeightDefinite = true,
                CbHeight = viewport.Height,
                CbHeightKnown = true,
            };

            // Computed definiteness after walking the real containing-block chain.
            // The top-down memo of the layout this one follows, when that layout's top-down pass
            // ran in the same context: same viewport and root font size, and a text engine with
            // the same fonts (the font units it stored on retained styles come from them).
            TopDownMemo? carriedTopDown = previousLayout?.TopDown;
            if (previousLayout is not null)
            {
                previousLayout.TopDown = null;
            }

            var topDownContext = (rootFs, vw, vh, viewport.Width, viewport.Height, initialCbWidth);
            TopDownMemo topDown = carriedTopDown is { } memo
                && freshStyles is not null
                && RetainedTaffyLayout.Enabled
                && memo.Context == topDownContext
                && engine.SharesShapeCacheWith(previousLayout!.TextEngine)
                && memo.Received.Count <= (2 * styles.Count) + 64
                    ? memo
                    : new TopDownMemo();
            topDown.Carried = ReferenceEquals(topDown, carriedTopDown);
            topDown.Context = topDownContext;
            HashSet<NodeId> definiteHeightNodes = topDown.DefiniteHeight;
            currentTopDown = topDown;
            // `ch` and `ex` are measured on the face the text engine selects, so the resolver
            // reads the same font database layout will shape with. One per pass: it memoizes the
            // face decision, which every element asks for.
            FontUnitResolver fontUnits = new(engine);
            topDownVisits = ResolveComputedValues(
                tree,
                rootId,
                styles,
                freshStyles,
                cascadeContext.VisitOnly,
                topDown,
                rootInherited,
                fontUnits,
                rootFs,
                vw,
                vh,
                viewport,
                initialCbWidth);

        LayoutPhaseProfile.Mark("topdown");
            LayoutPhaseProfile.Note("topdownVisits", topDownVisits);
            // Root/body overflow propagated to the viewport leaves the source element itself
            // overflow-visible for Taffy and BFC decisions.
            DomTransforms.MarkViewportOverflowSource(tree, rootId, styles);

            // Border-collapse is inherited, so only distribute a table's effective spacing
            // after the computed top-down values are known.
            DomStyleFixups.PropagateBorderSpacing(tree, styles);

            ApplyNativeControlSizes(tree, styles, engine);

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
                if (style.LogicalFloatClear != 0)
                {
                    DomBuild.ResolveLogicalFloatClear(style);
                }

                if (style.Position == TaffyPosition.Absolute || style.Float is not null)
                {
                    LayoutStyleExtensions.BlockifyOuterDisplay(style);
                }
            }

            DomStyleFixups.BlockifyGeneratedPseudos(styles);
        LayoutPhaseProfile.Mark("fixups");

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

            // The last point at which this pass's style objects are comparable to the ones the
            // offered layout was produced from: the cascade, the top-down pass and the style
            // fixups have all run, and nothing below reads a style without also writing layout
            // output back into it. If no element the cascade recomputed differs in any member a
            // later pass reads, that layout is still exact and none of the work below is worth
            // doing. See RetainedLayoutReuse for why this fails closed.
            if (reuseCandidate is { } candidate
                && RetainedLayoutReuse.ClassifyAll(candidate.Before, styles)
                    != RetainedRestyleImpact.Layout)
            {
                // The throwaway TextEngine this pass built is left to the GC, exactly as the
                // engine of every superseded layout already is; nothing in the engine disposes
                // one, and the faces it loaded are its own.
                DomPasses.ReapplyPaddingUsedByPreviousLayout(styles);
                candidate.Previous.Styles = styles;
                candidate.Previous.CustomProperties = customProperties;
                candidate.Previous.TopDown = topDown;
                return (candidate.Previous, signature, queryStats);
            }

            if (retained is not null)
            {
                DomPasses.ForgetPaddingUsedByPreviousLayout(styles);
            }

            // A retained style survives into the next layout, and whether its box still shows a
            // scrollbar is a property of that layout, not of the style.
            DomScrollbarPasses.ResetScrollbarGutters(styles);

            List<DeferredCyclicInlineSize> deferredCyclicInlineSizes =
                DomSubgridPasses.DeferCyclicFlexInlineSizes(tree, styles, rootFs, vw, vh);

            Dictionary<NodeId, Dimension> deferredInlineWidths = [];
            foreach (DeferredCyclicInlineSize entry in deferredCyclicInlineSizes)
            {
                if (entry.Slot != 0)
                {
                    continue;
                }

                // Only a plain percentage can be handed back as a typed width; a functional
                // expression has no Dimension spelling and stays neutralized.
                if (entry.SourceKind == DeferredCyclicInlineSourceKind.Percent)
                {
                    deferredInlineWidths[entry.Node] = Dimension.Percent(entry.Percent);
                }
            }

            // What the previous pass built that this one may take over: decided before the
            // build, which takes over that pass's inline items for unchanged containers.
            bool anyFloat = DomBuild.AnyFloat(styles);
            HashSet<NodeId>? dirtyNodes = null;
            if (transplantSource is { Consumed: false } source
                && freshStyles is not null
                && RetainedTaffyLayout.Enabled
                && engine.SharesShapeCacheWith(source.Engine))
            {
                dirtyNodes = RetainedTaffyLayout.DirtyClosure(
                    tree, source, freshStyles, layoutMutations ?? [], intrinsic, styles);
                source.Consume();

                // Not across a pass that gained or lost its floats, which builds the box tree
                // differently (RetainedTaffyLayout.Transplant carries nothing then either). An
                // item keeps nothing of the floats it was laid out beside that a later layout
                // reads without setting first (TextEngine.TakeAdoptedItem).
                if (anyFloat == source.HadFloats)
                {
                    engine.AdoptInlineItems(source.Engine, source.Whole, dirtyNodes);
                }
            }

            BuildContext buildContext = new()
            {
                Tree = tree,
                TaffyTree = taffyTree,
                IdMap = idMap,
                Words = words,
                Engine = engine,
                Ifc = ifcItems,
                Styles = styles,
                DeferredInlineWidths = deferredInlineWidths,
                HasFloats = anyFloat,
            };
            taffyTree.HasFloats = buildContext.HasFloats;
            taffyTree.FloatBlindCache = RetainedTaffyLayout.Enabled;

            TaffyNodeId? builtTaffyRoot = DomBuild.Build(buildContext, rootId);
            engine.EndInlineItemAdoption();
            if (builtTaffyRoot is { } taffyRoot)
            {
                LayoutPhaseProfile.Mark("build");
                if (buildContext.HasFloats)
                {
                    DomBuild.MarkBlockFormattingContextRoots(taffyTree, idMap, styles);
                }

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

                foreach ((TaffyNodeId taffyId, NodeId domId) in idMap)
                {
                    if (styles.TryGetValue(domId, out LayoutStyle? controlStyle)
                        && controlStyle.NativeControlContent is { } controlContent)
                    {
                        ifcItems.NativeControlContent[taffyId] =
                            new Layout.Size<float>(controlContent.Width, controlContent.Height);
                    }
                }

                builtRoot = taffyRoot;
                LayoutPhaseProfile.Mark("reparent");

                // The box tree is complete and nothing has been laid out yet: carry the previous
                // pass's layout results onto every box whose subtree is unchanged.
                if (transplantSource is { } carriedSource && dirtyNodes is not null)
                {
                    transplanted = RetainedTaffyLayout.Transplant(
                        carriedSource,
                        taffyTree,
                        taffyRoot,
                        idMap,
                        words,
                        ifcItems.NativeControlContent,
                        engine,
                        dirtyNodes);
                }

                LayoutPhaseProfile.Mark("transplant");
                LayoutPhaseProfile.Note("carried", transplanted);
                LayoutPhaseProfile.Note("items", engine.AdoptedItemCount);
                LayoutPhaseProfile.Note("boxes", taffyTree.TotalNodeCount());

                Layout.Size<float> Measure(
                    Layout.Size<float?> known,
                    Layout.Size<TaffyAvailableSpace> avail,
                    TaffyNodeId node,
                    int? ctx,
                    TaffyStyle _)
                {
                    if (ctx is { } index)
                    {
                        return engine.MeasureTaffy(index, known, avail);
                    }

                    if (!ifcItems.NativeControlContent.TryGetValue(node, out Layout.Size<float> content))
                    {
                        return new Layout.Size<float>(0f, 0f);
                    }

                    // Chromium contributes a control's size-based width to a max-content sizing
                    // pass but not to a min-content one: a text field is allowed to shrink below
                    // the box its `size` attribute asks for, so it must not raise a flex item's
                    // automatic minimum size.
                    float width = avail.Width.Kind == TaffyAvailableSpaceKind.MinContent ? 0f : content.Width;

                    return new Layout.Size<float>(known.Width ?? width, known.Height ?? content.Height);
                }

                if (taffyTree.HasFloats)
                {
                    taffyTree.ExclusionMeasure = (known, avail, node, ctx, style, bands, runMode) =>
                        ctx is { } index
                            ? engine.MeasureTaffyAroundFloats(index, known, avail, bands, runMode)
                            : Measure(known, avail, node, ctx, style);
                    taffyTree.ExclusionReset = ctx =>
                    {
                        if (ctx is { } index)
                        {
                            engine.ForgetFloatBands(index);
                        }
                    };
                    taffyTree.FloatAnchors = ifcItems.FloatAnchors;
                    taffyTree.AnchorLines = (ctx, offsets) => ctx is { } index
                        ? engine.AnchorLines(index, offsets)
                        : new (float Top, float Height, float Width, float Used)?[offsets.Length];
                }

                float? IntrinsicWidth(TaffyTree t, TaffyNodeId node, TaffyAvailableSpace width)
                {
                    t.ComputeLayoutWithMeasure(
                        node,
                        new Layout.Size<TaffyAvailableSpace>(width, TaffyAvailableSpace.MaxContent),
                        Measure);
                    return t.GetLayout(node).Size.Width;
                }

                float? IntrinsicHeight(TaffyTree t, TaffyNodeId node, float width)
                {
                    t.ComputeLayoutWithMeasure(
                        node,
                        new Layout.Size<TaffyAvailableSpace>(
                            TaffyAvailableSpace.Definite(width),
                            TaffyAvailableSpace.MaxContent),
                        Measure);
                    return t.GetLayout(node).Size.Height;
                }

                ApplyTableUsedWidths(
                    tree,
                    taffyTree,
                    taffyRoot,
                    idMap,
                    styles,
                    ifcItems,
                    initialCbWidth,
                    available,
                    deferredCyclicInlineSizes,
                    Measure);

        LayoutPhaseProfile.Mark("tables");
                // The cyclic-percentage neutralization above collapses the very content a flex
                // item's automatic minimum size is measured from, so that floor is re-derived
                // from the typed percentages before the first layout reads it.
                DomSubgridPasses.ApplyDeferredFlexAutomaticMinimums(
                    taffyTree, idMap, styles, deferredCyclicInlineSizes, IntrinsicWidth);

                taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
        LayoutPhaseProfile.Mark("taffy1");
                if (deferredCyclicInlineSizes.Count == 0
                    && DomPasses.ApplyIntrinsicInlineSizes(
                        taffyTree, idMap, styles, initialCbWidth, IntrinsicWidth))
                {
                    taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                }

                // Block-axis keywords measure at the used inline size, so they follow the
                // inline pass and its relayout.
                if (DomPasses.ApplyIntrinsicBlockSizes(taffyTree, idMap, styles, IntrinsicHeight))
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

                List<PinnedFlexItem> pinnedFlexItems = [];
                DomSubgridPasses.ResolveDeferredFlexInlineSizes(
                    tree,
                    taffyTree,
                    idMap,
                    styles,
                    deferredCyclicInlineSizes,
                    rootFs,
                    vw,
                    vh,
                    pinnedFlexItems,
                    ifcItems.TableGridCells,
                    (t, resolvedStyles, phase) =>
                    {
                        if (phase == DeferredFlexReflowPhase.Layout)
                        {
                            t.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                        }
                        else if (DomPasses.ApplyIntrinsicInlineSizes(
                            t, idMap, resolvedStyles, initialCbWidth, IntrinsicWidth))
                        {
                            t.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                        }
                    });

                // A deferred cyclic inline size skips the pass above, so the block-axis
                // keywords under it are only measurable once those widths have settled.
                if (deferredCyclicInlineSizes.Count != 0
                    && DomPasses.ApplyIntrinsicBlockSizes(taffyTree, idMap, styles, IntrinsicHeight))
                {
                    taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                }

                if (DomPasses.ApplyMulticolBalance(taffyTree, ifcItems.Multicol))
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

                // A classic scrollbar takes space out of its scroll container, and whether an
                // `overflow: auto` box shows one is only knowable from a settled layout - so
                // this follows every intrinsic, table and fragmentation repair, for the same
                // reason the static-position harvest below does. The pass only ever adds
                // gutters, so the loop terminates; two rounds cover a vertical bar narrowing a
                // box into needing a horizontal one, and the third covers a box that overflows
                // only once the re-resolution below has widened something under it.
                for (int pass = 0; pass < 3; pass++)
                {
                    if (!DomScrollbarPasses.ApplyScrollbarGutters(taffyTree, idMap, styles))
                    {
                        break;
                    }

                    taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);

                    // A cyclic flex item was pinned to the width the flex algorithm chose for
                    // it before any of that width was taken away, and its flex factors were
                    // frozen with it, so nothing re-derives the pin. Carry it onto the narrowed
                    // container before the expressions below are resolved against it. Outermost
                    // first, one round per level: scaling an outer item is what gives the
                    // container of the next level down its new width.
                    for (int repin = 0; repin < 3; repin++)
                    {
                        if (!DomSubgridPasses.RescalePinnedFlexItems(taffyTree, pinnedFlexItems))
                        {
                            break;
                        }

                        taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                    }

                    // A calc() inline size under a cyclic flex item was flattened to a px length
                    // against the containing block as it stood before the gutter was taken out of
                    // it, and unlike a bare percentage - which reaches taffy typed and re-resolves
                    // on its own - nothing puts it right. Re-resolve those expressions against the
                    // narrowed box. A no-op costs one dictionary walk and no reflow.
                    DomSubgridPasses.ReresolveFunctionalInlineSizes(
                        tree,
                        taffyTree,
                        idMap,
                        styles,
                        deferredCyclicInlineSizes,
                        rootFs,
                        vw,
                        vh,
                        ifcItems.TableGridCells,
                        (t, _, phase) =>
                        {
                            if (phase == DeferredFlexReflowPhase.Layout)
                            {
                                t.ComputeLayoutWithMeasure(taffyRoot, available, Measure);
                            }
                        });
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
        LayoutPhaseProfile.Mark("repairs");
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
                    generatedRects,
                    initialCbX,
                    0f,
                    subpixelRects);

        LayoutPhaseProfile.Mark("absrects");
                // The used track sizes getComputedStyle() reports for a grid container.
                foreach ((TaffyNodeId taffyId, NodeId domId) in idMap)
                {
                    if (taffyTree.GetDetailedLayoutInfo(taffyId) is Layout.DetailedGridInfo gridInfo)
                    {
                        gridTracks[domId] = GridTrackSizes.From(gridInfo);
                    }
                }

                inlineFragments = SynthesizeOrdinaryInlineFragments(rects, styles, engine);
                DomTableSupport.SynthesizeRowRects(tree, rects);
        LayoutPhaseProfile.Mark("frags");
            }
        }

        DomPasses.SyncPositionedPseudoPercentagePadding(rects, styles);

        Dictionary<NodeId, OverflowClip?> clipRects = new(styles.Count);
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

        LayoutPhaseProfile.Mark("clips");
        FinalizeShapedItems(
            engine, ifcItems, rects, styles, clipRects, translates, anonRects, viewport);
        LayoutPhaseProfile.Mark("finalize");

        // Pure-text IFC descendants do not own Taffy nodes. Once their shared buffer has its
        // final line breaks, derive their real continuations from shaping provenance.
        if (engine.HasInlineOwners())
        {
            Dictionary<NodeId, Rect?> replacedRects = [];
            Dictionary<NodeId, List<Rect>> canonical =
                SynthesizeShapedInlineFragments(tree, rects, styles, engine, replacedRects);
            LayoutPhaseProfile.Mark("synth");
            if (canonical.Count != 0)
            {
                Dictionary<NodeId, (float X, float Y)> relativeOffsets = [];
                Dictionary<NodeId, (float X, float Y)?> relativeMemo = [];
                foreach (NodeId owner in canonical.Keys)
                {
                    if (FoldedInlineRelativeOffset(tree, owner, rects, styles, relativeMemo) is { } offset
                        && offset != (0f, 0f))
                    {
                        relativeOffsets[owner] = offset;
                    }
                }

                engine.SetInlineOwnerOffsets(relativeOffsets);
                LayoutPhaseProfile.Mark("ownerOffsets");
                foreach ((NodeId owner, List<Rect> fragments) in canonical)
                {
                    inlineFragments[owner] = fragments;
                }

                if (root is { } reclipRootId
                    && ClipWalkReadsReplacedRects(replacedRects, rects, styles, reclipRootId, viewport))
                {
                    clipRects.Clear();
                    translates.Clear();
                    transforms.Clear();
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

                LayoutPhaseProfile.Mark("reclip");
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

        if (ifcItems.SplicedInlines.Count != 0)
        {
            SynthesizeSplicedInlineRects(tree, ifcItems.SplicedInlines, rects, styles, textRuns);
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

        Dictionary<NodeId, Rect> svgRects = [];
        SvgBoxes.Measure(tree, styles, rects, svgRects);
        LayoutPhaseProfile.Mark("inlinesynth+svg");

        DomLayout layout = new()
        {
            Rects = rects,
            SubpixelRects = subpixelRects,
            SvgRects = svgRects,
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
            GridTracks = gridTracks,
            TransplantedBoxes = transplanted,
            TopDown = currentTopDown,
            TopDownVisits = topDownVisits,
            AdoptedInlineItems = engine.AdoptedItemCount,
            RetainedBoxes = builtRoot is { } keptRoot
                ? new RetainedTaffyLayout
                {
                    Tree = taffyTree,
                    Root = keptRoot,
                    IdMap = idMap,
                    Words = words,
                    NativeControlContent = ifcItems.NativeControlContent,
                    Engine = engine,
                    Whole = ifcItems.Whole,
                    HadFloats = taffyTree.HasFloats,
                    Intrinsic = new Dictionary<NodeId, ReplacedIntrinsic>(intrinsic),
                    Generated = RetainedTaffyLayout.SnapshotGenerated(styles),
                }
                : null,
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
    /// Whether the clip/transform walk can come out differently now that
    /// <c>SynthesizeShapedInlineFragments</c> replaced the rects in <paramref name="replaced"/>.
    /// </summary>
    /// <remarks>
    /// <c>ResolveClipRects</c> reads a node's rect for exactly three things: its own translate
    /// and transform (percentages and the transform origin resolve against it) and, when it
    /// clips, its overflow clip. An inline owner that does none of these, or whose new rect
    /// resolves them to the same bits, leaves the walk's output unchanged, so running it a
    /// second time over a large page (it visits every node) bought nothing. Not in
    /// crates/obscura-render, which always walks twice.
    /// </remarks>
    private static bool ClipWalkReadsReplacedRects(
        Dictionary<NodeId, Rect?> replaced,
        Dictionary<NodeId, Rect> rects,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        NodeId root,
        (float Width, float Height) viewport)
    {
        float rootFontSize = styles.TryGetValue(root, out LayoutStyle? rootStyle) ? rootStyle.FontSize ?? 16f : 16f;
        foreach ((NodeId owner, Rect? before) in replaced)
        {
            if (!styles.TryGetValue(owner, out LayoutStyle? style)
                || !rects.TryGetValue(owner, out Rect after))
            {
                return true;
            }

            if (style.OverflowHidden)
            {
                return true;
            }

            Rect old = before ?? default;
            (float X, float Y) oldTranslate = DomTransforms.ResolvedOwnTranslate(style, old, rootFontSize, viewport);
            (float X, float Y) newTranslate = DomTransforms.ResolvedOwnTranslate(style, after, rootFontSize, viewport);
            Affine2 oldMatrix = DomTransforms.ResolvedTransformMatrix(style, old, rootFontSize, viewport);
            Affine2 newMatrix = DomTransforms.ResolvedTransformMatrix(style, after, rootFontSize, viewport);
            if (!SameBits(oldTranslate.X, newTranslate.X)
                || !SameBits(oldTranslate.Y, newTranslate.Y)
                || !SameBits(oldMatrix.A, newMatrix.A)
                || !SameBits(oldMatrix.B, newMatrix.B)
                || !SameBits(oldMatrix.C, newMatrix.C)
                || !SameBits(oldMatrix.D, newMatrix.D)
                || !SameBits(oldMatrix.E, newMatrix.E)
                || !SameBits(oldMatrix.F, newMatrix.F))
            {
                return true;
            }
        }

        return false;

        static bool SameBits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
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

    /// <summary>
    /// Give a spliced inline wrapper that owns no shaped range the union of its content.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-render/src/dom.rs, where a decoration-free inline wrapper
    /// spliced out of a block's child list has no layout box at all, so
    /// <c>getBoundingClientRect()</c> reported 0,0,0,0 and a click by coordinates could not
    /// reach it. Where its run folds into a shaped item it gets its line fragments from
    /// shaping; where the run does not fold (it holds an image or a form control) this takes
    /// the union of what its children laid out to. Chromium 141 reports
    /// <c>&lt;a&gt;&lt;img width=20 height=20&gt;&lt;/a&gt;</c> as the image's width over the
    /// link's font box, 20x17; this gives the image's box, 20x20. <paramref name="spliced"/>
    /// lists outer wrappers before inner ones, so walking it backwards sees every nested
    /// wrapper's union before its parent reads it, and each child is visited once.
    /// </remarks>
    internal static void SynthesizeSplicedInlineRects(
        DomTree tree,
        List<NodeId> spliced,
        Dictionary<NodeId, Rect> rects,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        Dictionary<NodeId, List<(Rect Rect, string Text)>> textRuns)
    {
        for (int index = spliced.Count - 1; index >= 0; index--)
        {
            NodeId wrapper = spliced[index];
            if (rects.ContainsKey(wrapper))
            {
                continue;
            }

            WorkCancellation.ThrowIfCancellationRequested();
            float left = float.PositiveInfinity;
            float top = float.PositiveInfinity;
            float right = float.NegativeInfinity;
            float bottom = float.NegativeInfinity;
            void Include(Rect rect)
            {
                left = F32.Min(left, rect.X);
                top = F32.Min(top, rect.Y);
                right = F32.Max(right, rect.X + rect.Width);
                bottom = F32.Max(bottom, rect.Y + rect.Height);
            }

            for (NodeId? child = tree.GetNode(wrapper)?.FirstChild;
                child is { } cid;
                child = tree.GetNode(cid)?.NextSibling)
            {
                if (textRuns.TryGetValue(cid, out List<(Rect Rect, string Text)>? words))
                {
                    foreach ((Rect rect, _) in words)
                    {
                        Include(rect);
                    }
                }
                else if (rects.TryGetValue(cid, out Rect rect)
                    && styles.TryGetValue(cid, out LayoutStyle? style)
                    && style.Display != Display.None
                    && style.Position != TaffyPosition.Absolute)
                {
                    Include(rect);
                }
            }

            if (left <= right && top <= bottom)
            {
                rects[wrapper] = new Rect(left, top, right - left, bottom - top);
            }
        }
    }

    internal static Dictionary<NodeId, List<Rect>> SynthesizeShapedInlineFragments(
        DomTree tree,
        Dictionary<NodeId, Rect> rects,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        TextEngine engine,
        Dictionary<NodeId, Rect?>? replacedRects = null)
    {
        Dictionary<NodeId, List<((int Item, int Line) Order, Rect Rect)>> fragments = [];
        Dictionary<NodeId, (float X, float Y)?> relativeMemo = [];
        Dictionary<NodeId, (float Ascent, float Descent)> fontBoxes = [];
        foreach (InlineOwnerLineFragment shaped in engine.InlineOwnerLineFragments())
        {
            if (!styles.TryGetValue(shaped.Owner, out LayoutStyle? style))
            {
                continue;
            }

            // Per owner, not per fragment: an owner open across many lines has one per line.
            if (!fontBoxes.TryGetValue(shaped.Owner, out (float Ascent, float Descent) fontBox))
            {
                fontBox = engine.InlineFontBoxMetrics(style);
                fontBoxes[shaped.Owner] = fontBox;
            }

            (float ascent, float descent) = fontBox;
            if (FoldedInlineRelativeOffset(tree, shaped.Owner, rects, styles, relativeMemo) is not { } relative)
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

            replacedRects?.TryAdd(owner, rects.TryGetValue(owner, out Rect before) ? before : null);
            rects[owner] = union;
            canonical[owner] = ordered;
        }

        return canonical;
    }

    /// <remarks>
    /// <paramref name="memo"/> caches results across calls with the same geometry. The walk up
    /// the inline ancestors is O(depth), and callers ask for every owner fragment of every
    /// line, which made nested inline boxes cubic. A node reached with nothing accumulated yet
    /// continues exactly as its own walk would, so every such node on a walk shares its result.
    /// </remarks>
    internal static (float X, float Y)? FoldedInlineRelativeOffset(
        DomTree tree,
        NodeId owner,
        IReadOnlyDictionary<NodeId, Rect> rects,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        Dictionary<NodeId, (float X, float Y)?>? memo = null)
    {
        if (memo is null)
        {
            return FoldedInlineRelativeOffsetWalk(tree, owner, rects, styles, null, null);
        }

        if (memo.TryGetValue(owner, out (float X, float Y)? cached))
        {
            return cached;
        }

        List<NodeId> path = [];
        (float X, float Y)? result = FoldedInlineRelativeOffsetWalk(tree, owner, rects, styles, memo, path);
        foreach (NodeId id in path)
        {
            memo[id] = result;
        }

        return result;
    }

    private static (float X, float Y)? FoldedInlineRelativeOffsetWalk(
        DomTree tree,
        NodeId owner,
        IReadOnlyDictionary<NodeId, Rect> rects,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        Dictionary<NodeId, (float X, float Y)?>? memo,
        List<NodeId>? path)
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
                            - style.UsedBorder.Left
                            - style.UsedBorder.Right
                            - style.Padding.Left
                            - style.Padding.Right,
                            0f),
                        F32.Max(
                            rect.Height
                            - style.UsedBorder.Top
                            - style.UsedBorder.Bottom
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
            if (path is not null && offset.X == 0f && offset.Y == 0f)
            {
                if (id != owner && memo!.TryGetValue(id, out (float X, float Y)? known))
                {
                    return known;
                }

                path.Add(id);
            }

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
                    - style.UsedBorder.Top
                    - style.UsedBorder.Bottom;
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
