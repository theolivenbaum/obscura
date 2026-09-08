using System.Globalization;
using System.Text;
using Obscura.Dom;
using Obscura.Render.Layout;
using NodeId = Obscura.Dom.NodeId;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>A run of same-styled inline text.</summary>
public sealed record SpanAttrs
{
    public required float FontSize { get; init; }

    public required float LineHeight { get; init; }

    public required float LetterSpacing { get; init; }

    public required bool LetterSpacingNonNormal { get; init; }

    public required ushort Weight { get; init; }

    public required FontOpticalSizing OpticalSizing { get; init; }

    public FontId? FontId { get; init; }

    public FontVariations? Variations { get; init; }

    public required bool Italic { get; init; }

    public required bool SyntheticItalic { get; init; }

    public required bool Underline { get; init; }

    public required RgbaColor Color { get; init; }

    public required string Family { get; init; }

    public int? ClipFill { get; init; }

    public required WhiteSpace WhiteSpace { get; init; }

    public required OverflowWrap OverflowWrap { get; init; }

    public required WordBreak WordBreak { get; init; }

    public bool WrappingEnabled => WhiteSpace is not (WhiteSpace.NoWrap or WhiteSpace.Pre);

    public bool HasLayoutEmergencyBreaks =>
        WrappingEnabled && (WordBreak == WordBreak.BreakWord || OverflowWrap != OverflowWrap.Normal);

    public bool HasMinContentEmergencyBreaks =>
        WrappingEnabled && (WordBreak == WordBreak.BreakWord || OverflowWrap == OverflowWrap.Anywhere);

    /// <summary>Build the shaping attributes for this span, with its variation-set index.</summary>
    public TextAttrs ToAttrs(int variationIndex)
    {
        // Clip-text glyphs must be shaped with an opaque fill so their coverage reaches paint;
        // the real gradient is selected through metadata.
        RgbaColor color = ClipFill is not null ? new RgbaColor(255, 255, 255, 255) : Color;
        ulong fill = ClipFill is { } index ? (ulong)(index + 1) << InlineGeometry.MetaFillShift : 0;
        if ((fill & ~InlineGeometry.MetaFillMask) != 0)
        {
            throw new InvalidOperationException("clip fill index overflows its metadata field");
        }

        ulong variation = (ulong)variationIndex << InlineGeometry.MetaVariationShift;
        if ((variation & ~InlineGeometry.MetaVariationMask) != 0)
        {
            throw new InvalidOperationException("variation index overflows its metadata field");
        }

        ShapingFeature[] features = [];
        if (LetterSpacingNonNormal && float.IsFinite(LetterSpacing) && LetterSpacing != 0f)
        {
            features =
            [
                new ShapingFeature(Tag("liga"), 0),
                new ShapingFeature(Tag("clig"), 0),
            ];
        }

        return new TextAttrs
        {
            Family = Family,
            FontId = FontId,
            // Inline descendants keep their own computed font metrics inside the enclosing line
            // box. Without per-span metrics, an <a>/<span> with a relative font-size shapes at
            // the block container's size even though cascade resolved the descendant correctly.
            Metrics = (F32.Max(FontSize, 1f), F32.Max(LineHeight, 1f)),
            Weight = Weight,
            FontWeightAxis = Weight,
            FontOpticalSize = OpticalSizing == FontOpticalSizing.Auto ? FontSize : null,
            FontItalicAxis = Italic,
            Style = Italic ? FaceStyle.Italic : FaceStyle.Normal,
            FakeItalic = SyntheticItalic,
            LetterSpacingEm = float.IsFinite(LetterSpacing) && LetterSpacing != 0f
                ? LetterSpacing / F32.Max(FontSize, 1f)
                : null,
            Features = features,
            Color = color,
            Variations = Variations,
            CssLineBreakPolicy = new CssLineBreak(WrappingEnabled, WordBreak, OverflowWrap),
            Metadata = fill | variation | (Underline ? InlineGeometry.MetaUnderline : 0),
        };
    }

    internal static uint Tag(string tag) =>
        ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];
}

/// <summary>Inherited inline context threaded down the subtree while collecting spans.</summary>
internal sealed record SpanCtx
{
    public required float FontSize { get; init; }

    public required float LineHeight { get; init; }

    public required float LetterSpacing { get; init; }

    public required bool LetterSpacingNonNormal { get; init; }

    public required RgbaColor Color { get; init; }

    public required ushort Weight { get; init; }

    public required FontOpticalSizing OpticalSizing { get; init; }

    public FontId? FontId { get; init; }

    public required FaceMetrics FontMetrics { get; init; }

    public FontVariations? Variations { get; init; }

    public required bool Italic { get; init; }

    public required bool SyntheticItalic { get; init; }

    public required bool Underline { get; init; }

    public required TextTransform Transform { get; init; }

    public required WhiteSpace WhiteSpace { get; init; }

    public required OverflowWrap OverflowWrap { get; init; }

    public required WordBreak WordBreak { get; init; }

    public required string Family { get; init; }

    public int? ClipFill { get; init; }

    public SpanAttrs ToSpanAttrs() => new()
    {
        FontSize = FontSize,
        LineHeight = LineHeight,
        LetterSpacing = LetterSpacing,
        LetterSpacingNonNormal = LetterSpacingNonNormal,
        Weight = Weight,
        OpticalSizing = OpticalSizing,
        FontId = FontId,
        Variations = Variations,
        Italic = Italic,
        SyntheticItalic = SyntheticItalic,
        Underline = Underline,
        Color = Color,
        Family = Family,
        ClipFill = ClipFill,
        WhiteSpace = WhiteSpace,
        OverflowWrap = OverflowWrap,
        WordBreak = WordBreak,
    };
}

/// <summary>Accumulates the collapsed text, owner provenance, and clip fills of one IFC.</summary>
internal sealed class Collector
{
    public bool LastWasSpace = true;
    public List<ClipTextFill> ClipFills = [];
    public List<ActiveInlineOwner> Owners = [];
    public List<OwnerTextRange> OwnerRanges = [];
    public List<InlineOwnerBox> OwnerBoxes = [];
    public List<InlineBoundaryEvent> BoundaryEvents = [];
    public int TextLength;

    public void RecordText(int length)
    {
        int start = TextLength;
        TextLength += length;
        foreach (ActiveInlineOwner owner in Owners)
        {
            OwnerRanges.Add(new OwnerTextRange(owner.Owner, start, TextLength));
        }
    }

    public void BeginOwner(NodeId owner, LayoutStyle style)
    {
        var startEdge = new InlineEdge(style.Margin.Left, style.Border.Left, style.Padding.Left);
        var endEdge = new InlineEdge(style.Margin.Right, style.Border.Right, style.Padding.Right);
        int startEvent = BoundaryEvents.Count;
        BoundaryEvents.Add(new InlineBoundaryEvent(owner, TextLength, true, startEdge));
        Owners.Add(new ActiveInlineOwner(owner, TextLength, startEdge, endEdge, startEvent));
    }

    public void EndOwner(NodeId owner)
    {
        ActiveInlineOwner active = Owners[^1];
        Owners.RemoveAt(Owners.Count - 1);
        int endEvent = BoundaryEvents.Count;
        BoundaryEvents.Add(new InlineBoundaryEvent(owner, TextLength, false, active.EndEdge));
        OwnerBoxes.Add(new InlineOwnerBox(
            owner,
            active.Start,
            TextLength,
            active.StartEdge,
            active.EndEdge,
            active.StartEvent,
            endEvent));
    }
}

/// <summary>
/// Inline formatting context classification, span collection, and the geometry helpers lib.rs
/// re-exports.
/// </summary>
public static class Inline
{
    /// <summary>
    /// Content-box origin for a container whose border box is <paramref name="rect"/>: inside
    /// its border and padding, where inline text actually starts.
    /// </summary>
    public static (float X, float Y) ContentOrigin(Rect rect, LayoutStyle style) => (
        rect.X + style.Border.Left + style.Padding.Left,
        rect.Y + style.Border.Top + style.Padding.Top);

    public static float ContentWidth(Rect rect, LayoutStyle style) => F32.Max(
        rect.Width - style.Border.Left - style.Border.Right - style.Padding.Left - style.Padding.Right,
        0f);

    /// <summary>
    /// Replaced / atomic-inline tags: their box does not contain text, so folding one into a
    /// shaped buffer would drop its content entirely.
    /// </summary>
    /// <remarks>
    /// This is by tag, not display, so a stylesheet setting <c>img{display:inline}</c> cannot
    /// trick the engine into folding it.
    /// </remarks>
    public static bool IsReplaced(string local) => local switch
    {
        "img" or "svg" or "canvas" or "video" or "audio" or "iframe" or "embed" or "object"
            or "input" or "textarea" or "select" or "button" or "progress" or "meter" => true,
        _ => false,
    };

    /// <summary>
    /// Elements that use CSS's intrinsic replaced-size algorithm. Deliberately narrower than
    /// <see cref="IsReplaced"/>: ordinary form controls are atomic inline boxes, but grid
    /// <c>normal</c> stretches them like non-replaced boxes in Chromium and Gecko.
    /// </summary>
    public static bool HasReplacedSizing(string local) => local switch
    {
        "img" or "canvas" or "video" or "audio" or "iframe" or "embed" or "object"
            or "progress" or "meter" => true,
        _ => false,
    };

    /// <summary>
    /// The used size of a replaced element whose preferred width and height are both auto,
    /// with its min/max constraints transferred through the preferred aspect ratio.
    /// </summary>
    internal static Size<float> ConstrainedAutoReplacedSize(float width, float height, LayoutStyle style) =>
        ReplacedItem.FromStyle(width, height, style).Size(new Size<float?>(null, null));

    /// <summary>
    /// HTML's default object size for replaced media whose intrinsic metadata is not available
    /// yet. Canvas dimensions and decoded video metadata can replace these defaults before
    /// layout when present.
    /// </summary>
    public static (float Width, float Height)? DefaultReplacedIntrinsicSize(
        string local,
        float fontSize,
        bool hasControls,
        bool hasResource) => local switch
    {
        "canvas" or "video" or "iframe" or "object" => (300f, 150f),
        "embed" when hasResource => (300f, 150f),
        "audio" when hasControls => (300f, 54f),
        "progress" => (fontSize * 10f, fontSize),
        "meter" => (fontSize * 5f, fontSize),
        _ => null,
    };

    /// <summary>
    /// The children that generate boxes for <paramref name="id"/>.
    /// </summary>
    /// <remarks>
    /// RECONCILIATION NOTE: <c>dom.rs</c> owns <c>rendered_children</c> in the Rust tree. This
    /// is the same function, living here because the inline port needs it and the dom.rs port
    /// has not landed. The dom agent should keep one copy.
    /// </remarks>
    public static List<NodeId> RenderedChildren(DomTree tree, NodeId id)
    {
        if (tree.ShadowChildren(id) is { } shadowChildren)
        {
            // A shadow host's light children stay in the DOM but its box tree is generated from
            // the shadow root. Matching light children re-enter at slot insertion points below;
            // unslotted children generate no boxes.
            return shadowChildren;
        }

        if (tree.SlotRenderedChildren(id) is { } assignedOrFallback)
        {
            return assignedOrFallback;
        }

        Node? node = tree.GetNode(id);
        if (node is null)
        {
            return [];
        }

        bool closedDetails = node.AsElement() is { } element
            && string.Equals(element.Name.Ns, Namespaces.Html, StringComparison.Ordinal)
            && string.Equals(element.Name.Local, "details", StringComparison.Ordinal)
            && node.GetAttribute("open") is null;
        if (!closedDetails)
        {
            return tree.Children(id);
        }

        // HTML gives a closed <details> a rendered child list containing only its first direct
        // <summary> element child.
        foreach (NodeId child in tree.Children(id))
        {
            if (tree.GetNode(child)?.AsElement() is { } summary
                && string.Equals(summary.Name.Ns, Namespaces.Html, StringComparison.Ordinal)
                && string.Equals(summary.Name.Local, "summary", StringComparison.Ordinal))
            {
                return [child];
            }
        }

        return [];
    }

    /// <summary>
    /// Does <paramref name="id"/> establish an inline formatting context made purely of text
    /// and plain inline formatting?
    /// </summary>
    /// <remarks>
    /// Such a container collapses cleanly to one shaped buffer; anything else keeps the general
    /// build path.
    /// </remarks>
    public static bool IsPureTextIfc(DomTree tree, NodeId id, IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        if (!styles.TryGetValue(id, out LayoutStyle? style))
        {
            return false;
        }

        // Out-of-flow generated boxes neither contribute to nor interrupt the host's inline
        // formatting context. Keeping a positioned decorative pseudo must not demote an
        // otherwise text-only shrink-to-fit control to approximate per-word flex leaves.
        static bool HasInFlowPseudo(LayoutStyle? pseudo) =>
            pseudo is not null && pseudo.Display != Display.None && pseudo.Position != Position.Absolute;

        if (HasInFlowPseudo(style.BeforePseudo) || HasInFlowPseudo(style.AfterPseudo))
        {
            return false;
        }

        // Only containers that lay their children out in normal flow (block, or the flex-column
        // stand-ins our UA sheet uses for td/th/center). A real flex/grid row with inline
        // children is rare and better left to taffy.
        bool flow = style.Display == Display.Block
            || style.IsInlineBlock
            || (style.Display == Display.Flex && style.FlexDirection == FlexDirection.Column);
        if (!flow)
        {
            return false;
        }

        bool hasText = false;
        List<NodeId> children = RenderedChildren(tree, id);
        if (children.Count == 0)
        {
            return false;
        }

        foreach (NodeId child in children)
        {
            if (!InlineChildOk(tree, child, styles, ref hasText))
            {
                return false;
            }
        }

        return hasText;
    }

    /// <summary>
    /// Is <paramref name="cid"/> (and its whole subtree) inline-level, in-flow content safe to
    /// fold into a shaped buffer?
    /// </summary>
    /// <remarks>
    /// Sets <paramref name="hasText"/> if it contributes any non-whitespace text. Inline
    /// wrappers are accepted and recursed into even when they carry a background or border of
    /// their own: keeping the whole paragraph as one shaped run is worth losing an inline
    /// decoration. Only boxes that genuinely cannot fold are rejected: replaced/atomic
    /// elements, block-level children, floats, out-of-flow positioned boxes, and elements with
    /// generated content.
    /// </remarks>
    internal static bool InlineChildOk(
        DomTree tree,
        NodeId cid,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        ref bool hasText)
    {
        Node? node = tree.GetNode(cid);
        if (node is null)
        {
            return true;
        }

        if (node.Data is TextData text)
        {
            if (text.Contents.Trim().Length != 0)
            {
                hasText = true;
            }

            return true;
        }

        if (node.AsElement() is not { } element)
        {
            return true;
        }

        if (!styles.TryGetValue(cid, out LayoutStyle? style))
        {
            return false;
        }

        if (style.Display == Display.None)
        {
            return true; // removed from flow; ignore its subtree
        }

        if (string.Equals(element.Name.Local, "br", StringComparison.Ordinal))
        {
            hasText = true;
            return true;
        }

        // A replaced element or an atomic inline-block has its own box with non-text content.
        if (IsReplaced(element.Name.Local) || style.IsInlineBlock)
        {
            return false;
        }

        bool foldableInline = style.Display == Display.Inline
            && style.Position != Position.Absolute
            && style.Float is null
            && !style.OverflowHidden
            && style.BeforePseudo is null
            && style.AfterPseudo is null;
        if (!foldableInline)
        {
            return false;
        }

        if (style.Margin != default || style.Padding != default || style.Border != default)
        {
            hasText = true;
        }

        foreach (NodeId grandchild in RenderedChildren(tree, cid))
        {
            if (!InlineChildOk(tree, grandchild, styles, ref hasText))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The background to paint through the glyphs for <c>-webkit-background-clip: text</c> when
    /// the element's own text color is transparent.
    /// </summary>
    /// <remarks>
    /// Returns the background gradient as is, or a solid background color as a flat two-stop
    /// gradient. <c>null</c> when the element is not a transparent-text clip-to-text box, so
    /// ordinary transparent text still renders invisibly.
    /// </remarks>
    internal static ClipTextFill? ClipTextFillFor(LayoutStyle style)
    {
        if (!style.BackgroundClipText)
        {
            return null;
        }

        // Only when the text itself is transparent: an opaque color paints normally and the
        // clip is a no-op we would otherwise recolor incorrectly.
        if (style.Color is not { } color || color.A != 0)
        {
            return null;
        }

        if (style.BackgroundGradient is { } gradient && gradient.Stops.Count >= 2)
        {
            List<(RgbaColor Color, float? Position)> stops = new(gradient.Stops.Count);
            foreach (GradientStop stop in gradient.Stops)
            {
                stops.Add((stop.Color, stop.Position));
            }

            return new ClipTextFill(gradient.Angle, stops);
        }

        if (style.BackgroundColor is not { } background || background.A == 0)
        {
            return null;
        }

        return new ClipTextFill(180f, [(background, 0f), (background, 1f)]);
    }

    /// <summary>
    /// Sample a CSS linear gradient at <c>(x, y)</c> inside a <c>w</c> x <c>h</c> text box.
    /// </summary>
    /// <remarks>
    /// <paramref name="fill"/>'s angle is CSS degrees clockwise from 12 o'clock (0 = to top,
    /// 90 = to right, 180 = to bottom). Positionless stops are spread evenly.
    /// </remarks>
    internal static RgbaColor SampleGradient(ClipTextFill fill, float x, float y, float w, float h)
    {
        List<(RgbaColor Color, float? Position)> stops = fill.Stops;
        switch (stops.Count)
        {
            case 0:
                return new RgbaColor(0, 0, 0, 255);
            case 1:
                return stops[0].Color;
        }

        float rad = F32.ToRadians(fill.Angle);
        float dx = MathF.Sin(rad);
        float dy = -MathF.Cos(rad);
        w = F32.Max(w, 1f);
        h = F32.Max(h, 1f);

        // Full extent of the box along the gradient direction (the CSS gradient-line length),
        // so the endpoints land at the box's projected corners.
        float length = MathF.Abs(w * dx) + MathF.Abs(h * dy);
        float t = length <= 0f
            ? 0.5f
            : Math.Clamp((((x - (w / 2f)) * dx) + ((y - (h / 2f)) * dy)) / length + 0.5f, 0f, 1f);

        int n = stops.Count;
        float Position(int i) => Math.Clamp(stops[i].Position ?? (i / (n - 1f)), 0f, 1f);

        int lo = 0;
        while (lo + 1 < n && Position(lo + 1) < t)
        {
            lo++;
        }

        int hi = Math.Min(lo + 1, n - 1);
        float p0 = Position(lo);
        float p1 = Position(hi);
        float f = MathF.Abs(p1 - p0) < 1e-6f ? 0f : Math.Clamp((t - p0) / (p1 - p0), 0f, 1f);
        RgbaColor c0 = stops[lo].Color;
        RgbaColor c1 = stops[hi].Color;

        byte Lerp(byte a, byte b) => (byte)Math.Clamp(F32.Round(a + ((b - (float)a) * f)), 0f, 255f);

        return new RgbaColor(Lerp(c0.R, c1.R), Lerp(c0.G, c1.G), Lerp(c0.B, c1.B), Lerp(c0.A, c1.A));
    }

    /// <summary>
    /// The low-level authored variation tuple for a style.
    /// </summary>
    /// <remarks>
    /// Automatic high-level axes travel separately with every glyph so they can be resolved
    /// against the face actually selected during fallback. Only low-level authored settings
    /// need an allocated tuple here.
    /// </remarks>
    internal static FontVariations? ResolvedFontVariations(LayoutStyle style)
    {
        List<FontVariationSetting>? settings = style.FontVariationSettings;
        if (settings is null || settings.Count == 0)
        {
            return null;
        }

        var variations = new FontVariations();
        foreach (FontVariationSetting setting in settings)
        {
            if (float.IsFinite(setting.Value) && setting.Tag.Length == 4)
            {
                variations.Set(VariationTag.FromAscii(setting.Tag), setting.Value);
            }
        }

        return variations.IsEmpty ? null : variations;
    }

    /// <summary>
    /// Append one text node's whitespace-collapsed, transformed runs.
    /// </summary>
    /// <remarks>
    /// Collapsing spans HTML's insignificant whitespace (runs of spaces, tabs, and newlines
    /// fold to one space; leading space at the start of the context is dropped) exactly as
    /// <c>white-space: normal</c> requires. Adjacent runs with identical attributes are merged
    /// so shaping sees the fewest spans.
    /// </remarks>
    internal static void PushText(
        string raw,
        TextTransform transform,
        WhiteSpace whiteSpace,
        SpanAttrs attrs,
        List<(string Text, SpanAttrs Attrs)> output,
        Collector collector)
    {
        var buffer = new StringBuilder();
        bool atWordStart = collector.LastWasSpace;
        foreach (Rune rune in raw.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                switch (whiteSpace)
                {
                    case WhiteSpace.Pre or WhiteSpace.PreWrap or WhiteSpace.BreakSpaces:
                        buffer.Append(rune.ToString());
                        break;
                    case WhiteSpace.PreLine when rune.Value == '\n':
                        if (buffer.Length > 0 && buffer[^1] == ' ')
                        {
                            buffer.Length--;
                        }

                        buffer.Append('\n');
                        break;
                    default:
                        if (!collector.LastWasSpace)
                        {
                            buffer.Append(' ');
                        }

                        break;
                }

                collector.LastWasSpace = true;
                atWordStart = true;
            }
            else
            {
                switch (transform)
                {
                    case TextTransform.Uppercase:
                        buffer.Append(ToUpper(rune));
                        break;
                    case TextTransform.Lowercase:
                        buffer.Append(ToLower(rune));
                        break;
                    case TextTransform.Capitalize when atWordStart:
                        buffer.Append(ToUpper(rune));
                        break;
                    default:
                        buffer.Append(rune.ToString());
                        break;
                }

                collector.LastWasSpace = false;
                atWordStart = false;
            }
        }

        if (buffer.Length == 0)
        {
            return;
        }

        string text = buffer.ToString();
        collector.RecordText(text.Length);
        if (output.Count > 0 && output[^1].Attrs == attrs)
        {
            output[^1] = (output[^1].Text + text, output[^1].Attrs);
            return;
        }

        output.Add((text, attrs));
    }

    /// <summary>
    /// Rust's <c>char::to_uppercase</c> applies Unicode's <em>full</em> uppercase mapping, so a
    /// single scalar can expand: German sharp s becomes "SS" and the Latin ligatures decompose.
    /// </summary>
    /// <remarks>
    /// .NET's invariant <c>ToUpper</c> is the <em>simple</em> (one-to-one) mapping and leaves
    /// all of these unchanged, which would make <c>text-transform: uppercase</c> measure
    /// narrower than both Chromium and the Rust engine. The unconditional expansions from
    /// Unicode's SpecialCasing data are applied here first.
    /// <para>
    /// Not implemented: the Greek iota-subscript block (U+1F80-U+1FFC), whose uppercase
    /// mappings append U+0399. Greek polytonic text under <c>text-transform: uppercase</c> will
    /// measure narrower here than in the Rust engine.
    /// </para>
    /// </remarks>
    private static string ToUpper(Rune rune) => rune.Value switch
    {
        0x00DF => "SS",
        0x0149 => "\u02BCN",
        0x01F0 => "J\u030C",
        0x0390 => "\u0399\u0308\u0301",
        0x03B0 => "\u03A5\u0308\u0301",
        0x0587 => "\u0535\u0552",
        0x1E96 => "H\u0331",
        0x1E97 => "T\u0308",
        0x1E98 => "W\u030A",
        0x1E99 => "Y\u030A",
        0x1E9A => "A\u02BE",
        0x1F50 => "\u03A5\u0313",
        0x1F52 => "\u03A5\u0313\u0300",
        0x1F54 => "\u03A5\u0313\u0301",
        0x1F56 => "\u03A5\u0313\u0342",
        0x1FB6 => "\u0391\u0342",
        0x1FC6 => "\u0397\u0342",
        0x1FD2 => "\u0399\u0308\u0300",
        0x1FD3 => "\u0399\u0308\u0301",
        0x1FD6 => "\u0399\u0342",
        0x1FD7 => "\u0399\u0308\u0342",
        0x1FE2 => "\u03A5\u0308\u0300",
        0x1FE3 => "\u03A5\u0308\u0301",
        0x1FE4 => "\u03A1\u0313",
        0x1FE6 => "\u03A5\u0342",
        0x1FE7 => "\u03A5\u0308\u0342",
        0x1FF6 => "\u03A9\u0342",
        0xFB00 => "FF",
        0xFB01 => "FI",
        0xFB02 => "FL",
        0xFB03 => "FFI",
        0xFB04 => "FFL",
        0xFB05 or 0xFB06 => "ST",
        0xFB13 => "\u0544\u0546",
        0xFB14 => "\u0544\u0535",
        0xFB15 => "\u0544\u053B",
        0xFB16 => "\u054E\u0546",
        0xFB17 => "\u0544\u053D",
        _ => rune.ToString().ToUpper(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Full lowercase mapping. Only U+0130 expands unconditionally, and .NET's invariant
    /// lowercase leaves it unchanged.
    /// </summary>
    private static string ToLower(Rune rune) => rune.Value switch
    {
        0x0130 => "i\u0307",
        _ => rune.ToString().ToLower(CultureInfo.InvariantCulture),
    };
}
