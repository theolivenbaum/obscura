using System.Diagnostics;
using System.Text;
using Obscura.Dom;
using Obscura.Render.Layout;
using NodeId = Obscura.Dom.NodeId;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>
/// Owns the font set and shaping caches for one render pass, plus every inline formatting
/// context discovered while building the tree.
/// </summary>
/// <remarks>
/// Port of the Rust <c>inline::TextEngine</c>. Shaping is HarfBuzz where the Rust engine uses
/// rustybuzz through cosmic-text; rasterization is Skia where the Rust engine uses swash. The
/// observable contract - line break positions, run splitting, advance widths, ascent/descent/
/// line-height, and glyph positions - is reproduced rather than the internals.
/// </remarks>
public sealed partial class TextEngine : IDisposable
{
    /// <summary>Marks a measure context as a replaced box rather than an inline item.</summary>
    private const int ReplacedContextBit = unchecked((int)0x8000_0000);

    private readonly FontDatabase _database = new();
    private readonly Dictionary<string, LoadedFamily> _loadedFamilies = new(StringComparer.Ordinal);
    private readonly List<InlineItem> _items = [];
    private readonly List<ReplacedItem> _replaced = [];
    private readonly TextShaper _shaper;
    private readonly GlyphRasterizer _rasterizer;
    private readonly VariableGlyphCache _variableCache;

    public TextEngine()
        : this([], loadEmoji: false)
    {
    }

    public TextEngine(IReadOnlyList<byte[]> fonts)
        : this(ToWebFonts(fonts), loadEmoji: false)
    {
    }

    public TextEngine(IReadOnlyList<WebFont> fonts, bool loadEmoji)
    {
        // Build a database from embedded and page-provided faces. The host's font set is never
        // consulted: it would make layout differ machine to machine and add a multi-millisecond
        // startup scan.
        List<(FontId Id, string? Family, (ushort Min, ushort Max)? Weight, bool? Italic)> declarations = [];
        foreach (string stem in FontAssets.BundledFaceFiles)
        {
            foreach (FontId id in _database.LoadFontSource(FontAssets.Load(stem)))
            {
                declarations.Add((id, null, null, null));
            }
        }

        if (loadEmoji)
        {
            foreach (FontId id in _database.LoadFontSource(FontAssets.Load(FontAssets.EmojiFaceFile)))
            {
                declarations.Add((id, null, null, null));
            }
        }

        foreach (WebFont font in fonts)
        {
            foreach (FontId id in _database.LoadFontSource(font.Data))
            {
                declarations.Add((id, font.Family, font.Weight, font.Italic));
            }
        }

        foreach ((FontId id, string? declaredFamily, (ushort Min, ushort Max)? declaredWeight, bool? declaredItalic)
            in declarations)
        {
            FaceRecord? face = _database.Face(id);
            if (face is null)
            {
                continue;
            }

            string internalName = face.FamilyName;
            bool italic = declaredItalic ?? face.Style != FaceStyle.Normal;
            (ushort Min, ushort Max) weight = declaredWeight ?? (face.Weight, face.Weight);
            List<string> declaredNames = declaredFamily is null ? [internalName] : [declaredFamily];
            foreach (string name in declaredNames)
            {
                string key = name.ToLowerInvariant();
                if (!_loadedFamilies.TryGetValue(key, out LoadedFamily? family))
                {
                    family = new LoadedFamily();
                    _loadedFamilies[key] = family;
                }

                family.Faces.Add(new LoadedFace(
                    internalName,
                    id,
                    face.Metrics,
                    weight.Min,
                    weight.Max,
                    italic));
            }
        }

        _shaper = new TextShaper(_database);
        _rasterizer = new GlyphRasterizer(_database);
        _variableCache = new VariableGlyphCache(_database);
    }

    private static List<WebFont> ToWebFonts(IReadOnlyList<byte[]> fonts)
    {
        List<WebFont> result = new(fonts.Count);
        foreach (byte[] data in fonts)
        {
            result.Add(new WebFont { Data = data });
        }

        return result;
    }

    /// <summary>Every loaded CSS family, keyed by its lowercase name.</summary>
    public IReadOnlyDictionary<string, LoadedFamily> LoadedFamilies => _loadedFamilies;

    /// <summary>The font database backing shaping and rasterization.</summary>
    public FontDatabase Database => _database;

    /// <summary>The inline formatting contexts collected so far.</summary>
    public IReadOnlyList<InlineItem> Items => _items;

    /// <summary>The variable-instance raster cache. Tests inspect the axis tuples it holds.</summary>
    public GlyphRasterizer Rasterizer => _rasterizer;

    public VariableGlyphCache VariableCache => _variableCache;

    /// <summary>Number of inline formatting contexts collected (for debug/stats).</summary>
    public int Count => _items.Count;

    public bool IsEmpty => _items.Count == 0;

    /// <summary>
    /// Raw selected-font box used by ordinary inline fragments. This is deliberately not CSS
    /// <c>line-height</c>: leading belongs to the containing line and must not enlarge
    /// backgrounds, borders, or DOM client rects.
    /// </summary>
    public float InlineFontBoxHeight(LayoutStyle style)
    {
        (float ascent, float descent) = InlineFontBoxMetrics(style);
        return ascent + descent;
    }

    public (float Ascent, float Descent) InlineFontBoxMetrics(LayoutStyle style)
    {
        ResolvedFont font = FontResolution.ResolveLoadedFont(
            style.FontFamily,
            ComputedStyle.UsedFontWeight(style),
            style.FontStyleItalic ?? false,
            _loadedFamilies);
        return FontAssets.FittedFontBoxMetrics(style.FontSize ?? 16f, font.Metrics);
    }

    /// <summary>
    /// Used line-height for the same selected face, so layout can distribute leading around the
    /// raw fragment using one font decision.
    /// </summary>
    public float SelectedLineHeight(LayoutStyle style)
    {
        ResolvedFont font = FontResolution.ResolveLoadedFont(
            style.FontFamily,
            ComputedStyle.UsedFontWeight(style),
            style.FontStyleItalic ?? false,
            _loadedFamilies);
        return FontResolution.UsedLineHeightForFont(style, font);
    }

    /// <summary>The shaped text of one item, concatenated across its buffer lines.</summary>
    public string ItemText(int index)
    {
        var text = new StringBuilder();
        foreach (BufferLine line in _items[index].Buffer.Lines)
        {
            text.Append(line.Text);
        }

        return text.ToString();
    }

    /// <summary>
    /// Build an inline formatting context for <paramref name="id"/>'s subtree if it is one that
    /// collapses to shaped text; returns the item index to store as the leaf's measure context.
    /// </summary>
    /// <remarks>
    /// <c>null</c> means the container is not a pure-text IFC and should build through the
    /// normal (block / flex / word-split) path.
    /// </remarks>
    public int? TryBuild(DomTree tree, NodeId id, IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        if (!Inline.IsPureTextIfc(tree, id, styles))
        {
            return null;
        }

        if (!styles.TryGetValue(id, out LayoutStyle? style))
        {
            return null;
        }

        var collector = new Collector();
        ResolvedFont font = FontResolution.ResolveLoadedFont(
            style.FontFamily,
            ComputedStyle.UsedFontWeight(style),
            style.FontStyleItalic ?? false,
            _loadedFamilies);
        SpanCtx context = BaseSpanCtx(style, font, collector);
        float lineHeight = context.LineHeight;
        List<(string Text, SpanAttrs Attrs)> spans = [];
        CollectSpans(tree, id, styles, context, spans, collector);
        return PushShapedItem(style, lineHeight, spans, collector);
    }

    /// <summary>
    /// Build an inline formatting context from a run of consecutive inline-level siblings
    /// inside <paramref name="parent"/>.
    /// </summary>
    /// <remarks>
    /// The run folds to one shaped buffer exactly like a whole-container IFC, using the parent's
    /// style as the base. Returns <c>null</c> when any node in the run cannot fold or the run
    /// has no visible text; the caller then falls back to the flex-wrap wrapper for that run.
    /// </remarks>
    public int? TryBuildRun(
        DomTree tree,
        NodeId parent,
        IReadOnlyList<NodeId> run,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        bool hasText = false;
        foreach (NodeId cid in run)
        {
            if (!Inline.InlineChildOk(tree, cid, styles, ref hasText))
            {
                return null;
            }
        }

        if (!hasText)
        {
            return null;
        }

        if (!styles.TryGetValue(parent, out LayoutStyle? style))
        {
            return null;
        }

        var collector = new Collector();
        ResolvedFont font = FontResolution.ResolveLoadedFont(
            style.FontFamily,
            ComputedStyle.UsedFontWeight(style),
            style.FontStyleItalic ?? false,
            _loadedFamilies);
        SpanCtx context = BaseSpanCtx(style, font, collector);
        float lineHeight = context.LineHeight;
        List<(string Text, SpanAttrs Attrs)> spans = [];
        foreach (NodeId cid in run)
        {
            CollectNodeSpans(tree, cid, styles, context, spans, collector);
        }

        return PushShapedItem(style, lineHeight, spans, collector);
    }

    /// <summary>
    /// Shape generated text that owns a positioned pseudo box.
    /// </summary>
    /// <remarks>
    /// Positioned <c>::before</c>/<c>::after</c> boxes do not participate in the layout tree,
    /// but their text still uses the same authored webfonts, variable weight selection,
    /// transformations, and glyph rasterizer as an ordinary inline formatting context.
    /// </remarks>
    public int? PushGeneratedText(string text, LayoutStyle style)
    {
        var collector = new Collector();
        ResolvedFont font = FontResolution.ResolveLoadedFont(
            style.FontFamily,
            ComputedStyle.UsedFontWeight(style),
            style.FontStyleItalic ?? false,
            _loadedFamilies);
        SpanCtx context = BaseSpanCtx(style, font, collector);
        float lineHeight = context.LineHeight;
        SpanAttrs attrs = context.ToSpanAttrs();
        List<(string Text, SpanAttrs Attrs)> spans = [];
        Inline.PushText(text, context.Transform, context.WhiteSpace, attrs, spans, collector);
        return PushShapedItem(style, lineHeight, spans, collector);
    }

    /// <summary>
    /// Shared tail of the build entry points: shape the collected spans under the base style's
    /// font metrics and alignment, and store the result as a new inline item.
    /// </summary>
    private int? PushShapedItem(
        LayoutStyle baseStyle,
        float lineHeight,
        List<(string Text, SpanAttrs Attrs)> spans,
        Collector collector)
    {
        List<ClipTextFill> clipFills = collector.ClipFills;
        List<OwnerTextRange> ownerRanges = collector.OwnerRanges;
        List<InlineOwnerBox> ownerBoxes = collector.OwnerBoxes;
        List<InlineBoundaryEvent> boundaryEvents = collector.BoundaryEvents;

        WhiteSpace whiteSpace = baseStyle.WhiteSpace ?? WhiteSpace.Normal;
        Wrap layoutWrap = Wrap.Word;
        Wrap minContentWrap = Wrap.Word;
        foreach ((string _, SpanAttrs attrs) in spans)
        {
            if (attrs.HasLayoutEmergencyBreaks)
            {
                layoutWrap = Wrap.WordOrGlyph;
            }

            if (attrs.HasMinContentEmergencyBreaks)
            {
                minContentWrap = Wrap.WordOrGlyphMinContent;
            }
        }

        bool collapsible = whiteSpace is WhiteSpace.Normal or WhiteSpace.NoWrap or WhiteSpace.PreLine;

        // Collapsible trailing whitespace does not widen the last line.
        if (collapsible && spans.Count > 0 && spans[^1].Text.EndsWith(' '))
        {
            spans[^1] = (spans[^1].Text[..^1], spans[^1].Attrs);
        }

        if (ownerBoxes.Count == 0)
        {
            bool allEmpty = true;
            foreach ((string text, SpanAttrs _) in spans)
            {
                bool empty = text.Length == 0
                    || (collapsible && text.Trim().Length == 0 && !text.Contains('\n', StringComparison.Ordinal));
                if (!empty)
                {
                    allEmpty = false;
                    break;
                }
            }

            if (allEmpty)
            {
                return null;
            }
        }

        int textLength = 0;
        foreach ((string text, SpanAttrs _) in spans)
        {
            textLength += text.Length;
        }

        for (int i = ownerRanges.Count - 1; i >= 0; i--)
        {
            OwnerTextRange range = ownerRanges[i] with
            {
                Start = Math.Min(ownerRanges[i].Start, textLength),
                End = Math.Min(ownerRanges[i].End, textLength),
            };
            if (range.Start < range.End)
            {
                ownerRanges[i] = range;
            }
            else
            {
                ownerRanges.RemoveAt(i);
            }
        }

        for (int i = 0; i < ownerBoxes.Count; i++)
        {
            ownerBoxes[i] = ownerBoxes[i] with
            {
                Start = Math.Min(ownerBoxes[i].Start, textLength),
                End = Math.Min(ownerBoxes[i].End, textLength),
            };
        }

        for (int i = 0; i < boundaryEvents.Count; i++)
        {
            boundaryEvents[i] = boundaryEvents[i] with
            {
                Position = Math.Min(boundaryEvents[i].Position, textLength),
            };
        }

        string? ownerText = null;
        if (ownerBoxes.Count > 0)
        {
            var builder = new StringBuilder(textLength);
            foreach ((string text, SpanAttrs _) in spans)
            {
                builder.Append(text);
            }

            ownerText = builder.ToString();
        }

        float baseSize = baseStyle.FontSize ?? 16f;

        int forcedBreaks = 0;
        bool visibleAfterLastBreak = false;
        foreach ((string text, SpanAttrs _) in spans)
        {
            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    forcedBreaks++;
                    visibleAfterLastBreak = false;
                }
                else if (!char.IsWhiteSpace(ch))
                {
                    visibleAfterLastBreak = true;
                }
            }
        }

        int forcedLines = forcedBreaks + (forcedBreaks > 0 && visibleAfterLastBreak ? 1 : 0);
        if (ownerBoxes.Count != 0)
        {
            forcedLines = Math.Max(forcedLines, 1);
        }

        float forcedMinHeight = forcedLines * F32.Max(lineHeight, 1f);

        // The Rust engine floors font size and line height at 1px because cosmic-text asserts
        // (an uncatchable process abort) when either is zero, and `font-size:0` is a common
        // whitespace-collapse trick. Keep the floor: it is observable geometry, not a guard.
        float shapedSize = F32.Max(baseSize, 1f);
        var metrics = new TextMetrics(shapedSize, F32.Max(lineHeight, 1f));
        var buffer = new TextBuffer(metrics);
        buffer.SetWrap(layoutWrap);

        List<FontVariations> variationSets = [];
        foreach ((string _, SpanAttrs attrs) in spans)
        {
            if (attrs.Variations is { } variations)
            {
                bool seen = false;
                foreach (FontVariations existing in variationSets)
                {
                    if (existing.Equals(variations))
                    {
                        seen = true;
                        break;
                    }
                }

                if (!seen)
                {
                    variationSets.Add(variations);
                }
            }
        }

        int? lineClamp = null;
        if (baseStyle.WebkitBoxDisplay is not null && baseStyle.WebkitBoxOrientVertical
            && baseStyle.WebkitLineClamp is { } clamp)
        {
            lineClamp = (int)clamp;
        }

        bool ellipsisOverflow = baseStyle.TextOverflow == TextOverflow.Ellipsis && baseStyle.ClipsOverflowX();
        SpanAttrs? markerAttrs = (lineClamp is not null || ellipsisOverflow) && spans.Count > 0
            ? spans[^1].Attrs
            : null;

        var rich = new List<(string Text, TextAttrs Attrs)>(spans.Count);
        foreach ((string text, SpanAttrs attrs) in spans)
        {
            int variationIndex = 0;
            if (attrs.Variations is { } variations)
            {
                for (int i = 0; i < variationSets.Count; i++)
                {
                    if (variationSets[i].Equals(variations))
                    {
                        variationIndex = i + 1;
                        break;
                    }
                }
            }

            rich.Add((text, attrs.ToAttrs(variationIndex)));
        }

        var defaults = new TextAttrs { Family = FontAssets.SansFamily };
        buffer.SetRichText(rich, defaults);

        TextBuffer? markerBuffer = null;
        if (markerAttrs is { } marker)
        {
            int variationIndex = 0;
            if (marker.Variations is { } variations)
            {
                for (int i = 0; i < variationSets.Count; i++)
                {
                    if (variationSets[i].Equals(variations))
                    {
                        variationIndex = i + 1;
                        break;
                    }
                }
            }

            markerBuffer = new TextBuffer(metrics);
            markerBuffer.SetWrap(Wrap.None);
            markerBuffer.SetRichText([("…", marker.ToAttrs(variationIndex))], defaults);
            markerBuffer.SetSize(null, null);
            markerBuffer.ShapeUntilScroll(_shaper);
        }

        Align? align = baseStyle.TextAlign is { } textAlign
            ? textAlign.GetKeyword() switch
            {
                AlignItemsKeyword.Center => Align.Center,
                AlignItemsKeyword.FlexEnd => Align.End,
                _ => null,
            }
            : null;
        if (align is { } alignment)
        {
            foreach (BufferLine line in buffer.Lines)
            {
                line.SetAlign(alignment);
            }
        }

        int index = _items.Count;
        Dimension textIndent = baseStyle.TextIndent ?? Dimension.Px(0f);
        bool zeroIndent = textIndent.Kind == DimensionKind.Px && textIndent.Value == 0f;
        TextBuffer? sourceBuffer = !zeroIndent || boundaryEvents.Count > 0 ? buffer.Clone() : null;

        _items.Add(new InlineItem
        {
            Buffer = buffer,
            LayoutWrap = layoutWrap,
            MinContentWrap = minContentWrap,
            SourceBuffer = sourceBuffer,
            TextIndent = textIndent,
            FirstLineOffset = 0f,
            BalanceWrap = baseStyle.TextWrapStyle == TextWrapStyle.Balance,
            Align = align,
            ForcedMinHeight = forcedMinHeight,
            Origin = (0f, 0f),
            Clip = null,
            ClipFills = clipFills,
            VariationSets = variationSets,
            LineClamp = lineClamp,
            EllipsisOverflow = ellipsisOverflow,
            MarkerBuffer = markerBuffer,
            Marker = null,
            OwnerText = ownerText,
            OwnerRanges = ownerRanges,
            OwnerBoxes = ownerBoxes,
            BoundaryEvents = boundaryEvents,
            RelativeOwnerRanges = [],
        });
        return index;
    }

    /// <summary>
    /// Measure the inline context <paramref name="index"/> at <paramref name="width"/>
    /// (content-box width, or <c>null</c> for max-content), returning its shaped size.
    /// </summary>
    public (float Width, float Height) Measure(int index, float? width)
    {
        if ((index & ReplacedContextBit) != 0)
        {
            Size<float> size = _replaced[index & ~ReplacedContextBit].Size(new Size<float?>(width, null));
            return (size.Width, size.Height);
        }

        return MeasureTextWithWrap(index, width, _items[index].LayoutWrap);
    }

    private (float Width, float Height) MeasureTextWithWrap(int index, float? width, Wrap wrap)
    {
        InlineItem item = _items[index];
        ShapeWithTextIndent(item, width, wrap);
        (float shapedWidth, float height, bool clamped) = InlineGeometry.BufferSize(item);
        return (shapedWidth, clamped ? height : F32.Max(height, item.ForcedMinHeight));
    }

    /// <summary>
    /// Exact max-content size for one fallback word item.
    /// </summary>
    /// <remarks>
    /// Paragraph IFCs keep their historical integer-ceiled intrinsic width, but word boxes need
    /// the selected webfont's fractional advance or every token accumulates a pixel of
    /// horizontal drift against browser geometry.
    /// </remarks>
    /// <summary>
    /// Max-content width of a form control's label, shaped through exactly the path that
    /// will lay the label out, so the control's box fits its own text.
    /// </summary>
    /// <remarks>
    /// <c>&lt;button&gt;</c> sizes its auto width from its label in the control pass, but the
    /// label itself is real inline content shaped here. Measuring it with
    /// <see cref="DomTextMeasure.TextWidth"/> instead mixed two notions of font size:
    /// ab_glyph's <c>PxScale</c> is height-based (hhea ascender minus descender), the
    /// shaper's is em-based, and Liberation Sans is 2288 units against a 2048 em. The box
    /// came out 10.5% narrower than the text it had to hold, so a two-word label wrapped
    /// inside its own button while layout still reported a single line.
    /// <para>
    /// The item is pushed only to be shaped and is removed again, so it cannot reach layout
    /// or paint. Letter spacing is applied by the shaper, so callers must not add it twice.
    /// </para>
    /// </remarks>
    public float MeasureControlLabel(string text, LayoutStyle style)
    {
        if (text.Length == 0)
        {
            return 0f;
        }

        if (PushGeneratedText(text, style) is not { } index)
        {
            return 0f;
        }

        float width = MeasureWord(index).Width;
        Debug.Assert(index + 1 == _items.Count, "measurement item must be last");
        _items.RemoveRange(index, _items.Count - index);
        return F32.Max(width, 0f);
    }

    public (float Width, float Height) MeasureWord(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            return (0f, 0f);
        }

        InlineItem item = _items[index];
        ShapeWithTextIndent(item, null, item.LayoutWrap);
        float width = 0f;
        float height = 0f;
        List<int> starts = item.OwnerText is { } source
            ? InlineGeometry.SourceLineStarts(item.Buffer, source)
            : [];
        int lineIndex = 0;
        foreach (LayoutRun run in item.Buffer.LayoutRuns())
        {
            float offset = lineIndex == 0 ? item.FirstLineOffset : 0f;
            int lineStart = run.LineIndex < starts.Count ? starts[run.LineIndex] : 0;
            int lineEnd = lineStart + run.Text.Length;
            width = F32.Max(
                width,
                F32.Max(run.LineW + offset + InlineGeometry.LineEdgeAdvance(item, lineStart, lineEnd), 0f));
            height = F32.Max(height, run.LineTop + run.LineHeight);
            lineIndex++;
        }

        return (width, F32.Max(height, item.ForcedMinHeight));
    }

    /// <summary>
    /// Register a replaced element's intrinsic size as a measure context.
    /// </summary>
    /// <remarks>
    /// Percentage-sized image leaves still need their intrinsic max-content contribution while
    /// an auto-sized ancestor is measured.
    /// </remarks>
    public int RegisterReplaced(float width, float height, LayoutStyle style) =>
        RegisterReplacedIntrinsic(ReplacedIntrinsic.FromDimensions(width, height), style);

    internal int RegisterReplacedIntrinsic(ReplacedIntrinsic intrinsic, LayoutStyle style)
    {
        int index = _replaced.Count;
        _replaced.Add(ReplacedItem.FromIntrinsic(intrinsic, style));
        return ReplacedContextBit | index;
    }

    /// <summary>
    /// Measure either a shaped text context or an intrinsic replaced element.
    /// </summary>
    /// <remarks>
    /// Replaced boxes transfer a definite axis through their intrinsic ratio; with neither axis
    /// definite they contribute their natural size.
    /// </remarks>
    public Size<float> MeasureTaffy(int index, Size<float?> known, Size<AvailableSpace> available)
    {
        if ((index & ReplacedContextBit) != 0)
        {
            ReplacedItem replaced = _replaced[index & ~ReplacedContextBit];
            float? stretchWidth = null;
            if (replaced.RatioOnly
                && replaced.PreferredWidth is null
                && replaced.PreferredHeight is null
                && known.Width is null)
            {
                stretchWidth = available.Width.Kind == AvailableSpaceKind.Definite
                    ? F32.Max(available.Width.Unwrap(), 0f)
                    : replaced.RatioOnlyAvailableWidth;
            }

            Size<float> size = replaced.Size(new Size<float?>(known.Width ?? stretchWidth, known.Height));
            if (replaced.ZeroInlineMinContent
                && known.Width is null
                && available.Width.Kind == AvailableSpaceKind.MinContent)
            {
                size.Width = 0f;
            }

            return size;
        }

        bool minContentQuery = known.Width is null && available.Width.Kind == AvailableSpaceKind.MinContent;
        float? width = known.Width ?? available.Width.Kind switch
        {
            AvailableSpaceKind.Definite => available.Width.Unwrap(),
            AvailableSpaceKind.MinContent => 0f,
            _ => (float?)null,
        };

        Wrap wrap = minContentQuery ? _items[index].MinContentWrap : _items[index].LayoutWrap;
        (float measuredWidth, float measuredHeight) = MeasureTextWithWrap(index, width, wrap);
        return new Size<float>(measuredWidth, measuredHeight);
    }

    /// <summary>
    /// After layout, pin each context to its final content-box origin and clip, reshaping once
    /// at the resolved width so paint draws the same line breaks the box was sized for.
    /// </summary>
    public void Finalize(int index, (float X, float Y) contentOrigin, float contentWidth, Rect? clip)
    {
        InlineItem item = _items[index];
        contentWidth = F32.Max(contentWidth, 0f);
        ShapeWithTextIndent(item, contentWidth, item.LayoutWrap);
        float balancedWidth = item.BalanceWrap
            ? BalanceWrapWidth(item.Buffer, contentWidth) ?? contentWidth
            : contentWidth;
        float alignmentInset = F32.Max(contentWidth - balancedWidth, 0f) * item.Align switch
        {
            Align.Center => 0.5f,
            Align.End or Align.Right => 1f,
            _ => 0f,
        };

        item.Marker = null;
        float? markerWidth = null;
        if (item.MarkerBuffer is { } markerBuffer)
        {
            foreach (LayoutRun run in markerBuffer.LayoutRuns())
            {
                markerWidth = run.LineW;
                break;
            }
        }

        if (markerWidth is { } markerLineWidth)
        {
            int nonempty = 0;
            (int LineIndex, float LineY, float ContentEnd)? clampTarget = null;
            bool clampHasFollowingLine = false;
            (int LineIndex, float LineY, float ContentEnd)? overflowTarget = null;
            int lineIndex = 0;
            foreach (LayoutRun run in item.Buffer.LayoutRuns())
            {
                if (run.Glyphs.Count == 0)
                {
                    lineIndex++;
                    continue;
                }

                nonempty++;
                float lineOffset = lineIndex == 0 ? item.FirstLineOffset : 0f;
                float contentEnd = 0f;
                foreach (LayoutGlyph glyph in run.Glyphs)
                {
                    contentEnd = F32.Max(contentEnd, glyph.X + glyph.W + lineOffset);
                }

                if (item.LineClamp == nonempty)
                {
                    clampTarget = (lineIndex, run.LineY, contentEnd);
                }
                else if (item.LineClamp is { } limit && nonempty > limit)
                {
                    clampHasFollowingLine = true;
                }

                if (overflowTarget is null && item.EllipsisOverflow && contentEnd > contentWidth + 0.01f)
                {
                    overflowTarget = (lineIndex, run.LineY, contentEnd);
                }

                lineIndex++;
            }

            ((int LineIndex, float LineY, float ContentEnd) Target, bool IsClamp)? target =
                clampHasFollowingLine
                    ? clampTarget is { } clamp ? (clamp, true) : null
                    : overflowTarget is { } overflow ? (overflow, false) : null;

            if (target is { } chosen)
            {
                float availableMarkerStart = F32.Max(contentWidth - markerLineWidth, 0f);
                float markerX = chosen.IsClamp
                    ? F32.Min(chosen.Target.ContentEnd, availableMarkerStart)
                    : availableMarkerStart;
                float markerLineY = 0f;
                if (item.MarkerBuffer is { } buffer)
                {
                    foreach (LayoutRun run in buffer.LayoutRuns())
                    {
                        markerLineY = run.LineY;
                        break;
                    }
                }

                item.Marker = new MarkerPlacement(
                    chosen.Target.LineIndex,
                    markerX,
                    chosen.Target.LineY - markerLineY,
                    markerX);
            }
        }

        item.Origin = (contentOrigin.X + alignmentInset, contentOrigin.Y);
        item.Clip = clip;
    }

    /// <summary>
    /// Replace only the finalized clip without reshaping. Used when canonical inline fragment
    /// rects make a second clip/transform tree walk necessary.
    /// </summary>
    public void SetClip(int index, Rect? clip)
    {
        if (index >= 0 && index < _items.Count)
        {
            _items[index].Clip = clip;
        }
    }

    public bool HasInlineOwners()
    {
        foreach (InlineItem item in _items)
        {
            if (item.OwnerBoxes.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Install cumulative used offsets for relative ordinary-inline owners.
    /// </summary>
    /// <remarks>
    /// DOM resolves percentages against the real containing block. Selecting the deepest
    /// matching range at paint time is therefore sufficient even for nested relative inlines:
    /// its offset already includes its ancestors.
    /// </remarks>
    public void SetInlineOwnerOffsets(IReadOnlyDictionary<NodeId, (float X, float Y)> offsets)
    {
        foreach (InlineItem item in _items)
        {
            List<RelativeOwnerTextRange> ranges = [];
            foreach (OwnerTextRange range in item.OwnerRanges)
            {
                if (offsets.TryGetValue(range.Owner, out (float X, float Y) offset)
                    && offset != (0f, 0f))
                {
                    ranges.Add(new RelativeOwnerTextRange(range.Start, range.End, offset));
                }
            }

            item.RelativeOwnerRanges = ranges;
        }
    }

    /// <summary>
    /// Derive ordinary inline continuation extents from finalized shaping.
    /// </summary>
    /// <remarks>
    /// Provenance is a sidecar rather than glyph metadata, so changing DOM owners never creates
    /// a font-shaping boundary or disables ligatures.
    /// </remarks>
    public List<InlineOwnerLineFragment> InlineOwnerLineFragments()
    {
        List<InlineOwnerLineFragment> output = [];
        for (int itemIndex = 0; itemIndex < _items.Count; itemIndex++)
        {
            InlineItem item = _items[itemIndex];
            if (item.OwnerText is not { } source)
            {
                continue;
            }

            List<int> lineStarts = InlineGeometry.SourceLineStarts(item.Buffer, source);
            int lineIndex = 0;
            foreach (LayoutRun run in item.Buffer.LayoutRuns())
            {
                if (run.LineIndex >= lineStarts.Count)
                {
                    lineIndex++;
                    continue;
                }

                int lineStart = lineStarts[run.LineIndex];
                int lineEnd = lineStart + run.Text.Length;
                float firstLineOffset = lineIndex == 0 ? item.FirstLineOffset : 0f;
                float alignmentShift = InlineGeometry.LineEdgeAlignmentShift(item, lineStart, lineEnd);
                foreach (InlineOwnerBox owner in item.OwnerBoxes)
                {
                    bool empty = owner.Start == owner.End;
                    bool intersects = empty
                        ? (owner.Start >= lineStart && owner.Start < lineEnd)
                            || (lineEnd == source.Length && owner.Start == lineEnd)
                        : owner.Start < lineEnd && owner.End > lineStart;
                    if (!intersects)
                    {
                        continue;
                    }

                    bool first = (owner.Start >= lineStart && owner.Start < lineEnd) || empty;
                    bool last = (owner.End > lineStart && owner.End <= lineEnd) || empty;
                    float rawLeft = first
                        ? InlineGeometry.RunCursorX(run, Math.Max(0, owner.Start - lineStart))
                            + InlineGeometry.LineAdvanceBeforeEvent(item, owner.StartEvent, lineStart, lineEnd)
                            + owner.StartEdge.Margin
                        : InlineGeometry.RunCursorX(run, 0);
                    float rawRight = last
                        ? InlineGeometry.RunCursorX(run, Math.Max(0, owner.End - lineStart))
                            + InlineGeometry.LineAdvanceBeforeEvent(item, owner.EndEvent, lineStart, lineEnd)
                            + owner.EndEdge.BorderPadding
                        : run.LineW + InlineGeometry.LineEdgeAdvance(item, lineStart, lineEnd);
                    float x = item.Origin.X + firstLineOffset + alignmentShift + rawLeft;
                    float width = F32.Max(rawRight - rawLeft, 0f);
                    float baselineY = item.Origin.Y + run.LineY;

                    int existing = -1;
                    for (int i = 0; i < output.Count; i++)
                    {
                        if (output[i].Owner == owner.Owner
                            && output[i].ItemIndex == itemIndex
                            && output[i].LineIndex == lineIndex)
                        {
                            existing = i;
                            break;
                        }
                    }

                    if (existing >= 0)
                    {
                        InlineOwnerLineFragment fragment = output[existing];
                        float left = F32.Min(fragment.X, x);
                        float right = F32.Max(fragment.X + fragment.Width, x + width);
                        output[existing] = fragment with { X = left, Width = F32.Max(right - left, 0f) };
                    }
                    else
                    {
                        output.Add(new InlineOwnerLineFragment(
                            owner.Owner,
                            itemIndex,
                            lineIndex,
                            x,
                            baselineY,
                            F32.Max(width, 0f)));
                    }
                }

                lineIndex++;
            }
        }

        return output;
    }

    /// <summary>
    /// Shape one IFC with first-line indent and ordinary-inline boundary advances.
    /// </summary>
    /// <remarks>
    /// The pristine buffer remains one shaping stream across DOM owners. For a definite width
    /// the natural wrap boundary is probed first, the ordered margin/border/padding events that
    /// fall on that candidate line are subtracted, and the probe monotonically retries if those
    /// edges force an earlier break. Only final visual-line boundaries split buffer lines, so
    /// ligatures and kerning are not broken merely because an inline element starts or ends.
    /// </remarks>
    private void ShapeWithTextIndent(InlineItem item, float? width, Wrap wrap)
    {
        float indent = InlineGeometry.UsedTextIndent(item.TextIndent, width);
        item.FirstLineOffset = indent * item.Align switch
        {
            Align.Center => 0.5f,
            Align.End or Align.Right => 0f,
            _ => 1f,
        };

        if (item.SourceBuffer is not { } source)
        {
            item.Buffer.SetWrap(wrap);
            item.Buffer.SetSize(width is { } value ? F32.Max(value, 0f) : null, null);
            item.Buffer.ShapeUntilScroll(_shaper);
            return;
        }

        item.Buffer = source.Clone();
        item.Buffer.SetWrap(wrap);

        if (width is { } fullWidth && item.OwnerText is { } sourceText)
        {
            TextMetrics metrics = item.Buffer.Metrics;
            float? mono = item.Buffer.MonospaceWidth;
            int tabWidth = item.Buffer.TabWidth;
            int lineIndex = 0;
            while (lineIndex < item.Buffer.Lines.Count)
            {
                List<int> starts = InlineGeometry.SourceLineStarts(item.Buffer, sourceText);
                int globalStart = lineIndex < starts.Count ? starts[lineIndex] : 0;
                float firstIndent = lineIndex == 0 ? indent : 0f;
                float baseAvailable = F32.Max(fullWidth - firstIndent, 0f);

                // Negative inline margins can admit content that would not fit in the text-only
                // probe. Begin at the widest possible candidate and retain the same monotonic
                // decrease used for positive padding/border advances.
                int lineSourceEnd = globalStart + item.Buffer.Lines[lineIndex].Text.Length;
                float negativeEdges = 0f;
                foreach (InlineBoundaryEvent evt in item.BoundaryEvents)
                {
                    if (evt.Position >= globalStart && evt.Position <= lineSourceEnd)
                    {
                        negativeEdges += F32.Min(evt.Edge.Advance, 0f);
                    }
                }

                float available = F32.Max(baseAvailable - negativeEdges, 0f);
                int? split = null;

                for (int attempt = 0; attempt <= item.BoundaryEvents.Count; attempt++)
                {
                    BufferLine line = item.Buffer.Lines[lineIndex];
                    List<LayoutLine> layouts = line.Layout(_shaper, metrics.FontSize, available, wrap, mono, tabWidth);
                    if (layouts.Count == 0)
                    {
                        break;
                    }

                    LayoutLine layout = layouts[0];
                    int? candidate = null;
                    foreach (LayoutGlyph glyph in layout.Glyphs)
                    {
                        candidate = candidate is { } current ? Math.Max(current, glyph.End) : glyph.End;
                    }

                    if (candidate is not { } candidateEndLocal)
                    {
                        break;
                    }

                    float lineWidth = layout.W;
                    int candidateEnd = globalStart + candidateEndLocal;
                    float edges = InlineGeometry.LineEdgeAdvance(item, globalStart, candidateEnd);
                    float requiredAvailable = F32.Max(baseAvailable - edges, 0f);
                    split = candidateEndLocal;
                    if (lineWidth + edges <= baseAvailable + 0.01f || requiredAvailable + 0.01f >= available)
                    {
                        break;
                    }

                    available = requiredAvailable;
                    item.Buffer.Lines[lineIndex].ResetLayout();
                }

                if (split is not { } splitAt)
                {
                    lineIndex++;
                    continue;
                }

                string text = item.Buffer.Lines[lineIndex].Text;
                splitAt = SkipTrailingWhitespace(text, splitAt);
                if (splitAt > 0 && splitAt < text.Length)
                {
                    BufferLine tail = item.Buffer.Lines[lineIndex].SplitOff(splitAt);
                    item.Buffer.Lines.Insert(lineIndex + 1, tail);
                }

                lineIndex++;
            }
        }
        else if (width is { } indentWidth)
        {
            item.Buffer.SetSize(F32.Max(indentWidth - indent, 0f), null);
            item.Buffer.ShapeUntilScroll(_shaper);
            int? firstBreak = null;
            foreach (LayoutRun run in item.Buffer.LayoutRuns())
            {
                foreach (LayoutGlyph glyph in run.Glyphs)
                {
                    firstBreak = firstBreak is { } current ? Math.Max(current, glyph.End) : glyph.End;
                }

                break;
            }

            item.Buffer = source.Clone();
            item.Buffer.SetWrap(wrap);
            if (firstBreak is { } split && item.Buffer.Lines.Count > 0)
            {
                string text = item.Buffer.Lines[0].Text;
                split = SkipTrailingWhitespace(text, split);
                if (split > 0 && split < text.Length)
                {
                    BufferLine tail = item.Buffer.Lines[0].SplitOff(split);
                    item.Buffer.Lines.Insert(1, tail);
                }
            }
        }

        item.Buffer.SetSize(width is { } finalWidth ? F32.Max(finalWidth, 0f) : null, null);
        item.Buffer.ShapeUntilScroll(_shaper);
    }

    private static int SkipTrailingWhitespace(string text, int split)
    {
        while (split < text.Length && char.IsWhiteSpace(text[split]))
        {
            split++;
        }

        return split;
    }

    /// <summary>
    /// Chromium's bisection implementation only balances paragraphs of at most six lines.
    /// Keeping the same cap bounds repeated shaping on long body copy and confines this work to
    /// the heading-sized content the property targets.
    /// </summary>
    private const int MaxBalancedLines = 6;

    /// <summary>
    /// Tighten the buffer to the narrowest (within one CSS pixel) wrapping width that retains
    /// its natural line count.
    /// </summary>
    /// <remarks>
    /// This is deliberately a final-shaping operation: intrinsic measurement and the block's
    /// used geometry remain unchanged, while the line grouping changes.
    /// </remarks>
    private float? BalanceWrapWidth(TextBuffer buffer, float availableWidth)
    {
        List<LayoutRun> natural = [.. buffer.LayoutRuns()];
        int lineCount = natural.Count;
        if (lineCount < 2 || lineCount > MaxBalancedLines)
        {
            return null;
        }

        // Blink's bisection path is inapplicable to forced breaks; balancing each hard-break
        // delimited paragraph needs separate segment constraints.
        if (buffer.Lines.Count > 1)
        {
            return null;
        }

        float total = 0f;
        foreach (LayoutRun run in natural)
        {
            total += run.LineW;
        }

        float averageLineWidth = total / lineCount;
        float lower = Math.Clamp(averageLineWidth * 0.8f, 0f, availableWidth);
        float upper = availableWidth;
        while (lower + 1f < upper)
        {
            float middle = (lower + upper) * 0.5f;
            buffer.SetSize(middle, null);
            buffer.ShapeUntilScroll(_shaper);
            int count = 0;
            foreach (LayoutRun _ in buffer.LayoutRuns())
            {
                count++;
            }

            if (count == lineCount)
            {
                upper = middle;
            }
            else
            {
                lower = middle;
            }
        }

        buffer.SetSize(upper, null);
        buffer.ShapeUntilScroll(_shaper);
        return upper;
    }

    /// <summary>
    /// Whether one shaped inline item can contribute ink to the destination surface.
    /// </summary>
    /// <remarks>
    /// Rasterizing a glyph before its callback can reject out-of-bounds pixels makes viewport
    /// screenshots scale with the full page height, so this test runs first. It is deliberately
    /// conservative: line boxes are expanded by four ems for unusual font ink bounds, and every
    /// authored relative-inline offset participates in the envelope.
    /// </remarks>
    internal static bool InlineItemMayIntersectSurface(
        InlineItem item,
        (float X, float Y) offset,
        Rect? clip,
        uint surfaceHeight,
        float rasterScale)
    {
        if (!float.IsFinite(rasterScale) || rasterScale <= 0f || surfaceHeight == 0)
        {
            return false;
        }

        float surfaceBottom = surfaceHeight / rasterScale;
        if (clip is { } bounds && (bounds.Y >= surfaceBottom || bounds.Y + bounds.Height <= 0f))
        {
            return false;
        }

        float lineTop = float.PositiveInfinity;
        float lineBottom = float.NegativeInfinity;
        float maxFontSize = 0f;
        foreach (LayoutRun run in item.Buffer.LayoutRuns())
        {
            lineTop = F32.Min(lineTop, run.LineTop);
            lineBottom = F32.Max(lineBottom, run.LineTop + run.LineHeight);
            foreach (LayoutGlyph glyph in run.Glyphs)
            {
                if (float.IsFinite(glyph.FontSize))
                {
                    maxFontSize = F32.Max(maxFontSize, F32.Max(glyph.FontSize, 0f));
                }
            }
        }

        if (!float.IsFinite(lineTop) || !float.IsFinite(lineBottom))
        {
            return false;
        }

        // Some fonts have ink well outside their ascender/descender metrics. Four ems on each
        // side is intentionally much larger than normal glyph ink, while still culling text
        // that is genuinely pages away from the viewport.
        float inkGuard = MathF.FusedMultiplyAdd(maxFontSize, 4f, 4f);
        float relativeTop = 0f;
        float relativeBottom = 0f;
        foreach (RelativeOwnerTextRange range in item.RelativeOwnerRanges)
        {
            if (float.IsFinite(range.Offset.Y))
            {
                relativeTop = F32.Min(relativeTop, range.Offset.Y);
                relativeBottom = F32.Max(relativeBottom, range.Offset.Y);
            }
        }

        float originY = item.Origin.Y + offset.Y;
        float visualTop = originY + lineTop + relativeTop - inkGuard;
        float visualBottom = originY + lineBottom + relativeBottom + inkGuard;
        return visualTop < surfaceBottom && visualBottom > 0f;
    }

    private SpanCtx BaseSpanCtx(LayoutStyle baseStyle, ResolvedFont font, Collector collector)
    {
        int? clipFill = null;
        if (Inline.ClipTextFillFor(baseStyle) is { } fill)
        {
            clipFill = collector.ClipFills.Count;
            collector.ClipFills.Add(fill);
        }

        FontVariations? variations = Inline.ResolvedFontVariations(baseStyle);
        float lineHeight = FontResolution.UsedLineHeightForFont(baseStyle, font);
        return new SpanCtx
        {
            FontSize = baseStyle.FontSize ?? 16f,
            LineHeight = lineHeight,
            LetterSpacing = baseStyle.LetterSpacing ?? 0f,
            LetterSpacingNonNormal = baseStyle.LetterSpacingNonNormal ?? false,
            Color = baseStyle.Color ?? new RgbaColor(0, 0, 0, 255),
            Weight = ComputedStyle.UsedFontWeight(baseStyle),
            OpticalSizing = baseStyle.FontOpticalSizing ?? FontOpticalSizing.Auto,
            FontId = font.FontId,
            FontMetrics = font.Metrics,
            Variations = variations,
            Italic = baseStyle.FontStyleItalic ?? false,
            SyntheticItalic = font.SyntheticItalic,
            Underline = baseStyle.Underline ?? false,
            Transform = baseStyle.TextTransform ?? TextTransform.None,
            WhiteSpace = baseStyle.WhiteSpace ?? WhiteSpace.Normal,
            OverflowWrap = baseStyle.OverflowWrap ?? OverflowWrap.Normal,
            WordBreak = baseStyle.WordBreak ?? WordBreak.Normal,
            Family = font.Family,
            ClipFill = clipFill,
        };
    }

    private void CollectSpans(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        SpanCtx context,
        List<(string Text, SpanAttrs Attrs)> output,
        Collector collector)
    {
        foreach (NodeId cid in Inline.RenderedChildren(tree, id))
        {
            CollectNodeSpans(tree, cid, styles, context, output, collector);
        }
    }

    /// <summary>
    /// Collect the spans contributed by one node: a text node's runs, or an element's whole
    /// subtree with its style threaded through.
    /// </summary>
    private void CollectNodeSpans(
        DomTree tree,
        NodeId cid,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        SpanCtx context,
        List<(string Text, SpanAttrs Attrs)> output,
        Collector collector)
    {
        Node? node = tree.GetNode(cid);
        if (node is null)
        {
            return;
        }

        if (node.Data is TextData text)
        {
            Inline.PushText(text.Contents, context.Transform, context.WhiteSpace, context.ToSpanAttrs(), output, collector);
            return;
        }

        if (node.AsElement() is not { } element)
        {
            return;
        }

        styles.TryGetValue(cid, out LayoutStyle? style);
        if (style is not null && style.Display == Display.None)
        {
            return;
        }

        if (string.Equals(element.Name.Local, "br", StringComparison.Ordinal))
        {
            output.Add(("\n", context.ToSpanAttrs()));
            collector.TextLength += 1;
            collector.LastWasSpace = true;
            return;
        }

        bool ownsInlineFragment = style is not null && style.IgnoresUsedBoxSizes() && !style.DisplayContents;
        if (ownsInlineFragment)
        {
            collector.BeginOwner(cid, style!);
        }

        int? ownClipFill = null;
        if (style is not null && Inline.ClipTextFillFor(style) is { } fill)
        {
            ownClipFill = collector.ClipFills.Count;
            collector.ClipFills.Add(fill);
        }

        RgbaColor color = style?.Color ?? context.Color;

        // A descendant with its own clip-text background replaces the inherited fill.
        // Transparent descendants otherwise continue an ancestor's fill; an opaque text color
        // paints normally.
        int? clipFill = ownClipFill ?? (color.A == 0 ? context.ClipFill : null);
        ushort requestedWeight = style is not null ? ComputedStyle.UsedFontWeight(style) : context.Weight;
        ResolvedFont font;
        if (style?.FontFamily is { } family)
        {
            font = FontResolution.ResolveLoadedFont(
                family,
                requestedWeight,
                style.FontStyleItalic ?? context.Italic,
                _loadedFamilies);
        }
        else
        {
            font = new ResolvedFont(context.Family, context.FontId, context.FontMetrics, context.SyntheticItalic);
        }

        FontVariations? variations = style is not null
            ? Inline.ResolvedFontVariations(style)
            : context.Variations;

        var child = new SpanCtx
        {
            FontSize = style?.FontSize ?? context.FontSize,
            LineHeight = style is not null
                ? FontResolution.UsedLineHeightForFont(style, font)
                : context.LineHeight,
            LetterSpacing = style?.LetterSpacing ?? context.LetterSpacing,
            LetterSpacingNonNormal = style?.LetterSpacingNonNormal ?? context.LetterSpacingNonNormal,
            Color = color,
            Weight = requestedWeight,
            OpticalSizing = style?.FontOpticalSizing ?? context.OpticalSizing,
            FontId = font.FontId,
            FontMetrics = font.Metrics,
            Variations = variations,
            Italic = context.Italic || (style?.FontStyleItalic ?? false),
            SyntheticItalic = font.SyntheticItalic,
            // Underline propagates in: an ancestor's underline covers descendant text; an
            // element only sets its own via CSS.
            Underline = context.Underline || (style?.Underline ?? false),
            Transform = style?.TextTransform ?? context.Transform,
            WhiteSpace = style?.WhiteSpace ?? context.WhiteSpace,
            OverflowWrap = style?.OverflowWrap ?? context.OverflowWrap,
            WordBreak = style?.WordBreak ?? context.WordBreak,
            Family = font.Family,
            ClipFill = clipFill,
        };

        CollectSpans(tree, cid, styles, child, output, collector);
        if (ownsInlineFragment)
        {
            collector.EndOwner(cid);
        }
    }

    public void Dispose()
    {
        _rasterizer.Dispose();
        _database.Dispose();
    }
}
