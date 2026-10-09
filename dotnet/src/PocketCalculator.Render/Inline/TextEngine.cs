using System.Diagnostics;
using System.Text;
using PocketCalculator.Dom;
using PocketCalculator.Render.Layout;
using NodeId = PocketCalculator.Dom.NodeId;
using RgbaColor = PocketCalculator.Render.Css.RgbaColor;

namespace PocketCalculator.Render;

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
    private readonly WebFont[] _fonts;
    private readonly FontDirectorySet _directoryFonts;
    private readonly GlyphRasterizer _rasterizer;
    private readonly VariableGlyphCache _variableCache;
    private readonly bool _loadsEmoji;
    private readonly bool _loadsCjk;

    public TextEngine()
        : this([], loadEmoji: false)
    {
    }

    public TextEngine(IReadOnlyList<byte[]> fonts)
        : this(ToWebFonts(fonts), loadEmoji: false)
    {
    }

    public TextEngine(IReadOnlyList<WebFont> fonts, bool loadEmoji, bool loadCjk = false)
        : this(fonts, loadEmoji, FontDirectories.Current, loadCjk)
    {
    }

    internal TextEngine(
        IReadOnlyList<WebFont> fonts,
        bool loadEmoji,
        FontDirectorySet directoryFonts,
        bool loadCjk = false)
    {
        // Build a database from embedded and page-provided faces. The host's font set is never
        // consulted implicitly: it would make layout differ machine to machine and add a
        // multi-millisecond startup scan. The one exception is a font directory the operator
        // configured (upstream `--font-dir`), which is empty unless asked for.
        List<(FontId Id, string? Family, (ushort Min, ushort Max)? Weight, bool? Italic)> declarations = [];
        foreach (string stem in FontAssets.BundledFaceFiles)
        {
            foreach (FontId id in _database.LoadFontSource(FontAssets.Load(stem)))
            {
                declarations.Add((id, null, null, null));
            }
        }

        // Directory faces join the base set after the embedded faces and before the emoji face,
        // the order upstream's base_font_database builds: they register under their own family
        // names, and a name an embedded face already has gains them as later faces.
        foreach (byte[] data in directoryFonts.Data)
        {
            foreach (FontId id in _database.LoadFontSource(data))
            {
                declarations.Add((id, null, null, null));
            }
        }

        _directoryFonts = directoryFonts;

        // The two large faces are read in place and shared across passes (FontAssets.Embedded),
        // and each joins only a pass whose text needs it.
        _loadsEmoji = loadEmoji;
        if (loadEmoji)
        {
            foreach (FontId id in _database.LoadEmbeddedSource(FontAssets.Embedded(FontAssets.EmojiFaceFile)))
            {
                declarations.Add((id, null, null, null));
            }
        }

        // DEVIATION from crates/obscura-render/src/inline.rs, which embeds no CJK face and draws
        // every Han, kana and Hangul character as a missing glyph. See "Known deviations" in
        // todo.md.
        _loadsCjk = loadCjk;
        if (loadCjk)
        {
            foreach (FontId id in _database.LoadEmbeddedSource(FontAssets.Embedded(FontAssets.CjkFaceFile)))
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
        _fonts = [.. fonts];
    }

    /// <summary>
    /// Take over an earlier pass's shaped paragraphs, or start a cache of our own. Reuse happens
    /// only when that pass was built from the same web-font set; otherwise its shaping could
    /// have resolved to faces this pass does not have.
    /// </summary>
    internal void AdoptShapeCache(TextEngine? previous)
    {
        if (ShapeCache.Disabled)
        {
            return;
        }

        // The optional embedded faces decide which FontId each later face gets, and shaped
        // glyphs carry FontIds, so a pass that loaded a different set cannot reuse them.
        _shaper.Cache = previous?._shaper.Cache is { } inherited
            && ReferenceEquals(previous._directoryFonts, _directoryFonts)
            && previous._loadsEmoji == _loadsEmoji
            && previous._loadsCjk == _loadsCjk
            && inherited.MatchesFontSet(_fonts)
            ? inherited
            : new ShapeCache(_fonts);
    }

    /// <summary>
    /// Whether this engine shapes through the very cache <paramref name="previous"/> did, which
    /// <see cref="AdoptShapeCache"/> grants only for the same font set. Measure contexts of the
    /// two engines can then be compared by their inputs.
    /// </summary>
    internal bool SharesShapeCacheWith(TextEngine? previous) =>
        previous is not null && _shaper.Cache is { } cache && ReferenceEquals(cache, previous._shaper.Cache);

    /// <summary>
    /// Whether measure context <paramref name="first"/> of this engine and context
    /// <paramref name="second"/> of <paramref name="other"/> are the same kind and, for a
    /// replaced box, size identically. Text contexts compare only the layout parameters held
    /// beside the shaped text; their content is the caller's to prove unchanged.
    /// </summary>
    internal bool MeasureContextMatches(int first, TextEngine other, int second)
    {
        bool replaced = (first & ReplacedContextBit) != 0;
        if (replaced != ((second & ReplacedContextBit) != 0))
        {
            return false;
        }

        if (replaced)
        {
            int a = first & ~ReplacedContextBit;
            int b = second & ~ReplacedContextBit;
            return a < _replaced.Count && b < other._replaced.Count
                && _replaced[a].SameSizing(other._replaced[b]);
        }

        if (first < 0 || first >= _items.Count || second < 0 || second >= other._items.Count)
        {
            return false;
        }

        InlineItem x = _items[first];
        InlineItem y = other._items[second];
        return x.LayoutWrap == y.LayoutWrap
            && x.MinContentWrap == y.MinContentWrap
            && x.TextIndent == y.TextIndent
            && x.BalanceWrap == y.BalanceWrap
            && x.Align == y.Align
            && x.ForcedMinHeight.Equals(y.ForcedMinHeight)
            && x.LineClamp == y.LineClamp
            && x.EllipsisOverflow == y.EllipsisOverflow
            && string.Equals(x.OwnerText, y.OwnerText, StringComparison.Ordinal)
            && x.OwnerBoxes.Count == y.OwnerBoxes.Count
            && x.BoundaryEvents.Count == y.BoundaryEvents.Count;
    }

    private TextEngine? _adoptFrom;
    private IReadOnlyDictionary<NodeId, int>? _adoptWhole;
    private HashSet<NodeId>? _adoptDirty;
    private HashSet<int>? _adoptTaken;

    /// <summary>
    /// Offers this pass the whole-container inline items <paramref name="previous"/> built, for
    /// <see cref="TryBuild"/> to take over instead of collecting and shaping them again.
    /// </summary>
    /// <remarks>
    /// Not in crates/obscura-render, which builds every item on every pass. An item is a pure
    /// function of its container's DOM subtree, the computed styles in it and the font set, so
    /// one built for an element outside <paramref name="dirty"/> (the closure of everything the
    /// retained pass changed, see <c>RetainedTaffyLayout.DirtyClosure</c>) is the item this pass
    /// would build, provided the font set is the same - which sharing the shape cache proves.
    /// What an item accumulates during a pass (its laid-out buffer, origin, clip, marker and
    /// relative owner ranges) is reset to the state a new item starts in. The previous engine
    /// must not be used again; its render has been superseded.
    /// </remarks>
    internal void AdoptInlineItems(TextEngine previous, IReadOnlyDictionary<NodeId, int> previousWhole, HashSet<NodeId> dirty)
    {
        if (!SharesShapeCacheWith(previous))
        {
            return;
        }

        _adoptFrom = previous;
        _adoptWhole = previousWhole;
        _adoptDirty = dirty;
        _adoptTaken = [];
    }

    /// <summary>How many items this engine took over from the previous pass.</summary>
    internal int AdoptedItemCount { get; private set; }

    /// <summary>
    /// Ends the offer <see cref="AdoptInlineItems"/> made, once the box tree is built. Holding
    /// on to the previous engine would chain every pass's engine to the one before it.
    /// </summary>
    internal void EndInlineItemAdoption()
    {
        AdoptedItemCount = _adoptTaken?.Count ?? 0;
        _adoptFrom = null;
        _adoptWhole = null;
        _adoptDirty = null;
        _adoptTaken = null;
    }

    private int? TakeAdoptedItem(NodeId id)
    {
        if (_adoptWhole is not { } whole
            || !whole.TryGetValue(id, out int previousIndex)
            || _adoptDirty!.Contains(id)
            || previousIndex < 0
            || previousIndex >= _adoptFrom!._items.Count
            || !_adoptTaken!.Add(previousIndex))
        {
            return null;
        }

        InlineItem item = _adoptFrom._items[previousIndex];
        item.FirstLineOffset = 0f;
        item.Origin = (0f, 0f);
        item.Clip = null;
        item.Marker = null;
        item.RelativeOwnerRanges = [];

        // The exclusions of the last final layout beside floats: a carried final layout of
        // this item's box exists only for a layout that read no float (TaffyTree's float-blind
        // cache), and any other layout records its own before Finalize reads them.
        item.FloatBands = null;
        int index = _items.Count;
        _items.Add(item);
        return index;
    }

    /// <summary>The shaped-paragraph cache this engine shapes through, for tests.</summary>
    internal ShapeCache? CurrentShapeCache => _shaper.Cache;

    /// <summary>Shaped-paragraph cache statistics, for tests and profiling.</summary>
    internal (int Entries, int Hits, int Misses) ShapeCacheStats =>
        _shaper.Cache is { } cache ? (cache.Count, cache.Hits, cache.Misses) : (0, 0, 0);

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

    /// <summary>
    /// The two font metrics Chromium sizes a native text control from: the character width it
    /// multiplies by the <c>size</c>/<c>cols</c> attribute, and the widest character box, which
    /// pays for the control's chrome.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-render/src/dom.rs, which multiplies the font size by a fixed
    /// 0.6 (0.6075 for a textarea) and adds another 0.675em, so every face gets the same control
    /// width. Chromium reads the face: the per-character term is the OS/2 <c>xAvgCharWidth</c>
    /// scaled to the used font size, raised to the next integer only when rounding would round it
    /// up (<c>max(avg, round(avg))</c> - Blink rounds the metric but never below the real advance),
    /// and the constant is the head bounding box's width, rounded, less that character width.
    /// Reproduced exactly on Liberation Sans, DejaVu Sans, Liberation Mono and Plus Jakarta Sans
    /// at font sizes 10-20. See "Known deviations" in todo.md.
    /// </remarks>
    public (float CharWidth, float MaxCharWidth) ControlCharacterMetrics(LayoutStyle style)
    {
        float fontSize = QuantizeToFontUnits(F32.Max(style.FontSize ?? 13.333333f, 1f));
        ResolvedFont font = FontResolution.ResolveLoadedFont(
            style.FontFamily,
            ComputedStyle.UsedFontWeight(style),
            style.FontStyleItalic ?? false,
            _loadedFamilies);
        FaceMetrics metrics = font.Metrics;
        float unitsPerEm = metrics.UnitsPerEm > 0f ? metrics.UnitsPerEm : 1000f;
        float average = metrics.AverageCharWidth / unitsPerEm * fontSize;
        if (!(average > 0f))
        {
            // No usable OS/2 entry: Chromium falls back to the advance of '0'.
            float zero = MeasureControlLabel("0", style);
            return (F32.Max(zero, 0f), 0f);
        }

        return (F32.Max(average, F32.Round(average)), F32.Round(metrics.MaxCharWidth / unitsPerEm * fontSize));
    }

    /// <summary>
    /// The used font size as the face scaler actually sees it: FreeType sets a face's size in
    /// 26.6 fixed point, so every metric scaled out of it is scaled by the size rounded to 1/64
    /// of a pixel, never by the CSS value.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-render/src/dom.rs, which has no face metrics to scale - it
    /// multiplies the CSS font size by a fixed em fraction. The rounding matters only where a
    /// scaled metric lands within 1/64px of an integer and a `ceil` sits on top of it, which is
    /// exactly the default control: at the UA's 13.3333px Liberation Mono averages 8.0013px
    /// unrounded against Blink's 7.9982px, so `ceil(20 * charWidth)` came out 161 where Chromium
    /// gives 160 and every empty textarea and unsized input was 1px too wide. See "Known
    /// deviations" in todo.md.
    /// </remarks>
    private static float QuantizeToFontUnits(float fontSize) => F32.Round(fontSize * 64f) / 64f;

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
        if (TakeAdoptedItem(id) is { } adopted)
        {
            return adopted;
        }

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
        List<(string Text, SpanAttrs Attrs)> spans = [];

        CollectSpans(tree, id, styles, context, spans, collector);
        return PushShapedItem(style, context, spans, collector);
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
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        bool allowFloats = false)
    {
        run = LiftFlattenedWrappers(tree, parent, run, styles);
        bool hasText = false;
        foreach (NodeId cid in run)
        {
            if (!Inline.InlineChildOk(tree, cid, styles, ref hasText, allowFloats))
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
        if (allowFloats)
        {
            collector.FloatAnchors = [];
        }

        ResolvedFont font = FontResolution.ResolveLoadedFont(
            style.FontFamily,
            ComputedStyle.UsedFontWeight(style),
            style.FontStyleItalic ?? false,
            _loadedFamilies);
        SpanCtx context = BaseSpanCtx(style, font, collector);
        List<(string Text, SpanAttrs Attrs)> spans = [];

        foreach (NodeId cid in run)
        {
            CollectNodeSpans(tree, cid, styles, context, spans, collector);
        }

        return PushShapedItem(style, context, spans, collector);
    }

    /// <summary>
    /// Whether <see cref="TryBuildRun"/> with floats allowed would fold <paramref name="run"/>,
    /// without building anything.
    /// </summary>
    internal static bool CanFoldRun(
        DomTree tree,
        NodeId parent,
        IReadOnlyList<NodeId> run,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        run = LiftFlattenedWrappers(tree, parent, run, styles);
        bool hasText = false;
        foreach (NodeId cid in run)
        {
            if (!Inline.InlineChildOk(tree, cid, styles, ref hasText, allowFloats: true))
            {
                return false;
            }
        }

        return hasText && styles.ContainsKey(parent);
    }

    /// <summary>
    /// Put back the decoration-free inline wrappers a block's child list splices away.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-render/src/dom.rs, which folds a mixed block's run from
    /// the spliced list: the text inside <c>&lt;a&gt;</c>, <c>&lt;span&gt;</c> or
    /// <c>&lt;b&gt;</c> reached the shaper as the block's own text, so the wrapper owned no
    /// shaped range, <c>getBoundingClientRect()</c> reported 0,0,0,0 for it (a CDP click by
    /// coordinates missed the link) and its own color and font were not applied. Chromium
    /// 141: <c>&lt;div&gt;before &lt;a&gt;mixed link&lt;/a&gt;&lt;div&gt;block&lt;/div&gt;&lt;/div&gt;</c>
    /// reports the link at 57.8,8,71.1,17. Each run node is replaced by its outermost
    /// ancestor below <paramref name="parent"/>, and the lifted list is kept only when
    /// splicing it again gives back exactly <paramref name="run"/>, so a wrapper whose content
    /// is split across runs (it holds a block) keeps the spliced form.
    /// </remarks>
    private static IReadOnlyList<NodeId> LiftFlattenedWrappers(
        DomTree tree,
        NodeId parent,
        IReadOnlyList<NodeId> run,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        List<NodeId>? lifted = null;
        for (int index = 0; index < run.Count; index++)
        {
            NodeId top = run[index];
            NodeId? up = tree.GetNode(top)?.Parent;
            int guard = 0;
            while (up is { } ancestor && ancestor != parent)
            {
                if (++guard > 4096)
                {
                    return run;
                }

                top = ancestor;
                up = tree.GetNode(ancestor)?.Parent;
            }

            if (up is null)
            {
                return run;
            }

            if (top != run[index] && lifted is null)
            {
                lifted = new List<NodeId>(run.Count);
                for (int before = 0; before < index; before++)
                {
                    lifted.Add(run[before]);
                }
            }

            if (lifted is not null && (lifted.Count == 0 || lifted[^1] != top))
            {
                lifted.Add(top);
            }
        }

        if (lifted is null)
        {
            return run;
        }

        List<NodeId> respliced = new(run.Count);
        DomBuild.FlattenBoxlessInlineChildren(tree, lifted, styles, respliced);
        if (respliced.Count != run.Count)
        {
            return run;
        }

        for (int index = 0; index < run.Count; index++)
        {
            if (respliced[index] != run[index])
            {
                return run;
            }
        }

        return lifted;
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
        SpanAttrs attrs = context.ToSpanAttrs();
        List<(string Text, SpanAttrs Attrs)> spans = [];

        Inline.PushText(text, context.Transform, context.WhiteSpace, attrs, spans, collector);
        return PushShapedItem(style, context, spans, collector);
    }

    /// <summary>
    /// Shared tail of the build entry points: shape the collected spans under the base style's
    /// font metrics and alignment, and store the result as a new inline item.
    /// </summary>
    private int? PushShapedItem(
        LayoutStyle baseStyle,
        SpanCtx strut,
        List<(string Text, SpanAttrs Attrs)> spans,
        Collector collector)
    {
        collector.FlushLastSpan(spans);
        float lineHeight = strut.LineHeight;
        List<ClipTextFill> clipFills = collector.ClipFills;
        List<OwnerTextChunk> ownerChunks = collector.OwnerChunks;
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

        // Clamp to the trimmed text and drop what became empty, in one ordered pass.
        int keptChunks = 0;
        for (int i = 0; i < ownerChunks.Count; i++)
        {
            OwnerTextChunk chunk = ownerChunks[i] with
            {
                Start = Math.Min(ownerChunks[i].Start, textLength),
                End = Math.Min(ownerChunks[i].End, textLength),
            };
            if (chunk.Start < chunk.End)
            {
                ownerChunks[keptChunks++] = chunk;
            }
        }

        ownerChunks.RemoveRange(keptChunks, ownerChunks.Count - keptChunks);

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

        List<(NodeId Float, int Offset)>? floatAnchors = null;
        if (collector.FloatAnchors is { Count: > 0 } anchors)
        {
            floatAnchors = new List<(NodeId Float, int Offset)>(anchors.Count);
            foreach ((NodeId floatNode, int offset) in anchors)
            {
                floatAnchors.Add((floatNode, Math.Min(offset, textLength)));
            }
        }

        string? ownerText = null;
        if (ownerBoxes.Count > 0 || floatAnchors is not null)
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
        var metrics = new TextMetrics(
            shapedSize,
            F32.Max(lineHeight, 1f),
            strut.Above,
            strut.Below);
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
        DropTrailingEmptyLineBox(buffer, ownerBoxes, textLength);

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

        // DEVIATION from crates/obscura-render/src/inline.rs, which aligns only `center` and
        // `right`/`end` (to the right), ignores `justify`, `text-align-last` and `direction`,
        // and lets each paragraph's first strong character pick its direction. The paragraph
        // direction is the block's (Chromium 141 starts a dir=rtl paragraph at the right),
        // start/end resolve against it, the last line takes text-align-last, and the white
        // space a line ends with stays out of its alignment (TextLayout).
        bool rtl = baseStyle.Direction == Layout.Direction.Rtl;
        Align align = PhysicalAlign(baseStyle.TextAlignKeyword ?? TextAlignKeyword.Start, rtl);
        Align lastAlign = baseStyle.TextAlignLast is { } lastKeyword
            ? PhysicalAlign(lastKeyword, rtl)
            : align == Align.Justified ? PhysicalAlign(TextAlignKeyword.Start, rtl) : align;
        var alignOptions = new LineAlignOptions(
            lastAlign,
            whiteSpace switch
            {
                WhiteSpace.Normal or WhiteSpace.NoWrap or WhiteSpace.PreLine => TrailingSpace.Removed,
                WhiteSpace.PreWrap => TrailingSpace.HangsAtSoftWrap,
                _ => TrailingSpace.Counts,
            },
            SoftWrapEnd: false);
        foreach (BufferLine line in buffer.Lines)
        {
            line.SetAlign(align);
            line.AlignOptions = alignOptions;
            line.BaseRtl = rtl;
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
            Rtl = rtl,
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
            OwnerChunks = ownerChunks,
            OwnerChain = collector.OwnerChain,
            OwnerBoxes = ownerBoxes,
            BoundaryEvents = boundaryEvents,
            RelativeOwnerRanges = [],
            FloatAnchors = floatAnchors,
        });
        return index;
    }

    /// <summary>
    /// CSS 2.1 9.4.2: a line box holding no text, no preserved white space and no inline box
    /// with a non-zero margin, border or padding "must be treated as not existing". The only
    /// one an inline formatting context can produce is the line after a forced break that ends
    /// the context - a trailing <c>&lt;br&gt;</c>, or a preserved newline at the end of a
    /// <c>white-space: pre</c> block.
    /// </summary>
    /// <remarks>
    /// DEVIATION from <c>crates/obscura-render/src/inline.rs</c>, which feeds the collapsed
    /// text straight into cosmic-text and keeps every buffer line the newline split produces,
    /// so a trailing forced break adds a whole empty line box. Chromium 141 at 16px/20px
    /// 'Liberation Mono' in a 600px block: <c>&lt;div&gt;&lt;br&gt;&lt;/div&gt;</c> is 20 tall
    /// (Rust/C# gave 40), <c>a&lt;br&gt;</c> 20 (40), <c>&lt;br&gt;&lt;br&gt;</c> 40 (60), and
    /// <c>&lt;pre&gt;a\n&lt;/pre&gt;</c> 20 (40); the lines that carry content are unchanged -
    /// <c>a&lt;br&gt;b</c> stays 40 and <c>&lt;pre&gt;a\n\n&lt;/pre&gt;</c> stays 40.
    /// Only the last line is dropped, and only one of them, which is what Chromium does for a
    /// run of trailing breaks.
    ///
    /// An inline box that merely *ends* on the trailing line does not keep it alive, but one
    /// that *starts* there does. Measured in Chromium 141:
    /// <c>&lt;span style="border:1px solid"&gt;a&lt;br&gt;&lt;/span&gt;</c> is 20 (the span's
    /// closing edge is not content) while <c>a&lt;br&gt;&lt;span style="border:1px solid"&gt;
    /// &lt;/span&gt;</c> is 40, and an edgeless <c>a&lt;br&gt;&lt;span&gt;&lt;/span&gt;</c> is
    /// 20. See "Known deviations" in todo.md.
    /// </remarks>
    private static void DropTrailingEmptyLineBox(
        TextBuffer buffer,
        IReadOnlyList<InlineOwnerBox> ownerBoxes,
        int textLength)
    {
        if (buffer.Lines.Count < 2 || buffer.Lines[^1].Text.Length != 0)
        {
            return;
        }

        foreach (InlineOwnerBox owner in ownerBoxes)
        {
            // Clamped to `textLength` above, so a box opening on the trailing line starts there.
            if (owner.Start == textLength
                && (owner.StartEdge.Advance != 0f || owner.EndEdge.Advance != 0f))
            {
                return;
            }
        }

        buffer.Lines.RemoveAt(buffer.Lines.Count - 1);
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

    /// <summary>Forget the float exclusions recorded by the last final layout of an IFC.</summary>
    internal void ForgetFloatBands(int index)
    {
        if ((index & ReplacedContextBit) == 0 && index < _items.Count)
        {
            _items[index].FloatBands = null;
        }
    }

    /// <summary>The floats anchored in inline context <paramref name="index"/>, or null.</summary>
    internal List<(NodeId Float, int Offset)>? FloatAnchorsOf(int index) =>
        (index & ReplacedContextBit) == 0 && index < _items.Count ? _items[index].FloatAnchors : null;

    /// <summary>
    /// For each of <paramref name="offsets"/> (ascending text offsets), the line box of the
    /// last layout of context <paramref name="index"/> that holds it, relative to the content
    /// box: its top, its height, its width (the space floats leave it), and how much of that
    /// the content before the offset already uses. A float anchored there is placed on that
    /// line when it fits in the rest (CSS 2.1 9.5.1 rule 8: as high as possible), below it
    /// otherwise.
    /// </summary>
    public (float Top, float Height, float Width, float Used)?[] AnchorLines(int index, int[] offsets)
    {
        var result = new (float Top, float Height, float Width, float Used)?[offsets.Length];
        if ((index & ReplacedContextBit) != 0 || index >= _items.Count || offsets.Length == 0)
        {
            return result;
        }

        InlineItem item = _items[index];
        if (item.OwnerText is not { } source)
        {
            return result;
        }

        List<int> starts = InlineGeometry.SourceLineStarts(item.Buffer, source);
        int next = 0;
        LayoutRun? previous = null;
        int previousStart = 0;
        foreach (LayoutRun run in item.Buffer.LayoutRuns())
        {
            int lineStart = run.LineIndex < starts.Count ? starts[run.LineIndex] : 0;
            int end = int.MinValue;
            foreach (LayoutGlyph glyph in run.Glyphs)
            {
                end = Math.Max(end, glyph.End);
            }

            // An offset before this run's start belongs to the line before (a line ends at
            // its last glyph; the collapsed space after it starts nothing).
            while (next < offsets.Length && previous is not null && offsets[next] < lineStart)
            {
                result[next] = AnchorOn(previous, offsets[next] - previousStart);
                next++;
            }

            while (next < offsets.Length
                && offsets[next] >= lineStart
                && (run.Glyphs.Count == 0 || offsets[next] - lineStart < end))
            {
                result[next] = AnchorOn(run, offsets[next] - lineStart);
                next++;
            }

            previous = run;
            previousStart = lineStart;
            if (next == offsets.Length)
            {
                break;
            }
        }

        for (; next < offsets.Length && previous is not null; next++)
        {
            result[next] = AnchorOn(previous, offsets[next] - previousStart);
        }

        return result;

        static (float Top, float Height, float Width, float Used) AnchorOn(LayoutRun line, int local)
        {
            float lineStartX = line.Glyphs.Count > 0 ? F32.Min(line.Glyphs[0].X, line.X) : line.X;
            float used = F32.Max(InlineGeometry.RunCursorX(line, Math.Max(local, 0)) - lineStartX, 0f);
            return (line.LineTop, line.LineHeight, line.LineWidth, used);
        }
    }

    /// <summary>
    /// <see cref="MeasureTaffy"/> for a leaf in a block formatting context with floats:
    /// <paramref name="bands"/> are the floats beside its content box. A final layout records
    /// them, so <see cref="Finalize"/> breaks the lines the way the box was sized.
    /// </summary>
    public Size<float> MeasureTaffyAroundFloats(
        int index,
        Size<float?> known,
        Size<AvailableSpace> available,
        FloatBands? bands,
        RunMode runMode)
    {
        if ((index & ReplacedContextBit) != 0)
        {
            return MeasureTaffy(index, known, available);
        }

        InlineItem item = _items[index];
        float? width = known.Width ?? (available.Width.Kind == AvailableSpaceKind.Definite
            ? available.Width.Unwrap()
            : null);
        if (!bands!.Intrudes || width is not { } definite)
        {
            if (runMode == RunMode.PerformLayout)
            {
                item.FloatBands = null;
            }

            return MeasureTaffy(index, known, available);
        }

        if (runMode == RunMode.PerformLayout)
        {
            item.FloatBands = bands;
        }

        ShapeWithTextIndent(item, definite, item.LayoutWrap, bands);
        (float shapedWidth, float height, bool clamped) = InlineGeometry.BufferSize(item);
        return new Size<float>(shapedWidth, clamped ? height : F32.Max(height, item.ForcedMinHeight));
    }

    /// <summary>
    /// After layout, pin each context to its final content-box origin and clip, reshaping once
    /// at the resolved width so paint draws the same line breaks the box was sized for.
    /// </summary>
    public void Finalize(int index, (float X, float Y) contentOrigin, float contentWidth, Rect? clip)
    {
        InlineItem item = _items[index];
        contentWidth = F32.Max(contentWidth, 0f);
        ShapeWithTextIndent(item, contentWidth, item.LayoutWrap, item.FloatBands);
        float balancedWidth = item.BalanceWrap && item.FloatBands is null
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
            if (offsets.Count > 0 && item.OwnerChunks.Count > 0)
            {
                // Paint takes the last range overlapping a glyph, and the old flat list held
                // one range per open owner per run, outermost first, so the innermost open
                // owner with an offset won. Resolve that owner once per chain node (a parent
                // always precedes its children) and emit one range per run.
                List<OwnerChainNode> chain = item.OwnerChain;
                var nearest = new int[chain.Count];
                var nodeOffsets = new (float X, float Y)[chain.Count];
                for (int i = 0; i < chain.Count; i++)
                {
                    if (offsets.TryGetValue(chain[i].Owner, out (float X, float Y) offset)
                        && offset != (0f, 0f))
                    {
                        nearest[i] = i;
                        nodeOffsets[i] = offset;
                    }
                    else
                    {
                        nearest[i] = chain[i].Parent >= 0 ? nearest[chain[i].Parent] : -1;
                    }
                }

                foreach (OwnerTextChunk chunk in item.OwnerChunks)
                {
                    int owner = chunk.Node >= 0 ? nearest[chunk.Node] : -1;
                    if (owner >= 0)
                    {
                        ranges.Add(new RelativeOwnerTextRange(chunk.Start, chunk.End, nodeOffsets[owner]));
                    }
                }
            }

            item.RelativeOwnerRanges = ranges;
        }
    }

    /// <summary>
    /// Where one inline box's own baseline sits inside a line box it has a fragment on.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-render/src/inline.rs, which puts every fragment on the
    /// line's single baseline. An inline box with a <c>vertical-align</c> has a baseline of its
    /// own, and its client rect follows it: Chromium 141 reports a <c>vertical-align: 50%</c>
    /// 16px span in a <c>16px/18px 'Liberation Mono'</c> block 4px above the block's top, not
    /// on the line's baseline.
    /// </remarks>
    private static float OwnerBaselineY(LayoutRun run, InlineBoxExtent extent) => extent.Align switch
    {
        LineBoxAlign.LineTop => run.LineTop + extent.Above,
        LineBoxAlign.LineBottom => run.LineTop + run.LineHeight - extent.Below,
        _ => run.LineY - extent.Shift,
    };

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
        Dictionary<(NodeId Owner, int Line), int> existingByOwner = [];
        Dictionary<int, float> cursorByOffset = [];
        List<(float Left, float Right)> extents = [];
        for (int itemIndex = 0; itemIndex < _items.Count; itemIndex++)
        {
            InlineItem item = _items[itemIndex];
            if (item.OwnerText is not { } source)
            {
                continue;
            }

            List<int> lineStarts = InlineGeometry.SourceLineStarts(item.Buffer, source);
            List<LayoutRun> runs = [.. item.Buffer.LayoutRuns()];
            existingByOwner.Clear();
            int lineIndex = 0;
            for (int runIndex = 0; runIndex < runs.Count; runIndex++)
            {
                LayoutRun run = runs[runIndex];
                if (run.LineIndex >= lineStarts.Count)
                {
                    lineIndex++;
                    continue;
                }

                int lineStart = lineStarts[run.LineIndex];
                int lineEnd = lineStart + run.Text.Length;

                // A buffer line soft-wraps into several runs that all report the whole line's
                // text, so an empty owner sitting at the line's end would otherwise land on
                // every one of them. It belongs to the run the text actually ends on.
                bool lastRunOfLine = runIndex + 1 == runs.Count
                    || runs[runIndex + 1].LineIndex != run.LineIndex;
                float firstLineOffset = lineIndex == 0 ? item.FirstLineOffset : 0f;
                float alignmentShift = InlineGeometry.LineEdgeAlignmentShift(item, lineStart, lineEnd);
                // The line's content starts where its first glyph is: an aligned line does not
                // start at the line box's left edge, and a continuing fragment runs to the
                // content's end, not to the line box's width past it (Chromium 141: a
                // right-aligned line of 280.13 in 300px is 280.13 wide, where this gave 264.68).
                float lineRight = (run.Glyphs.Count > 0 && !run.Rtl ? run.Glyphs[0].X : run.X)
                    + run.LineW + InlineGeometry.LineEdgeAdvance(item, lineStart, lineEnd);

                // DEVIATION from crates/obscura-render/src/inline.rs, whose continuing fragment
                // runs to the line's full advance. The white space a soft wrap leaves at the
                // end of a line hangs, and Chromium 141 ends the fragment before it: the
                // first line of a span wrapped after "three " is 36.47 wide ("three"), where
                // this gave 40.91. White space that is removed (normal, nowrap, pre-line) ends
                // no fragment and starts none, before a <br> or at a soft wrap alike; pre and
                // break-spaces keep theirs; pre-wrap's hangs at a soft wrap, and Chromium
                // reports it as a fragment of its own past the line's content.
                LineAlignOptions lineOptions = item.Buffer.Lines[run.LineIndex].AlignOptions;
                bool softWrapHere = !lastRunOfLine || lineOptions.SoftWrapEnd;
                bool trailingRemoved = lineOptions.Trailing == TrailingSpace.Removed;
                bool trailingHangs = lineOptions.Trailing == TrailingSpace.HangsAtSoftWrap && softWrapHere;
                float trailingAdvance = 0f;
                int contentEnd = int.MaxValue;
                if (item.OwnerBoxes.Count > 0 && (trailingRemoved || trailingHangs))
                {
                    trailingAdvance = TrailingSpaceAdvance(run, out contentEnd);
                    lineRight -= trailingAdvance;
                }

                // RunCursorX scans the run's glyphs, and every owner open across the line asks
                // for the same few offsets, so it is memoized per run.
                cursorByOffset.Clear();
                float CursorX(int offset)
                {
                    if (!cursorByOffset.TryGetValue(offset, out float x))
                    {
                        x = InlineGeometry.RunCursorX(run, offset);
                        cursorByOffset[offset] = x;
                    }

                    return x;
                }

                bool lineHasRtl = item.OwnerBoxes.Count > 0 && LineHasRtlGlyph(run);
                int rtlTrailingFrom = lineHasRtl && (trailingRemoved || trailingHangs)
                    ? LogicalContentEnd(run)
                    : int.MaxValue;
                float[]? visualShifts = lineHasRtl ? InlineGeometry.VisualEdgeShifts(item, run, lineStart, lineEnd) : null;
                foreach (InlineOwnerBox owner in item.OwnerBoxes)
                {
                    bool empty = owner.Start == owner.End;
                    bool intersects = empty
                        ? (owner.Start >= lineStart && owner.Start < lineEnd)
                            || (lastRunOfLine && owner.Start == lineEnd)
                        : owner.Start < lineEnd && owner.End > lineStart;
                    if (!intersects)
                    {
                        continue;
                    }

                    bool first = (owner.Start >= lineStart && owner.Start < lineEnd) || empty;
                    bool last = (owner.End > lineStart && owner.End <= lineEnd) || empty;
                    float rawLeft = first
                        ? CursorX(Math.Max(0, owner.Start - lineStart))
                            + InlineGeometry.LineAdvanceBeforeEvent(item, owner.StartEvent, lineStart, lineEnd)
                            + owner.StartEdge.Margin
                        : CursorX(0);
                    float rawRight = last
                        ? CursorX(Math.Max(0, owner.End - lineStart))
                            + InlineGeometry.LineAdvanceBeforeEvent(item, owner.EndEvent, lineStart, lineEnd)
                            + owner.EndEdge.BorderPadding
                        : lineRight;
                    bool onlyTrailing = false;
                    if (trailingAdvance > 0f && !empty && !lineHasRtl)
                    {
                        float baseX = item.Origin.X + firstLineOffset + alignmentShift;
                        if (trailingHangs && owner.End - lineStart > contentEnd)
                        {
                            float hangLeft = F32.Max(lineRight, rawLeft);
                            float hangRight = F32.Min(
                                lineRight + trailingAdvance,
                                F32.Max(last ? rawRight : lineRight + trailingAdvance, hangLeft));
                            output.Add(new InlineOwnerLineFragment(
                                owner.Owner,
                                itemIndex,
                                lineIndex,
                                baseX + hangLeft,
                                item.Origin.Y + OwnerBaselineY(run, owner.Extent),
                                F32.Max(hangRight - hangLeft, 0f)));
                        }

                        onlyTrailing = owner.Start - lineStart >= contentEnd;
                        rawRight = F32.Min(rawRight, lineRight);
                    }

                    if (onlyTrailing)
                    {
                        continue;
                    }
                    // DEVIATION from crates/obscura-render/src/inline.rs, which measures from
                    // the logical start cursor to the logical end cursor. On a right-to-left
                    // line the start cursor is the right edge, so the width came out negative
                    // and clamped to 0: Chromium 141 reports a `direction: rtl` span of
                    // Arabic at 1235,37 wide where this gave 1263.4,0. A line with any
                    // right-to-left glyph takes the visual extent of the owner's glyphs.
                    // Chromium gives an owner one fragment per bidi run it has on the line,
                    // left to right (a dir=rtl span of Hebrew, English and Hebrew is three).
                    if (!empty && lineHasRtl)
                    {
                        bool startOnRight = OwnerGlyphExtents(item, run, lineStart, lineEnd, owner, rtlTrailingFrom, visualShifts, extents);
                        if (extents.Count > 0)
                        {
                            // The box's own border and padding sit outside its glyphs, on the
                            // start side where it opens on this line and the end side where it
                            // closes; the start is on the right when its text is right to left.
                            int startSide = startOnRight ? extents.Count - 1 : 0;
                            int endSide = startOnRight ? 0 : extents.Count - 1;
                            if (first)
                            {
                                extents[startSide] = startOnRight
                                    ? (extents[startSide].Left, extents[startSide].Right + owner.StartEdge.BorderPadding)
                                    : (extents[startSide].Left - owner.StartEdge.BorderPadding, extents[startSide].Right);
                            }

                            if (last)
                            {
                                extents[endSide] = startOnRight
                                    ? (extents[endSide].Left - owner.EndEdge.BorderPadding, extents[endSide].Right)
                                    : (extents[endSide].Left, extents[endSide].Right + owner.EndEdge.BorderPadding);
                            }

                            rawLeft = extents[0].Left;
                            rawRight = extents[0].Right;
                            for (int more = 1; more < extents.Count; more++)
                            {
                                output.Add(new InlineOwnerLineFragment(
                                    owner.Owner,
                                    itemIndex,
                                    lineIndex,
                                    item.Origin.X + firstLineOffset + alignmentShift + extents[more].Left,
                                    item.Origin.Y + OwnerBaselineY(run, owner.Extent),
                                    F32.Max(extents[more].Right - extents[more].Left, 0f)));
                            }
                        }
                        else if (owner.Start - lineStart >= rtlTrailingFrom)
                        {
                            continue;
                        }
                    }

                    float x = item.Origin.X + firstLineOffset + alignmentShift + rawLeft;
                    float width = F32.Max(rawRight - rawLeft, 0f);
                    float baselineY = item.Origin.Y + OwnerBaselineY(run, owner.Extent);

                    // Keyed lookup: a linear search of the output made nested inline boxes,
                    // which put every open owner on every line, quadratic in fragments.
                    if (existingByOwner.TryGetValue((owner.Owner, lineIndex), out int existing))
                    {
                        InlineOwnerLineFragment fragment = output[existing];
                        float left = F32.Min(fragment.X, x);
                        float right = F32.Max(fragment.X + fragment.Width, x + width);
                        output[existing] = fragment with { X = left, Width = F32.Max(right - left, 0f) };
                    }
                    else
                    {
                        existingByOwner[(owner.Owner, lineIndex)] = output.Count;
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

    /// <summary>The advance of the spaces a left-to-right visual line ends with.</summary>
    /// <param name="run">The line.</param>
    /// <param name="from">The text offset the trailing spaces start at; <c>int.MaxValue</c> when none.</param>
    private static float TrailingSpaceAdvance(LayoutRun run, out int from)
    {
        from = int.MaxValue;
        if (run.Rtl)
        {
            return 0f;
        }

        float advance = 0f;
        for (int index = run.Glyphs.Count - 1; index >= 0; index--)
        {
            LayoutGlyph glyph = run.Glyphs[index];
            if ((glyph.Level & 1) != 0
                || glyph.Start < 0
                || glyph.End > run.Text.Length
                || glyph.End <= glyph.Start)
            {
                break;
            }

            bool spaces = true;
            for (int at = glyph.Start; at < glyph.End; at++)
            {
                if (run.Text[at] != ' ')
                {
                    spaces = false;
                    break;
                }
            }

            if (!spaces)
            {
                break;
            }

            advance += glyph.W;
            from = Math.Min(from, glyph.Start);
        }

        return advance;
    }

    /// <summary>The end of the last glyph, in text order, that is not a space.</summary>
    private static int LogicalContentEnd(LayoutRun run)
    {
        int end = 0;
        foreach (LayoutGlyph glyph in run.Glyphs)
        {
            if (glyph.End <= end || glyph.Start < 0 || glyph.End > run.Text.Length)
            {
                continue;
            }

            for (int at = glyph.Start; at < glyph.End; at++)
            {
                if (run.Text[at] != ' ')
                {
                    end = glyph.End;
                    break;
                }
            }
        }

        return end;
    }

    private static bool LineHasRtlGlyph(LayoutRun run)
    {
        if (run.Rtl)
        {
            return true;
        }

        foreach (LayoutGlyph glyph in run.Glyphs)
        {
            if ((glyph.Level & 1) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The visual x extent of the glyphs whose text lies in <c>start..end</c>.</summary>
    /// <param name="run">The line.</param>
    /// <param name="start">The owner's first text offset.</param>
    /// <param name="end">The owner's text end.</param>
    /// <param name="trailingFrom">Glyphs from this text offset on are white space the line
    /// ends with, removed or hanging, and belong to no fragment.</param>
    /// <param name="shifts">The line's <see cref="InlineGeometry.VisualEdgeShifts"/>.</param>
    /// <param name="extents">Receives one extent per bidi run, left to right, each glyph moved
    /// by the inline-box edges the way paint moves it.</param>
    /// <returns>Whether the owner's logically first glyph on the line is right to left.</returns>
    private static bool OwnerGlyphExtents(
        InlineItem item,
        LayoutRun run,
        int lineStart,
        int lineEnd,
        InlineOwnerBox owner,
        int trailingFrom,
        float[]? shifts,
        List<(float Left, float Right)> extents)
    {
        extents.Clear();
        int start = owner.Start - lineStart;
        int end = owner.End - lineStart;
        int firstStart = int.MaxValue;
        bool firstRtl = item.Rtl;
        int glyphIndex = -1;
        float left = float.PositiveInfinity;
        float right = float.NegativeInfinity;
        int level = -1;
        foreach (LayoutGlyph glyph in run.Glyphs)
        {
            glyphIndex++;
            bool owned = glyph.Start < end && glyph.End > start && glyph.Start < trailingFrom;
            if (owned && glyph.Start < firstStart)
            {
                firstStart = glyph.Start;
                firstRtl = (glyph.Level & 1) != 0;
            }

            if (!owned || glyph.Level != level)
            {
                if (left <= right)
                {
                    extents.Add((left, right));
                }

                left = float.PositiveInfinity;
                right = float.NegativeInfinity;
                level = owned ? glyph.Level : -1;
                if (!owned)
                {
                    continue;
                }
            }

            float x = glyph.X + (shifts is not null
                ? shifts[glyphIndex]
                : InlineGeometry.LineAdvanceBeforeText(item, lineStart + glyph.Start, lineStart, lineEnd));
            left = F32.Min(left, x);
            right = F32.Max(right, x + glyph.W);
        }

        if (left <= right)
        {
            extents.Add((left, right));
        }

        extents.Sort((a, b) => a.Left.CompareTo(b.Left));
        return firstRtl;
    }

    /// <summary>Lines at most this long are always probed whole.</summary>
    /// <remarks>
    /// Was 2048. Below it every line of a paragraph re-shaped the whole remainder, so a 2000
    /// character post body cost ~40 times its length, and a min-content measurement (one
    /// word per line) far more: reddit.com's feed shaped 2.35M characters in one cold layout
    /// pass, 0.33M with these windows. The window is exact whatever its size (see
    /// <see cref="ProbeFirstLine"/>).
    /// </remarks>
    private const int ProbeWindowMinLine = 256;

    /// <summary>The first window probed; later lines start at four times the last split.</summary>
    private const int ProbeInitialWindow = 64;

    /// <summary>
    /// Lay out the first visual line of <paramref name="line"/> at <paramref name="available"/>,
    /// shaping only a prefix of it when that provably gives the same first line.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-render/src/inline.rs <c>shape_with_text_indent</c>, which
    /// shapes and wraps all of a line to learn where its first visual line ends; the remainder
    /// becomes the next line to probe, so a long paragraph with inline owners was shaped once
    /// per line: quadratic in its length, 12s for 20000 sibling spans. The result is the same.
    /// For a long line this shapes a window instead, cut between two printable ASCII characters so
    /// that the grapheme clusters, bidi levels (the caller checked there is no right-to-left
    /// text) and break opportunities before the cut are the ones the whole line has, and
    /// shaping (which is per word) agrees on every word that ends before the window's last
    /// one. Wrapping is greedy and looks back only, so when the second visual line ends before
    /// that last word, the first line is exactly the first line of the whole. Otherwise the
    /// window grows, up to the whole line.
    /// </remarks>
    private List<LayoutLine> ProbeFirstLine(
        BufferLine line,
        ref BufferLine? probe,
        ref int probeChars,
        float fontSize,
        float available,
        Wrap wrap,
        float? mono,
        int tabWidth)
    {
        string text = line.Text;
        if (text.Length <= ProbeWindowMinLine)
        {
            return line.Layout(_shaper, fontSize, available, wrap, mono, tabWidth);
        }

        while (true)
        {
            if (probe is null)
            {
                int cut = SafeProbeCut(text, probeChars);
                if (cut < 0)
                {
                    return line.Layout(_shaper, fontSize, available, wrap, mono, tabWidth);
                }

                probe = new BufferLine(text[..cut], line.AttrsList.Prefix(cut)) { Align = line.Align, BaseRtl = line.BaseRtl };
            }

            List<LayoutLine> layouts = probe.Layout(_shaper, fontSize, available, wrap, mono, tabWidth);
            if (FirstLineSettled(probe, layouts))
            {
                return layouts;
            }

            probeChars = Math.Max(probeChars, probe.Text.Length) * 4;
            probe = null;
        }
    }

    /// <summary>
    /// The first offset at or after <paramref name="from"/> that sits between two printable
    /// ASCII characters, the first of them not a space, or -1 when there is none in reach
    /// before the end of the text.
    /// </summary>
    private static int SafeProbeCut(string text, int from)
    {
        int limit = Math.Min(text.Length - 1, from + 256);
        for (int cut = Math.Max(from, 1); cut <= limit; cut++)
        {
            char before = text[cut - 1];
            char after = text[cut];
            if (before > ' ' && before < '\u007f' && after >= ' ' && after < '\u007f')
            {
                return cut;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether a window's first visual line is final: its second line has a glyph, and ends
    /// before the window's last word (the only one the cut can have changed) begins.
    /// </summary>
    private static bool FirstLineSettled(BufferLine probe, List<LayoutLine> layouts)
    {
        if (layouts.Count < 2 || layouts[1].Glyphs.Count == 0 || probe.ShapeOpt is not { } shape)
        {
            return false;
        }

        if (shape.Rtl || shape.Spans.Count != 1 || shape.Spans[0].IsRtl || shape.Spans[0].Words.Count == 0)
        {
            return false;
        }

        ShapeWord last = shape.Spans[0].Words[^1];
        if (last.Blank || last.Glyphs.Count == 0)
        {
            return false;
        }

        int lastStart = int.MaxValue;
        foreach (ShapeGlyph glyph in last.Glyphs)
        {
            lastStart = Math.Min(lastStart, glyph.Start);
        }

        int secondEnd = 0;
        foreach (LayoutGlyph glyph in layouts[1].Glyphs)
        {
            secondEnd = Math.Max(secondEnd, glyph.End);
        }

        return secondEnd <= lastStart;
    }

    /// <summary>A <c>text-align</c> keyword as the physical side a line aligns to.</summary>
    internal static Align PhysicalAlign(TextAlignKeyword keyword, bool rtl) => keyword switch
    {
        TextAlignKeyword.Center => Align.Center,
        TextAlignKeyword.Justify => Align.Justified,
        TextAlignKeyword.Left => Align.Left,
        TextAlignKeyword.Right => Align.Right,
        TextAlignKeyword.End => rtl ? Align.Left : Align.Right,
        _ => rtl ? Align.Right : Align.Left,
    };

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
    private void ShapeWithTextIndent(InlineItem item, float? width, Wrap wrap, FloatBands? bands = null)
    {
        float indent = InlineGeometry.UsedTextIndent(item.TextIndent, width);
        // The first line is aligned in the full width and then moved: by the indent on the
        // left of a left-aligned line, half of it when centred, none when right-aligned. Right
        // to left, the indent is on the right, so each moves back by all of it. A justified
        // line, and a line with a negative indent, which can be wider than the box, are laid
        // out in the width the indent leaves (see NarrowLines), so they move by the indent left
        // to right and not at all right to left.
        // DEVIATION from crates/obscura-render/src/inline.rs, which has no right-to-left case
        // and indents a justified line by moving it, past the right edge, and lays a line out
        // of a negative indent in the box's width, where it wrapped again (Chromium 141 keeps
        // "... adipiscing", 280px, on the first line of a 270px box with text-indent:-30px).
        item.FirstLineOffset = indent * item.Align switch
        {
            _ when indent < 0f => item.Rtl ? 0f : 1f,
            Align.Justified => item.Rtl ? 0f : 1f,
            Align.Center => item.Rtl ? -0.5f : 0.5f,
            Align.End or Align.Right => item.Rtl ? -1f : 0f,
            _ => item.Rtl ? 0f : 1f,
        };

        if (bands is not null && width is { } floatWidth)
        {
            ShapeAroundFloats(item, floatWidth, indent, wrap, bands);
            return;
        }

        if (item.SourceBuffer is not { } source)
        {
            if (item.PristineBuffer is { } pristine)
            {
                // A float layout split the lines; start again from the paragraphs as built.
                item.Buffer = pristine.Clone();
            }

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
            bool windowable = !item.Rtl && !Bidi.AnyRightToLeft(sourceText);
            int probeChars = ProbeInitialWindow;
            int lineIndex = 0;

            // SourceLineStarts, kept incrementally: lines before lineIndex no longer change.
            int sourceOffset = 0;
            bool sourceAfterBreak = false;
            while (lineIndex < item.Buffer.Lines.Count)
            {
                int lineOffset = sourceOffset;
                bool lineAfterBreak = sourceAfterBreak;
                int globalStart = InlineGeometry.NextSourceLineStart(
                    item.Buffer.Lines[lineIndex].Text, sourceText, ref sourceOffset, ref sourceAfterBreak);
                float firstIndent = lineIndex == 0 ? indent : 0f;
                float baseAvailable = F32.Max(fullWidth - firstIndent, 0f);

                // Negative inline margins can admit content that would not fit in the text-only
                // probe. Begin at the widest possible candidate and retain the same monotonic
                // decrease used for positive padding/border advances.
                int lineSourceEnd = globalStart + item.Buffer.Lines[lineIndex].Text.Length;
                float negativeEdges = item.BoundaryEvents.Count == 0
                    ? 0f
                    : item.EdgeIndex.NegativeEdges(globalStart, lineSourceEnd);

                float available = F32.Max(baseAvailable - negativeEdges, 0f);
                int? split = null;
                BufferLine? probe = null;

                for (int attempt = 0; attempt <= item.BoundaryEvents.Count; attempt++)
                {
                    BufferLine line = item.Buffer.Lines[lineIndex];
                    List<LayoutLine> layouts = windowable
                        ? ProbeFirstLine(line, ref probe, ref probeChars, metrics.FontSize, available, wrap, mono, tabWidth)
                        : line.Layout(_shaper, metrics.FontSize, available, wrap, mono, tabWidth);
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
                    if (lineWidth + edges > baseAvailable + 0.01f
                        && LastWordStartX(line.Text, layout) is { } lastWordCut
                        && lastWordCut > requiredAvailable)
                    {
                        // DEVIATION from crates/obscura-render/src/inline.rs, which retries at
                        // the width left once every edge on the candidate line is paid for.
                        // The closing edges of the boxes that end with the text all sit on its
                        // last word, so 300 nested `padding:0 3px;margin:0 2px` spans asked
                        // for 1500px there and the retry fell to 0: each later line held one
                        // or two words, 60 lines where Chromium 141 has 6. Never retry below
                        // the width that drops just the last word; that word and its edges go
                        // to the next line, and overflow there when they alone do not fit.
                        requiredAvailable = lastWordCut;
                    }

                    if (lineWidth + edges <= baseAvailable + 0.01f || requiredAvailable + 0.01f >= available)
                    {
                        break;
                    }

                    available = requiredAvailable;
                    item.Buffer.Lines[lineIndex].ResetLayout();
                    probe?.ResetLayout();
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

                    // The next line's start follows this line's new text.
                    sourceOffset = lineOffset;
                    sourceAfterBreak = lineAfterBreak;
                    InlineGeometry.NextSourceLineStart(
                        item.Buffer.Lines[lineIndex].Text, sourceText, ref sourceOffset, ref sourceAfterBreak);
                    probeChars = Math.Max(ProbeInitialWindow, splitAt * 4);
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
        if (width is { } narrowWidth)
        {
            NarrowLines(item, narrowWidth, indent);
        }

        item.Buffer.ShapeUntilScroll(_shaper);
    }

    /// <summary>
    /// A justified line fills the width its indent and its inline boxes' margins, borders
    /// and padding leave, so it is laid out in that width rather than in the whole and moved
    /// (Chromium 141: a 300px paragraph with text-indent:40px justifies its first line to
    /// 40..300, and a span with 5px side padding on a justified line takes 10px from the
    /// spaces). Other alignments place the line in the whole width and shift it instead,
    /// except below a negative indent, where the first line can be wider than the box.
    /// </summary>
    private static void NarrowLines(InlineItem item, float width, float indent)
    {
        List<BufferLine> lines = item.Buffer.Lines;
        if (lines.Count == 0)
        {
            return;
        }

        if (item.Align != Align.Justified && lines[^1].AlignOptions.Last != Align.Justified)
        {
            if (indent < 0f)
            {
                lines[0].WidthOverride = (lines[0].WidthOverride ?? width) - indent;
            }

            return;
        }

        bool edges = item.BoundaryEvents.Count > 0 && item.OwnerText is not null;
        if (indent == 0f && !edges)
        {
            return;
        }

        List<int>? starts = edges ? InlineGeometry.SourceLineStarts(item.Buffer, item.OwnerText!) : null;
        for (int index = 0; index < lines.Count; index++)
        {
            BufferLine line = lines[index];
            float narrowed = (line.WidthOverride ?? width) - (index == 0 ? indent : 0f);
            if (starts is not null && index < starts.Count)
            {
                narrowed -= InlineGeometry.LineEdgeAdvance(item, starts[index], starts[index] + line.Text.Length);
            }

            line.WidthOverride = F32.Max(narrowed, 0f);
        }
    }

    /// <summary>
    /// Lay the IFC's lines out around floats (CSS 2.1 9.5): each line box is shortened by the
    /// floats beside it, and a line whose first word does not fit beside them moves down to the
    /// next float edge. Every visual line becomes its own buffer line, laid out (and aligned)
    /// at its own width and drawn from its own left edge.
    /// </summary>
    /// <remarks>
    /// New behaviour, not a port: crates/obscura-render lays floats out as flex rows and its
    /// line breaker only knows one width.
    /// </remarks>
    private void ShapeAroundFloats(InlineItem item, float width, float indent, Wrap wrap, FloatBands bands)
    {
        TextBuffer source = item.SourceBuffer ?? (item.PristineBuffer ??= item.Buffer.Clone());
        item.Buffer = source.Clone();
        TextBuffer buffer = item.Buffer;
        buffer.SetWrap(wrap);
        TextMetrics metrics = buffer.Metrics;
        float? mono = buffer.MonospaceWidth;
        int tabWidth = buffer.TabWidth;
        float fullWidth = F32.Max(width, 0f);
        string? sourceText = item.OwnerText;
        float strut = F32.Max(metrics.Above + metrics.Below, 0f);
        bool windowable = !item.Rtl;
        foreach (BufferLine paragraph in source.Lines)
        {
            if (Bidi.AnyRightToLeft(paragraph.Text))
            {
                windowable = false;
                break;
            }
        }

        int sourceOffset = 0;
        bool sourceAfterBreak = false;
        float y = 0f;
        int lineIndex = 0;
        while (lineIndex < buffer.Lines.Count)
        {
            WorkCancellation.ThrowIfCancellationRequested();
            int lineOffset = sourceOffset;
            bool lineAfterBreak = sourceAfterBreak;
            int globalStart = sourceText is null
                ? 0
                : InlineGeometry.NextSourceLineStart(
                    buffer.Lines[lineIndex].Text, sourceText, ref sourceOffset, ref sourceAfterBreak);
            float firstIndent = lineIndex == 0 ? indent : 0f;

            float lineTop = y;
            float probeHeight = strut;
            float left = 0f;
            float lineWidth = fullWidth;
            int? split = null;
            float height = strut;
            for (int attempt = 0; attempt < 512; attempt++)
            {
                (float bandLeft, float bandRight) = bands.Insets(lineTop, probeHeight);
                left = bandLeft;
                lineWidth = F32.Max(fullWidth - bandLeft - bandRight, 0f);
                bool intruded = bandLeft > 0f || bandRight > 0f;
                float baseAvailable = F32.Max(lineWidth - firstIndent, 0f);
                (LayoutLine? first, split) = LayoutFirstLine(
                    item, buffer.Lines[lineIndex], windowable, globalStart, baseAvailable, metrics, wrap, mono, tabWidth);
                if (first is null)
                {
                    break;
                }

                height = buffer.LineBox(first).Height;
                float firstWidth = first.W + (split is { } end
                    ? InlineGeometry.LineEdgeAdvance(item, globalStart, globalStart + end)
                    : 0f);
                if (intruded
                    && first.Glyphs.Count != 0
                    && firstWidth > baseAvailable + 0.01f
                    && bands.NextEdgeBelow(lineTop) is { } next)
                {
                    // Not even the first word fits beside the floats: try below the next edge.
                    lineTop = next;
                    probeHeight = strut;
                    continue;
                }

                if (height > probeHeight + 0.01f)
                {
                    // A tall inline box reaches further down than the strut did; the floats
                    // over the whole line box decide its width.
                    (float tallLeft, float tallRight) = bands.Insets(lineTop, height);
                    if (tallLeft > bandLeft + 0.01f || tallRight > bandRight + 0.01f)
                    {
                        probeHeight = height;
                        continue;
                    }
                }

                break;
            }

            BufferLine current = buffer.Lines[lineIndex];
            if (split is { } splitAt)
            {
                string text = current.Text;
                splitAt = SkipTrailingWhitespace(text, splitAt);
                if (splitAt > 0 && splitAt < text.Length)
                {
                    BufferLine tail = current.SplitOff(splitAt);
                    buffer.Lines.Insert(lineIndex + 1, tail);
                    if (sourceText is not null)
                    {
                        sourceOffset = lineOffset;
                        sourceAfterBreak = lineAfterBreak;
                        InlineGeometry.NextSourceLineStart(
                            current.Text, sourceText, ref sourceOffset, ref sourceAfterBreak);
                    }
                }
            }

            current.ResetLayout();
            current.WidthOverride = lineWidth;

            current.OffsetX = left;
            current.GapBefore = lineTop - y;
            y = lineTop + height;
            lineIndex++;
        }

        buffer.SetSize(fullWidth, null);
        NarrowLines(item, fullWidth, indent);
        buffer.ShapeUntilScroll(_shaper);
    }

    /// <summary>
    /// The first visual line of <paramref name="line"/> at <paramref name="baseAvailable"/>,
    /// and where it ends, paying for the inline boxes' margins, borders and padding on it the
    /// way <see cref="ShapeWithTextIndent"/> does.
    /// </summary>
    private (LayoutLine? First, int? Split) LayoutFirstLine(
        InlineItem item,
        BufferLine line,
        bool windowable,
        int globalStart,
        float baseAvailable,
        TextMetrics metrics,
        Wrap wrap,
        float? mono,
        int tabWidth)
    {
        int lineSourceEnd = globalStart + line.Text.Length;
        float negativeEdges = item.BoundaryEvents.Count == 0
            ? 0f
            : item.EdgeIndex.NegativeEdges(globalStart, lineSourceEnd);
        float available = F32.Max(baseAvailable - negativeEdges, 0f);
        LayoutLine? first = null;
        int? split = null;

        // Shape a window of a long line rather than all of it for each visual line (see
        // ProbeFirstLine), or a long paragraph beside floats is quadratic in its length.
        BufferLine? probe = null;
        int probeChars = ProbeInitialWindow;
        for (int attempt = 0; attempt <= item.BoundaryEvents.Count; attempt++)
        {
            line.ResetLayout();
            probe?.ResetLayout();
            List<LayoutLine> layouts = windowable
                ? ProbeFirstLine(line, ref probe, ref probeChars, metrics.FontSize, available, wrap, mono, tabWidth)
                : line.Layout(_shaper, metrics.FontSize, available, wrap, mono, tabWidth);
            if (layouts.Count == 0)
            {
                break;
            }

            first = layouts[0];
            int? candidate = null;
            foreach (LayoutGlyph glyph in first.Glyphs)
            {
                candidate = candidate is { } current ? Math.Max(current, glyph.End) : glyph.End;
            }

            if (candidate is not { } candidateEndLocal)
            {
                split = null;
                break;
            }

            split = candidateEndLocal;
            if (item.BoundaryEvents.Count == 0)
            {
                break;
            }

            float edges = InlineGeometry.LineEdgeAdvance(item, globalStart, globalStart + candidateEndLocal);
            float requiredAvailable = F32.Max(baseAvailable - edges, 0f);
            if (first.W + edges > baseAvailable + 0.01f
                && LastWordStartX(line.Text, first) is { } lastWordCut
                && lastWordCut > requiredAvailable)
            {
                // See ShapeWithTextIndent: never retry below the width that drops the last word.
                requiredAvailable = lastWordCut;
            }

            if (first.W + edges <= baseAvailable + 0.01f || requiredAvailable + 0.01f >= available)
            {
                break;
            }

            available = requiredAvailable;
        }

        return (first, split);
    }

    /// <summary>
    /// The width at which <paramref name="layout"/> keeps everything but its last word: the x
    /// of the last space that has a non-space glyph after it. <c>null</c> when the line is one
    /// word, or is not a plain left-to-right line.
    /// </summary>
    private static float? LastWordStartX(string text, LayoutLine layout)
    {
        List<LayoutGlyph> glyphs = layout.Glyphs;
        bool sawWord = false;
        for (int index = glyphs.Count - 1; index >= 0; index--)
        {
            LayoutGlyph glyph = glyphs[index];
            if ((glyph.Level & 1) != 0 || glyph.Start < 0 || glyph.Start >= text.Length)
            {
                return null;
            }

            if (text[glyph.Start] == ' ')
            {
                if (sawWord)
                {
                    return glyph.X - glyphs[0].X + 0.01f;
                }
            }
            else
            {
                sawWord = true;
            }
        }

        return null;
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
        float baseSize = baseStyle.FontSize ?? 16f;

        // The block container's own box is the line box's strut: it participates in every line
        // box the block generates, and its `vertical-align` (if any) is not its own business.
        (float strutAbove, float strutBelow) =
            FontAssets.LineBoxHalves(baseSize, lineHeight, font.Metrics);
        return new SpanCtx
        {
            FontSize = baseSize,
            LineHeight = lineHeight,
            LineHeightNormal = Inline.IsNormalLineHeight(baseStyle),
            Above = strutAbove,
            Below = strutBelow,
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

        if (collector.FloatAnchors is { } floatAnchors
            && style is { Float: not null }
            && style.Position != Position.Absolute)
        {
            // Out of the inline flow: remember where it sits for the block formatting context.
            floatAnchors.Add((cid, collector.TextLength));
            return;
        }

        if (string.Equals(element.Name.Local, "br", StringComparison.Ordinal))
        {
            // DEVIATION from crates/obscura-render/src/inline.rs, which turns a <br> into a
            // newline and nothing else, so the element owns no shaped range and
            // getBoundingClientRect() reports 0,0,0,0 for it. Chromium gives a <br> a real
            // zero-width box on the line it ends, as tall as its font box: measured at
            // 16px/20px 'Liberation Mono' in a 600px block, `alpha<br>beta` reports the break
            // at 48.02,1.00,0.00,18.00 - x at the end of the line's content, y at the font-box
            // top (the baseline less the ascent), not the line-box top. Registering it as an
            // empty inline owner at the pre-newline offset routes it through the same
            // machinery an empty <span> uses, which already matched Chromium wherever it
            // produced a fragment at all.
            if (style is not null)
            {
                collector.BeginOwner(cid, style);
                collector.EndOwner(
                    cid,
                    new InlineBoxExtent(context.Above, context.Below, context.Align, context.BaselineShift));
            }

            collector.FlushLastSpan(output);
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

        float childFontSize = style?.FontSize ?? context.FontSize;
        float childLineHeight = style is not null
            ? FontResolution.UsedLineHeightForFont(style, font)
            : context.LineHeight;
        InlineBoxExtent extent = Inline.LineBoxExtent(
            style?.InlineVerticalAlign,
            childFontSize,
            childLineHeight,
            font.Metrics,
            context.FontSize,
            context.FontMetrics,
            context.BaselineShift,
            context.Align);

        var child = new SpanCtx
        {
            FontSize = childFontSize,
            LineHeight = childLineHeight,
            LineHeightNormal = style is not null ? Inline.IsNormalLineHeight(style) : context.LineHeightNormal,
            Above = extent.Above,
            Below = extent.Below,
            Align = extent.Align,
            BaselineShift = extent.Shift,
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
            collector.EndOwner(cid, extent);
        }
    }

    public void Dispose()
    {
        _rasterizer.Dispose();
        _database.Dispose();
    }
}
